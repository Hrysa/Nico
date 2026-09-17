//! World server runtime and MCP adapter. Tool handlers never borrow the world.
use super::{ObjectKind, Vec2, server::WorldServer};
use nico_ops::{
    commands::FifoCommands,
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use nico_runtime::{AppBuilder, RuntimeError, Stage};
use serde_json::{Value, json};
use std::{
    io,
    sync::{Arc, Mutex},
};
#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct Spawn {
    kind: ObjectKind,
    x: f64,
    z: f64,
}
struct Operations {
    commands: FifoCommands<(u64, Spawn), Value, 32>,
    snapshot: Publication<Value>,
}
fn error(code: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code}}))
}
fn runtime(error: impl std::fmt::Display) -> RuntimeError {
    RuntimeError::System {
        stage: "FixedUpdate",
        name: "open_world::server".into(),
        message: error.to_string(),
    }
}
fn publish(server: &WorldServer, ops: &mut Operations) {
    let players: Vec<_> = server
        .world
        .records()
        .iter()
        .filter_map(|(id, _)| server.world.snapshot(*id).ok())
        .collect();
    ops.snapshot.publish(json!({"tick":server.world.tick(),"address":server.address().ok().map(|a|a.to_string()),"zone":server.world.zone,"item":server.world.item,"objects":server.world.objects(),"players":players,"sessions":server.sessions(),"last_save_tick":server.last_save_tick,"commands":ops.commands.history()}));
}
pub fn register(
    mut builder: AppBuilder,
    server: WorldServer,
) -> io::Result<(AppBuilder, ToolExtensions)> {
    let ops = Arc::new(Mutex::new(Operations {
        commands: FifoCommands::new(128),
        snapshot: Publication::default(),
    }));
    publish(&server, &mut ops.lock().unwrap());
    let mut tools = ToolExtensions::default();
    let reader = ops.clone();
    tools.register(Tool::new("world_state","Inspect published authoritative zone, sessions, entities, per-player interest views and command outcomes. Snapshot age and closed state are explicit.",json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()),move|args|{
        if !args.is_empty(){return error("invalid_arguments");}
        reader.lock().unwrap().snapshot.json().map(CallToolResult::structured).unwrap_or_else(||error("not_ready"))
    })?;
    let sender = ops.clone();
    tools.register(Tool::new("world_spawn","Queue a monster spawn at a fixed boundary. Poll world_state.commands for the command_id; acceptance does not mean spawn succeeded. Never blindly retry timeouts.",json!({"type":"object","properties":{"kind":{"enum":["grunt","brute"]},"x":{"type":"number","minimum":-60,"maximum":60},"z":{"type":"number","minimum":-60,"maximum":60}},"required":["kind","x","z"],"additionalProperties":false}).as_object().unwrap().clone()),move|args|{
        let Ok(request)=serde_json::from_value::<Spawn>(Value::Object(args)) else{return error("invalid_arguments");};
        if !matches!(request.kind,ObjectKind::Grunt|ObjectKind::Brute)||!request.x.is_finite()||!request.z.is_finite()||request.x.abs()>60.||request.z.abs()>60.{return error("invalid_arguments");}
        let mut ops=sender.lock().unwrap();if ops.commands.is_closed(){return error("shutting_down");}
        match ops.commands.submit(|id|(id,request)){Ok(id)=>CallToolResult::structured(json!({"accepted":true,"command_id":id})),Err(_)=>error("busy")}
    })?;
    builder.insert_resource(ops.clone());
    builder.insert_resource(server);
    let shared = ops.clone();
    builder.add_system(Stage::FixedUpdate, "open_world::server", move |ctx| {
        if ctx.time.delta() != crate::FIXED_STEP {
            return Err(runtime("world server requires 60 Hz"));
        }
        let server = ctx.world.resource_mut::<WorldServer>()?;
        let mut ops = shared.lock().unwrap();
        while let Some((id, request)) = ops.commands.pop() {
            let result = server
                .world
                .spawn_monster(request.kind, Vec2::new(request.x, request.z));
            ops.commands.record(match result {
                Ok(entity) => json!({"command_id":id,"state":"completed","entity":entity}),
                Err(code) => json!({"command_id":id,"state":"rejected","error":code}),
            });
        }
        server.step().map_err(runtime)?;
        publish(server, &mut ops);
        Ok(())
    });
    builder.add_system(Stage::Shutdown, "open_world::save", move |ctx| {
        let server = ctx.world.resource_mut::<WorldServer>()?;
        let result = server.shutdown();
        let mut ops = ops.lock().unwrap();
        ops.commands
            .close_with(|(id, _)| json!({"command_id":id,"state":"cancelled","error":"shutdown"}));
        publish(server, &mut ops);
        ops.snapshot.close();
        result.map_err(|error| RuntimeError::System {
            stage: "Shutdown",
            name: "open_world::save".into(),
            message: error.to_string(),
        })
    });
    Ok((builder, tools))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{FIXED_STEP, characters::CharacterCatalog, open_world::OpenWorld};

    #[test]
    fn spawn_commands_apply_at_fixed_boundaries_and_shutdown_cancels_pending_work() {
        let root = std::env::temp_dir().join(format!(
            "nico-world-runtime-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let server = WorldServer::bind(
            "127.0.0.1:0".parse().unwrap(),
            &root,
            OpenWorld::new(CharacterCatalog::builtin()),
        )
        .unwrap();
        let (builder, _tools) =
            register(AppBuilder::new().with_fixed_step(FIXED_STEP), server).unwrap();
        let mut app = builder.build().unwrap();
        app.start().unwrap();
        let ops = app
            .world()
            .resource::<Arc<Mutex<Operations>>>()
            .unwrap()
            .clone();
        let enqueue = || {
            ops.lock()
                .unwrap()
                .commands
                .submit(|id| {
                    (
                        id,
                        Spawn {
                            kind: ObjectKind::Grunt,
                            x: 10.,
                            z: 10.,
                        },
                    )
                })
                .unwrap()
        };
        let accepted = enqueue();
        assert!(
            ops.lock().unwrap().snapshot.get().unwrap()["objects"]
                .as_array()
                .unwrap()
                .is_empty()
        );
        app.tick(FIXED_STEP).unwrap();
        let state = ops.lock().unwrap().snapshot.json().unwrap();
        assert_eq!(state["objects"].as_array().unwrap().len(), 1);
        assert!(
            state["commands"]
                .as_array()
                .unwrap()
                .iter()
                .any(|c| c["command_id"] == accepted && c["state"] == "completed")
        );
        let invalid = enqueue();
        app.tick(FIXED_STEP).unwrap();
        let state = ops.lock().unwrap().snapshot.json().unwrap();
        assert!(
            state["commands"]
                .as_array()
                .unwrap()
                .iter()
                .any(|c| c["command_id"] == invalid && c["state"] == "rejected")
        );
        let pending = enqueue();
        app.shutdown().unwrap();
        let state = ops.lock().unwrap().snapshot.json().unwrap();
        assert_eq!(state["closed"], true);
        assert!(ops.lock().unwrap().commands.is_closed());
        assert!(
            state["commands"]
                .as_array()
                .unwrap()
                .iter()
                .any(|c| c["command_id"] == pending && c["state"] == "cancelled")
        );
        drop(app);
        drop(ops);
        std::fs::remove_dir_all(root).unwrap();
    }
}
