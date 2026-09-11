use std::{
    collections::BTreeMap,
    error::Error,
    io,
    net::SocketAddr,
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};

use rmcp::{
    ErrorData, RoleServer, ServerHandler, ServiceExt,
    model::{
        CallToolRequestParams, CallToolResponse, Implementation, ListToolsResult,
        PaginatedRequestParams, ServerCapabilities, ServerInfo,
    },
    service::{NotificationContext, RequestContext},
};
use serde_json::json;
use tokio::{
    io::BufReader,
    net::{TcpListener, TcpStream},
    sync::{Semaphore, mpsc, oneshot, watch},
    time::Instant,
};

use super::{
    check_address,
    wire::{self, GameRole, Message},
};
use crate::{
    HostStatus,
    mcp::{CallToolResult, Map, Tool, Value},
};

const MAX_CONNECTIONS: usize = 32;
const MAX_CATALOGS: usize = 32;
const MAX_INSTANCES: usize = 128;
type CatalogKey = (String, GameRole);

struct Catalog {
    version: String,
    tools: Vec<Tool>,
}
struct Command {
    name: String,
    arguments: Map<String, Value>,
    reply: oneshot::Sender<CallToolResult>,
}
struct Instance {
    key: CatalogKey,
    version: String,
    pid: u32,
    status: HostStatus,
    last_seen: Instant,
    sender: Option<mpsc::Sender<Command>>,
    disconnect_reason: Option<String>,
}
struct Registry {
    session: String,
    next_id: u64,
    catalogs: BTreeMap<CatalogKey, Catalog>,
    instances: BTreeMap<String, Instance>,
}

impl Registry {
    fn new() -> Self {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        Self {
            session: format!("{}-{nonce:x}", std::process::id()),
            next_id: 0,
            catalogs: BTreeMap::new(),
            instances: BTreeMap::new(),
        }
    }

    fn register(&mut self, hello: Message, sender: mpsc::Sender<Command>) -> io::Result<String> {
        let Message::Register {
            protocol,
            game,
            role,
            api_version,
            pid,
            tools,
            status,
        } = hello
        else {
            return Err(io::Error::other("first message must register a game"));
        };
        if protocol != wire::VERSION {
            return Err(io::Error::other("unsupported protocol version"));
        }
        if !wire::identifier(&game)
            || !wire::identifier(&api_version)
            || tools.len() > wire::MAX_TOOLS
        {
            return Err(io::Error::other(
                "invalid registration identity or tool count",
            ));
        }
        let mut names = std::collections::BTreeSet::new();
        for tool in &tools {
            if !wire::identifier(&tool.name) || !names.insert(tool.name.to_string()) {
                return Err(io::Error::other("invalid or duplicate tool name"));
            }
        }
        let key = (game, role);
        if !self.catalogs.contains_key(&key) && self.catalogs.len() >= MAX_CATALOGS {
            return Err(io::Error::other("bridge catalog capacity reached"));
        }
        if self
            .instances
            .values()
            .any(|instance| instance.key == key && instance.sender.is_some())
        {
            let existing = &self.catalogs[&key];
            if existing.version != api_version || existing.tools != tools {
                return Err(io::Error::other(
                    "API conflicts with a connected instance of the same game and role",
                ));
            }
        }
        if self.instances.len() >= MAX_INSTANCES {
            let oldest = self
                .instances
                .iter()
                .filter(|(_, instance)| instance.sender.is_none())
                .min_by_key(|(_, instance)| instance.last_seen)
                .map(|(id, _)| id.clone());
            if let Some(id) = oldest {
                self.instances.remove(&id);
            } else {
                return Err(io::Error::other("bridge instance capacity reached"));
            }
        }
        self.next_id += 1;
        let id = format!("{}-{}", self.session, self.next_id);
        self.catalogs.insert(
            key.clone(),
            Catalog {
                version: api_version.clone(),
                tools,
            },
        );
        self.instances.insert(
            id.clone(),
            Instance {
                key,
                version: api_version,
                pid,
                status,
                last_seen: Instant::now(),
                sender: Some(sender),
                disconnect_reason: None,
            },
        );
        Ok(id)
    }

    fn snapshot(&self, id: &str) -> Option<Value> {
        self.instances.get(id).map(|instance| json!({
            "instance_id": id, "game": instance.key.0, "role": instance.key.1,
            "api_version": instance.version, "pid": instance.pid,
            "connected": instance.sender.is_some(), "last_seen_ms": instance.last_seen.elapsed().as_millis() as u64,
            "host": instance.status, "ready": instance.sender.is_some() && instance.status.is_ready(),
            "disconnect_reason": instance.disconnect_reason,
        }))
    }

    fn dynamic_tools(&self) -> Vec<Tool> {
        self.catalogs.iter().flat_map(|((game, role), catalog)| catalog.tools.iter().map(move |tool| {
            let mut exposed = tool.clone();
            exposed.name = tool_name(game, *role, &tool.name).into();
            exposed.description = Some(format!("{} [{} {} API {}]. Requires a connected instance. Game arguments use the advertised schema; list_game_tools returns the original definition.", tool.description.as_deref().unwrap_or("Game operation"), game, role.name(), catalog.version).into());
            let mut arguments = (*tool.input_schema).clone();
            // Preserve document-local references when nesting an original root schema.
            arguments.entry("$id").or_insert(json!(format!("urn:nico:{game}:{}:{}", role.name(), tool.name)));
            exposed.input_schema = Arc::new(json!({"type":"object", "required":["instance_id","arguments"], "additionalProperties":false,
                "properties":{"instance_id":{"type":"string"}, "arguments":arguments}}).as_object().unwrap().clone());
            exposed
        })).collect()
    }

    fn route(
        &self,
        id: &str,
        name: &str,
        expected: Option<&CatalogKey>,
    ) -> Result<mpsc::Sender<Command>, CallToolResult> {
        let instance = self
            .instances
            .get(id)
            .ok_or_else(|| failure("instance_unavailable", "unknown or expired instance ID"))?;
        if expected.is_some_and(|key| *key != instance.key) {
            return Err(failure(
                "wrong_instance",
                "tool belongs to another game or role",
            ));
        }
        if !self.catalogs[&instance.key]
            .tools
            .iter()
            .any(|tool| tool.name == name)
        {
            return Err(failure("unknown_tool", "tool is not registered"));
        }
        instance
            .sender
            .clone()
            .ok_or_else(|| failure("instance_unavailable", "game is disconnected"))
    }
}

fn tool_name(game: &str, role: GameRole, name: &str) -> String {
    format!("{game}.{}.{name}", role.name())
}
fn failure(code: &str, message: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code,"message":message}}))
}

#[derive(Clone)]
struct Bridge {
    registry: Arc<Mutex<Registry>>,
    revision: watch::Sender<u64>,
}

impl Bridge {
    fn changed(&self) {
        self.revision
            .send_modify(|revision| *revision = revision.wrapping_add(1));
    }

    async fn invoke(&self, request: CallToolRequestParams) -> CallToolResult {
        let args = request.arguments.unwrap_or_default();
        let allowed: &[&str] = match request.name.as_ref() {
            "bridge_status" | "list_instances" | "list_game_tools" => &[],
            "instance_status" => &["instance_id"],
            "call_game_tool" => &["instance_id", "tool_name", "arguments"],
            _ => &["instance_id", "arguments"],
        };
        if args.keys().any(|key| !allowed.contains(&key.as_str())) {
            return failure("invalid_arguments", "unexpected argument");
        }
        if allowed.contains(&"instance_id")
            && args.get("instance_id").and_then(Value::as_str).is_none()
        {
            return failure("invalid_arguments", "instance_id string required");
        }
        match request.name.as_ref() {
            "bridge_status" => {
                let registry = self.registry.lock().unwrap();
                return CallToolResult::structured(
                    json!({"connected_instances":registry.instances.values().filter(|instance| instance.sender.is_some()).count(),
                    "cached_catalogs":registry.catalogs.len(), "retained_instances":registry.instances.len(),
                    "cache_lifetime":"bridge_process", "launches_games":false}),
                );
            }
            "list_instances" => {
                let registry = self.registry.lock().unwrap();
                return CallToolResult::structured(
                    json!({"instances":registry.instances.keys().filter_map(|id| registry.snapshot(id)).collect::<Vec<_>>()}),
                );
            }
            "instance_status" => {
                let registry = self.registry.lock().unwrap();
                return registry
                    .snapshot(args["instance_id"].as_str().unwrap())
                    .map(CallToolResult::structured)
                    .unwrap_or_else(|| {
                        failure("instance_unavailable", "unknown or expired instance ID")
                    });
            }
            "list_game_tools" => {
                let registry = self.registry.lock().unwrap();
                return CallToolResult::structured(
                    json!({"catalogs":registry.catalogs.iter().map(|((game,role), catalog)| json!({
                    "game":game,"role":role,"api_version":catalog.version,"tools":catalog.tools,
                    "connected_instances":registry.instances.iter().filter(|(_, instance)| instance.key == (game.clone(), *role) && instance.sender.is_some()).map(|(id,_)|id).collect::<Vec<_>>()
                })).collect::<Vec<_>>()}),
                );
            }
            _ => {}
        }
        let Some(arguments) = args.get("arguments").and_then(Value::as_object).cloned() else {
            return failure("invalid_arguments", "arguments object required");
        };
        if serde_json::to_vec(&arguments).map_or(true, |bytes| bytes.len() > wire::MAX_FRAME / 2) {
            return failure("invalid_arguments", "arguments too large");
        }
        let id = args["instance_id"].as_str().unwrap();
        let routed = {
            let registry = self.registry.lock().unwrap();
            if request.name == "call_game_tool" {
                let Some(name) = args.get("tool_name").and_then(Value::as_str) else {
                    return failure("invalid_arguments", "tool_name string required");
                };
                registry
                    .route(id, name, None)
                    .map(|sender| (sender, name.to_owned()))
            } else {
                let entry = registry.catalogs.iter().find_map(|(key, catalog)| {
                    catalog
                        .tools
                        .iter()
                        .find(|tool| tool_name(&key.0, key.1, &tool.name) == request.name)
                        .map(|tool| (key, tool.name.to_string()))
                });
                match entry {
                    Some((key, name)) => registry
                        .route(id, &name, Some(key))
                        .map(|sender| (sender, name)),
                    None => Err(failure(
                        "unknown_tool",
                        "tool is not advertised by the bridge",
                    )),
                }
            }
        };
        let (sender, name) = match routed {
            Ok(route) => route,
            Err(error) => return error,
        };
        let (reply, response) = oneshot::channel();
        if let Err(error) = sender.try_send(Command {
            name,
            arguments,
            reply,
        }) {
            return match error {
                mpsc::error::TrySendError::Full(_) => {
                    failure("overloaded", "game command queue is full")
                }
                mpsc::error::TrySendError::Closed(_) => {
                    failure("instance_unavailable", "game disconnected")
                }
            };
        }
        match tokio::time::timeout(wire::CALL_TIMEOUT, response).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => failure(
                "connection_lost",
                "game disconnected; execution outcome may be unknown",
            ),
            Err(_) => failure(
                "timeout",
                "call timed out; it may already have executed; do not blindly retry mutations",
            ),
        }
    }
}

impl ServerHandler for Bridge {
    fn get_info(&self) -> ServerInfo {
        ServerInfo::new(ServerCapabilities::builder().enable_tools().enable_tool_list_changed().build())
            .with_server_info(Implementation::new("nico-bridge", env!("CARGO_PKG_VERSION")))
            .with_instructions("Games start independently. First list_instances; use connected instance IDs. Cached tools may be offline. list_game_tools returns original game schemas; call_game_tool invokes them even if dynamic tool refresh is unavailable. A timeout or disconnect may leave execution outcome unknown. The bridge never starts or kills processes.")
    }

    async fn on_initialized(&self, context: NotificationContext<RoleServer>) {
        let mut changes = self.revision.subscribe();
        tokio::spawn(async move {
            while changes.changed().await.is_ok() {
                if context.peer.notify_tool_list_changed().await.is_err() {
                    break;
                }
            }
        });
    }

    async fn list_tools(
        &self,
        request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        if request.is_some_and(|request| request.cursor.is_some()) {
            return Err(ErrorData::invalid_params(
                "pagination cursor is not supported",
                None,
            ));
        }
        let mut tools = management_tools();
        tools.extend(self.registry.lock().unwrap().dynamic_tools());
        Ok(ListToolsResult::with_all_items(tools))
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        Ok(self.invoke(request).await.into())
    }
}

fn management_tools() -> Vec<Tool> {
    [
        ("bridge_status", "Read bridge connectivity and schema-cache counts. Never launches games.", json!({}), json!([])),
        ("list_instances", "List connected and retained disconnected game instances with reported host state and snapshot age.", json!({}), json!([])),
        ("instance_status", "Read cached status for one instance. connected and ready are distinct; this does not prove process exit or window activity.", json!({"instance_id":{"type":"string"}}), json!(["instance_id"])),
        ("list_game_tools", "Discover cached game API descriptions and original input/output schemas, including offline games.", json!({}), json!([])),
        ("call_game_tool", "Invoke a registered game tool by instance ID, original tool_name and arguments. Discover schemas with list_game_tools first. Mutations may execute even if the call times out.", json!({"instance_id":{"type":"string"},"tool_name":{"type":"string"},"arguments":{"type":"object"}}), json!(["instance_id","tool_name","arguments"])),
    ].into_iter().map(|(name, description, properties, required)| {
        Tool::new(name, description, json!({"type":"object","properties":properties,"required":required,"additionalProperties":false}).as_object().unwrap().clone())
    }).collect()
}

async fn serve_game(stream: TcpStream, bridge: Bridge) -> io::Result<()> {
    let (reader, mut writer) = stream.into_split();
    let mut reader = BufReader::new(reader);
    let hello = tokio::time::timeout(wire::IO_TIMEOUT, wire::read_message(&mut reader))
        .await
        .map_err(io::Error::other)??;
    let (sender, mut commands) = mpsc::channel(wire::QUEUE);
    let registration = bridge.registry.lock().unwrap().register(hello, sender);
    let id = match registration {
        Ok(id) => id,
        Err(error) => {
            wire::write_message(
                &mut writer,
                &Message::Rejected {
                    reason: error.to_string(),
                },
            )
            .await?;
            return Err(error);
        }
    };
    bridge.changed();
    let (mut incoming, reader_task) = wire::reader_task(reader);
    let result = async {
        wire::write_message(&mut writer, &Message::Registered { instance_id:id.clone() }).await?;
        let mut pending: BTreeMap<u64, oneshot::Sender<CallToolResult>> = BTreeMap::new();
        let mut next_id = 0_u64;
        let mut heartbeat = tokio::time::interval(std::time::Duration::from_secs(1));
        loop {
            tokio::select! {
                _ = heartbeat.tick() => {
                    pending.retain(|_, reply| !reply.is_closed());
                    if bridge.registry.lock().unwrap().instances[&id].last_seen.elapsed() > wire::STALE_TIMEOUT {
                        return Err(io::Error::other("game heartbeat timeout"));
                    }
                    wire::write_message(&mut writer, &Message::Ping).await?;
                }
                command = commands.recv() => {
                    let Some(command) = command else { return Ok(()); };
                    if command.reply.is_closed() { continue; }
                    pending.retain(|_, reply| !reply.is_closed());
                    if pending.len() >= wire::QUEUE {
                        let _ = command.reply.send(failure("overloaded", "too many in-flight game calls"));
                        continue;
                    }
                    next_id += 1;
                    wire::write_message(&mut writer, &Message::Call {id:next_id,name:command.name,arguments:command.arguments}).await?;
                    pending.insert(next_id, command.reply);
                }
                message = incoming.recv() => {
                    let message = message.ok_or_else(|| io::Error::other("game reader closed"))??;
                    match message {
                        Message::Snapshot { status } => {
                            let mut registry = bridge.registry.lock().unwrap();
                            let instance = registry.instances.get_mut(&id).unwrap();
                            instance.status = status;
                            instance.last_seen = Instant::now();
                        }
                        Message::Reply { id, result } => {
                            if let Some(reply) = pending.remove(&id) { let _ = reply.send(result); }
                        }
                        _ => return Err(io::Error::other("unexpected game message")),
                    }
                }
            }
        }
    }.await;
    reader_task.abort();
    {
        let mut registry = bridge.registry.lock().unwrap();
        let instance = registry.instances.get_mut(&id).unwrap();
        instance.sender = None;
        instance.disconnect_reason = Some(
            result
                .as_ref()
                .err()
                .map_or("connection closed".into(), ToString::to_string),
        );
    }
    bridge.changed();
    result
}

/// Serves Codex on stdio and accepts independently launched games on loopback TCP.
/// No game processes are spawned, terminated, or stopped on MCP disconnect.
pub fn serve_stdio(address: SocketAddr) -> Result<(), Box<dyn Error + Send + Sync>> {
    check_address(address)?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    let result = runtime.block_on(async move {
        let listener = TcpListener::bind(address).await?;
        eprintln!("Nico bridge listening on {}", listener.local_addr()?);
        let (revision, _) = watch::channel(0);
        let bridge = Bridge {
            registry: Arc::new(Mutex::new(Registry::new())),
            revision,
        };
        let accept_bridge = bridge.clone();
        let accepting = tokio::spawn(async move {
            let slots = Arc::new(Semaphore::new(MAX_CONNECTIONS));
            loop {
                let (stream, _) = listener.accept().await?;
                let Ok(slot) = slots.clone().try_acquire_owned() else {
                    drop(stream);
                    continue;
                };
                let bridge = accept_bridge.clone();
                tokio::spawn(async move {
                    let _slot = slot;
                    if let Err(error) = serve_game(stream, bridge).await {
                        eprintln!("Nico game connection ended: {error}");
                    }
                });
            }
            #[allow(unreachable_code)]
            Ok::<(), io::Error>(())
        });
        let service = bridge.serve(rmcp::transport::stdio()).await?;
        let result = service.waiting().await;
        accepting.abort();
        result?;
        Ok(())
    });
    runtime.shutdown_background();
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{HostState, control_channel};

    fn hello(game: &str, role: GameRole, names: &[&str]) -> Message {
        let (control, _endpoint) = control_channel();
        Message::Register {
            protocol: wire::VERSION,
            game: game.into(),
            role,
            api_version: "1".into(),
            pid: 123,
            tools: names
                .iter()
                .map(|name| {
                    Tool::new(
                        name.to_string(),
                        "test tool",
                        json!({"type":"object"}).as_object().unwrap().clone(),
                    )
                })
                .collect(),
            status: control.status(),
        }
    }
    fn bridge() -> Bridge {
        Bridge {
            registry: Arc::new(Mutex::new(Registry::new())),
            revision: watch::channel(0).0,
        }
    }
    fn request(name: &str, args: Value) -> CallToolRequestParams {
        let mut request = CallToolRequestParams::new(name.to_owned());
        request.arguments = Some(args.as_object().unwrap().clone());
        request
    }
    fn error_code(result: CallToolResult) -> String {
        assert_eq!(result.is_error, Some(true));
        result.structured_content.unwrap()["error"]["code"]
            .as_str()
            .unwrap()
            .to_owned()
    }

    #[test]
    fn registration_reconciles_offline_schemas_and_rejects_live_conflicts() {
        let mut registry = Registry::new();
        let (sender, _commands) = mpsc::channel(wire::QUEUE);
        let id = registry
            .register(hello("demo", GameRole::Server, &["old"]), sender.clone())
            .unwrap();
        assert!(
            registry
                .register(hello("demo", GameRole::Server, &["new"]), sender.clone())
                .is_err()
        );
        registry.instances.get_mut(&id).unwrap().sender = None;
        assert_eq!(registry.dynamic_tools()[0].name, "demo.server.old");
        let new_id = registry
            .register(hello("demo", GameRole::Server, &["new"]), sender.clone())
            .unwrap();
        assert_ne!(id, new_id);
        assert_eq!(registry.dynamic_tools()[0].name, "demo.server.new");
        assert_eq!(
            error_code(registry.route(&id, "new", None).unwrap_err()),
            "instance_unavailable"
        );
        assert!(
            registry
                .register(hello("demo", GameRole::Client, &["different"]), sender)
                .is_ok()
        );
        assert_eq!(registry.dynamic_tools().len(), 2);
    }

    #[test]
    fn registration_rejects_protocol_duplicates_and_capacity_overflow() {
        let mut registry = Registry::new();
        let (sender, _commands) = mpsc::channel(wire::QUEUE);
        let mut invalid = hello("demo", GameRole::Server, &["test"]);
        if let Message::Register { protocol, .. } = &mut invalid {
            *protocol = 99;
        }
        assert!(registry.register(invalid, sender.clone()).is_err());
        assert!(
            registry
                .register(
                    hello("demo", GameRole::Server, &["test", "test"]),
                    sender.clone()
                )
                .is_err()
        );
        assert!(
            registry
                .register(hello("bad.name", GameRole::Server, &[]), sender.clone())
                .is_err()
        );
        for index in 0..MAX_CATALOGS {
            registry
                .register(
                    hello(&format!("game{index}"), GameRole::Server, &[]),
                    sender.clone(),
                )
                .unwrap();
        }
        assert!(
            registry
                .register(hello("overflow", GameRole::Server, &[]), sender)
                .is_err()
        );
    }

    #[tokio::test]
    async fn routing_reports_invalid_input_wrong_instances_overload_and_disconnect() {
        let bridge = bridge();
        let (sender, mut commands) = mpsc::channel(1);
        let id = bridge
            .registry
            .lock()
            .unwrap()
            .register(hello("demo", GameRole::Server, &["echo"]), sender.clone())
            .unwrap();
        assert_eq!(
            error_code(bridge.invoke(request("instance_status", json!({}))).await),
            "invalid_arguments"
        );
        assert_eq!(
            error_code(
                bridge
                    .invoke(request("list_instances", json!({"extra":true})))
                    .await
            ),
            "invalid_arguments"
        );
        assert_eq!(
            error_code(
                bridge
                    .invoke(request(
                        "call_game_tool",
                        json!({"instance_id":id,"arguments":{}})
                    ))
                    .await
            ),
            "invalid_arguments"
        );
        let (reply, _response) = oneshot::channel();
        sender
            .try_send(Command {
                name: "echo".into(),
                arguments: Map::new(),
                reply,
            })
            .unwrap();
        let call = request("demo.server.echo", json!({"instance_id":id,"arguments":{}}));
        assert_eq!(error_code(bridge.invoke(call.clone()).await), "overloaded");
        commands.recv().await.unwrap();
        let remote = tokio::spawn(async move {
            let command = commands.recv().await.unwrap();
            command
                .reply
                .send(CallToolResult::structured(json!({"ok":true})))
                .unwrap();
        });
        assert_eq!(
            bridge
                .invoke(call.clone())
                .await
                .structured_content
                .unwrap()["ok"],
            true
        );
        remote.await.unwrap();
        assert_eq!(
            error_code(bridge.invoke(call).await),
            "instance_unavailable"
        );
        let other = hello("other", GameRole::Client, &["echo"]);
        let other_id = bridge
            .registry
            .lock()
            .unwrap()
            .register(other, sender)
            .unwrap();
        assert_eq!(
            error_code(
                bridge
                    .invoke(request(
                        "demo.server.echo",
                        json!({"instance_id":other_id,"arguments":{}})
                    ))
                    .await
            ),
            "wrong_instance"
        );
    }

    #[tokio::test]
    async fn timeout_does_not_retry_or_redirect_a_mutation() {
        let bridge = bridge();
        let (sender, mut commands) = mpsc::channel(1);
        let id = bridge
            .registry
            .lock()
            .unwrap()
            .register(hello("demo", GameRole::Server, &["change"]), sender)
            .unwrap();
        let call = bridge.invoke(request(
            "demo.server.change",
            json!({"instance_id":id,"arguments":{}}),
        ));
        assert_eq!(error_code(call.await), "timeout");
        let command = commands.try_recv().unwrap();
        assert!(command.reply.is_closed());
        assert!(commands.try_recv().is_err());
        assert_eq!(
            bridge.registry.lock().unwrap().instances[&id].status.state,
            HostState::Starting
        );
    }

    #[tokio::test]
    async fn missing_heartbeats_disconnect_without_claiming_process_exit() {
        let bridge = bridge();
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let connection = TcpStream::connect(listener.local_addr().unwrap())
            .await
            .unwrap();
        let (stream, _) = listener.accept().await.unwrap();
        let task = tokio::spawn(serve_game(stream, bridge.clone()));
        let (reader, mut writer) = connection.into_split();
        wire::write_message(&mut writer, &hello("silent", GameRole::Server, &["query"]))
            .await
            .unwrap();
        let mut reader = BufReader::new(reader);
        let Message::Registered { instance_id } = wire::read_message(&mut reader).await.unwrap()
        else {
            panic!("registration expected");
        };
        // Simulate an already-stale report; the heartbeat observes it without a real delay.
        bridge
            .registry
            .lock()
            .unwrap()
            .instances
            .get_mut(&instance_id)
            .unwrap()
            .last_seen = Instant::now() - wire::STALE_TIMEOUT - std::time::Duration::from_secs(1);
        assert!(
            tokio::time::timeout(std::time::Duration::from_secs(3), task)
                .await
                .unwrap()
                .unwrap()
                .is_err()
        );
        let registry = bridge.registry.lock().unwrap();
        let snapshot = registry.snapshot(&instance_id).unwrap();
        assert_eq!(snapshot["connected"], false);
        assert_eq!(snapshot["host"]["state"], "starting");
        assert_eq!(registry.dynamic_tools().len(), 1);
    }
}
