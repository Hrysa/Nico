//! Optional MCP stdio adapter for one host. Protocol I/O never accesses the App.

use std::{collections::BTreeMap, error::Error, io};

// Protocol types are confined to this optional adapter API.
pub use rmcp::model::{CallToolResult, Tool};
pub use serde_json::{Map, Value};

use rmcp::{
    ErrorData, RoleServer, ServerHandler, ServiceExt,
    model::{
        CallToolRequestParams, CallToolResponse, Implementation, ListToolsResult,
        PaginatedRequestParams, ServerCapabilities, ServerInfo, ToolAnnotations,
    },
    service::RequestContext,
};
use serde_json::json;

use crate::{HostControl, HostState, HostStatus};

type ToolHandler = Box<dyn Fn(Map<String, Value>) -> CallToolResult + Send + Sync>;

/// Additional game tools registered before the engine starts MCP.
///
/// Handlers run on the MCP thread and must return promptly. Validate arguments
/// against the supplied schema and return tool errors on invalid input. Read
/// owned snapshots or enqueue bounded host requests; never mutate the App here.
#[derive(Default)]
pub struct ToolExtensions(BTreeMap<String, (Tool, ToolHandler)>);

impl ToolExtensions {
    /// Registers a tool. Duplicate names and engine-owned `status`/`stop` are rejected.
    pub fn register(
        &mut self,
        tool: Tool,
        handler: impl Fn(Map<String, Value>) -> CallToolResult + Send + Sync + 'static,
    ) -> Result<(), io::Error> {
        let name = tool.name.to_string();
        if name.is_empty()
            || matches!(name.as_str(), "status" | "stop")
            || self.0.contains_key(&name)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("tool name is empty, reserved, or already registered: {name}"),
            ));
        }
        self.0.insert(name, (tool, Box::new(handler)));
        Ok(())
    }
}

/// Serves `status` and `stop` over this process's stdin/stdout.
///
/// Call once, on a dedicated thread; this blocks until MCP disconnects or fails.
/// Reserve stdout for MCP and send application logs to stderr. Host shutdown does
/// not close MCP: callers may inspect the final status, then close stdin to exit.
/// Disconnect (including failed initialization) requests orderly host stop.
pub fn serve_stdio(control: HostControl) -> Result<(), Box<dyn Error + Send + Sync>> {
    serve_stdio_with_tools(control, ToolExtensions::default())
}

/// Serves the engine lifecycle tools plus registered game tools.
/// Has the same thread and connection lifecycle as [`serve_stdio`].
pub fn serve_stdio_with_tools(
    control: HostControl,
    tools: ToolExtensions,
) -> Result<(), Box<dyn Error + Send + Sync>> {
    let adapter = Adapter(control, tools);
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    let result = runtime.block_on(async move {
        let service = adapter.serve(rmcp::transport::stdio()).await?;
        service.waiting().await?;
        Ok(())
    });
    // Tokio's stdin read is blocking and cannot be cancelled. On protocol failure,
    // do not wait for a still-connected client to supply another line or EOF.
    runtime.shutdown_background();
    result
}

struct Adapter(HostControl, ToolExtensions);

impl Drop for Adapter {
    fn drop(&mut self) {
        // Explicitly stop even if another observer retains a control clone.
        let _ = self.0.request_stop();
    }
}

impl Adapter {
    fn invoke(&self, request: CallToolRequestParams) -> CallToolResult {
        if matches!(request.name.as_ref(), "status" | "stop")
            && request
                .arguments
                .as_ref()
                .is_some_and(|args| !args.is_empty())
        {
            return CallToolResult::structured_error(json!({"error": "Tools accept no arguments"}));
        }
        match request.name.as_ref() {
            "status" => CallToolResult::structured(snapshot(self.0.status())),
            "stop" => {
                let accepted = self.0.request_stop().is_ok();
                let result = json!({"accepted": accepted, "status": snapshot(self.0.status())});
                if accepted {
                    CallToolResult::structured(result)
                } else {
                    CallToolResult::structured_error(result)
                }
            }
            name => match self.1.0.get(name) {
                Some((_, handler)) => handler(request.arguments.unwrap_or_default()),
                None => CallToolResult::structured_error(json!({"error": "Unknown tool"})),
            },
        }
    }
}

impl ServerHandler for Adapter {
    fn get_info(&self) -> ServerInfo {
        ServerInfo::new(ServerCapabilities::builder().enable_tools().build())
            .with_server_info(Implementation::new("nico-ops", env!("CARGO_PKG_VERSION")))
            .with_instructions("Controls this host only. Poll status for readiness or completion. stop acknowledges a request; close stdin after inspecting the final status to exit.")
    }

    async fn list_tools(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        let mut tools: Vec<Tool> = [
            ("status", "Read the latest cached host status. ready means a successful first step; finished means a terminal host result, not process exit.", true),
            ("stop", "Request orderly host stop. accepted confirms delivery, not completion. Poll status until finished, then close the MCP connection.", false),
        ].into_iter().map(|(name, description, read_only)| {
            let mut annotations = ToolAnnotations::default();
            annotations.read_only_hint = Some(read_only);
            annotations.destructive_hint = Some(false);
            annotations.idempotent_hint = Some(true);
            annotations.open_world_hint = Some(false);
            Tool::new(name, description, json!({"type": "object", "properties": {}, "additionalProperties": false}).as_object().unwrap().clone())
                .with_annotations(annotations)
                .with_raw_output_schema(output_schema(name).as_object().unwrap().clone().into())
        }).collect();
        tools.extend(self.1.0.values().map(|(tool, _)| tool.clone()));
        Ok(ListToolsResult::with_all_items(tools))
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        Ok(self.invoke(request).into())
    }
}

fn output_schema(name: &str) -> Value {
    let status = json!({
        "type": "object", "additionalProperties": false,
        "required": ["state", "completed_steps", "ready", "finished", "failure"],
        "properties": {
            "state": {"type": "string", "enum": ["starting", "running", "stopping", "stopped", "failed"]},
            "completed_steps": {"type": "integer", "minimum": 0},
            "ready": {"type": "boolean"}, "finished": {"type": "boolean"},
            "failure": {"type": ["string", "null"]}
        }
    });
    if name == "status" {
        status
    } else {
        json!({
            "type": "object", "additionalProperties": false,
            "required": ["accepted", "status"],
            "properties": {"accepted": {"type": "boolean"}, "status": status}
        })
    }
}

fn snapshot(status: HostStatus) -> Value {
    let state = match status.state {
        HostState::Starting => "starting",
        HostState::Running => "running",
        HostState::Stopping => "stopping",
        HostState::Stopped => "stopped",
        HostState::Failed => "failed",
    };
    json!({
        "state": state,
        "completed_steps": status.completed_steps,
        "ready": status.is_ready(),
        "finished": status.is_finished(),
        "failure": status.failure,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::control_channel;

    #[test]
    fn failed_host_is_readable_but_stop_is_a_tool_error() {
        let (control, endpoint) = control_channel();
        endpoint.finish(Err("startup failed".into()));
        let adapter = Adapter(control, ToolExtensions::default());
        let status = adapter.invoke(CallToolRequestParams::new("status"));
        assert_eq!(status.is_error, Some(false));
        let status = status.structured_content.unwrap();
        assert_eq!(status["state"], "failed");
        assert_eq!(status["failure"], "startup failed");
        assert_eq!(status["finished"], true);
        let stop = adapter.invoke(CallToolRequestParams::new("stop"));
        assert_eq!(stop.is_error, Some(true));
        assert_eq!(stop.structured_content.unwrap()["accepted"], false);
    }

    #[test]
    fn adapter_disconnect_requests_stop_with_another_observer_alive() {
        let (control, mut endpoint) = control_channel();
        drop(Adapter(control.clone(), ToolExtensions::default()));
        assert!(endpoint.stop_requested());
        endpoint.finish(Ok(()));
        assert!(control.status().is_finished());
    }

    #[test]
    fn game_tools_extend_without_replacing_engine_operations() {
        let tool = |name: &str| Tool::new(name.to_owned(), "Game query", Map::new());
        let mut tools = ToolExtensions::default();
        for name in ["status", "stop", ""] {
            assert!(
                tools
                    .register(tool(name), |_| CallToolResult::structured(json!({})))
                    .is_err()
            );
        }
        tools
            .register(tool("game_echo"), |args| {
                match args.get("message").and_then(Value::as_str) {
                    Some(message) => CallToolResult::structured(json!({"message": message})),
                    None => CallToolResult::structured_error(json!({"error": "message required"})),
                }
            })
            .unwrap();
        assert!(
            tools
                .register(tool("game_echo"), |_| unreachable!())
                .is_err()
        );
        let (control, mut endpoint) = control_channel();
        let adapter = Adapter(control, tools);
        let mut request = CallToolRequestParams::new("game_echo");
        request.arguments = Some(json!({"message": "hello"}).as_object().unwrap().clone());
        assert_eq!(
            adapter.invoke(request).structured_content.unwrap()["message"],
            "hello"
        );
        assert_eq!(
            adapter
                .invoke(CallToolRequestParams::new("game_echo"))
                .is_error,
            Some(true)
        );
        assert_eq!(
            adapter
                .invoke(CallToolRequestParams::new("status"))
                .structured_content
                .unwrap()["state"],
            "starting"
        );
        assert_eq!(
            adapter
                .invoke(CallToolRequestParams::new("stop"))
                .structured_content
                .unwrap()["accepted"],
            true
        );
        assert!(endpoint.stop_requested());
        endpoint.finish(Ok(()));
    }
}
