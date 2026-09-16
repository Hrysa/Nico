use crate::model::Character;
use glam::Vec3;
use nico_animation::{humanoid::RootMotion, playback::PlayMode};
use nico_input::{InputDeviceKind as Kind, InputState};
use nico_ops::{
    commands::FifoCommands,
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use nico_winit::keyboard;
use serde_json::{Map, Value, json};
use std::sync::{Arc, Mutex};
use std::time::Duration;
pub struct Input {
    pub toggle: bool,
    pub reset: bool,
    pub orbit: [f32; 2],
    pub zoom: f32,
    pub clip_step: i8,
}
pub fn map_input(input: &InputState, out: &mut Vec<Input>) {
    let held = |k| input.button(Kind::Keyboard, k) as u8 as f32;
    out.push(Input {
        toggle: input.just_pressed(Kind::Keyboard, keyboard::SPACE),
        reset: input.just_pressed(Kind::Keyboard, keyboard::R),
        orbit: [
            held(keyboard::RIGHT) - held(keyboard::LEFT),
            held(keyboard::UP) - held(keyboard::DOWN),
        ],
        zoom: held(keyboard::S) - held(keyboard::W),
        clip_step: input.just_pressed(Kind::Keyboard, keyboard::D) as i8
            - input.just_pressed(Kind::Keyboard, keyboard::A) as i8,
    });
}
pub struct Orbit {
    pub yaw: f32,
    pub pitch: f32,
    pub distance: f32,
    pub target: Vec3,
}
impl Default for Orbit {
    fn default() -> Self {
        Self {
            yaw: 0.,
            pitch: 0.1,
            distance: 3.,
            target: Vec3::ZERO,
        }
    }
}
#[derive(Debug)]
pub enum Action {
    Playing(bool),
    Seek(f32),
    Speed(f32),
    Clip(usize),
    Bind(bool),
    InPlace(bool),
    Looping(bool),
    Fade(f32),
    Camera([f32; 3]),
    Select(usize),
    Position([f32; 3]),
    Target([f32; 3]),
    UpdateHz(f32),
}
pub type Queue = FifoCommands<(u64, Action), Value, 32>;
fn number(args: &Map<String, Value>, key: &str, min: f32, max: f32) -> Result<f32, &'static str> {
    let v = args
        .get(key)
        .and_then(Value::as_f64)
        .ok_or("missing number")? as f32;
    if !v.is_finite() || v < min || v > max {
        return Err("number outside bounds");
    }
    Ok(v)
}
fn parse(args: &Map<String, Value>) -> Result<Action, &'static str> {
    let action = args
        .get("action")
        .and_then(Value::as_str)
        .ok_or("missing action")?;
    if action == "camera" && args.len() == 4 {
        return Ok(Action::Camera([
            number(args, "yaw", -100., 100.)?,
            number(args, "pitch", -1.4, 1.4)?,
            number(args, "distance", 0.1, 100.)?,
        ]));
    }
    if matches!(action, "position" | "target") && args.len() == 4 {
        let value = [
            number(args, "x", -1000., 1000.)?,
            number(args, "y", -1000., 1000.)?,
            number(args, "z", -1000., 1000.)?,
        ];
        return Ok(if action == "position" {
            Action::Position(value)
        } else {
            Action::Target(value)
        });
    }
    if args.len() != 2 {
        return Err("expected action and value");
    }
    Ok(match action {
        "playing" | "bind_pose" | "in_place" | "looping" => {
            let v = args
                .get("value")
                .and_then(Value::as_bool)
                .ok_or("expected boolean")?;
            match action {
                "playing" => Action::Playing(v),
                "bind_pose" => Action::Bind(v),
                "looping" => Action::Looping(v),
                _ => Action::InPlace(v),
            }
        }
        "seek" => Action::Seek(number(args, "value", 0., 86400.)?),
        "update_hz" => {
            let v = args
                .get("value")
                .and_then(Value::as_f64)
                .ok_or("missing number")?;
            if v != 0. && !(1. ..=120.).contains(&v) {
                return Err("update rate must be zero or 1..120 Hz");
            }
            Action::UpdateHz(v as f32)
        }
        "select" => Action::Select(
            usize::try_from(
                args.get("value")
                    .and_then(Value::as_u64)
                    .ok_or("expected instance index")?,
            )
            .map_err(|_| "instance index too large")?,
        ),
        "speed" => Action::Speed(number(args, "value", 0., 4.)?),
        "fade_seconds" => Action::Fade(number(args, "value", 0., 5.)?),
        "clip" => Action::Clip(
            usize::try_from(
                args.get("value")
                    .and_then(Value::as_u64)
                    .ok_or("expected clip index")?,
            )
            .map_err(|_| "clip index too large")?,
        ),
        _ => return Err("unknown action"),
    })
}
pub fn apply(action: Action, c: &mut Character, camera: &mut Orbit) -> Result<(), String> {
    match action {
        Action::Select(_) => return Err("instance selection is owned by the preview".into()),
        Action::Position(value) => c.position = value,
        Action::Target(value) => camera.target = value.into(),
        Action::UpdateHz(value) => {
            if value != 0. && value < 1. {
                return Err("update rate must be zero or at least 1 Hz".into());
            }
            c.evaluation_interval = if value == 0. {
                Duration::ZERO
            } else {
                Duration::from_secs_f64(1. / f64::from(value))
            };
        }
        Action::Playing(v) => c.player.set_paused(!v),
        Action::Speed(v) => c
            .player
            .set_speed(f64::from(v))
            .map_err(|e| e.to_string())?,
        Action::Fade(v) => c.fade_seconds = f64::from(v),
        Action::Looping(v) => {
            c.play_mode = if v { PlayMode::Loop } else { PlayMode::Once };
            c.player.set_mode(c.play_mode);
        }
        Action::Bind(v) => c.bind_pose = v,
        Action::InPlace(v) => {
            if !c.assets.retargeted {
                return Err("root-motion policy requires humanoid retargeting".into());
            }
            c.player
                .set_root_motion(if v {
                    RootMotion::InPlace
                } else {
                    RootMotion::Preserve
                })
                .map_err(|e| e.to_string())?;
            c.in_place = v;
        }
        Action::Seek(v) => {
            c.player.seek(f64::from(v)).map_err(|e| e.to_string())?;
            c.player.set_paused(true);
        }
        Action::Clip(v) => {
            c.player
                .play(v, c.play_mode, Duration::from_secs_f64(c.fade_seconds))
                .map_err(|e| e.to_string())?;
            c.bind_pose = false;
        }
        Action::Camera([yaw, pitch, distance]) => {
            camera.yaw = yaw;
            camera.pitch = pitch;
            camera.distance = distance;
        }
    }
    c.invalidate();
    Ok(())
}
fn error(message: impl ToString) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":message.to_string()}))
}
pub fn register(
    queue: Arc<Mutex<Queue>>,
    publication: Arc<Mutex<Publication<Value>>>,
) -> std::io::Result<ToolExtensions> {
    let mut tools = ToolExtensions::default();
    tools.register(Tool::new("preview_state","Read preview state, wall-clock Update FPS and interval statistics, available clips and terminal command results. Frame timing is null during the first measurement window; it is neither CPU execution time nor GPU frame rate. Host status reports presentation API counts, not GPU completion.",json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["snapshot_sequence","snapshot_age_ms","closed","mode","time","playing","frame_timing","command_results","looping","finished","fade_seconds","fade_weight","render_bounds","visible","selected","instance_count","visible_count","evaluated_instances","instances"],"properties":{
            "snapshot_sequence":{"type":"integer","minimum":1},"snapshot_age_ms":{"type":"integer","minimum":0},"closed":{"type":"boolean"},
            "mode":{"const":"gpu_skinned_preview"},"time":{"type":"number","minimum":0},"playing":{"type":"boolean"},
            "looping":{"type":"boolean"},"finished":{"type":"boolean"},"fade_seconds":{"type":"number","minimum":0,"maximum":5},"fade_weight":{"type":["number","null"],"minimum":0,"maximum":1},
            "selected":{"type":"integer","minimum":0,"maximum":63},"instance_count":{"type":"integer","minimum":1,"maximum":64},"visible_count":{"type":"integer","minimum":0,"maximum":64},"evaluated_instances":{"type":"integer","minimum":0,"maximum":64},
            "instances":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"object","required":["id","position","clip","time","pending_seconds","playing","finished","visible","render_bounds","update_hz"],"properties":{"id":{"type":"integer","minimum":0,"maximum":63},"position":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},"clip":{"type":["integer","null"],"minimum":0},"time":{"type":"number","minimum":0},"pending_seconds":{"type":"number","minimum":0},"playing":{"type":"boolean"},"finished":{"type":"boolean"},"visible":{"type":"boolean"},"render_bounds":{"type":"object"},"update_hz":{"type":"number","minimum":0,"maximum":121}},"additionalProperties":false}},
            "visible":{"type":"boolean"},
            "render_bounds":{"type":"object","required":["min","max"],"additionalProperties":false,"properties":{"min":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},"max":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}}}},
            "frame_timing":{"anyOf":[{"type":"null"},{"type":"object","required":["update_fps","mean_frame_ms","min_frame_ms","max_frame_ms","sample_count","window_seconds"],"properties":{
                "update_fps":{"type":"number","minimum":0},"mean_frame_ms":{"type":"number","minimum":0},"min_frame_ms":{"type":"number","minimum":0},"max_frame_ms":{"type":"number","minimum":0},"sample_count":{"type":"integer","minimum":1},"window_seconds":{"type":"number","minimum":1}
            },"additionalProperties":false}]},
            "command_results":{"type":"array","maxItems":32,"items":{"type":"object","required":["command_id","error"],"properties":{"command_id":{"type":"integer","minimum":1},"error":{"type":["string","null"]}},"additionalProperties":false}}
        }}).as_object().unwrap().clone().into()),move |args| {
        if !args.is_empty() { return error("no arguments accepted"); }
        publication.lock().unwrap().json().map(CallToolResult::structured).unwrap_or_else(||error("not ready"))
    })?;
    tools.register(Tool::new("preview_control","Queue an edit for the selected instance at Update. select changes the instance; position moves it; target moves the camera target. update_hz=0 evaluates every Update, or 1..120 caps evaluation while preserving elapsed time. Seek pauses playback. Clip selects across the loaded animation set with fade_seconds crossfade. looping=false holds the last frame and reports finished. Inspect preview_state for application; do not retry timed-out commands blindly.",json!({"type":"object","oneOf":[
        {"required":["action","value"],"properties":{"action":{"enum":["playing","bind_pose","in_place","looping"]},"value":{"type":"boolean"}},"additionalProperties":false},
        {"required":["action","value"],"properties":{"action":{"const":"seek"},"value":{"type":"number","minimum":0,"maximum":86400}},"additionalProperties":false},
        {"required":["action","value"],"properties":{"action":{"const":"speed"},"value":{"type":"number","minimum":0,"maximum":4}},"additionalProperties":false},
        {"required":["action","value"],"properties":{"action":{"const":"fade_seconds"},"value":{"type":"number","minimum":0,"maximum":5}},"additionalProperties":false},
        {"required":["action","value"],"properties":{"action":{"const":"select"},"value":{"type":"integer","minimum":0,"maximum":63}},"additionalProperties":false},
        {"required":["action","value"],"properties":{"action":{"const":"update_hz"},"value":{"anyOf":[{"const":0},{"type":"number","minimum":1,"maximum":120}]}},"additionalProperties":false},
        {"required":["action","x","y","z"],"properties":{"action":{"enum":["position","target"]},"x":{"type":"number","minimum":-1000,"maximum":1000},"y":{"type":"number","minimum":-1000,"maximum":1000},"z":{"type":"number","minimum":-1000,"maximum":1000}},"additionalProperties":false},
        {"required":["action","value"],"properties":{"action":{"const":"clip"},"value":{"type":"integer","minimum":0}},"additionalProperties":false},
        {"required":["action","yaw","pitch","distance"],"properties":{"action":{"const":"camera"},"yaw":{"type":"number","minimum":-100,"maximum":100},"pitch":{"type":"number","minimum":-1.4,"maximum":1.4},"distance":{"type":"number","minimum":0.1,"maximum":100}},"additionalProperties":false}
    ]}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["accepted","command_id"],"properties":{"accepted":{"const":true},"command_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone().into()),move |args| {
        let action=match parse(&args) {Ok(v)=>v,Err(e)=>return error(e)};
        match queue.lock().unwrap().submit(|id|(id,action)) {Ok(id)=>CallToolResult::structured(json!({"accepted":true,"command_id":id})),Err(e)=>error(format!("{e:?}"))}
    })?;
    Ok(tools)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn control_rejects_unknown_fields_invalid_types_and_ranges() {
        for v in [
            json!({"action":"update_hz","value":0.1}),
            json!({"action":"update_hz","value":1e-50}),
            json!({"action":"position","x":0,"y":0,"z":1001}),
            json!({"action":"select","value":-1}),
            json!({"action":"speed","value":5}),
            json!({"action":"seek","value":-1}),
            json!({"action":"clip","value":0.5}),
            json!({"action":"playing","value":true,"extra":1}),
            json!({"action":"camera","yaw":0,"pitch":2,"distance":1}),
        ] {
            assert!(parse(v.as_object().unwrap()).is_err());
        }
        assert!(matches!(
            parse(json!({"action":"seek","value":1.5}).as_object().unwrap()),
            Ok(Action::Seek(1.5))
        ));
    }
}
