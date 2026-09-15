//! Native window tools; handlers never access platform windows or the application.
use nico_ops::{
    HostControl,
    mcp::{CallToolResult, Tool, ToolExtensions},
    window::{RequestState, WindowAction},
};
use serde_json::{Map, Value, json};
use std::io;

fn action(args: &Map<String, Value>) -> Option<WindowAction> {
    let name = args.get("action")?.as_str()?;
    if name == "resize" {
        if args.len() != 3 {
            return None;
        }
        let width = args
            .get("width")?
            .as_u64()
            .filter(|v| (320..=3840).contains(v))? as u32;
        let height = args
            .get("height")?
            .as_u64()
            .filter(|v| (240..=2160).contains(v))? as u32;
        Some(WindowAction::Resize { width, height })
    } else if name == "pointer_capture" {
        if args.len() != 2 {
            return None;
        }
        Some(WindowAction::PointerCapture {
            value: args.get("value")?.as_bool()?,
        })
    } else {
        if args.len() != 1 {
            return None;
        }
        match name {
            "maximize" => Some(WindowAction::Maximize),
            "minimize" => Some(WindowAction::Minimize),
            "restore" => Some(WindowAction::Restore),
            "focus" => Some(WindowAction::Focus),
            _ => None,
        }
    }
}

pub(crate) fn register(
    mut tools: ToolExtensions,
    control: HostControl,
) -> io::Result<ToolExtensions> {
    let reader = control.clone();
    tools.register(Tool::new("window_state", "Read observed native window state and snapshot age, optionally the latest request outcome. Applied means the platform call returned, not that the window manager honored it. Minimized may be null on unsupported platforms.", json!({"type":"object","properties":{"request_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
        let error = |e: &str| CallToolResult::structured_error(json!({"error":e}));
        if args.keys().any(|k| k != "request_id") { return error("invalid_arguments"); }
        let mut result = json!({});
        if let Some(value) = args.get("request_id") {
            let Some(id) = value.as_u64().filter(|id| *id > 0) else { return error("invalid_arguments"); };
            result["request_id"] = json!(id);
            match reader.window().read(id) {
                Ok(RequestState::Pending) => result["request_state"] = json!("pending"),
                Ok(RequestState::Applied) => result["request_state"] = json!("applied"),
                Ok(RequestState::Failed(e)) => { result["request_state"] = json!("failed"); result["error"] = json!(e); }
                Err(e) => return error(e),
            }
        }
        let Some((state, age)) = reader.window().state() else { return error("window_not_ready"); };
        result["snapshot_age_ms"] = json!(age.as_millis().min(u64::MAX as u128) as u64);
        result["physical_size"] = json!(state.physical_size);
        result["logical_size"] = json!(state.logical_size);
        result["focused"] = json!(state.focused);
        result["minimized"] = json!(state.minimized);
        result["maximized"] = json!(state.maximized);
        result["pointer_captured"] = json!(state.pointer_captured);
        CallToolResult::structured(result)
    })?;
    tools.register(Tool::new("window_control", "Queue one native window operation. Resize uses logical pixels. Restore clears minimization and maximization. Focus requests foreground activation. Pointer capture requires an enabled, active, focused window; release is allowed while inactive. Poll window_state with request_id and verify observed state. Only the latest outcome is retained; never blindly retry a timed-out mutation. Use stop for orderly shutdown.", json!({"type":"object","oneOf":[{"properties":{"action":{"const":"pointer_capture"},"value":{"type":"boolean"}},"required":["action","value"],"additionalProperties":false},{"properties":{"action":{"const":"resize"},"width":{"type":"integer","minimum":320,"maximum":3840},"height":{"type":"integer","minimum":240,"maximum":2160}},"required":["action","width","height"],"additionalProperties":false},{"properties":{"action":{"enum":["maximize","minimize","restore","focus"]}},"required":["action"],"additionalProperties":false}]}).as_object().unwrap().clone()), move |args| {
        let Some(action) = action(&args) else { return CallToolResult::structured_error(json!({"error":"invalid_arguments"})); };
        match control.request_window(action) {
            Ok(id) => CallToolResult::structured(json!({"request_id":id,"accepted":true})),
            Err(e) => CallToolResult::structured_error(json!({"error":e})),
        }
    })?;
    Ok(tools)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn window_actions_reject_unknown_fields_invalid_sizes_and_fractional_values() {
        for value in [
            json!({}),
            json!({"action":"close"}),
            json!({"action":"minimize","width":800}),
            json!({"action":"resize","width":800}),
            json!({"action":"resize","width":0,"height":600}),
            json!({"action":"resize","width":800.5,"height":600}),
            json!({"action":"pointer_capture"}),
            json!({"action":"pointer_capture","value":1}),
            json!({"action":"pointer_capture","value":true,"extra":0}),
        ] {
            assert!(action(value.as_object().unwrap()).is_none(), "{value}");
        }
        assert_eq!(
            action(
                json!({"action":"resize","width":800,"height":600})
                    .as_object()
                    .unwrap()
            ),
            Some(WindowAction::Resize {
                width: 800,
                height: 600
            })
        );
        assert_eq!(
            action(json!({"action":"restore"}).as_object().unwrap()),
            Some(WindowAction::Restore)
        );
        for value in [false, true] {
            assert_eq!(
                action(
                    json!({"action":"pointer_capture","value":value})
                        .as_object()
                        .unwrap()
                ),
                Some(WindowAction::PointerCapture { value })
            );
        }
    }
}
