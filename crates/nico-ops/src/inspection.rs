//! Bounded, owned debug views. Games publish at a runtime boundary; tooling never
//! reads the world. Paged queries pin a snapshot and cannot outlive world replacement.

use crate::mcp::{CallToolResult, Map, Tool, ToolAccess, ToolExtensions, Value};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::{
    collections::{BTreeMap, BTreeSet},
    io,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

const MAX_ENTITIES: usize = 4096;
const MAX_SNAPSHOT_BYTES: usize = 1024 * 1024;
const MAX_ENTITY_BYTES: usize = 16 * 1024;
const MAX_QUERIES: usize = 8;
const QUERY_LIFETIME: Duration = Duration::from_secs(30);

/// Game-owned, serializable inspection properties; no component pointers or raw memory.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EntityRecord {
    /// Stable generational game/ECS ID within this world generation.
    pub id: String,
    pub properties: Map<String, Value>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EntityReference {
    pub process_session: String,
    pub world_id: String,
    pub generation: u64,
    pub connection_epoch: u64,
    pub entity_id: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Cursor {
    snapshot_id: u64,
    offset: usize,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PageArgs {
    cursor: Option<Cursor>,
    #[serde(default = "page_limit")]
    limit: usize,
}
fn page_limit() -> usize {
    32
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct EntityArgs {
    snapshot_id: u64,
    reference: EntityReference,
}

struct Snapshot {
    revision: u64,
    tick: u64,
    at: Instant,
    total_entities: usize,
    entities: Vec<EntityRecord>,
}
struct State {
    world_id: String,
    generation: u64,
    connection_epoch: u64,
    revision: u64,
    next_query: u64,
    closed: bool,
    current: Option<Arc<Snapshot>>,
    queries: BTreeMap<u64, (Arc<Snapshot>, Instant)>,
}

/// Clones share bounded snapshot publication. Keep one per inspectable world.
#[derive(Clone)]
pub struct Inspection(Arc<Mutex<State>>);

impl Default for Inspection {
    fn default() -> Self {
        Self(Arc::new(Mutex::new(State {
            world_id: uuid::Uuid::new_v4().to_string(),
            generation: 1,
            connection_epoch: 0,
            revision: 0,
            next_query: 0,
            closed: false,
            current: None,
            queries: BTreeMap::new(),
        })))
    }
}

impl Inspection {
    /// Atomically replace the last-good owned view. Callers cap extraction before
    /// allocation; total_entities reports omitted entities when a game hits the cap.
    pub fn publish(
        &self,
        tick: u64,
        total_entities: usize,
        mut entities: Vec<EntityRecord>,
    ) -> io::Result<()> {
        if entities.len() > MAX_ENTITIES || total_entities < entities.len() {
            return Err(io::Error::other("inspection entity bound exceeded"));
        }
        let mut ids = BTreeSet::new();
        let mut bytes = 0;
        for entity in &entities {
            let size = serde_json::to_vec(entity).map_err(io::Error::other)?.len();
            bytes += size;
            if entity.id.is_empty()
                || entity.id.len() > 128
                || !ids.insert(&entity.id)
                || entity.properties.len() > 64
                || size > MAX_ENTITY_BYTES
                || bytes > MAX_SNAPSHOT_BYTES
            {
                return Err(io::Error::other("invalid or oversized inspection snapshot"));
            }
        }
        entities.sort_by(|left, right| left.id.cmp(&right.id));
        let mut state = self.0.lock().unwrap();
        if state.closed {
            return Err(io::Error::other("inspection closed"));
        }
        let revision = state
            .revision
            .checked_add(1)
            .ok_or_else(|| io::Error::other("inspection revisions exhausted"))?;
        state.revision = revision;
        state.current = Some(Arc::new(Snapshot {
            revision,
            tick,
            at: Instant::now(),
            total_entities,
            entities,
        }));
        Ok(())
    }

    /// Call at world replacement/reset before publishing new entities. Old entity
    /// references and pinned snapshots become invalid even when numeric IDs repeat.
    pub fn replace_world(&self) -> io::Result<()> {
        let mut state = self.0.lock().unwrap();
        state.generation = state
            .generation
            .checked_add(1)
            .ok_or_else(|| io::Error::other("world generations exhausted"))?;
        state.current = None;
        state.queries.clear();
        Ok(())
    }

    pub fn close(&self) {
        let mut state = self.0.lock().unwrap();
        state.closed = true;
        state.queries.clear();
    }

    pub fn register_tools(&self, tools: &mut ToolExtensions) -> io::Result<()> {
        let view = self.clone();
        tools.on_bridge_connection(move || {
            let mut state = view.0.lock().unwrap();
            state.queries.clear();
            if let Some(epoch) = state.connection_epoch.checked_add(1) {
                state.connection_epoch = epoch;
            } else {
                state.closed = true;
            }
        })?;
        let reader = self.clone();
        tools.register(Tool::new("debug_entities", "Page an owned entity snapshot. A fresh query pins a revision for 30 seconds; only eight snapshots are retained. Continue with next_cursor. References expire on world replacement; inspect properties with debug_entity and the returned snapshot_id.", json!({"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":64},"cursor":{"type":"object","required":["snapshot_id","offset"],"properties":{"snapshot_id":{"type":"integer","minimum":1},"offset":{"type":"integer","minimum":0}},"additionalProperties":false}},"additionalProperties":false}).as_object().unwrap().clone()), move |args| reader.page(args))?;
        tools.set_access("debug_entities", ToolAccess::Inspect)?;
        let reader = self.clone();
        tools.register(Tool::new("debug_entity", "Inspect owned properties from the same pinned snapshot as debug_entities. Process, world, and generation must match; stale handles fail rather than resolve to reused IDs.", json!({"type":"object","required":["snapshot_id","reference"],"properties":{"snapshot_id":{"type":"integer","minimum":1},"reference":{"type":"object","required":["process_session","world_id","generation","connection_epoch","entity_id"],"properties":{"process_session":{"type":"string"},"world_id":{"type":"string"},"generation":{"type":"integer","minimum":1},"connection_epoch":{"type":"integer","minimum":0},"entity_id":{"type":"string"}},"additionalProperties":false}},"additionalProperties":false}).as_object().unwrap().clone()), move |args| reader.entity(args))?;
        tools.set_access("debug_entity", ToolAccess::Inspect)
    }

    fn page(&self, args: Map<String, Value>) -> CallToolResult {
        let Ok(args) = serde_json::from_value::<PageArgs>(Value::Object(args)) else {
            return failure("invalid_arguments");
        };
        if !(1..=64).contains(&args.limit) {
            return failure("invalid_arguments");
        }
        let mut state = self.0.lock().unwrap();
        if state.closed {
            return failure("closed");
        }
        state
            .queries
            .retain(|_, (_, at)| at.elapsed() < QUERY_LIFETIME);
        let (snapshot_id, offset) = if let Some(cursor) = args.cursor {
            (cursor.snapshot_id, cursor.offset)
        } else {
            let Some(snapshot) = state.current.clone() else {
                return failure("not_ready");
            };
            let Some(id) = state.next_query.checked_add(1) else {
                return failure("query_ids_exhausted");
            };
            state.next_query = id;
            if state.queries.len() == MAX_QUERIES {
                state.queries.pop_first();
            }
            state.queries.insert(id, (snapshot, Instant::now()));
            (id, 0)
        };
        let Some((snapshot, _)) = state.queries.get(&snapshot_id) else {
            return failure("stale_snapshot");
        };
        if offset > snapshot.entities.len() {
            return failure("invalid_cursor");
        }
        let mut entities = Vec::new();
        let mut bytes = 0;
        for entity in snapshot.entities.iter().skip(offset).take(args.limit) {
            let value = json!({"reference":reference(&state, &entity.id),"properties":entity.properties.keys().collect::<Vec<_>>()});
            bytes += value.to_string().len();
            if bytes > 64 * 1024 {
                break;
            }
            entities.push(value);
        }
        let end = offset + entities.len();
        CallToolResult::structured(
            json!({"snapshot_id":snapshot_id,"revision":snapshot.revision,"tick":snapshot.tick,
            "snapshot_age_ms":snapshot.at.elapsed().as_millis() as u64,"generation":state.generation,
            "total_entities":snapshot.total_entities,"published_entities":snapshot.entities.len(),"truncated":snapshot.total_entities > snapshot.entities.len(),
            "entities":entities,"next_cursor":if end < snapshot.entities.len() { Some(json!({"snapshot_id":snapshot_id,"offset":end})) } else { None }}),
        )
    }

    fn entity(&self, args: Map<String, Value>) -> CallToolResult {
        let Ok(args) = serde_json::from_value::<EntityArgs>(Value::Object(args)) else {
            return failure("invalid_arguments");
        };
        let mut state = self.0.lock().unwrap();
        if state.closed {
            return failure("closed");
        }
        if args.reference.process_session != crate::identity::process_session_id()
            || args.reference.world_id != state.world_id
            || args.reference.generation != state.generation
            || args.reference.connection_epoch != state.connection_epoch
        {
            return failure("stale_entity");
        }
        state
            .queries
            .retain(|_, (_, at)| at.elapsed() < QUERY_LIFETIME);
        let Some((snapshot, _)) = state.queries.get(&args.snapshot_id) else {
            return failure("stale_snapshot");
        };
        let Some(entity) = snapshot
            .entities
            .iter()
            .find(|entity| entity.id == args.reference.entity_id)
        else {
            return failure("unknown_entity");
        };
        CallToolResult::structured(
            json!({"reference":args.reference,"snapshot_id":args.snapshot_id,"revision":snapshot.revision,"tick":snapshot.tick,"snapshot_age_ms":snapshot.at.elapsed().as_millis() as u64,"properties":entity.properties}),
        )
    }
}

fn reference(state: &State, id: &str) -> EntityReference {
    EntityReference {
        process_session: crate::identity::process_session_id().into(),
        world_id: state.world_id.clone(),
        generation: state.generation,
        connection_epoch: state.connection_epoch,
        entity_id: id.into(),
    }
}
fn failure(code: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code}}))
}

#[cfg(test)]
mod tests {
    use super::*;
    fn args(value: Value) -> Map<String, Value> {
        value.as_object().unwrap().clone()
    }
    fn record(id: &str, value: i32) -> EntityRecord {
        EntityRecord {
            id: id.into(),
            properties: args(json!({"value":value})),
        }
    }
    fn data(result: CallToolResult) -> Value {
        assert_ne!(result.is_error, Some(true));
        result.structured_content.unwrap()
    }
    fn code(result: CallToolResult) -> Value {
        assert_eq!(result.is_error, Some(true));
        result.structured_content.unwrap()["error"]["code"].clone()
    }

    #[test]
    fn pagination_pins_revision_and_reused_ids_do_not_survive_world_replacement() {
        let view = Inspection::default();
        view.publish(1, 2, vec![record("b", 2), record("a", 1)])
            .unwrap();
        let first = data(view.page(args(json!({"limit":1}))));
        assert_eq!(first["entities"][0]["reference"]["entity_id"], "a");
        view.publish(2, 1, vec![record("a", 99)]).unwrap();
        let second = data(view.page(args(json!({"cursor":first["next_cursor"]}))));
        assert_eq!(second["tick"], 1);
        assert_eq!(second["entities"][0]["reference"]["entity_id"], "b");
        let lookup = json!({"snapshot_id":first["snapshot_id"],"reference":first["entities"][0]["reference"]});
        assert_eq!(
            data(view.entity(args(lookup.clone())))["properties"]["value"],
            1
        );
        view.replace_world().unwrap();
        view.publish(3, 1, vec![record("a", 500)]).unwrap();
        assert_eq!(code(view.entity(args(lookup))), "stale_entity");
        assert_eq!(
            code(view.page(args(json!({"cursor":first["next_cursor"]})))),
            "stale_snapshot"
        );
    }

    #[cfg(feature = "bridge")]
    #[test]
    fn reconnect_invalidates_handles_without_claiming_a_new_world_generation() {
        let view = Inspection::default();
        let mut tools = ToolExtensions::default();
        view.register_tools(&mut tools).unwrap();
        tools.notify_bridge_connected();
        view.publish(1, 1, vec![record("a", 1)]).unwrap();
        let first = data(view.page(Map::new()));
        let lookup = json!({"snapshot_id":first["snapshot_id"],"reference":first["entities"][0]["reference"]});
        tools.notify_bridge_connected();
        assert_eq!(code(view.entity(args(lookup))), "stale_entity");
        let second = data(view.page(Map::new()));
        assert_eq!(first["generation"], second["generation"]);
        assert_ne!(
            first["entities"][0]["reference"]["connection_epoch"],
            second["entities"][0]["reference"]["connection_epoch"]
        );
        assert_eq!(first["tick"], second["tick"]);
    }

    #[test]
    fn bounds_eviction_expiry_and_failed_publication_are_explicit() {
        let view = Inspection::default();
        assert_eq!(code(view.page(Map::new())), "not_ready");
        view.publish(1, 4, vec![record("a", 1)]).unwrap();
        assert!(
            view.publish(2, 2, vec![record("a", 1), record("a", 2)])
                .is_err()
        );
        let first = data(view.page(Map::new()));
        assert_eq!(first["tick"], 1);
        assert_eq!(first["truncated"], true);
        let cursor = json!({"cursor":{"snapshot_id":first["snapshot_id"],"offset":0}});
        for _ in 0..MAX_QUERIES {
            data(view.page(Map::new()));
        }
        assert_eq!(code(view.page(args(cursor))), "stale_snapshot");
        assert_eq!(
            code(view.page(args(json!({"limit":65})))),
            "invalid_arguments"
        );
        let last = data(view.page(Map::new()));
        for (_, at) in view.0.lock().unwrap().queries.values_mut() {
            *at = Instant::now() - QUERY_LIFETIME;
        }
        assert_eq!(
            code(view.page(args(
                json!({"cursor":{"snapshot_id":last["snapshot_id"],"offset":0}})
            ))),
            "stale_snapshot"
        );
        view.close();
        assert!(view.publish(3, 0, vec![]).is_err());
        assert_eq!(code(view.page(Map::new())), "closed");
    }
}
