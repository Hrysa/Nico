use std::{
    io::{BufRead, BufReader, Write},
    process::{Child, Command, Stdio},
    sync::mpsc::{self, Receiver},
    thread,
    time::{Duration, Instant},
};

use serde_json::{Value, json};

struct Client {
    child: Child,
    output: Receiver<String>,
    next_id: u64,
}

impl Client {
    fn spawn() -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_minimal-game-server"))
            .args(["--mcp-stdio", "--tick-rate", "1", "--log-level", "info"])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .expect("server launches");
        let stdout = child.stdout.take().unwrap();
        let (sender, output) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                if sender.send(line.expect("read protocol stdout")).is_err() {
                    break;
                }
            }
        });
        Self {
            child,
            output,
            next_id: 0,
        }
    }

    fn send(&mut self, value: Value) {
        let stdin = self.child.stdin.as_mut().unwrap();
        writeln!(stdin, "{value}").unwrap();
        stdin.flush().unwrap();
    }

    fn request(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        self.send(
            json!({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}),
        );
        let line = self
            .output
            .recv_timeout(Duration::from_secs(10))
            .expect("MCP reply within deadline");
        let response: Value =
            serde_json::from_str(&line).expect("stdout contains only JSON protocol");
        assert_eq!(response["jsonrpc"], "2.0");
        assert_eq!(response["id"], self.next_id);
        assert!(response.get("error").is_none(), "{response}");
        response["result"].clone()
    }

    fn initialize(&mut self) {
        let info = self.request(
            "initialize",
            json!({
                "protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "nico-test", "version": "1"}
            }),
        );
        assert_eq!(info["serverInfo"]["name"], "nico-ops");
        assert!(info["capabilities"].get("tools").is_some());
        self.send(json!({"jsonrpc": "2.0", "method": "notifications/initialized"}));
    }

    fn call(&mut self, name: &str) -> Value {
        self.request("tools/call", json!({"name": name, "arguments": {}}))
    }

    fn await_state(&mut self, state: &str) -> Value {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            let response = self.call("status");
            assert_eq!(response["isError"], false);
            let status = response["structuredContent"].clone();
            let text: Value =
                serde_json::from_str(response["content"][0]["text"].as_str().unwrap()).unwrap();
            assert_eq!(status, text);
            if status["state"] == state {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "state never became {state}: {status}"
            );
            thread::sleep(Duration::from_millis(10));
        }
    }

    fn disconnect(&mut self) -> std::process::ExitStatus {
        drop(self.child.stdin.take());
        self.await_exit()
    }

    fn await_exit(&mut self) -> std::process::ExitStatus {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "server did not exit within deadline"
            );
            thread::sleep(Duration::from_millis(10));
        }
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        // Reap even if an assertion fails, so regressions leave no running server.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
fn agent_discovers_observes_and_stops_the_real_server() {
    let mut client = Client::spawn();
    client.initialize();
    let tools = client.request("tools/list", json!({}));
    let tools = tools["tools"].as_array().unwrap();
    assert_eq!(tools.len(), 2);
    assert_eq!(tools[0]["name"], "status");
    assert_eq!(tools[1]["name"], "stop");
    for tool in tools {
        assert_eq!(tool["inputSchema"]["additionalProperties"], false);
    }
    let running = client.await_state("running");
    assert_eq!(running["ready"], true);
    assert!(running["completed_steps"].as_u64().unwrap() >= 1);
    let invalid = client.request(
        "tools/call",
        json!({"name": "stop", "arguments": {"unexpected": true}}),
    );
    assert_eq!(invalid["isError"], true);
    assert_eq!(client.call("unknown")["isError"], true);
    assert_eq!(
        client.call("status")["structuredContent"]["state"],
        "running"
    );
    let stop = client.call("stop");
    assert_eq!(stop["isError"], false);
    assert_eq!(stop["structuredContent"]["accepted"], true);
    let stopped = client.await_state("stopped");
    assert_eq!(stopped["finished"], true);
    assert_eq!(stopped["ready"], false);
    assert_eq!(stopped["failure"], Value::Null);
    assert_eq!(client.call("stop")["structuredContent"]["accepted"], true);
    assert_eq!(client.call("status")["structuredContent"], stopped);
    assert!(
        client.child.try_wait().unwrap().is_none(),
        "final status remains available until disconnect"
    );
    assert!(client.disconnect().success());
}

#[test]
fn disconnect_without_stop_shuts_down_the_host() {
    let mut client = Client::spawn();
    client.initialize();
    client.await_state("running");
    assert!(client.disconnect().success());
}

#[test]
fn disconnect_before_initialize_does_not_leave_a_server_running() {
    let mut client = Client::spawn();
    // Initialization never completed, so a service error is expected.
    assert!(!client.disconnect().success());
}

#[test]
fn failed_initialization_exits_even_when_stdin_remains_open() {
    let mut client = Client::spawn();
    client.send(json!({"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}));
    assert!(!client.await_exit().success());
}
