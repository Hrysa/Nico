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
}

impl GameRegistration {
    pub fn new(game: impl Into<String>, role: GameRole, api_version: impl Into<String>) -> Self {
        Self {
            game: game.into(),
            role,
            api_version: api_version.into(),
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
                        Ok(stream) => connected(stream, hello.clone(), &worker_control, &tools, closing.clone()).await,
                        Err(error) => Err(error),
                    };
                    if *closing.borrow() { break; }
                    if let Err(error) = result { eprintln!("Nico bridge disconnected: {error}; retrying"); }
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
    mut closing: watch::Receiver<bool>,
) -> io::Result<()> {
    let (reader, mut writer) = stream.into_split();
    let mut reader = BufReader::new(reader);
    if let Message::Register { status, .. } = &mut hello {
        *status = control.status();
    }
    wire::write_message(&mut writer, &hello).await?;
    match tokio::time::timeout(IO_TIMEOUT, wire::read_message(&mut reader))
        .await
        .map_err(io::Error::other)??
    {
        Message::Registered { .. } => {}
        Message::Rejected { reason } => return Err(io::Error::other(reason)),
        _ => {
            return Err(io::Error::other(
                "expected bridge registration acknowledgement",
            ));
        }
    }
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
                        Message::Call { id, name, arguments } => {
                            if id <= last_call { return Err(io::Error::other("duplicate or out-of-order call ID")); }
                            last_call = id;
                            let mut request = rmcp::model::CallToolRequestParams::new(name);
                            request.arguments = Some(arguments);
                            let result = invoke_host_tool(control, tools, request);
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
