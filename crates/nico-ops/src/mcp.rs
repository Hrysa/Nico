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

/// Host-enforced operation class. MCP annotations are documentation, not authority.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Ord, PartialOrd)]
#[cfg_attr(feature = "bridge", derive(serde::Serialize, serde::Deserialize))]
#[cfg_attr(feature = "bridge", serde(rename_all = "snake_case"))]
pub enum ToolAccess {
    Inspect,
    Capture,
    #[default]
    Mutate,
    Stop,
}

/// Additional game tools registered before the engine starts MCP.
///
/// Handlers run on the MCP thread and must return promptly. Validate arguments
/// against the supplied schema and return tool errors on invalid input. Read
/// owned snapshots or enqueue bounded host requests; never mutate the App here.
#[derive(Default)]
pub struct ToolExtensions {
    tools: BTreeMap<String, (Tool, ToolHandler, ToolAccess)>,
    connected: Vec<Box<dyn Fn() + Send + Sync>>,
}

impl ToolExtensions {
    /// Register a prompt owned-data invalidation callback for bridge reconnects.
    /// Callbacks must not access the App or world; engine transport invokes them
    /// after registration, before admitting the first call on the new connection.
    pub fn on_bridge_connection(
        &mut self,
        handler: impl Fn() + Send + Sync + 'static,
    ) -> io::Result<()> {
        if self.connected.len() >= 64 {
            return Err(io::Error::other("too many connection callbacks"));
        }
        self.connected.push(Box::new(handler));
        Ok(())
    }

    #[cfg(feature = "bridge")]
    pub(crate) fn notify_bridge_connected(&self) {
        for handler in &self.connected {
            handler();
        }
    }

    /// Registers a tool. Duplicate names and engine-owned `status`/`stop` are rejected.
    pub fn register(
        &mut self,
        tool: Tool,
        handler: impl Fn(Map<String, Value>) -> CallToolResult + Send + Sync + 'static,
    ) -> Result<(), io::Error> {
        let name = tool.name.to_string();
        if name.is_empty()
            || matches!(name.as_str(), "status" | "stop")
            || self.tools.contains_key(&name)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("tool name is empty, reserved, or already registered: {name}"),
            ));
        }
        self.tools
            .insert(name, (tool, Box::new(handler), ToolAccess::Mutate));
        Ok(())
    }

    /// Classifies a registered tool before host startup. Unclassified tools require
    /// mutation permission, even if their MCP annotation claims they are read-only.
    pub fn set_access(&mut self, name: &str, access: ToolAccess) -> io::Result<()> {
        let entry = self
            .tools
            .get_mut(name)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "tool is not registered"))?;
        entry.2 = access;
        Ok(())
    }

    pub fn access(&self, name: &str) -> Option<ToolAccess> {
        match name {
            "status" => Some(ToolAccess::Inspect),
            "stop" => Some(ToolAccess::Stop),
            _ => self.tools.get(name).map(|entry| entry.2),
        }
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
        name => match extensions.tools.get(name) {
            Some((_, handler, _)) => handler(request.arguments.unwrap_or_default()),
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
    tools.extend(extensions.tools.values().map(|(tool, _, _)| tool.clone()));
    tools
}

#[cfg(any(feature = "bridge", test))]
fn instancing_schema() -> Value {
    let fields = [
        "gpu_readback_pending",
        "gpu_readback_skipped",
        "gpu_readback_failed",
        "host_frame",
        "prepared_view",
        "visible_chunks",
        "culled_chunks",
        "submitted_instances",
        "submitted_draws",
        "indirect_draws",
        "gpu_candidate_instances",
        "visibility_upload_bytes",
        "visibility_retirement_upload_bytes",
        "foliage_upload_bytes",
        "influence_overflow",
        "visible_record_upload_bytes",
        "instance_upload_bytes",
        "deferred_upload_chunks",
        "visibility_reused_batches",
        "visibility_dispatched_pages",
        "visibility_dispatches",
        "mesh_upload_bytes",
        "retained_instance_bytes",
        "retained_split_cpu_bytes",
        "retained_batches",
    ];
    let mut properties: serde_json::Map<String, Value> = fields
        .iter()
        .map(|f| ((*f).into(), json!({"type":"integer","minimum":0})))
        .collect();
    properties.insert("ordinary_fallback".into(), json!({"type":"boolean"}));
    let sample_fields = [
        "prepared_view",
        "visible_instances",
        "candidate_instances",
        "indirect_draws",
        "age_ms",
    ];
    let sample_properties: serde_json::Map<String, Value> = sample_fields
        .iter()
        .map(|name| ((*name).into(), json!({"type":"integer","minimum":0})))
        .collect();
    properties.insert("gpu_sample".into(), json!({"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"required":sample_fields,"properties":sample_properties}]}));
    let required: Vec<_> = fields
        .into_iter()
        .chain(["ordinary_fallback", "gpu_sample"])
        .collect();
    json!({"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"required":required,"properties":properties}]})
}

#[cfg(any(feature = "bridge", test))]
fn output_schema(name: &str) -> Value {
    let status = json!({
        "type": "object", "additionalProperties": false,
        "required": ["state", "completed_steps", "ready", "finished", "failure", "graphics", "instancing"],
        "properties": {
            "state": {"type": "string", "enum": ["starting", "running", "stopping", "stopped", "failed"]},
            "completed_steps": {"type": "integer", "minimum": 0},
            "graphics": {"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
                "required":["presented_frames","last_outcome"],"properties":{
                    "presented_frames":{"type":"integer","minimum":0},
                    "last_outcome":{"type":"string","enum":["not_attempted","presented","zero_sized","timeout","occluded","initialization_failed","render_failed"]}}}]},
            "instancing": instancing_schema(),
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
        "instancing": status.instancing,
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
    fn instancing_report_retains_source_frame_without_claiming_presentation() {
        let (control, mut host) = control_channel();
        assert!(snapshot(control.status())["instancing"].is_null());
        host.instancing(crate::InstancingStatus {
            host_frame: 3,
            prepared_view: 2,
            submitted_instances: 100,
            foliage_upload_bytes: 800,
            influence_overflow: 4,
            visibility_dispatched_pages: 2,
            visibility_dispatches: 7,
            gpu_sample: Some(crate::GpuVisibilityStatus {
                prepared_view: 1,
                visible_instances: 70,
                candidate_instances: 100,
                indirect_draws: 2,
                age_ms: 40,
            }),
            ..Default::default()
        });
        host.progress(10);
        let report = snapshot(control.status());
        assert_eq!(report["instancing"]["host_frame"], 3);
        assert_eq!(report["instancing"]["submitted_instances"], 100);
        assert_eq!(report["instancing"]["foliage_upload_bytes"], 800);
        assert_eq!(report["instancing"]["influence_overflow"], 4);
        assert_eq!(report["instancing"]["visibility_dispatched_pages"], 2);
        assert_eq!(report["instancing"]["visibility_dispatches"], 7);
        assert_eq!(report["instancing"]["gpu_sample"]["prepared_view"], 1);
        assert_eq!(report["instancing"]["gpu_sample"]["visible_instances"], 70);
        assert_eq!(report["instancing"]["gpu_sample"]["age_ms"], 40);
        assert!(report["graphics"].is_null());
        assert_eq!(report["completed_steps"], 10);
        host.stopping();
        host.instancing(crate::InstancingStatus::default());
        assert_eq!(
            snapshot(control.status())["instancing"],
            report["instancing"]
        );
    }

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
