//! Client-only snapshot tool. Tooling queues requests; the host captures at rendering.
use nico_ops::snapshot::Pixels;
use nico_ops::{
    HostControl,
    mcp::{CallToolResult, Tool, ToolExtensions},
    snapshot::SnapshotState,
};
use serde_json::json;
use std::{
    io,
    path::PathBuf,
    sync::{Arc, Mutex},
    thread::{self, JoinHandle},
};

type Encoded = Result<PathBuf, String>;
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
    ) -> Result<Option<PathBuf>, String> {
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
    let write = || -> Result<PathBuf, Box<dyn std::error::Error>> {
        let directory = std::env::temp_dir().join(format!("nico-snapshot-{}", std::process::id()));
        std::fs::create_dir_all(&directory)?;
        let path = directory.join("window.png");
        let file = std::fs::File::create(&path)?;
        let mut encoder =
            png::Encoder::new(std::io::BufWriter::new(file), pixels.width, pixels.height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder.write_header()?;
        writer.write_image_data(&pixels.rgba)?;
        writer.finish()?;
        Ok(path)
    };
    write().map_err(|e| format!("snapshot_write_failed: {e}"))
}

pub(crate) fn register(
    mut tools: ToolExtensions,
    control: HostControl,
) -> io::Result<ToolExtensions> {
    let encoder = Mutex::new(Encoder::default());
    tools.register(Tool::new("window_snapshot",
        "Capture window content including HUD to a local PNG. No arguments queues one capture; poll with request_id. Only the latest request/result is retained. The returned local file is overwritten by the next completed capture. GPU readback is not desktop or display scanout capture.",
        json!({"type":"object","properties":{"request_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","properties":{"request_id":{"type":"integer"},"state":{"type":"string"},"path":{"type":"string"},"width":{"type":"integer"},"height":{"type":"integer"},"error":{"type":"string"}},"additionalProperties":false}).as_object().unwrap().clone().into()),
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
                        Ok(Some(path)) => CallToolResult::structured(json!({"request_id":id,"state":"ready","path":path,"width":width,"height":height})),
                        Err(e) => CallToolResult::structured_error(json!({"request_id":id,"state":"failed","error":e})),
                    }
                }
            }
        })?;
    Ok(tools)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        sync::mpsc,
        time::{Duration, Instant},
    };

    #[test]
    fn blocked_encoding_keeps_polling_responsive_and_rejects_overlap() {
        let (release, wait) = mpsc::sync_channel(1);
        let (started, running) = mpsc::sync_channel(1);
        let mut encoder = Encoder::default();
        assert_eq!(
            encoder.poll(1, move || {
                started.send(()).unwrap();
                wait.recv_timeout(Duration::from_secs(5)).unwrap();
                Ok(PathBuf::from("capture.png"))
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
                assert_eq!(path, PathBuf::from("capture.png"));
                break;
            }
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
        assert_eq!(
            encoder.poll(1, || panic!("cached result lost")),
            Ok(Some(PathBuf::from("capture.png")))
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
                Ok(PathBuf::new())
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
