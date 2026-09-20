//! Joined editor-side worker. UI and automation enqueue the same bounded operations.

use super::{EditorConnection, EditorRequest, access::credential_valid};
use crate::mcp::{CallToolResult, Map, Tool, ToolAccess, ToolExtensions, Value};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::{
    collections::VecDeque,
    io::{self, Read},
    net::SocketAddr,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::Instant,
};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "action", rename_all = "snake_case", deny_unknown_fields)]
pub enum EditorAction {
    Connect {
        address: SocketAddr,
        token_file: PathBuf,
    },
    Discover,
    Attach {
        instance_id: String,
        api_version: String,
        credential_file: PathBuf,
    },
    Catalog,
    Capture {
        destination: PathBuf,
    },
    Call {
        tool_name: String,
        arguments: Map<String, Value>,
    },
    Detach,
    Disconnect,
}

struct State {
    connected: bool,
    attached: Option<String>,
    next_id: u64,
    pending: usize,
    history: VecDeque<Value>,
    changed: Instant,
    evicted: u64,
    cancelled: usize,
}

impl State {
    fn record(&mut self, outcome: Value) {
        while self.history.len() >= 32
            || self
                .history
                .iter()
                .map(|item| item.to_string().len())
                .sum::<usize>()
                + outcome.to_string().len()
                > 1024 * 1024
        {
            if self.history.pop_front().is_none() {
                break;
            }
            self.evicted += 1;
        }
        self.history.push_back(outcome);
        self.changed = Instant::now();
    }
}

struct Inner {
    sender: Mutex<Option<mpsc::SyncSender<(u64, EditorAction)>>>,
    state: Arc<Mutex<State>>,
    closing: Arc<AtomicBool>,
    worker: Mutex<Option<thread::JoinHandle<()>>>,
}

impl Inner {
    fn shutdown(&self) {
        self.closing.store(true, Ordering::Release);
        self.sender.lock().unwrap().take();
        if let Some(worker) = self.worker.lock().unwrap().take() {
            let _ = worker.join();
        }
    }
}
impl Drop for Inner {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// Clones share one worker and bounded owned state. Dropping the last handle joins
/// the worker. Shutdown discards not-yet-started requests and never stops a game.
#[derive(Clone)]
pub struct EditorSession(Arc<Inner>);

impl EditorSession {
    pub fn start() -> io::Result<Self> {
        let (sender, receiver) = mpsc::sync_channel::<(u64, EditorAction)>(8);
        let state = Arc::new(Mutex::new(State {
            connected: false,
            attached: None,
            next_id: 0,
            pending: 0,
            history: VecDeque::new(),
            changed: Instant::now(),
            evicted: 0,
            cancelled: 0,
        }));
        let closing = Arc::new(AtomicBool::new(false));
        let worker_state = state.clone();
        let worker_closing = closing.clone();
        let worker = thread::Builder::new().name("nico-editor-rpc".into()).spawn(move || {
            let mut connection = None;
            while let Ok((id, action)) = receiver.recv() {
                if worker_closing.load(Ordering::Acquire) { break; }
                let target = match &action {
                    EditorAction::Attach { instance_id, .. } => Some(instance_id.clone()),
                    EditorAction::Connect { .. } | EditorAction::Discover | EditorAction::Disconnect => None,
                    _ => worker_state.lock().unwrap().attached.clone(),
                };
                let result = execute(&mut connection, &action, &worker_closing);
                let mut state = worker_state.lock().unwrap();
                state.pending = state.pending.saturating_sub(1);
                state.connected = connection.is_some();
                match &action {
                    EditorAction::Connect { .. } | EditorAction::Attach { .. } | EditorAction::Detach | EditorAction::Disconnect => state.attached = None,
                    _ => {}
                }
                let outcome = match result {
                    Ok(result) => {
                        if result.is_error != Some(true) && let EditorAction::Attach { instance_id, .. } = action { state.attached = Some(instance_id); }
                        json!({"command_id":id,"instance_id":target,"outcome":"reply","result":result})
                    }
                    Err(error) if matches!(action, EditorAction::Capture { .. }) => {
                        state.attached = None;
                        json!({"command_id":id,"instance_id":target,"outcome":"capture_error","message":error.to_string()})
                    }
                    Err(_) => {
                        // Request text, filesystem errors, and credentials never enter publication.
                        state.attached = None;
                        json!({"command_id":id,"instance_id":target,"outcome":"transport_error","message":"connection or credential unavailable; a sent call may have executed; do not retry automatically"})
                    }
                };
                state.record(outcome);
            }
            drop(connection);
            let mut state = worker_state.lock().unwrap();
            state.connected = false;
            state.attached = None;
            // IDs are contiguous and executed FIFO; remaining accepted IDs form
            // a suffix. Every not-started request receives a terminal outcome.
            if state.pending > 0 {
                for id in (state.next_id - state.pending as u64 + 1)..=state.next_id {
                    state.record(json!({"command_id":id,"outcome":"cancelled","message":"editor closed before command started"}));
                }
            }
            state.cancelled += state.pending;
            state.pending = 0;
            state.changed = Instant::now();
        })?;
        Ok(Self(Arc::new(Inner {
            sender: Mutex::new(Some(sender)),
            state,
            closing,
            worker: Mutex::new(Some(worker)),
        })))
    }

    pub fn submit(&self, action: EditorAction) -> io::Result<u64> {
        // Bound the queue by bytes as well as count before retaining arbitrary arguments.
        if serde_json::to_vec(&action)
            .map_or(true, |bytes| bytes.len() > super::wire::MAX_FRAME / 2)
        {
            return Err(io::Error::other("editor command too large"));
        }
        let sender = self.0.sender.lock().unwrap();
        let sender = sender
            .as_ref()
            .ok_or_else(|| io::Error::other("editor worker closed"))?;
        let mut state = self.0.state.lock().unwrap();
        let id = state
            .next_id
            .checked_add(1)
            .ok_or_else(|| io::Error::other("editor command IDs exhausted"))?;
        sender
            .try_send((id, action))
            .map_err(|_| io::Error::other("editor queue full or closed"))?;
        state.next_id = id;
        state.pending += 1;
        Ok(id)
    }

    pub fn snapshot(&self) -> Value {
        let state = self.0.state.lock().unwrap();
        let outcomes: Vec<_> = state.history.iter().map(|outcome| {
            let size = outcome.to_string().len();
            if size > 2048 { json!({"command_id":outcome["command_id"],"outcome":outcome["outcome"],"instance_id":outcome["instance_id"],"result_truncated":true,"result_bytes":size}) } else { outcome.clone() }
        }).collect();
        json!({"connected":state.connected,"attached_instance":state.attached,"pending":state.pending,
            "closed":self.0.closing.load(Ordering::Acquire),"last_command_id":state.next_id,
            "snapshot_age_ms":state.changed.elapsed().as_millis() as u64,"outcomes":outcomes,"evicted_outcomes":state.evicted,"cancelled_on_shutdown":state.cancelled})
    }

    /// Page a retained reply as UTF-8 JSON text. Offsets are byte positions; only
    /// returned next_offset values are valid cursors. Evicted results stay unknown.
    pub fn result_page(&self, id: u64, offset: usize, limit: usize) -> io::Result<Value> {
        if !(1..=16384).contains(&limit) {
            return Err(io::Error::other("invalid result page limit"));
        }
        let state = self.0.state.lock().unwrap();
        let outcome = state
            .history
            .iter()
            .find(|item| item["command_id"].as_u64() == Some(id))
            .ok_or_else(|| io::Error::other("outcome_unknown"))?;
        let text = outcome.to_string();
        if offset > text.len() || !text.is_char_boundary(offset) {
            return Err(io::Error::other("invalid result offset"));
        }
        let mut end = offset.saturating_add(limit).min(text.len());
        while !text.is_char_boundary(end) {
            end -= 1;
        }
        if end == offset && offset < text.len() {
            return Err(io::Error::other("page limit too small for UTF-8 character"));
        }
        Ok(
            json!({"command_id":id,"offset":offset,"next_offset":end,"total_bytes":text.len(),"complete":end == text.len(),"json_fragment":&text[offset..end]}),
        )
    }

    pub fn shutdown(&self) {
        self.0.shutdown();
    }

    pub fn register_tools(&self, tools: &mut ToolExtensions) -> io::Result<()> {
        let reader = self.clone();
        tools.register(Tool::new("editor_debug_state", "Read editor RPC connectivity, selected instance, queue count and the last 32 replies. A reply may only acknowledge a game command; it does not prove application. No automatic retry.", json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
            if !args.is_empty() { return CallToolResult::structured_error(json!({"error":"no arguments accepted"})); }
            CallToolResult::structured(reader.snapshot())
        })?;
        tools.set_access("editor_debug_state", ToolAccess::Inspect)?;
        let reader = self.clone();
        tools.register(Tool::new("editor_debug_result", "Read a retained reply in bounded UTF-8 JSON fragments. Follow next_offset until complete. Missing/evicted results remain unknown; never replay a mutation to recover them.", json!({"type":"object","required":["command_id"],"properties":{"command_id":{"type":"integer","minimum":1},"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":16384}},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
            if args.keys().any(|key| !matches!(key.as_str(), "command_id" | "offset" | "limit")) { return CallToolResult::structured_error(json!({"error":"invalid_arguments"})); }
            let Some(id) = args.get("command_id").and_then(Value::as_u64) else { return CallToolResult::structured_error(json!({"error":"invalid_arguments"})); };
            let offset = args.get("offset").map_or(Some(0), Value::as_u64).and_then(|value| usize::try_from(value).ok());
            let limit = args.get("limit").map_or(Some(4096), Value::as_u64).and_then(|value| usize::try_from(value).ok());
            match offset.zip(limit).ok_or_else(|| io::Error::other("invalid_arguments")).and_then(|(offset,limit)| reader.result_page(id,offset,limit)) {
                Ok(value) => CallToolResult::structured(value),
                Err(error) => CallToolResult::structured_error(json!({"error":error.to_string()})),
            }
        })?;
        tools.set_access("editor_debug_result", ToolAccess::Inspect)?;
        let writer = self.clone();
        tools.register(Tool::new("editor_debug_command", "Queue editor attach/inspection operations. Connect and attach read credentials from private local files; no secrets are returned. Detach/disconnect do not stop hosts. Poll editor_debug_state by command_id.", json!({"type":"object","oneOf":[
            {"properties":{"action":{"const":"connect"},"address":{"type":"string"},"token_file":{"type":"string"}},"required":["action","address","token_file"],"additionalProperties":false},
            {"properties":{"action":{"const":"attach"},"instance_id":{"type":"string"},"api_version":{"type":"string"},"credential_file":{"type":"string"}},"required":["action","instance_id","api_version","credential_file"],"additionalProperties":false},
            {"properties":{"action":{"enum":["discover","catalog","detach","disconnect"]}},"required":["action"],"additionalProperties":false},
            {"properties":{"action":{"const":"capture"},"destination":{"type":"string"}},"required":["action","destination"],"additionalProperties":false},
            {"properties":{"action":{"const":"call"},"tool_name":{"type":"string"},"arguments":{"type":"object"}},"required":["action","tool_name","arguments"],"additionalProperties":false}
        ]}).as_object().unwrap().clone()), move |args| {
            let Ok(action) = serde_json::from_value(Value::Object(args)) else { return CallToolResult::structured_error(json!({"error":"invalid editor command"})); };
            match writer.submit(action) {
                Ok(id) => CallToolResult::structured(json!({"accepted":true,"command_id":id})),
                Err(_) => CallToolResult::structured_error(json!({"error":"editor queue closed, full, or request too large"}))
            }
        })
    }
}

fn read_token(path: &PathBuf) -> io::Result<String> {
    let mut token = String::new();
    std::fs::File::open(path)?
        .take(130)
        .read_to_string(&mut token)?;
    let token = token.trim().to_owned();
    if !credential_valid(&token) {
        return Err(io::Error::other("invalid credential file"));
    }
    Ok(token)
}

fn execute(
    connection: &mut Option<EditorConnection>,
    action: &EditorAction,
    closing: &AtomicBool,
) -> io::Result<CallToolResult> {
    let result = execute_inner(connection, action, closing);
    if result.is_err()
        || result
            .as_ref()
            .ok()
            .and_then(|result| result.structured_content.as_ref())
            .is_some_and(|value| value["error"]["code"] == "access_revoked")
    {
        *connection = None;
    }
    result
}

fn execute_inner(
    connection: &mut Option<EditorConnection>,
    action: &EditorAction,
    closing: &AtomicBool,
) -> io::Result<CallToolResult> {
    let request = match action {
        EditorAction::Connect {
            address,
            token_file,
        } => {
            *connection = None;
            *connection = Some(EditorConnection::connect(
                *address,
                read_token(token_file)?,
            )?);
            return Ok(CallToolResult::structured(json!({"connected":true})));
        }
        EditorAction::Disconnect => {
            *connection = None;
            return Ok(CallToolResult::structured(
                json!({"connected":false,"stopped":false}),
            ));
        }
        EditorAction::Discover => EditorRequest::List,
        EditorAction::Attach {
            instance_id,
            api_version,
            credential_file,
        } => EditorRequest::Attach {
            instance_id: instance_id.clone(),
            api_version: api_version.clone(),
            credential: read_token(credential_file)?,
        },
        EditorAction::Capture { destination } => {
            let connection = connection
                .as_mut()
                .ok_or_else(|| io::Error::other("not connected"))?;
            return super::editor_capture::download(
                destination,
                |name, arguments| {
                    connection.request(&EditorRequest::Call {
                        tool_name: name.into(),
                        arguments,
                    })
                },
                || closing.load(Ordering::Acquire),
            );
        }
        EditorAction::Catalog => EditorRequest::Catalog,
        EditorAction::Detach => EditorRequest::Detach,
        EditorAction::Call {
            tool_name,
            arguments,
        } => EditorRequest::Call {
            tool_name: tool_name.clone(),
            arguments: arguments.clone(),
        },
    };
    let result = connection
        .as_mut()
        .ok_or_else(|| io::Error::other("not connected"))?
        .request(&request);
    if result.is_err() {
        *connection = None;
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn wait(session: &EditorSession, id: u64) {
        let deadline = Instant::now() + std::time::Duration::from_secs(2);
        while session.result_page(id, 0, 4096).is_err() {
            assert!(Instant::now() < deadline, "worker did not finish command");
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
    }

    #[test]
    fn shutdown_records_a_terminal_outcome_for_every_accepted_command() {
        let session = EditorSession::start().unwrap();
        let ids: Vec<_> = (0..8)
            .map(|_| session.submit(EditorAction::Discover).unwrap())
            .collect();
        session.shutdown();
        for id in ids {
            let page = session.result_page(id, 0, 4096).unwrap();
            let result: Value =
                serde_json::from_str(page["json_fragment"].as_str().unwrap()).unwrap();
            assert!(matches!(
                result["outcome"].as_str(),
                Some("transport_error" | "cancelled")
            ));
        }
        assert_eq!(session.snapshot()["pending"], 0);
    }

    #[test]
    fn worker_reports_failures_evicts_results_and_joins_without_stopping_a_host() {
        let session = EditorSession::start().unwrap();
        let id = session
            .submit(EditorAction::Attach {
                instance_id: "missing".into(),
                api_version: "1".into(),
                credential_file: PathBuf::from("/missing/private-credential"),
            })
            .unwrap();
        wait(&session, id);
        let snapshot = session.snapshot();
        assert_eq!(snapshot["connected"], false);
        assert_eq!(snapshot["outcomes"][0]["outcome"], "transport_error");
        assert!(!snapshot.to_string().contains("private-credential"));
        for _ in 0..34 {
            let id = session.submit(EditorAction::Disconnect).unwrap();
            wait(&session, id);
        }
        assert!(session.result_page(1, 0, 4096).is_err());
        assert_eq!(session.snapshot()["evicted_outcomes"], 3);
        assert!(
            session
                .submit(EditorAction::Call {
                    tool_name: "oversized".into(),
                    arguments: json!({"data":"x".repeat(super::super::wire::MAX_FRAME)})
                        .as_object()
                        .unwrap()
                        .clone()
                })
                .is_err()
        );
        session.shutdown();
        assert!(session.submit(EditorAction::Discover).is_err());
        assert_eq!(session.snapshot()["closed"], true);
    }

    #[test]
    fn large_result_publication_is_bounded_and_pages_preserve_utf8() {
        let session = EditorSession::start().unwrap();
        let expected = json!({"command_id":1,"outcome":"reply","text":"世界".repeat(6000)});
        session
            .0
            .state
            .lock()
            .unwrap()
            .history
            .push_back(expected.clone());
        assert!(session.snapshot().to_string().len() < 4096);
        assert_eq!(session.snapshot()["outcomes"][0]["result_truncated"], true);
        let mut offset = 0;
        let mut text = String::new();
        loop {
            let page = session.result_page(1, offset, 101).unwrap();
            text.push_str(page["json_fragment"].as_str().unwrap());
            offset = page["next_offset"].as_u64().unwrap() as usize;
            if page["complete"] == true {
                break;
            }
        }
        assert_eq!(serde_json::from_str::<Value>(&text).unwrap(), expected);
        assert!(session.result_page(1, offset + 1, 100).is_err());
        assert!(session.result_page(1, 0, 16385).is_err());
        assert!(session.result_page(2, 0, 100).is_err());
    }
}
