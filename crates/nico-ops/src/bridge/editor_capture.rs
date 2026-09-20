//! Bounded capture transfer. The editor chooses a local destination; host paths
//! never participate in file access. Failed transfers remove only their own file.

use crate::mcp::{CallToolResult, Map, Value};
use base64::Engine;
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    fs::File,
    io::{self, Write},
    path::{Path, PathBuf},
    time::{Duration, Instant},
};

const MAX_BYTES: u64 = 32 * 1024 * 1024;
const CHUNK: u64 = 16 * 1024;

struct Output {
    file: Option<File>,
    path: PathBuf,
    complete: bool,
}
impl Drop for Output {
    fn drop(&mut self) {
        self.file.take();
        if !self.complete {
            let _ = std::fs::remove_file(&self.path);
        }
    }
}

pub(super) fn download(
    destination: &Path,
    mut invoke: impl FnMut(&str, Map<String, Value>) -> io::Result<CallToolResult>,
    cancelled: impl Fn() -> bool,
) -> io::Result<CallToolResult> {
    let deadline = Instant::now() + Duration::from_secs(30);
    let check = || {
        if cancelled() || Instant::now() >= deadline {
            Err(io::Error::other("capture cancelled or timed out"))
        } else {
            Ok(())
        }
    };
    check()?;
    let accepted = data(invoke("window_snapshot", Map::new())?)?;
    let id = accepted["request_id"]
        .as_u64()
        .filter(|id| *id > 0)
        .ok_or_else(|| io::Error::other("invalid capture ID"))?;
    let capture = loop {
        check()?;
        let result = data(invoke("window_snapshot", object(json!({"request_id":id})))?)?;
        if result["request_id"].as_u64() != Some(id) {
            return Err(io::Error::other("capture ID changed"));
        }
        match result["state"].as_str() {
            Some("ready") => break result,
            Some("pending") => std::thread::sleep(Duration::from_millis(25)),
            _ => return Err(io::Error::other("capture failed")),
        }
    };
    let total = capture["total_bytes"]
        .as_u64()
        .filter(|size| (1..=MAX_BYTES).contains(size))
        .ok_or_else(|| io::Error::other("capture size exceeds bound"))?;
    if capture["frame_id"].as_u64().is_none()
        || capture["process_session"]
            .as_str()
            .is_none_or(|id| id.is_empty() || id.len() > 128)
        || capture["sha256"].as_str().is_none_or(|hash| {
            hash.len() != 64 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit())
        })
    {
        return Err(io::Error::other("invalid capture identity"));
    }
    let file = File::options()
        .write(true)
        .create_new(true)
        .open(destination)?;
    let mut output = Output {
        file: Some(file),
        path: destination.into(),
        complete: false,
    };
    let mut digest = Sha256::new();
    let mut offset = 0;
    while offset < total {
        check()?;
        let chunk = data(invoke(
            "window_snapshot_read",
            object(json!({"request_id":id,"offset":offset,"limit":CHUNK})),
        )?)?;
        if [
            "request_id",
            "frame_id",
            "process_session",
            "total_bytes",
            "sha256",
        ]
        .iter()
        .any(|field| chunk[*field] != capture[*field])
            || chunk["offset"].as_u64() != Some(offset)
            || chunk["encoding"] != "base64"
        {
            return Err(io::Error::other("capture identity changed during transfer"));
        }
        let text = chunk["data"]
            .as_str()
            .filter(|text| text.len() <= 4 * (CHUNK as usize).div_ceil(3))
            .ok_or_else(|| io::Error::other("invalid capture chunk"))?;
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(text)
            .map_err(|_| io::Error::other("invalid capture encoding"))?;
        let next = offset + bytes.len() as u64;
        if bytes.is_empty()
            || bytes.len() as u64 > CHUNK
            || next > total
            || chunk["next_offset"].as_u64() != Some(next)
            || chunk["complete"].as_bool() != Some(next == total)
        {
            return Err(io::Error::other("invalid capture chunk range"));
        }
        output.file.as_mut().unwrap().write_all(&bytes)?;
        digest.update(bytes);
        offset = next;
    }
    if format!("{:x}", digest.finalize()) != capture["sha256"].as_str().unwrap() {
        return Err(io::Error::other("capture hash mismatch"));
    }
    output.file.as_mut().unwrap().flush()?;
    output.complete = true;
    Ok(CallToolResult::structured(
        json!({"state":"ready","path":destination,"request_id":id,"frame_id":capture["frame_id"],
        "process_session":capture["process_session"],"sha256":capture["sha256"],"total_bytes":total,"width":capture["width"],"height":capture["height"]}),
    ))
}

fn data(result: CallToolResult) -> io::Result<Value> {
    if result.is_error == Some(true) {
        return Err(io::Error::other("capture operation rejected by host"));
    }
    result
        .structured_content
        .ok_or_else(|| io::Error::other("capture result missing"))
}
fn object(value: Value) -> Map<String, Value> {
    value.as_object().unwrap().clone()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn transfer(path: &Path, corrupt: bool, changed_frame: bool) -> io::Result<CallToolResult> {
        let bytes = vec![17_u8; 20000];
        let mut capture = json!({"request_id":1,"frame_id":27,"process_session":"host-process","total_bytes":bytes.len(),"sha256":format!("{:x}",Sha256::digest(&bytes)),"state":"ready","width":100,"height":50,"path":"/host/path/must/not/be/read"});
        download(
            path,
            |tool, args| {
                if tool == "window_snapshot" {
                    return Ok(CallToolResult::structured(capture.clone()));
                }
                let offset = args["offset"].as_u64().unwrap() as usize;
                let end = (offset + CHUNK as usize).min(bytes.len());
                let mut part = bytes[offset..end].to_vec();
                if corrupt {
                    part[0] ^= 1;
                }
                if changed_frame {
                    capture["frame_id"] = 28.into();
                }
                let mut chunk = capture.clone();
                chunk.as_object_mut().unwrap().extend(object(json!({"offset":offset,"next_offset":end,"encoding":"base64","complete":end == bytes.len(),"data":base64::engine::general_purpose::STANDARD.encode(part)})));
                Ok(CallToolResult::structured(chunk))
            },
            || false,
        )
    }

    #[test]
    fn downloads_without_host_filesystem_access_and_does_not_overwrite_existing_files() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("capture.png");
        let result = transfer(&path, false, false).unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), vec![17_u8; 20000]);
        assert_eq!(result.structured_content.unwrap()["frame_id"], 27);
        assert!(transfer(&path, false, false).is_err());
        assert!(path.exists());
    }

    #[test]
    fn identity_changes_and_corrupt_bytes_remove_only_the_partial_download() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("capture.png");
        assert!(transfer(&path, true, false).is_err());
        assert!(!path.exists());
        assert!(transfer(&path, false, true).is_err());
        assert!(!path.exists());
        assert!(download(&path, |_, _| panic!("cancelled request sent"), || true).is_err());
    }
}
