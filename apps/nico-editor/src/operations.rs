use nico_ops::{
    commands::FifoCommands,
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "action", rename_all = "snake_case", deny_unknown_fields)]
pub enum Action {
    Inspect {
        asset: PathBuf,
    },
    Add {
        asset: PathBuf,
    },
    Transform {
        id: u64,
        position: [f32; 3],
        rotation: [f32; 3],
        scale: f32,
    },
    Remove {
        id: u64,
    },
    Select {
        id: u64,
    },
    Pan {
        offset: [f32; 3],
    },
    Camera {
        yaw: f32,
        pitch: f32,
        distance: f32,
    },
    Save,
    Reload,
    Refresh,
    Undo,
    Redo,
}
pub type Queue = FifoCommands<(u64, Action), Value, 32>;
pub type SharedQueue = Arc<Mutex<Queue>>;
pub type Published = Arc<Mutex<Publication<Value>>>;
fn error(message: impl ToString) -> CallToolResult {
    let mut result = CallToolResult::structured(json!({"error": message.to_string()}));
    result.is_error = Some(true);
    result
}
pub fn register(queue: SharedQueue, state: Published) -> std::io::Result<ToolExtensions> {
    let mut tools = ToolExtensions::default();
    tools.register(Tool::new("editor_state", "Inspect project sources, successful import revisions, errors, scene objects and command outcomes. Rendering and snapshot age are separate from host presentation counts.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()), move |args| {
        if !args.is_empty() { return error("no arguments accepted"); }
        state.lock().unwrap().json().map(CallToolResult::structured).unwrap_or_else(|| error("not_ready"))
    })?;
    let vector = json!({"type":"array","minItems":3,"maxItems":3,"items":{"type":"number","minimum":-10000,"maximum":10000}});
    tools.register(Tool::new("editor_command", "Queue an editor operation for its runtime update boundary. Assets are project-relative PNG/GLB paths. Save/reload use the manifest default scene, or scene.nico.json for loose content. Read editor_state for terminal command outcomes; acceptance is not completion.",
        json!({"type":"object","oneOf":[
            {"properties":{"action":{"enum":["save","reload","refresh","undo","redo"]}},"required":["action"],"additionalProperties":false},
            {"properties":{"action":{"enum":["add","inspect"]},"asset":{"type":"string","maxLength":1024}},"required":["action","asset"],"additionalProperties":false},
            {"properties":{"action":{"enum":["remove","select"]},"id":{"type":"integer","minimum":1}},"required":["action","id"],"additionalProperties":false},
            {"properties":{"action":{"const":"transform"},"id":{"type":"integer","minimum":1},"position":vector,"rotation":vector,"scale":{"type":"number","minimum":0.001,"maximum":1000}},"required":["action","id","position","rotation","scale"],"additionalProperties":false},
            {"properties":{"action":{"const":"pan"},"offset":vector},"required":["action","offset"],"additionalProperties":false},
            {"properties":{"action":{"const":"camera"},"yaw":{"type":"number","minimum":-100,"maximum":100},"pitch":{"type":"number","minimum":-1.4,"maximum":1.4},"distance":{"type":"number","minimum":0.1,"maximum":1000}},"required":["action","yaw","pitch","distance"],"additionalProperties":false}
        ]}).as_object().unwrap().clone()), move |args| {
        let action = match serde_json::from_value::<Action>(Value::Object(args)) { Ok(v) => v, Err(e) => return error(e) };
        match queue.lock().unwrap().submit(|id| (id, action)) {
            Ok(id) => CallToolResult::structured(json!({"accepted":true,"command_id":id})),
            Err(e) => error(format!("{e:?}")),
        }
    })?;
    Ok(tools)
}
