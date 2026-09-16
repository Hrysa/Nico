use nico_ops::{
    bridge::{BridgeClient, GameRegistration, GameRole},
    control_channel,
    mcp::{CallToolResult, Tool, ToolExtensions},
};
use serde_json::{Value, json};
use std::{
    io::{BufRead, BufReader, Write},
    net::{SocketAddr, TcpListener},
    process::{Child, Command, Stdio},
    sync::mpsc,
    thread,
    time::{Duration, Instant},
};

struct Mcp {
    child: Child,
    output: mpsc::Receiver<Value>,
    next_id: u64,
    changes: usize,
}
impl Mcp {
    fn start(address: SocketAddr) -> Self {
        let mut command = Command::new(env!("CARGO_BIN_EXE_nico-bridge"));
        command
            .args(["--listen", &address.to_string()])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000);
        }
        let mut child = command.spawn().unwrap();
        let output = child.stdout.take().unwrap();
        let (sender, receiver) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(output).lines() {
                let Ok(line) = line else {
                    break;
                };
                if sender.send(serde_json::from_str(&line).unwrap()).is_err() {
                    break;
                }
            }
        });
        let mut mcp = Self {
            child,
            output: receiver,
            next_id: 0,
            changes: 0,
        };
        let info = mcp.request("initialize", json!({"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"nico-test","version":"1"}}));
        assert_eq!(info["capabilities"]["tools"]["listChanged"], true);
        mcp.send(json!({"jsonrpc":"2.0","method":"notifications/initialized"}));
        mcp
    }
    fn send(&mut self, message: Value) {
        let input = self.child.stdin.as_mut().unwrap();
        writeln!(input, "{message}").unwrap();
        input.flush().unwrap();
    }
    fn request(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        self.send(json!({"jsonrpc":"2.0","id":self.next_id,"method":method,"params":params}));
        loop {
            let message = self.output.recv_timeout(Duration::from_secs(10)).unwrap();
            if message["method"] == "notifications/tools/list_changed" {
                self.changes += 1;
                continue;
            }
            assert_eq!(message["id"], self.next_id, "{message}");
            assert!(message.get("error").is_none(), "{message}");
            return message["result"].clone();
        }
    }
    fn call(&mut self, name: &str, args: Value) -> Value {
        self.request("tools/call", json!({"name":name,"arguments":args}))
    }
    fn connected(&mut self, role: &str) -> String {
        let deadline = Instant::now() + Duration::from_secs(8);
        loop {
            let result = self.call("list_instances", json!({}));
            if let Some(instance) = result["structuredContent"]["instances"]
                .as_array()
                .unwrap()
                .iter()
                .find(|instance| instance["role"] == role && instance["connected"] == true)
            {
                return instance["instance_id"].as_str().unwrap().to_owned();
            }
            assert!(Instant::now() < deadline, "{result}");
            thread::sleep(Duration::from_millis(20));
        }
    }
    fn close(&mut self) {
        self.child.stdin.take();
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                assert!(status.success());
                return;
            }
            assert!(Instant::now() < deadline);
            thread::sleep(Duration::from_millis(10));
        }
    }
}
impl Drop for Mcp {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn address() -> SocketAddr {
    TcpListener::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
}
fn tools() -> ToolExtensions {
    let mut tools = ToolExtensions::default();
    tools.register(Tool::new("echo", "Game-owned echo",json!({"type":"object","required":["message"],"properties":{"message":{"type":"string"}},"additionalProperties":false}).as_object().unwrap().clone()),|args| {
        if args.len()!=1 || args.get("message").and_then(Value::as_str).is_none() { CallToolResult::structured_error(json!({"error":"message string required"})) }
        else { CallToolResult::structured(json!({"message":args["message"]})) }
    }).unwrap();
    tools
}

#[test]
fn games_register_tools_dynamically_and_survive_bridge_restart() {
    let address = address();
    let (server_control, mut server) = control_channel();
    server.running(1);
    // Start the game adapter before the bridge exists; connection is optional.
    let server_adapter = BridgeClient::start(
        address,
        GameRegistration::new("demo", GameRole::Server, "1"),
        server_control.clone(),
        tools(),
    )
    .unwrap();
    assert!(!server.stop_requested());
    let mut mcp = Mcp::start(address);
    let server_id = mcp.connected("server");
    let (client_control, mut client) = control_channel();
    client.running(2);
    let client_adapter = BridgeClient::start(
        address,
        GameRegistration::new("demo", GameRole::Client, "1"),
        client_control,
        tools(),
    )
    .unwrap();
    let client_id = mcp.connected("client");
    let catalog = mcp.request("tools/list", json!({}));
    assert!(
        catalog["tools"]
            .as_array()
            .unwrap()
            .iter()
            .any(|tool| tool["name"] == "demo.server.echo")
    );
    assert!(
        mcp.changes > 0,
        "live tool-list change notification received"
    );
    let response = mcp.call(
        "demo.server.echo",
        json!({"instance_id":server_id,"arguments":{"message":"hello"}}),
    );
    assert_eq!(response["structuredContent"]["message"], "hello");
    assert_eq!(
        mcp.call(
            "call_game_tool",
            json!({"instance_id":client_id,"tool_name":"echo","arguments":{"message":"client"}})
        )["structuredContent"]["message"],
        "client"
    );
    assert_eq!(
        mcp.call(
            "demo.server.echo",
            json!({"instance_id":server_id,"arguments":{}})
        )["isError"],
        true
    );
    assert_eq!(
        mcp.call(
            "demo.server.echo",
            json!({"instance_id":client_id,"arguments":{"message":"wrong"}})
        )["structuredContent"]["error"]["code"],
        "wrong_instance"
    );
    client.finish(Ok(()));
    drop(client_adapter);
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        let state = mcp.call("instance_status", json!({"instance_id":client_id}));
        if state["structuredContent"]["connected"] == false {
            assert_eq!(state["structuredContent"]["host"]["state"], "stopped");
            break;
        }
        assert!(Instant::now() < deadline);
        thread::sleep(Duration::from_millis(10));
    }
    assert!(
        mcp.request("tools/list", json!({}))["tools"]
            .as_array()
            .unwrap()
            .iter()
            .any(|tool| tool["name"] == "demo.client.echo")
    );
    assert_eq!(
        mcp.call(
            "demo.client.echo",
            json!({"instance_id":client_id,"arguments":{"message":"offline"}})
        )["structuredContent"]["error"]["code"],
        "instance_unavailable"
    );
    mcp.close();
    assert!(
        !server.stop_requested(),
        "MCP disconnect must not stop the game"
    );
    let mut restarted = Mcp::start(address);
    let new_id = restarted.connected("server");
    assert_ne!(server_id, new_id);
    assert!(!server.stop_requested());
    let result = restarted.call(
        "demo.server.stop",
        json!({"instance_id":new_id,"arguments":{}}),
    );
    assert_eq!(result["structuredContent"]["accepted"], true);
    assert!(server.stop_requested());
    server.finish(Ok(()));
    drop(server_adapter);
    restarted.close();
    assert!(server_control.status().is_finished());
}

#[test]
fn bridge_starts_with_only_management_tools_and_no_game_processes() {
    let mut mcp = Mcp::start(address());
    assert_eq!(
        mcp.request("tools/list", json!({}))["tools"]
            .as_array()
            .unwrap()
            .len(),
        5
    );
    assert_eq!(
        mcp.call("list_instances", json!({}))["structuredContent"]["instances"],
        json!([])
    );
    assert_eq!(
        mcp.call("bridge_status", json!({}))["structuredContent"]["launches_games"],
        false
    );
    mcp.close();
}

#[test]
fn arena_tools_route_through_bridge_and_preserve_commands_across_reconnect() {
    use arena_arpg_shared::{ArenaPlugin, FIXED_STEP};
    let (builder, catalog) =
        arena_arpg_shared::tools::register(nico_runtime::AppBuilder::new().add_plugin(ArenaPlugin))
            .unwrap();
    let mut app = builder.build().unwrap();
    app.start().unwrap();
    let (control, mut host) = control_channel();
    host.running(0);
    let endpoint = address();
    let adapter = BridgeClient::start(
        endpoint,
        GameRegistration::new("arena-arpg", GameRole::Server, "1"),
        control,
        catalog,
    )
    .unwrap();
    let mut mcp = Mcp::start(endpoint);
    let first = mcp.connected("server");
    let catalog = mcp.call("list_game_tools", json!({}));
    assert!(catalog.to_string().contains("game_attack"));
    assert!(catalog.to_string().contains("game_move_hold"));
    assert!(catalog.to_string().contains("game_move_release"));
    assert!(catalog.to_string().contains("phase_ticks_remaining"));
    fn routed(mcp: &mut Mcp, instance: &str, name: &str, args: Value) -> Value {
        mcp.call(
            "call_game_tool",
            json!({"instance_id":instance,"tool_name":name,"arguments":args}),
        )["structuredContent"]
            .clone()
    }
    let initial = routed(&mut mcp, &first, "game_state", json!({}));
    assert_eq!(initial["run_id"], 1);
    let accepted = routed(
        &mut mcp,
        &first,
        "game_move",
        json!({"run_id":1,"x":1,"z":0,"ticks":3}),
    );
    let id = accepted["command_id"].as_u64().unwrap();
    assert_eq!(
        routed(&mut mcp, &first, "game_command", json!({"command_id":id}))["state"],
        "pending"
    );
    app.tick(FIXED_STEP).unwrap();
    assert_eq!(
        routed(&mut mcp, &first, "game_command", json!({"command_id":id}))["state"],
        "running"
    );
    mcp.close();
    assert!(!host.stop_requested());
    app.tick(FIXED_STEP).unwrap();
    app.tick(FIXED_STEP).unwrap();
    let mut reconnected = Mcp::start(endpoint);
    let second = reconnected.connected("server");
    assert_ne!(first, second);
    let completed = routed(
        &mut reconnected,
        &second,
        "game_command",
        json!({"command_id":id}),
    );
    assert_eq!(completed["state"], "completed");
    assert_eq!(completed["applied_ticks"], 3);
    assert_eq!(
        routed(&mut reconnected, &second, "game_state", json!({}))["tick"],
        3
    );
    let attack = routed(
        &mut reconnected,
        &second,
        "game_attack",
        json!({"run_id":1,"yaw":0}),
    )["command_id"]
        .as_u64()
        .unwrap();
    app.tick(FIXED_STEP).unwrap();
    assert_eq!(
        routed(
            &mut reconnected,
            &second,
            "game_command",
            json!({"command_id":attack})
        )["state"],
        "completed"
    );
    assert_eq!(
        routed(&mut reconnected, &second, "game_state", json!({}))["actors"][0]["action"]["phase"],
        "windup"
    );
    let reset = routed(
        &mut reconnected,
        &second,
        "game_restart",
        json!({"run_id":1}),
    )["command_id"]
        .as_u64()
        .unwrap();
    app.tick(FIXED_STEP).unwrap();
    assert_eq!(
        routed(
            &mut reconnected,
            &second,
            "game_command",
            json!({"command_id":reset})
        )["result_run_id"],
        2
    );
    let lease = routed(
        &mut reconnected,
        &second,
        "game_move_hold",
        json!({"run_id":2,"lease_id":0,"x":1,"z":0,"ticks":3}),
    )["command_id"]
        .as_u64()
        .unwrap();
    app.tick(FIXED_STEP).unwrap();
    let renewed = routed(
        &mut reconnected,
        &second,
        "game_move_hold",
        json!({"run_id":2,"lease_id":lease,"x":1,"z":0,"ticks":3}),
    )["command_id"]
        .as_u64()
        .unwrap();
    app.tick(FIXED_STEP).unwrap();
    assert_eq!(
        routed(
            &mut reconnected,
            &second,
            "game_command",
            json!({"command_id":renewed})
        )["state"],
        "completed"
    );
    assert_eq!(
        routed(&mut reconnected, &second, "game_state", json!({}))["movement_hold"]["remaining_ticks"],
        2
    );
    let release = routed(
        &mut reconnected,
        &second,
        "game_move_release",
        json!({"run_id":2,"lease_id":lease}),
    )["command_id"]
        .as_u64()
        .unwrap();
    app.tick(FIXED_STEP).unwrap();
    assert_eq!(
        routed(
            &mut reconnected,
            &second,
            "game_command",
            json!({"command_id":release})
        )["state"],
        "completed"
    );
    assert_eq!(
        routed(
            &mut reconnected,
            &second,
            "game_command",
            json!({"command_id":lease})
        )["reason"],
        "released"
    );
    assert!(!host.stop_requested());
    app.shutdown().unwrap();
    assert_eq!(
        routed(
            &mut reconnected,
            &second,
            "game_restart",
            json!({"run_id":2})
        )["error"]["code"],
        "shutting_down"
    );
    drop(adapter);
    reconnected.close();
}
