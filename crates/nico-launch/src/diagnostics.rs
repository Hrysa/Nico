//! Bounded event history for native host diagnostics; no span timing or profiling.
use nico_ops::mcp::{CallToolResult, Tool, ToolExtensions};
use serde_json::{Map, Value, json};
use std::{
    collections::VecDeque,
    fmt::{self, Write},
    io,
    sync::{Arc, Mutex, OnceLock},
    time::{SystemTime, UNIX_EPOCH},
};
use tracing::{
    Event, Subscriber,
    field::{Field, Visit},
};
use tracing_subscriber::{Layer, layer::Context};

const CAPACITY: usize = 256;
const PAGE_SIZE: u64 = 8;
const TEXT_BYTES: usize = 1024;

#[derive(Clone, Default)]
struct History(Arc<Mutex<Store>>);
#[derive(Default)]
struct Store {
    next: u64,
    records: VecDeque<Value>,
}
static HISTORY: OnceLock<History> = OnceLock::new();

impl History {
    fn push(&self, mut record: Value) {
        let mut store = self.0.lock().unwrap_or_else(|e| e.into_inner());
        store.next += 1;
        record["cursor"] = json!(store.next);
        if store.records.len() == CAPACITY {
            store.records.pop_front();
        }
        store.records.push_back(record);
    }
    fn read(&self, args: Map<String, Value>) -> CallToolResult {
        let invalid = || {
            CallToolResult::structured_error(
                json!({"error":{"code":"invalid_arguments","message":"after must be a nonnegative integer no greater than latest_cursor; limit must be 1..8; unknown arguments are rejected"}}),
            )
        };
        if args.keys().any(|key| key != "after" && key != "limit") {
            return invalid();
        }
        let after = match args.get("after") {
            None => 0,
            Some(v) => match v.as_u64() {
                Some(n) => n,
                None => return invalid(),
            },
        };
        let limit = match args.get("limit") {
            None => PAGE_SIZE,
            Some(v) => match v.as_u64() {
                Some(n @ 1..=PAGE_SIZE) => n,
                _ => return invalid(),
            },
        };
        let store = self.0.lock().unwrap_or_else(|e| e.into_inner());
        if after > store.next {
            return invalid();
        }
        let oldest = store.records.front().map(|r| r["cursor"].as_u64().unwrap());
        let dropped = oldest.map_or(0, |first| first.saturating_sub(after.saturating_add(1)));
        let records: Vec<_> = store
            .records
            .iter()
            .filter(|r| r["cursor"].as_u64().unwrap() > after)
            .take(limit as usize)
            .cloned()
            .collect();
        let next = records
            .last()
            .map_or(after, |r| r["cursor"].as_u64().unwrap());
        CallToolResult::structured(
            json!({"records":records,"next_cursor":next,"oldest_cursor":oldest,"latest_cursor":store.next,"dropped":dropped,"has_more":next < store.next,"capacity":CAPACITY}),
        )
    }
}

pub(crate) struct Capture(History);
pub(crate) fn capture_layer() -> Capture {
    Capture(HISTORY.get_or_init(History::default).clone())
}

impl<S: Subscriber> Layer<S> for Capture {
    fn on_event(&self, event: &Event<'_>, _context: Context<'_, S>) {
        let mut visitor = Fields::default();
        event.record(&mut visitor);
        let mut target = Bounded::new(128);
        let _ = write!(&mut target, "{}", event.metadata().target());
        self.0.push(json!({"timestamp_unix_ms":SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_millis() as u64,
            "level":event.metadata().level().as_str(),"target":target.text,"fields":visitor.values,"truncated":visitor.truncated || target.truncated}));
    }
}

struct Bounded {
    text: String,
    remaining: usize,
    truncated: bool,
}
impl Bounded {
    fn new(limit: usize) -> Self {
        Self {
            text: String::new(),
            remaining: limit,
            truncated: false,
        }
    }
}
impl Write for Bounded {
    fn write_str(&mut self, value: &str) -> fmt::Result {
        let mut end = value.len().min(self.remaining);
        while !value.is_char_boundary(end) {
            end -= 1;
        }
        self.text.push_str(&value[..end]);
        self.remaining -= end;
        if end != value.len() {
            self.truncated = true;
            return Err(fmt::Error);
        }
        Ok(())
    }
}
struct Fields {
    values: Map<String, Value>,
    remaining: usize,
    truncated: bool,
}
impl Default for Fields {
    fn default() -> Self {
        Self {
            values: Map::new(),
            remaining: TEXT_BYTES,
            truncated: false,
        }
    }
}
impl Fields {
    fn insert(&mut self, field: &Field, value: Value) {
        if self.values.len() >= 8 {
            self.truncated = true;
            return;
        }
        let mut name = Bounded::new(64);
        let _ = write!(&mut name, "{}", field.name());
        self.truncated |= name.truncated;
        self.values.insert(name.text, value);
    }
    fn formatted(&mut self, field: &Field, value: fmt::Arguments<'_>) {
        if self.values.len() >= 8 {
            self.truncated = true;
            return;
        }
        let mut text = Bounded::new(self.remaining);
        let _ = text.write_fmt(value);
        self.remaining -= text.text.len();
        self.truncated |= text.truncated;
        self.insert(field, Value::String(text.text));
    }
}
impl Visit for Fields {
    fn record_debug(&mut self, field: &Field, value: &dyn fmt::Debug) {
        self.formatted(field, format_args!("{value:?}"));
    }
    fn record_str(&mut self, field: &Field, value: &str) {
        self.formatted(field, format_args!("{value}"));
    }
    fn record_bool(&mut self, field: &Field, value: bool) {
        self.insert(field, json!(value));
    }
    fn record_i64(&mut self, field: &Field, value: i64) {
        self.insert(field, json!(value));
    }
    fn record_u64(&mut self, field: &Field, value: u64) {
        self.insert(field, json!(value));
    }
    fn record_f64(&mut self, field: &Field, value: f64) {
        self.insert(field, json!(value));
    }
}

pub(crate) fn register(mut tools: ToolExtensions) -> io::Result<ToolExtensions> {
    let history = HISTORY.get_or_init(History::default).clone();
    let input = json!({"type":"object","properties":{"after":{"type":"integer","minimum":0,"default":0},"limit":{"type":"integer","minimum":1,"maximum":8,"default":8}},"additionalProperties":false});
    let output = json!({"type":"object","required":["records","next_cursor","oldest_cursor","latest_cursor","dropped","has_more","capacity"],"additionalProperties":false,"properties":{
        "records":{"type":"array","maxItems":8,"items":{"type":"object","required":["cursor","timestamp_unix_ms","level","target","fields","truncated"],"additionalProperties":false,"properties":{
            "cursor":{"type":"integer"},"timestamp_unix_ms":{"type":"integer"},"level":{"type":"string"},"target":{"type":"string"},"fields":{"type":"object"},"truncated":{"type":"boolean"}}}},
        "next_cursor":{"type":"integer"},"oldest_cursor":{"type":["integer","null"]},"latest_cursor":{"type":"integer"},"dropped":{"type":"integer"},"has_more":{"type":"boolean"},"capacity":{"type":"integer"}}});
    let mut tool = Tool::new("diagnostics", "Read bounded native tracing events (same log filter as stderr). after is an exclusive process-local cursor; start at 0, then use next_cursor. dropped counts evicted records since after; truncated marks shortened fields. Retains 256 events, at most 8 per call. No span timing. History survives bridge reconnects but not game restart; offline instances cannot retrieve it.", input.as_object().unwrap().clone())
        .with_raw_output_schema(output.as_object().unwrap().clone().into());
    tool.annotations = Some(Default::default());
    if let Some(a) = tool.annotations.as_mut() {
        a.read_only_hint = Some(true);
        a.destructive_hint = Some(false);
        a.idempotent_hint = Some(true);
        a.open_world_hint = Some(false);
    }
    tools.register(tool, move |args| history.read(args))?;
    Ok(tools)
}

#[cfg(test)]
mod tests {
    use super::*;
    use tracing_subscriber::prelude::*;
    #[test]
    fn pagination_reports_eviction_and_rejects_invalid_cursors() {
        let history = History::default();
        assert_eq!(
            history.read(Map::new()).structured_content.unwrap()["oldest_cursor"],
            Value::Null
        );
        for n in 0..CAPACITY + 3 {
            history.push(json!({"n":n}));
        }
        let page = history.read(Map::new()).structured_content.unwrap();
        assert_eq!(page["dropped"], 3);
        assert_eq!(page["next_cursor"], 11);
        assert_eq!(page["records"].as_array().unwrap().len(), 8);
        let next = history
            .read(json!({"after":11,"limit":1}).as_object().unwrap().clone())
            .structured_content
            .unwrap();
        assert_eq!(next["dropped"], 0);
        assert_eq!(next["records"][0]["cursor"], 12);
        for args in [
            json!({"after":-1}),
            json!({"after":999}),
            json!({"limit":0}),
            json!({"limit":9}),
            json!({"unknown":1}),
            json!({"after":"1"}),
        ] {
            assert_eq!(
                history.read(args.as_object().unwrap().clone()).is_error,
                Some(true)
            );
        }
        let end = history
            .read(json!({"after":259}).as_object().unwrap().clone())
            .structured_content
            .unwrap();
        assert_eq!(end["has_more"], false);
        assert_eq!(end["next_cursor"], 259);
    }
    #[test]
    fn capture_preserves_typed_fields_filters_events_and_bounds_utf8() {
        let history = History::default();
        let subscriber = tracing_subscriber::registry()
            .with(tracing_subscriber::filter::LevelFilter::INFO)
            .with(Capture(history.clone()));
        tracing::subscriber::with_default(subscriber, || {
            tracing::debug!("excluded");
            tracing::info!(count = 7u64, enabled = true, "startup");
            tracing::warn!(message=%"界".repeat(2000), "large event");
        });
        let page = history.read(Map::new()).structured_content.unwrap();
        assert_eq!(page["records"].as_array().unwrap().len(), 2);
        assert_eq!(page["records"][0]["fields"]["count"], 7);
        assert_eq!(page["records"][0]["fields"]["enabled"], true);
        assert_eq!(page["records"][1]["truncated"], true);
        assert!(serde_json::to_vec(&page).unwrap().len() < 16000);
    }
}
