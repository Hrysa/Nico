//! Client-only snapshot tool. Tooling queues requests; the host captures at rendering.
use base64::Engine;
use nico_ops::snapshot::Pixels;
use nico_ops::{
    HostControl,
    mcp::{CallToolResult, Tool, ToolExtensions},
    snapshot::SnapshotState,
};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    io,
    path::PathBuf,
    sync::{Arc, Mutex},
    thread::{self, JoinHandle},
};

const MAX_PNG_BYTES: usize = 32 * 1024 * 1024;
const MAX_CHUNK: usize = 16 * 1024;
#[derive(Debug, PartialEq, Eq)]
struct Image {
    path: PathBuf,
    bytes: Vec<u8>,
    digest: String,
    frame_id: u64,
}
type Encoded = Result<Arc<Image>, String>;

#[derive(Default)]
struct BoundedBytes(Vec<u8>);
impl io::Write for BoundedBytes {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.0.len().saturating_add(bytes.len()) > MAX_PNG_BYTES {
            return Err(io::Error::other("PNG exceeds 32 MiB capture bound"));
        }
        self.0.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
struct Encoding {
    id: u64,
    worker: Option<JoinHandle<Encoded>>,
    result: Option<Encoded>,
}
#[derive(Default)]
struct Encoder {
    job: Option<Encoding>,
}
impl Encoder {
    fn busy(&self) -> bool {
        self.job
            .as_ref()
            .and_then(|job| job.worker.as_ref())
            .is_some_and(|worker| !worker.is_finished())
    }
    fn poll(
        &mut self,
        id: u64,
        write: impl FnOnce() -> Encoded + Send + 'static,
    ) -> Result<Option<Arc<Image>>, String> {
        if self.job.as_ref().is_none_or(|job| job.id != id) {
            if self.busy() {
                return Err("snapshot_encoder_busy".into());
            }
            let worker = thread::Builder::new()
                .name("nico-snapshot-encoder".into())
                .spawn(write)
                .map_err(|e| format!("snapshot_encoder_unavailable: {e}"))?;
            self.job = Some(Encoding {
                id,
                worker: Some(worker),
                result: None,
            });
            return Ok(None);
        }
        let job = self.job.as_mut().unwrap();
        if job
            .worker
            .as_ref()
            .is_some_and(|worker| worker.is_finished())
        {
            job.result = Some(
                job.worker
                    .take()
                    .unwrap()
                    .join()
                    .unwrap_or_else(|_| Err("snapshot_encoder_panicked".into())),
            );
        }
        job.result.clone().transpose()
    }
}
impl Drop for Encoder {
    fn drop(&mut self) {
        if let Some(worker) = self.job.as_mut().and_then(|job| job.worker.take()) {
            let _ = worker.join();
        }
    }
}
fn write_png(pixels: Arc<Pixels>) -> Encoded {
    let write = || -> Result<Arc<Image>, Box<dyn std::error::Error>> {
        let mut bytes = BoundedBytes::default();
        let mut encoder = png::Encoder::new(&mut bytes, pixels.width, pixels.height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder.write_header()?;
        writer.write_image_data(&pixels.rgba)?;
        writer.finish()?;
        let directory = std::env::temp_dir().join(format!("nico-snapshot-{}", std::process::id()));
        std::fs::create_dir_all(&directory)?;
        let path = directory.join("window.png");
        std::fs::write(&path, &bytes.0)?;
        let digest = format!("{:x}", Sha256::digest(&bytes.0));
        Ok(Arc::new(Image {
            path,
            bytes: bytes.0,
            digest,
            frame_id: pixels.frame_id,
        }))
    };
    write().map_err(|e| format!("snapshot_write_failed: {e}"))
}

fn read_chunk(encoder: &Encoder, id: u64, offset: usize, limit: usize) -> CallToolResult {
    let error = |code| CallToolResult::structured_error(json!({"error":{"code":code}}));
    if limit == 0 || limit > MAX_CHUNK {
        return error("invalid_arguments");
    }
    let Some(job) = encoder.job.as_ref().filter(|job| job.id == id) else {
        return error("unknown_or_expired_request");
    };
    let Some(Ok(image)) = &job.result else {
        return error("capture_not_ready");
    };
    if offset > image.bytes.len() {
        return error("invalid_offset");
    }
    let end = offset.saturating_add(limit).min(image.bytes.len());
    CallToolResult::structured(
        json!({"request_id":id,"process_session":nico_ops::identity::process_session_id(),"frame_id":image.frame_id,
        "encoding":"base64","sha256":image.digest,"total_bytes":image.bytes.len(),"offset":offset,"next_offset":end,"complete":end == image.bytes.len(),
        "data":base64::engine::general_purpose::STANDARD.encode(&image.bytes[offset..end])}),
    )
}

pub(crate) fn register(
    mut tools: ToolExtensions,
    control: HostControl,
) -> io::Result<ToolExtensions> {
    let encoder = Arc::new(Mutex::new(Encoder::default()));
    let reader = encoder.clone();
    let snapshots = control.snapshots();
    tools.register(Tool::new("window_snapshot_read", "Read immutable PNG bytes from the latest completed capture. Chunks are base64, at most 16 KiB decoded; follow next_offset. Every chunk carries process/frame identity and SHA-256. A newer capture expires old IDs. No filesystem path is accepted.", json!({"type":"object","required":["request_id"],"properties":{"request_id":{"type":"integer","minimum":1},"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":16384}},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
        let error = |code| CallToolResult::structured_error(json!({"error":{"code":code}}));
        if args.keys().any(|key| !matches!(key.as_str(), "request_id" | "offset" | "limit")) { return error("invalid_arguments"); }
        let Some(id) = args.get("request_id").and_then(|v| v.as_u64()).filter(|id| *id > 0) else { return error("invalid_arguments"); };
        let offset = args.get("offset").map_or(Some(0), |v| v.as_u64()).and_then(|v| usize::try_from(v).ok());
        let limit = args.get("limit").map_or(Some(MAX_CHUNK as u64), |v| v.as_u64()).and_then(|v| usize::try_from(v).ok());
        let Some((offset, limit)) = offset.zip(limit) else { return error("invalid_arguments"); };
        let encoder = reader.lock().unwrap();
        if snapshots.read(id).is_err() { return error("unknown_or_expired_request"); }
        read_chunk(&encoder, id, offset, limit)
    })?;
    tools.set_access("window_snapshot_read", nico_ops::mcp::ToolAccess::Capture)?;
    tools.register(Tool::new("window_snapshot",
        "Capture window content including HUD to a local PNG. No arguments queues one capture; poll with request_id. Only the latest request/result is retained. The returned local file is overwritten by the next completed capture. GPU readback is not desktop or display scanout capture.",
        json!({"type":"object","properties":{"request_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","properties":{"request_id":{"type":"integer"},"state":{"type":"string"},"path":{"type":"string"},"width":{"type":"integer"},"height":{"type":"integer"},"frame_id":{"type":"integer","minimum":0},"process_session":{"type":"string"},"sha256":{"type":"string"},"total_bytes":{"type":"integer"},"error":{"type":"string"}},"additionalProperties":false}).as_object().unwrap().clone().into()),
        move |args| {
            let error = |message: &str| CallToolResult::structured_error(json!({"error":message}));
            if args.keys().any(|k| k != "request_id") { return error("invalid_arguments"); }
            let snapshots = control.snapshots();
            let mut encoder = encoder.lock().unwrap();
            let Some(value) = args.get("request_id") else {
                let status = control.status();
                if !status.is_ready() || !status.active { return error("host_not_ready_or_inactive"); }
                if encoder.busy() { return error("snapshot_encoder_busy"); }
                return match snapshots.request() {
                    Ok(id) => CallToolResult::structured(json!({"request_id":id,"state":"pending"})),
                    Err(e) => error(e),
                };
            };
            let Some(id) = value.as_u64().filter(|id| *id > 0) else { return error("invalid_arguments"); };
            match snapshots.read(id) {
                Err(e) => error(e),
                Ok(SnapshotState::Pending) => CallToolResult::structured(json!({"request_id":id,"state":"pending"})),
                Ok(SnapshotState::Failed(e)) => CallToolResult::structured_error(json!({"request_id":id,"state":"failed","error":e})),
                Ok(SnapshotState::Ready(pixels)) => {
                    let (width, height) = (pixels.width, pixels.height);
                    match encoder.poll(id, move || write_png(pixels)) {
                        Ok(None) => CallToolResult::structured(json!({"request_id":id,"state":"pending"})),
                        Ok(Some(image)) => CallToolResult::structured(json!({"request_id":id,"state":"ready","path":image.path,"width":width,"height":height,"frame_id":image.frame_id,"process_session":nico_ops::identity::process_session_id(),"sha256":image.digest,"total_bytes":image.bytes.len()})),
                        Err(e) => CallToolResult::structured_error(json!({"request_id":id,"state":"failed","error":e})),
                    }
                }
            }
        })?;
    tools.set_access("window_snapshot", nico_ops::mcp::ToolAccess::Capture)?;
    Ok(tools)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        sync::mpsc,
        time::{Duration, Instant},
    };

    fn fixture(path: &str) -> Arc<Image> {
        Arc::new(Image {
            path: path.into(),
            bytes: vec![1, 2, 3, 4],
            digest: format!("{:x}", Sha256::digest([1, 2, 3, 4])),
            frame_id: 12,
        })
    }

    #[test]
    fn png_chunks_preserve_identity_and_reject_stale_ids_or_invalid_ranges() {
        let encoder = Encoder {
            job: Some(Encoding {
                id: 8,
                worker: None,
                result: Some(Ok(fixture("/nonexistent/host-path.png"))),
            }),
        };
        let first = read_chunk(&encoder, 8, 0, 2).structured_content.unwrap();
        let second = read_chunk(&encoder, 8, 2, 2).structured_content.unwrap();
        assert_eq!(first["frame_id"], 12);
        assert_eq!(
            first["process_session"],
            nico_ops::identity::process_session_id()
        );
        assert_eq!(first["sha256"], second["sha256"]);
        assert_eq!(first["complete"], false);
        assert_eq!(second["complete"], true);
        let mut bytes = base64::engine::general_purpose::STANDARD
            .decode(first["data"].as_str().unwrap())
            .unwrap();
        bytes.extend(
            base64::engine::general_purpose::STANDARD
                .decode(second["data"].as_str().unwrap())
                .unwrap(),
        );
        assert_eq!(bytes, vec![1, 2, 3, 4]);
        for (id, offset, limit) in [(7, 0, 2), (8, 5, 2), (8, 0, 0), (8, 0, MAX_CHUNK + 1)] {
            assert_eq!(read_chunk(&encoder, id, offset, limit).is_error, Some(true));
        }
    }

    #[test]
    fn oversized_png_output_is_rejected_before_growing_the_buffer() {
        use std::io::Write;
        let mut output = BoundedBytes(vec![0; MAX_PNG_BYTES - 1]);
        output.write_all(&[1]).unwrap();
        assert!(output.write_all(&[2]).is_err());
        assert_eq!(output.0.len(), MAX_PNG_BYTES);
    }

    #[test]
    fn blocked_encoding_keeps_polling_responsive_and_rejects_overlap() {
        let (release, wait) = mpsc::sync_channel(1);
        let (started, running) = mpsc::sync_channel(1);
        let mut encoder = Encoder::default();
        assert_eq!(
            encoder.poll(1, move || {
                started.send(()).unwrap();
                wait.recv_timeout(Duration::from_secs(5)).unwrap();
                Ok(fixture("capture.png"))
            }),
            Ok(None)
        );
        running.recv_timeout(Duration::from_secs(2)).unwrap();
        assert!(encoder.busy());
        for _ in 0..10 {
            assert_eq!(encoder.poll(1, || panic!("duplicate worker")), Ok(None));
        }
        assert_eq!(
            encoder.poll(2, || panic!("overlapping worker")),
            Err("snapshot_encoder_busy".into())
        );
        release.send(()).unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            let result = encoder.poll(1, || panic!("unexpected retry")).unwrap();
            if let Some(path) = result {
                assert_eq!(path.path, PathBuf::from("capture.png"));
                break;
            }
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
        assert_eq!(
            encoder.poll(1, || panic!("cached result lost")),
            Ok(Some(fixture("capture.png")))
        );
    }

    #[test]
    fn encoding_failure_is_retained_without_repeating_file_io() {
        let mut encoder = Encoder::default();
        encoder.poll(1, || Err("disk failure".into())).unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        while encoder.busy() {
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
        for _ in 0..2 {
            assert_eq!(
                encoder.poll(1, || panic!("failed write retried")),
                Err("disk failure".into())
            );
        }
    }

    #[test]
    fn shutdown_joins_active_encoding() {
        let (release, wait) = mpsc::sync_channel(1);
        let mut encoder = Encoder::default();
        encoder
            .poll(1, move || {
                wait.recv_timeout(Duration::from_secs(5)).unwrap();
                Ok(fixture(""))
            })
            .unwrap();
        let (finished, completed) = mpsc::sync_channel(1);
        let owner = thread::spawn(move || {
            drop(encoder);
            finished.send(()).unwrap();
        });
        assert!(completed.try_recv().is_err());
        release.send(()).unwrap();
        completed.recv_timeout(Duration::from_secs(2)).unwrap();
        owner.join().unwrap();
    }
}
