//! Render controls queue owned commands; no renderer access from tool threads.
use nico_ops::{
    HostControl,
    mcp::{CallToolResult, Tool, ToolAccess, ToolExtensions},
    rendering::{RenderAction, RenderInfluence, RenderMode},
    window::RequestState,
};
use serde_json::{Map, Value, json};

fn action(args: &Map<String, Value>) -> Option<RenderAction> {
    if args.len() != 2 {
        return None;
    }
    match args.get("action")?.as_str()? {
        "mode" => Some(RenderAction::Mode(match args.get("value")?.as_str()? {
            "auto" => RenderMode::Auto,
            "cpu" => RenderMode::Cpu,
            "gpu" => RenderMode::Gpu,
            _ => return None,
        })),
        "pause" => Some(RenderAction::Paused(args.get("value")?.as_bool()?)),
        "seek" => {
            Some(RenderAction::Seek(args.get("value")?.as_f64().filter(
                |t| t.is_finite() && (0. ..=1_000_000_000.).contains(t),
            )?))
        }
        "fields" => {
            let value = args.get("value")?;
            if value.is_null() {
                return Some(RenderAction::Fields(None));
            }
            let values = value.as_array()?;
            if values.len() > 256 {
                return None;
            }
            let vector = |value: &Value| -> Option<[f32; 3]> {
                let values = value.as_array()?;
                if values.len() != 3 {
                    return None;
                }
                let mut out = [0.; 3];
                for (out, value) in out.iter_mut().zip(values) {
                    *out = value.as_f64()? as f32;
                    if !out.is_finite() {
                        return None;
                    }
                }
                Some(out)
            };
            let fields = values
                .iter()
                .map(|value| {
                    let value = value.as_object()?;
                    if value.len() != 8 {
                        return None;
                    }
                    Some(RenderInfluence {
                        id: value.get("id")?.as_u64()?,
                        radial: match value.get("kind")?.as_str()? {
                            "wind" => false,
                            "radial" => true,
                            _ => return None,
                        },
                        position: vector(value.get("position")?)?,
                        direction: vector(value.get("direction")?)?,
                        radius: value.get("radius")?.as_f64()? as f32,
                        strength: value.get("strength")?.as_f64()? as f32,
                        start: value.get("start")?.as_f64()?,
                        end: value.get("end")?.as_f64()?,
                    })
                })
                .collect::<Option<Vec<_>>>()?;
            nico_ops::rendering::valid_influences(&fields)
                .then_some(RenderAction::Fields(Some(fields)))
        }
        _ => None,
    }
}
pub(crate) fn register(
    mut tools: ToolExtensions,
    control: HostControl,
) -> std::io::Result<ToolExtensions> {
    let reader = control.clone();
    tools.register(Tool::new("rendering_state","Read renderer mode, visual time, current-scene capabilities and snapshot age. Optional request_id reads one of the last 16 outcomes. Applied means the render owner applied the command, not GPU completion or display scanout.",
        json!({"type":"object","properties":{"request_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone()),move|args| {
        let error=|e:&str|CallToolResult::structured_error(json!({"error":e}));
        if args.keys().any(|k|k!="request_id") {return error("invalid_arguments");}
        let mut result=json!({});
        if let Some(id)=args.get("request_id") {
            let Some(id)=id.as_u64().filter(|id|*id>0) else {return error("invalid_arguments");};
            result["request_id"]=json!(id);
            match reader.rendering().read(id) {
                Ok(RequestState::Pending)=>result["request_state"]=json!("pending"),
                Ok(RequestState::Applied)=>result["request_state"]=json!("applied"),
                Ok(RequestState::Failed(e))=>{result["request_state"]=json!("failed");result["error"]=json!(e);},
                Err(e)=>return error(e),
            }
        }
        if let Some((state,age))=reader.rendering().state() {
            result["ready"]=json!(true);
            result["mode"]=json!(match state.mode {RenderMode::Auto=>"auto",RenderMode::Cpu=>"cpu",RenderMode::Gpu=>"gpu"});
            result["paused"]=json!(state.paused);
            result["visual_seconds"]=json!(state.visual_seconds);
            result["cpu_supported"]=json!(state.cpu_supported);
            result["gpu_supported"]=json!(state.gpu_supported);
            result["cpu_rejection"]=json!(state.cpu_rejection);
            result["gpu_rejection"]=json!(state.gpu_rejection);
            result["auto_gpu_min_records"]=json!(state.auto_gpu_min_records);
            result["auto_policy"]=json!("Prefer GPU at or above the record threshold per rendered segment when supported; prefer direct instancing below it. Fully visible segment bounds use resident direct instancing when its pipeline is available, bypassing GPU culling. If direct instancing is unavailable, try GPU regardless of threshold. Forced modes ignore this policy. Actual submitted paths are reported by status.instancing counters.");
            result["capabilities"]=json!(state.capabilities);
            result["host_frame"]=json!(state.host_frame);
            result["fields_overridden"]=json!(state.fields_overridden);
            result["influence_count"]=json!(state.influence_count);
            result["snapshot_age_ms"]=json!(age.as_millis().min(u64::MAX as u128) as u64);
        } else {result["ready"]=json!(false);}
        CallToolResult::structured(result)
    })?;
    tools.register(Tool::new("rendering_control","Queue mode selection, visual pause/resume, seek, or atomic diagnostic wind/radial field replacement. Fields use absolute visual seconds; [] disables fields and null restores scene fields. One pending command is allowed. Mode changes may rebuild residency. Poll rendering_state with request_id; acceptance is not application. Never blindly retry a timed-out mutation.",
        json!({"type":"object","oneOf":[
            {"properties":{"action":{"const":"mode"},"value":{"enum":["auto","cpu","gpu"]}},"required":["action","value"],"additionalProperties":false},
            {"properties":{"action":{"const":"pause"},"value":{"type":"boolean"}},"required":["action","value"],"additionalProperties":false},
            {"properties":{"action":{"const":"seek"},"value":{"type":"number","minimum":0,"maximum":1000000000}},"required":["action","value"],"additionalProperties":false},
            {"properties":{"action":{"const":"fields"},"value":{"description":"Atomic diagnostic field replacement. Empty array disables fields; null restores scene fields. Lifetime is in absolute visual seconds.","oneOf":[{"type":"null"},{"type":"array","maxItems":256,"items":{"type":"object","required":["id","kind","position","direction","radius","strength","start","end"],"additionalProperties":false,"properties":{
                "id":{"type":"integer","minimum":0},"kind":{"enum":["wind","radial"]},
                "position":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},
                "direction":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},
                "radius":{"type":"number","exclusiveMinimum":0},"strength":{"type":"number","minimum":0,"maximum":1},
                "start":{"type":"number"},"end":{"type":"number"}
            }}}]}},"required":["action","value"],"additionalProperties":false}
        ]}).as_object().unwrap().clone()),move|args| {
            let Some(action)=action(&args) else {return CallToolResult::structured_error(json!({"error":"invalid_arguments"}));};
            match control.request_rendering(action) {
                Ok(id)=>CallToolResult::structured(json!({"accepted":true,"request_id":id})),
                Err(e)=>CallToolResult::structured_error(json!({"error":e})),
            }
        })?;
    tools.set_access("rendering_state", ToolAccess::Inspect)?;
    Ok(tools)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn influence_commands_bound_payload_and_validate_every_field() {
        let field = json!({"id":1,"kind":"wind","position":[0,0,0],"direction":[1,0,0],"radius":5,"strength":0.8,"start":0,"end":10});
        let parse = |fields| {
            action(
                json!({"action":"fields","value":fields})
                    .as_object()
                    .unwrap(),
            )
        };
        assert!(matches!(
            parse(json!([field.clone()])),
            Some(RenderAction::Fields(Some(_)))
        ));
        assert_eq!(parse(Value::Null), Some(RenderAction::Fields(None)));
        assert_eq!(
            parse(json!([])),
            Some(RenderAction::Fields(Some(Vec::new())))
        );
        assert!(parse(json!([field.clone(), field.clone()])).is_none());
        assert!(parse(json!(vec![field.clone(); 257])).is_none());
        for (key, value) in [
            ("radius", json!(0)),
            ("direction", json!([0, 0, 0])),
            ("direction", json!([1e100, 0, 0])),
            ("end", json!(0)),
            ("strength", json!(2)),
            ("kind", json!("explosion")),
            ("extra", json!(0)),
        ] {
            let mut invalid = field.clone();
            invalid[key] = value;
            assert!(parse(json!([invalid])).is_none(), "{key}");
        }
    }
    #[test]
    fn rendering_actions_enforce_exact_shapes_and_bounds() {
        for invalid in [
            json!({}),
            json!({"action":"pause","value":1}),
            json!({"action":"mode","value":"nanite"}),
            json!({"action":"seek","value":-1}),
            json!({"action":"seek","value":1000000001_u64}),
            json!({"action":"pause","value":true,"extra":0}),
        ] {
            assert!(action(invalid.as_object().unwrap()).is_none());
        }
        assert_eq!(
            action(json!({"action":"mode","value":"gpu"}).as_object().unwrap()),
            Some(RenderAction::Mode(RenderMode::Gpu))
        );
        assert_eq!(
            action(json!({"action":"pause","value":true}).as_object().unwrap()),
            Some(RenderAction::Paused(true))
        );
        assert_eq!(
            action(json!({"action":"seek","value":0.25}).as_object().unwrap()),
            Some(RenderAction::Seek(0.25))
        );
    }
}
