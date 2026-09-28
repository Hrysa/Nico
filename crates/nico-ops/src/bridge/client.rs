use std::{io, net::SocketAddr, thread, time::Duration};

use tokio::{io::BufReader, net::TcpStream, sync::watch};

use super::{
    check_address,
    wire::{self, GameRole, IO_TIMEOUT, Message},
};
use crate::{
    HostControl,
    mcp::{ToolExtensions, host_tool_catalog, invoke_host_tool},
};

/// Identity and API revision supplied by a game; tool schemas come from its registry.
pub struct GameRegistration {
    pub game: String,
    pub role: GameRole,
    pub api_version: String,
    pub access: super::DebugAccess,
    pub content_revision: Option<String>,
}

impl GameRegistration {
    pub fn new(game: impl Into<String>, role: GameRole, api_version: impl Into<String>) -> Self {
        Self {
            game: game.into(),
            role,
            api_version: api_version.into(),
            access: super::DebugAccess::default(),
            content_revision: None,
        }
    }
}

/// Engine-owned reconnecting adapter. Keep this guard until host shutdown finishes.
///
/// No connection is required for gameplay. Disconnects never request host stop;
/// only a received `stop` operation does. The guard retains a controller even if
/// its worker fails, preserving the existing core's last-controller stop policy.
/// Handlers use the same prompt, validated, owned-data contract as ToolExtensions.
pub struct BridgeClient {
    _control: HostControl,
    shutdown: watch::Sender<bool>,
    worker: Option<thread::JoinHandle<()>>,
}

impl BridgeClient {
    pub fn start(
        address: SocketAddr,
        registration: GameRegistration,
        control: HostControl,
        tools: ToolExtensions,
    ) -> io::Result<Self> {
        check_address(address)?;
        if !wire::identifier(&registration.game) || !wire::identifier(&registration.api_version) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "game and API version must be 1-48 ASCII letters, digits, underscores or hyphens",
            ));
        }
        let access = registration.access;
        let catalog = host_tool_catalog(&tools);
        if catalog.len() > wire::MAX_TOOLS
            || catalog.iter().any(|tool| !wire::identifier(&tool.name))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "bridge supports at most 64 tools with identifier names",
            ));
        }
        let hello = Message::Register {
            protocol: wire::VERSION,
            game: registration.game,
            role: registration.role,
            api_version: registration.api_version,
            pid: std::process::id(),
            identity: crate::identity::DebugIdentity::capture(registration.content_revision)?,
            tools: catalog,
            status: control.status(),
        };
        if serde_json::to_vec(&hello).map_err(io::Error::other)?.len() + 1 > wire::MAX_FRAME {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "game tool catalog too large",
            ));
        }
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        let (shutdown, mut closing) = watch::channel(false);
        let worker_control = control.clone();
        let worker = thread::Builder::new().name("nico-bridge-client".into()).spawn(move || {
            runtime.block_on(async move {
                loop {
                    if *closing.borrow() { break; }
                    let connection = tokio::select! {
                        _ = closing.changed() => break,
                        result = tokio::time::timeout(IO_TIMEOUT, TcpStream::connect(address)) => result.map_err(io::Error::other).and_then(|stream| stream),
                    };
                    let result = match connection {
                        Ok(stream) => connected(stream, hello.clone(), &worker_control, &tools, &access, closing.clone()).await,
                        Err(error) => Err(error),
                    };
                    if *closing.borrow() { break; }
                    if let Err(error) = result {
                        tracing::trace!(target: "nico::bridge", %address, %error, "bridge connection ended or unavailable; retrying");
                    }
                    tokio::select! {
                        _ = closing.changed() => break,
                        _ = tokio::time::sleep(Duration::from_secs(1)) => {}
                    }
                }
            });
        })?;
        Ok(Self {
            _control: control,
            shutdown,
            worker: Some(worker),
        })
    }
}

impl Drop for BridgeClient {
    fn drop(&mut self) {
        let _ = self.shutdown.send(true);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

async fn connected(
    stream: TcpStream,
    mut hello: Message,
    control: &HostControl,
    tools: &ToolExtensions,
    access: &super::DebugAccess,
    mut closing: watch::Receiver<bool>,
) -> io::Result<()> {
    let (reader, mut writer) = stream.into_split();
    let mut reader = BufReader::new(reader);
    if let Message::Register { status, .. } = &mut hello {
        *status = control.status();
    }
    wire::write_message(&mut writer, &hello).await?;
    let instance_id = match tokio::time::timeout(IO_TIMEOUT, wire::read_message(&mut reader))
        .await
        .map_err(io::Error::other)??
    {
        Message::Registered { instance_id } => {
            tools.notify_bridge_connected();
            instance_id
        }
        Message::Rejected { reason } => return Err(io::Error::other(reason)),
        _ => {
            return Err(io::Error::other(
                "expected bridge registration acknowledgement",
            ));
        }
    };
    let (mut incoming, reader_task) = wire::reader_task(reader);
    let result = async {
        let mut heartbeat = tokio::time::interval(Duration::from_millis(250));
        let mut last_seen = tokio::time::Instant::now();
        let mut last_call = 0;
        loop {
            if *closing.borrow() {
                wire::write_message(&mut writer, &Message::Snapshot { status: control.status() }).await?;
                return Ok(());
            }
            tokio::select! {
                _ = closing.changed() => {
                    wire::write_message(&mut writer, &Message::Snapshot { status: control.status() }).await?;
                    return Ok(());
                }
                _ = heartbeat.tick() => {
                    if last_seen.elapsed() > wire::STALE_TIMEOUT { return Err(io::Error::other("bridge heartbeat timeout")); }
                    wire::write_message(&mut writer, &Message::Snapshot { status: control.status() }).await?;
                }
                message = incoming.recv() => {
                    last_seen = tokio::time::Instant::now();
                    match message.ok_or_else(|| io::Error::other("bridge reader closed"))?? {
                        Message::Ping => {}
                        Message::Call { id, origin, name, arguments } => {
                            if id <= last_call { return Err(io::Error::other("duplicate or out-of-order call ID")); }
                            last_call = id;
                            let result = invoke_authorized(control, tools, access, &origin, (&instance_id, id), name, arguments);
                            wire::write_message(&mut writer, &Message::Reply { id, result }).await?;
                        }
                        _ => return Err(io::Error::other("unexpected bridge message")),
                    }
                }
            }
        }
    }.await;
    reader_task.abort();
    result
}

fn invoke_authorized(
    control: &HostControl,
    tools: &ToolExtensions,
    access: &super::DebugAccess,
    origin: &super::access::CallOrigin,
    call: (&str, u64),
    name: String,
    arguments: crate::mcp::Map<String, crate::mcp::Value>,
) -> crate::mcp::CallToolResult {
    let permissions = access.permissions(origin);
    let admitted = tools
        .access(&name)
        .is_some_and(|required| permissions.contains(&required));
    let session = match origin {
        super::access::CallOrigin::Local => "local_mcp",
        super::access::CallOrigin::Editor { session, .. } => session.as_str(),
    };
    if !admitted {
        tracing::info!(target: "nico::debug", session, connection_instance = call.0, call_id = call.1, tool = name, outcome = "rejected", "debug operation");
        return crate::mcp::CallToolResult::structured_error(
            serde_json::json!({"error":{"code":"access_denied","message":"host denied operation"}}),
        );
    }
    let mut request = rmcp::model::CallToolRequestParams::new(name.clone());
    request.arguments = Some(arguments);
    let mut result = invoke_host_tool(control, tools, request);
    if tools.access(&name) != Some(crate::mcp::ToolAccess::Inspect) {
        tracing::info!(target: "nico::debug", session, connection_instance = call.0, call_id = call.1, tool = name, tool_error = result.is_error == Some(true), outcome = "handler_returned", "debug operation; reply may only acknowledge queued work");
    }
    if name == "status" && matches!(origin, super::access::CallOrigin::Editor { .. }) {
        let required: std::collections::BTreeMap<_, _> = host_tool_catalog(tools)
            .iter()
            .map(|tool| (tool.name.to_string(), tools.access(&tool.name)))
            .collect();
        result.meta.get_or_insert_with(Default::default).insert("nico.debug".into(), serde_json::json!({"permissions":permissions,"required_access":required,"session_id":session}));
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn host_denies_stop_before_invoking_handler() {
        let (control, _endpoint) = crate::control_channel();
        let result = invoke_authorized(
            &control,
            &ToolExtensions::default(),
            &super::super::DebugAccess::for_build(false),
            &super::super::access::CallOrigin::Local,
            ("test-connection", 1),
            "stop".into(),
            Default::default(),
        );
        assert_eq!(result.is_error, Some(true));
        assert_eq!(control.status().state, crate::HostState::Starting);
        let result = invoke_authorized(
            &control,
            &ToolExtensions::default(),
            &super::super::DebugAccess::for_build(true),
            &super::super::access::CallOrigin::Local,
            ("test-connection", 1),
            "stop".into(),
            Default::default(),
        );
        assert_ne!(result.is_error, Some(true));
    }
}
