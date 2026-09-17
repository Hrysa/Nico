//! Versioned game messages; transport carries opaque bounded frames.
use super::{PlayerInput, WorldSnapshot};
use serde::{Deserialize, Serialize};
pub const PROTOCOL_VERSION: u32 = 2;
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum ClientMessage {
    Hello { version: u32, character: String },
    Input { input: PlayerInput },
}
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum ServerMessage {
    Welcome {
        version: u32,
        player: u64,
        zone: super::content::ZoneDefinition,
        item: super::content::ItemDefinition,
        characters: Box<[crate::characters::CharacterDefinition; 3]>,
    },
    Snapshot {
        snapshot: WorldSnapshot,
    },
    Error {
        code: String,
    },
}
