//! Game-owned operations. Engine hosts provide transport and lifecycle.

use crate::{GameState, Position};
use nico_ops::{
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage};
use serde_json::{Value, json};
use std::{
    io,
    sync::{Arc, Mutex},
};

/// Publishes an owned snapshot after game updates; MCP handlers only read that copy.
pub fn register(builder: AppBuilder) -> io::Result<(AppBuilder, ToolExtensions)> {
    let snapshot = Arc::new(Mutex::new(Publication::<Value>::default()));
    let mut tools = ToolExtensions::default();
    let tool = Tool::new("game_state", "Read the minimal game's latest owned snapshot, published after Update. No world access occurs on the tooling thread.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["snapshot_sequence","snapshot_age_ms","closed","fixed_updates","frame_updates","stamina","coins","movement_quest_completed","entity_count"],
            "properties":{"snapshot_sequence":{"type":"integer","minimum":1},"snapshot_age_ms":{"type":"integer","minimum":0},"closed":{"type":"boolean"},"fixed_updates":{"type":"integer"},"frame_updates":{"type":"integer"},"stamina":{"type":"integer"},
            "coins":{"type":"integer"},"movement_quest_completed":{"type":"boolean"},"entity_count":{"type":"integer"}},"additionalProperties":false}).as_object().unwrap().clone().into());
    let plugin = SnapshotPlugin(snapshot.clone());
    tools.register(tool, move |arguments| {
        if !arguments.is_empty() { return CallToolResult::structured_error(json!({"error":{"code":"invalid_arguments","message":"game_state accepts no arguments"}})); }
        snapshot.lock().expect("game snapshot lock poisoned").json().map(CallToolResult::structured)
            .unwrap_or_else(|| CallToolResult::structured_error(json!({"error":{"code":"not_ready","message":"no game update published yet"}})))
    })?;
    Ok((builder.add_plugin(plugin), tools))
}

struct SnapshotPlugin(Arc<Mutex<Publication<Value>>>);

impl Plugin for SnapshotPlugin {
    fn build(&self, builder: &mut AppBuilder) -> RuntimeResult<()> {
        let published = self.0.clone();
        builder.add_system(Stage::Update, "publish game tooling snapshot", move |context| {
        let state = context.world.resource::<GameState>()?;
        let value = json!({"fixed_updates":state.fixed_updates(), "frame_updates":state.frame_updates(),
            "stamina":state.stamina(),"coins":state.coins(),"movement_quest_completed":state.movement_quest_completed(),
            "entity_count":context.world.query::<&Position>().iter().count()});
        published.lock().expect("game snapshot lock poisoned").publish(value);
        Ok(())
    });
        let published = self.0.clone();
        builder.add_system(Stage::Shutdown, "close game tooling snapshot", move |_| {
            published.lock().unwrap().close();
            Ok(())
        });
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::MinimalGamePlugin;
    use std::time::Duration;

    #[test]
    fn first_tooling_snapshot_includes_the_completed_game_update() -> RuntimeResult<()> {
        let snapshot = Arc::new(Mutex::new(Publication::default()));
        let mut app = AppBuilder::new()
            .add_plugin(MinimalGamePlugin)
            .add_plugin(SnapshotPlugin(snapshot.clone()))
            .build()?;
        app.start()?;
        app.tick(Duration::from_millis(17))?;
        let first = snapshot.lock().unwrap().json().unwrap();
        assert_eq!(first["frame_updates"], 1);
        assert_eq!(first["entity_count"], 1);
        app.tick(Duration::from_millis(17))?;
        assert_eq!(snapshot.lock().unwrap().get().unwrap()["frame_updates"], 2);
        assert_eq!(first["frame_updates"], 1);
        app.shutdown()
    }
}
