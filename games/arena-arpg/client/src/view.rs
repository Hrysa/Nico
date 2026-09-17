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
#[derive(Clone, Copy, Debug, PartialEq)]
enum Edit {
    Camera(f32, f32, Option<f32>),
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
struct ViewPlugin(
    Arc<Mutex<Operations>>,
    [Option<Arc<crate::character::CharacterAssets>>; 3],
    [crate::character::definition::VisualDefinition; 3],
    Arc<arena_arpg_shared::characters::CharacterCatalog>,
);
pub fn register_configured(
    builder: AppBuilder,
    tools: &mut ToolExtensions,
    character: [Option<Arc<crate::character::CharacterAssets>>; 3],
    definitions: [crate::character::definition::VisualDefinition; 3],
    logic: Arc<arena_arpg_shared::characters::CharacterCatalog>,
) -> std::io::Result<AppBuilder> {
    let assets = json!({"schema_version":1,"definitions":definitions,"hero_imported":character[0].is_some(),"hero_resolved":character[0].as_ref().map(|a| a.inspection()),"resolved":character.iter().map(|a|a.as_ref().map(|a|a.inspection())).collect::<Vec<_>>()});
    tools.register(Tool::new("client_characters", "Inspect validated character visual definitions loaded at startup. Paths are relative to --visual-characters; no live reload.", json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
        if !args.is_empty() { return error("invalid_arguments"); }
        CallToolResult::structured(assets.clone())
    })?;
    let ops = Arc::new(Mutex::new(Operations::default()));
    let observed = ops.clone();
    tools.register(Tool::new("client_state","Read the last published camera and drawing snapshot with age and closed state. Window fields describe that frame; use engine window_state for current host observations.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["snapshot_sequence","snapshot_age_ms","run_id","tick","last_applied_command","camera","focused","pointer_captured","capture_error","logical_size","mesh_draws","hud_quads","closed","cancelled_command_id","animation"],"properties":{
            "animation":crate::character::state_schema(),
            "actor_animations":{"type":"array","maxItems":4,"items":crate::character::state_schema()},
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
    tools.register(Tool::new("client_control","Queue a bounded camera request. Optional distance (0.5..12 metres) changes zoom; omission preserves it. Poll client_state.last_applied_command. Use engine window_control for pointer capture. Do not blindly retry timeouts.",
        json!({"type":"object","oneOf":[
            {"properties":{"action":{"const":"camera"},"yaw":{"type":"number","minimum":-std::f64::consts::PI,"maximum":std::f64::consts::PI},"pitch":{"type":"number","minimum":0.15,"maximum":1.1},"distance":{"type":"number","minimum":0.5,"maximum":12}},"required":["action","yaw","pitch"],"additionalProperties":false}
        ]}).as_object().unwrap().clone()).with_raw_output_schema(json!({"type":"object","required":["accepted","command_id"],"properties":{"accepted":{"const":true},"command_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone().into()),move |args| {
        let edit = match parse_edit(&args) { Ok(edit) => edit, Err(code) => return error(code) };
        let mut ops=sender.lock().unwrap();
        if ops.commands.is_closed(){return error("shutting_down");}
        let id = match ops.commands.submit(0, |id| (id, edit)) { Ok(id)=>id, Err(_)=>return error("busy") };
        CallToolResult::structured(json!({"accepted":true,"command_id":id}))
    })?;
    Ok(builder.add_plugin(ViewPlugin(ops, character, definitions, logic)))
}
fn parse_edit(args: &serde_json::Map<String, Value>) -> Result<Edit, &'static str> {
    if args.get("action").and_then(Value::as_str) != Some("camera")
        || args
            .keys()
            .any(|k| !matches!(k.as_str(), "action" | "yaw" | "pitch" | "distance"))
    {
        return Err("invalid_arguments");
    }
    let yaw = args
        .get("yaw")
        .and_then(Value::as_f64)
        .ok_or("invalid_arguments")?;
    let pitch = args
        .get("pitch")
        .and_then(Value::as_f64)
        .ok_or("invalid_arguments")?;
    if !yaw.is_finite()
        || !pitch.is_finite()
        || yaw.abs() > std::f64::consts::PI
        || !(0.15..=1.1).contains(&pitch)
    {
        return Err("invalid_arguments");
    }
    let distance = match args.get("distance") {
        None => None,
        Some(value) => match value.as_f64() {
            Some(v) if v.is_finite() && (0.5..=12.).contains(&v) => Some(v as f32),
            _ => return Err("invalid_arguments"),
        },
    };
    Ok(Edit::Camera(yaw as f32, pitch as f32, distance))
}
fn error(code: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code}}))
}
impl Plugin for ViewPlugin {
    fn build(&self, builder: &mut AppBuilder) -> RuntimeResult<()> {
        builder.insert_resource(Scene2d::default());
        builder.insert_resource(Scene3d::default());
        let ops = self.0.clone();
        let mut visuals = Visuals::configured(self.2.clone(), &self.3);
        visuals.imported = std::array::from_fn(|i| self.1[i].is_some());
        let assets = self.1.clone();
        let mut kinds = [None; 4];
        let mut characters: [Option<crate::character::Character>; 4] =
            std::array::from_fn(|_| None);
        builder.add_system(Stage::Update,"arena_client::extract",move |ctx| {
            let request = ops.lock().unwrap().commands[0].take();
            if let Some((_,edit))=request {
                let Edit::Camera(yaw,pitch,distance) = edit;
                let c=ctx.world.resource_mut::<Camera>()?;c.rig.set_angles(yaw,pitch);
                if let Some(distance) = distance { c.rig.set_distance(distance); }
            }
            let snapshot=ctx.world.resource::<Arena>()?.snapshot().clone();
            let window=ctx.world.resource::<NativeWindowState>().cloned().unwrap_or_default();
            let dt=ctx.time.delta().as_secs_f32();
            let camera=ctx.world.resource_mut::<Camera>()?;
            let view=camera.view([snapshot.actors[0].position.x as f32,snapshot.actors[0].position.z as f32],dt);
            let (mut scene,hud)=visuals.render(&snapshot,view,window.logical_size,window.pointer_captured,dt);
            for (i, actor) in snapshot.actors.iter().enumerate() {
                let index = match actor.kind { arena_arpg_shared::ActorKind::Hero => 0, arena_arpg_shared::ActorKind::Grunt => 1, arena_arpg_shared::ActorKind::Brute => 2 };
                if kinds[i] != Some(actor.kind) {
                    characters[i] = None;
                    kinds[i] = Some(actor.kind);
                }
                if let Some(assets) = &assets[index] {
                    let character = characters[i].get_or_insert_with(|| crate::character::Character::new(assets.clone()));
                    scene.meshes.extend(character.render_frame(&crate::character::CharacterFrame::arena_actor(&snapshot, i), ctx.time.delta(), view.view_projection(window.logical_size[0] / window.logical_size[1])).map_err(|e| nico_runtime::RuntimeError::System { stage: "Update", name: "arena_client::extract".into(), message:e.to_string() })?);
                }
            }
            if scene.meshes.len() > 256 { return Err(nico_runtime::RuntimeError::System { stage:"Update", name:"arena_client::extract".into(), message:"arena draw budget exceeded".into() }); }
            let mut ops=ops.lock().unwrap();
            if let Some((id,_))=request { ops.commands.record(id); }
            let last_applied = ops.commands.history().back().copied().unwrap_or(0);
            ops.snapshot.publish(json!({"closed":false,"cancelled_command_id":null,"run_id":snapshot.run_id,"tick":snapshot.tick,"last_applied_command":last_applied,
                "camera":{"position":view.position,"target":[snapshot.actors[0].position.x as f32,1.2,snapshot.actors[0].position.z as f32],"orientation":view.orientation.to_array(),"yaw":camera.rig.yaw(),"pitch":camera.rig.pitch(),"distance":camera.rig.distance()},
                "focused":window.focused,"pointer_captured":window.pointer_captured,"capture_error":window.capture_error,
                "animation":characters[0].as_ref().map(crate::character::Character::state),
                "actor_animations":characters.iter().map(|c|c.as_ref().map(crate::character::Character::state)).collect::<Vec<_>>(),
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
    fn camera_zoom_arguments_are_bounded_and_optional() {
        let mut args = json!({"action":"camera","yaw":0.,"pitch":0.4})
            .as_object()
            .unwrap()
            .clone();
        assert_eq!(parse_edit(&args), Ok(Edit::Camera(0., 0.4, None)));
        for distance in [json!(0.5), json!(12.)] {
            args.insert("distance".into(), distance);
            assert!(parse_edit(&args).is_ok());
        }
        for distance in [json!(0.), json!(12.1), json!(null), json!("1")] {
            args.insert("distance".into(), distance);
            assert!(parse_edit(&args).is_err());
        }
        args.remove("distance");
        args.insert("extra".into(), json!(1));
        assert!(parse_edit(&args).is_err());
    }
    #[test]
    fn queued_camera_edits_publish_and_shutdown_cancels_pending_request() {
        let ops = Arc::new(Mutex::new(Operations::default()));
        let mut app = AppBuilder::new()
            .add_plugin(ControlsPlugin)
            .add_plugin(ArenaPlugin)
            .add_plugin(ViewPlugin(
                ops.clone(),
                std::array::from_fn(|_| None),
                std::array::from_fn(crate::character::definition::VisualDefinition::builtin),
                arena_arpg_shared::characters::CharacterCatalog::builtin(),
            ))
            .build()
            .unwrap();
        app.start().unwrap();
        ops.lock()
            .unwrap()
            .commands
            .submit(0, |id| (id, Edit::Camera(0.4, 0.6, Some(1.5))))
            .unwrap();
        app.tick(FIXED_STEP).unwrap();
        let view = ops.lock().unwrap().snapshot.json().unwrap();
        assert_eq!(view["last_applied_command"], 1);
        assert_eq!(view["closed"], false);
        assert!((view["camera"]["yaw"].as_f64().unwrap() - 0.4).abs() < 1e-6);
        assert!((view["camera"]["distance"].as_f64().unwrap() - 1.5).abs() < 1e-6);
        ops.lock()
            .unwrap()
            .commands
            .submit(0, |id| (id, Edit::Camera(0.2, 0.5, None)))
            .unwrap();
        app.shutdown().unwrap();
        let ops = ops.lock().unwrap();
        assert!(ops.commands.is_closed());
        assert_eq!(ops.snapshot.json().unwrap()["cancelled_command_id"], 2);
        assert_eq!(ops.snapshot.json().unwrap()["closed"], true);
    }
}
