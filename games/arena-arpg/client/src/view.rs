use crate::{camera::Camera, visuals::Visuals};
use arena_arpg_shared::Arena;
use nico_ops::{
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use nico_presentation::{Scene2d, Scene3d};
use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage};
use nico_winit::NativeWindowState;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
#[derive(Clone, Copy)]
enum Edit {
    Camera(f32, f32),
}
struct Operations {
    commands: nico_ops::commands::CommandBook<(u64, Edit), u64, 1>,
    snapshot: Publication<Value>,
}
impl Default for Operations {
    fn default() -> Self {
        Self {
            commands: nico_ops::commands::CommandBook::new(1),
            snapshot: Publication::default(),
        }
    }
}
struct ViewPlugin(Arc<Mutex<Operations>>);
pub fn register(builder: AppBuilder, tools: &mut ToolExtensions) -> std::io::Result<AppBuilder> {
    let ops = Arc::new(Mutex::new(Operations::default()));
    let observed = ops.clone();
    tools.register(Tool::new("client_state","Read the last published camera and drawing snapshot with age and closed state. Window fields describe that frame; use engine window_state for current host observations.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["snapshot_sequence","snapshot_age_ms","run_id","tick","last_applied_command","camera","focused","pointer_captured","capture_error","logical_size","mesh_draws","hud_quads","closed","cancelled_command_id"],"properties":{
            "snapshot_sequence":{"type":"integer","minimum":1},"snapshot_age_ms":{"type":"integer","minimum":0},
            "closed":{"type":"boolean"},"cancelled_command_id":{"type":["integer","null"]},
            "run_id":{"type":"integer"},"tick":{"type":"integer"},"last_applied_command":{"type":"integer"},
            "camera":{"type":"object","required":["position","target","orientation","yaw","pitch","distance"],"properties":{
                "position":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},"target":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},
                "orientation":{"type":"array","minItems":4,"maxItems":4,"items":{"type":"number"}},
                "yaw":{"type":"number"},"pitch":{"type":"number"},"distance":{"type":"number"}},"additionalProperties":false},
            "focused":{"type":"boolean"},"pointer_captured":{"type":"boolean"},"capture_error":{"type":["string","null"]},
            "logical_size":{"type":"array","minItems":2,"maxItems":2,"items":{"type":"number"}},"mesh_draws":{"type":"integer"},"hud_quads":{"type":"integer"}},"additionalProperties":false}).as_object().unwrap().clone().into()),move |args| {
        if !args.is_empty() {return error("invalid_arguments");}
        observed.lock().unwrap().snapshot.json().map(CallToolResult::structured).unwrap_or_else(||error("not_ready"))
    })?;
    let sender = ops.clone();
    tools.register(Tool::new("client_control","Queue a bounded camera request. Poll client_state.last_applied_command. Use engine window_control for pointer capture. Do not blindly retry timeouts.",
        json!({"type":"object","oneOf":[
            {"properties":{"action":{"const":"camera"},"yaw":{"type":"number","minimum":-std::f64::consts::PI,"maximum":std::f64::consts::PI},"pitch":{"type":"number","minimum":0.15,"maximum":1.1}},"required":["action","yaw","pitch"],"additionalProperties":false}
        ]}).as_object().unwrap().clone()).with_raw_output_schema(json!({"type":"object","required":["accepted","command_id"],"properties":{"accepted":{"const":true},"command_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone().into()),move |args| {
        let edit=match args.get("action").and_then(Value::as_str) {
            Some("camera") if args.len()==3=>{
                let (Some(yaw),Some(pitch))=(args.get("yaw").and_then(Value::as_f64),args.get("pitch").and_then(Value::as_f64)) else {return error("invalid_arguments");};
                if !yaw.is_finite() || !pitch.is_finite() || yaw.abs()>std::f64::consts::PI || !(0.15..=1.1).contains(&pitch) {return error("invalid_arguments");}
                Edit::Camera(yaw as f32,pitch as f32)
            },
            _=>return error("invalid_arguments"),
        };
        let mut ops=sender.lock().unwrap();
        if ops.commands.is_closed(){return error("shutting_down");}
        let id = match ops.commands.submit(0, |id| (id, edit)) { Ok(id)=>id, Err(_)=>return error("busy") };
        CallToolResult::structured(json!({"accepted":true,"command_id":id}))
    })?;
    Ok(builder.add_plugin(ViewPlugin(ops)))
}
fn error(code: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code}}))
}
impl Plugin for ViewPlugin {
    fn build(&self, builder: &mut AppBuilder) -> RuntimeResult<()> {
        builder.insert_resource(Scene2d::default());
        builder.insert_resource(Scene3d::default());
        let ops = self.0.clone();
        let mut visuals = Visuals::new();
        builder.add_system(Stage::Update,"arena_client::extract",move |ctx| {
            let request = ops.lock().unwrap().commands[0].take();
            if let Some((_,edit))=request {
                let Edit::Camera(yaw,pitch) = edit;
                let c=ctx.world.resource_mut::<Camera>()?;c.rig.set_angles(yaw,pitch);
            }
            let snapshot=ctx.world.resource::<Arena>()?.snapshot().clone();
            let window=ctx.world.resource::<NativeWindowState>().cloned().unwrap_or_default();
            let dt=ctx.time.delta().as_secs_f32();
            let camera=ctx.world.resource_mut::<Camera>()?;
            let view=camera.view([snapshot.actors[0].position.x as f32,snapshot.actors[0].position.z as f32],dt);
            let (scene,hud)=visuals.render(&snapshot,view,window.logical_size,window.pointer_captured,dt);
            let mut ops=ops.lock().unwrap();
            if let Some((id,_))=request { ops.commands.record(id); }
            let last_applied = ops.commands.history().back().copied().unwrap_or(0);
            ops.snapshot.publish(json!({"closed":false,"cancelled_command_id":null,"run_id":snapshot.run_id,"tick":snapshot.tick,"last_applied_command":last_applied,
                "camera":{"position":view.position,"target":[snapshot.actors[0].position.x as f32,1.2,snapshot.actors[0].position.z as f32],"orientation":view.orientation.to_array(),"yaw":camera.rig.yaw(),"pitch":camera.rig.pitch(),"distance":camera.rig.distance()},
                "focused":window.focused,"pointer_captured":window.pointer_captured,"capture_error":window.capture_error,
                "logical_size":window.logical_size,"mesh_draws":scene.meshes.len(),"hud_quads":hud.hud.len()}));
            *ctx.world.resource_mut::<Scene3d>()?=scene;*ctx.world.resource_mut::<Scene2d>()?=hud;Ok(())
        });
        let ops = self.0.clone();
        builder.add_system(Stage::Shutdown, "arena_client::close", move |ctx| {
            let mut ops = ops.lock().unwrap();
            ops.commands.close();
            let cancelled = ops.commands[0].take().map(|(id, _)| id);
            let mut snapshot = ops.snapshot.get().cloned().unwrap_or_else(|| json!({}));
            snapshot["cancelled_command_id"] = json!(cancelled);
            if ops.snapshot.get().is_some() {
                ops.snapshot.publish(snapshot);
            }
            ops.snapshot.close();
            *ctx.world.resource_mut::<Scene3d>()? = Scene3d::default();
            *ctx.world.resource_mut::<Scene2d>()? = Scene2d::default();
            Ok(())
        });
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::controls::ControlsPlugin;
    use arena_arpg_shared::{ArenaPlugin, FIXED_STEP};
    #[test]
    fn queued_camera_edits_publish_and_shutdown_cancels_pending_request() {
        let ops = Arc::new(Mutex::new(Operations::default()));
        let mut app = AppBuilder::new()
            .add_plugin(ControlsPlugin)
            .add_plugin(ArenaPlugin)
            .add_plugin(ViewPlugin(ops.clone()))
            .build()
            .unwrap();
        app.start().unwrap();
        ops.lock()
            .unwrap()
            .commands
            .submit(0, |id| (id, Edit::Camera(0.4, 0.6)))
            .unwrap();
        app.tick(FIXED_STEP).unwrap();
        let view = ops.lock().unwrap().snapshot.json().unwrap();
        assert_eq!(view["last_applied_command"], 1);
        assert_eq!(view["closed"], false);
        assert!((view["camera"]["yaw"].as_f64().unwrap() - 0.4).abs() < 1e-6);
        ops.lock()
            .unwrap()
            .commands
            .submit(0, |id| (id, Edit::Camera(0.2, 0.5)))
            .unwrap();
        app.shutdown().unwrap();
        let ops = ops.lock().unwrap();
        assert!(ops.commands.is_closed());
        assert_eq!(ops.snapshot.json().unwrap()["cancelled_command_id"], 2);
        assert_eq!(ops.snapshot.json().unwrap()["closed"], true);
    }
}
