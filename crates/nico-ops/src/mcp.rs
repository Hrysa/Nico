//! Host tool catalogs uploaded through bridge connections. Handlers never access the App.

use std::{collections::BTreeMap, io};

// Protocol types are confined to this optional adapter API.
pub use rmcp::model::{CallToolResult, Tool};
pub use serde_json::{Map, Value};

#[cfg(any(feature = "bridge", test))]
use rmcp::model::{CallToolRequestParams, ToolAnnotations};
#[cfg(any(feature = "bridge", test))]
use serde_json::json;

#[cfg(any(feature = "bridge", test))]
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

#[cfg(any(feature = "bridge", test))]
pub(crate) fn invoke_host_tool(
    control: &HostControl,
    extensions: &ToolExtensions,
    request: CallToolRequestParams,
) -> CallToolResult {
    if matches!(request.name.as_ref(), "status" | "stop")
        && request
            .arguments
            .as_ref()
            .is_some_and(|args| !args.is_empty())
    {
        return CallToolResult::structured_error(json!({"error": "Tools accept no arguments"}));
    }
    match request.name.as_ref() {
        "status" => CallToolResult::structured(snapshot(control.status())),
        "stop" => {
            let accepted = control.request_stop().is_ok();
            let result = json!({"accepted": accepted, "status": snapshot(control.status())});
            if accepted {
                CallToolResult::structured(result)
            } else {
                CallToolResult::structured_error(result)
            }
        }
        name => match extensions.0.get(name) {
            Some((_, handler)) => handler(request.arguments.unwrap_or_default()),
            None => CallToolResult::structured_error(json!({"error": "Unknown tool"})),
        },
    }
}

#[cfg(any(feature = "bridge", test))]
pub(crate) fn host_tool_catalog(extensions: &ToolExtensions) -> Vec<Tool> {
    let mut tools: Vec<Tool> = [
            ("status", "Read the latest cached host status. ready means a successful first step; finished means a terminal host result, not process exit.", true),
            ("stop", "Request orderly host stop. accepted confirms delivery, not completion. Poll bridge instance status for completion.", false),
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
    tools.extend(extensions.0.values().map(|(tool, _)| tool.clone()));
    tools
}

#[cfg(any(feature = "bridge", test))]
fn output_schema(name: &str) -> Value {
    let status = json!({
        "type": "object", "additionalProperties": false,
        "required": ["state", "completed_steps", "ready", "finished", "failure", "graphics"],
        "properties": {
            "state": {"type": "string", "enum": ["starting", "running", "stopping", "stopped", "failed"]},
            "completed_steps": {"type": "integer", "minimum": 0},
            "graphics": {"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
                "required":["presented_frames","last_outcome"],"properties":{
                    "presented_frames":{"type":"integer","minimum":0},
                    "last_outcome":{"type":"string","enum":["not_attempted","presented","zero_sized","timeout","occluded","initialization_failed","render_failed"]}}}]},
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

#[cfg(any(feature = "bridge", test))]
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
        "graphics": status.graphics.map(|g| json!({"presented_frames":g.presented_frames,"last_outcome":g.last_outcome.as_str()})),
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
    fn routed_status_reports_optional_graphics_with_stable_outcomes() {
        let (control, mut host) = control_channel();
        let tools = ToolExtensions::default();
        assert!(snapshot(control.status())["graphics"].is_null());
        host.graphics(crate::GraphicsOutcome::Presented);
        let result = invoke_host_tool(&control, &tools, CallToolRequestParams::new("status"));
        assert_eq!(
            result.structured_content.unwrap()["graphics"],
            json!({"presented_frames":1,"last_outcome":"presented"})
        );
        host.finish(Ok(()));
    }

    #[test]
    fn connection_catalog_includes_host_and_game_tools() {
        let mut tools = ToolExtensions::default();
        tools
            .register(Tool::new("game_query", "Query", Map::new()), |_| {
                CallToolResult::structured(json!({}))
            })
            .unwrap();
        let catalog = host_tool_catalog(&tools);
        assert_eq!(
            catalog
                .iter()
                .map(|tool| tool.name.as_ref())
                .collect::<Vec<_>>(),
            ["status", "stop", "game_query"]
        );
    }

    #[test]
    fn failed_host_is_readable_but_stop_is_a_tool_error() {
        let (control, endpoint) = control_channel();
        endpoint.finish(Err("startup failed".into()));
        let tools = ToolExtensions::default();
        let invoke = |request| invoke_host_tool(&control, &tools, request);
        let status = invoke(CallToolRequestParams::new("status"));
        assert_eq!(status.is_error, Some(false));
        let status = status.structured_content.unwrap();
        assert_eq!(status["state"], "failed");
        assert_eq!(status["failure"], "startup failed");
        assert_eq!(status["finished"], true);
        let stop = invoke(CallToolRequestParams::new("stop"));
        assert_eq!(stop.is_error, Some(true));
        assert_eq!(stop.structured_content.unwrap()["accepted"], false);
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
        let invoke = |request| invoke_host_tool(&control, &tools, request);
        let mut request = CallToolRequestParams::new("game_echo");
        request.arguments = Some(json!({"message": "hello"}).as_object().unwrap().clone());
        assert_eq!(
            invoke(request).structured_content.unwrap()["message"],
            "hello"
        );
        assert_eq!(
            invoke(CallToolRequestParams::new("game_echo")).is_error,
            Some(true)
        );
        assert_eq!(
            invoke(CallToolRequestParams::new("status"))
                .structured_content
                .unwrap()["state"],
            "starting"
        );
        assert_eq!(
            invoke(CallToolRequestParams::new("stop"))
                .structured_content
                .unwrap()["accepted"],
            true
        );
        assert!(endpoint.stop_requested());
        endpoint.finish(Ok(()));
    }
}
