use super::prediction::Prediction;
use arena_arpg_shared::{
    characters::CharacterCatalog,
    open_world::{
        CharacterRecord, ObjectSnapshot, PlayerInput, WorldAction, WorldSnapshot,
        content::{ItemDefinition, ZoneDefinition},
        protocol::{ClientMessage, PROTOCOL_VERSION, ServerMessage},
    },
};
use nico_net::{Connection, Connector};
use std::{
    net::SocketAddr,
    sync::Arc,
    time::{Duration, Instant},
};
pub struct WorldClient {
    pub name: String,
    pub address: SocketAddr,
    pub status: String,
    pub error: Option<String>,
    pub last_disconnect: Option<String>,
    pub epoch: u64,
    pub characters: Arc<CharacterCatalog>,
    pub zone: ZoneDefinition,
    pub item: ItemDefinition,
    pub latest: Option<WorldSnapshot>,
    pub previous: Option<WorldSnapshot>,
    pub prediction: Option<Prediction>,
    pub received: Option<Instant>,
    pub sequence: u64,
    connection: Option<Connection>,
    connector: Option<Connector>,
    retry: Instant,
    player: Option<u64>,
}

impl WorldClient {
    pub fn new(
        address: SocketAddr,
        name: String,
        characters: Arc<CharacterCatalog>,
    ) -> Result<Self, String> {
        CharacterRecord::new(name.clone(), 1).validate()?;
        if !address.ip().is_loopback() {
            return Err("local world milestone requires loopback server".into());
        }
        Ok(Self {
            name,
            address,
            status: "connecting".into(),
            error: None,
            last_disconnect: None,
            epoch: 0,
            characters,
            zone: ZoneDefinition::default(),
            item: ItemDefinition::default(),
            latest: None,
            previous: None,
            prediction: None,
            received: None,
            sequence: 0,
            connection: None,
            connector: None,
            retry: Instant::now(),
            player: None,
        })
    }
    pub fn reconnect(&mut self) {
        self.fail("reconnecting".into());
        self.retry = Instant::now();
    }
    pub fn fail(&mut self, error: String) {
        self.last_disconnect = Some(error.clone());
        self.error = Some(error);
        self.status = "disconnected".into();
        self.connection = None;
        self.connector = None;
        self.latest = None;
        self.previous = None;
        self.prediction = None;
        self.player = None;
        self.received = None;
        self.retry = Instant::now() + Duration::from_secs(1);
    }
    pub fn close(&mut self) {
        self.connection = None;
        self.connector = None;
        self.status = "closed".into();
        self.latest = None;
        self.previous = None;
        self.prediction = None;
    }
    pub fn poll(&mut self) {
        if let Err(error) = self.poll_inner() {
            self.fail(error);
        }
    }
    fn poll_inner(&mut self) -> Result<(), String> {
        if self.status == "closed" {
            return Ok(());
        }
        if self.connection.is_none() && self.connector.is_none() && Instant::now() >= self.retry {
            self.connector = Some(Connector::start(self.address).map_err(|e| e.to_string())?);
            self.status = "connecting".into();
        }
        if let Some(result) = self.connector.as_mut().and_then(Connector::poll) {
            self.connector = None;
            let mut connection = result.map_err(|e| e.to_string())?;
            connection
                .send(
                    &serde_json::to_vec(&ClientMessage::Hello {
                        version: PROTOCOL_VERSION,
                        character: self.name.clone(),
                    })
                    .unwrap(),
                )
                .map_err(|e| e.to_string())?;
            self.connection = Some(connection);
            self.status = "joining".into();
            self.received = Some(Instant::now());
        }
        let frames = if let Some(connection) = &mut self.connection {
            connection.poll().map_err(|e| e.to_string())?
        } else {
            vec![]
        };
        for bytes in frames {
            match serde_json::from_slice::<ServerMessage>(&bytes).map_err(|e| e.to_string())? {
                ServerMessage::Welcome {
                    version,
                    player,
                    characters,
                    zone,
                    item,
                } => {
                    if self.player.is_some() || version != PROTOCOL_VERSION {
                        return Err("unexpected welcome".into());
                    }
                    self.characters =
                        CharacterCatalog::new(*characters).map_err(|e| e.to_string())?;
                    zone.validate()?;
                    item.validate()?;
                    self.zone = zone;
                    self.item = item;
                    self.player = Some(player);
                    self.sequence = 0;
                    self.epoch += 1;
                    self.error = None;
                }
                ServerMessage::Snapshot { snapshot } => {
                    if self.player != Some(snapshot.player)
                        || snapshot.acknowledged_input > self.sequence
                        || snapshot.objects.len() > 256
                    {
                        return Err("invalid snapshot".into());
                    }
                    if self
                        .latest
                        .as_ref()
                        .is_some_and(|last| snapshot.tick <= last.tick)
                    {
                        continue;
                    }
                    if let Some(prediction) = &mut self.prediction {
                        prediction.reconcile(&snapshot)?;
                    } else {
                        self.prediction = Some(Prediction::new(
                            &snapshot,
                            self.characters.clone(),
                            self.zone.clone(),
                        )?);
                    }
                    self.previous = self.latest.take();
                    self.latest = Some(snapshot);
                    self.received = Some(Instant::now());
                    self.status = "connected".into();
                }
                ServerMessage::Error { code } => return Err(code),
            }
        }
        if self
            .received
            .is_some_and(|received| received.elapsed() > Duration::from_secs(3))
        {
            return Err("server snapshot timeout".into());
        }
        Ok(())
    }
    /// Bound prediction lead instead of assuming client and server clocks stay equal.
    /// The caller retains button edges and queued commands while this is false.
    pub fn input_ready(&self) -> bool {
        self.status == "connected"
            && self
                .prediction
                .as_ref()
                .is_some_and(|p| p.pending.len() < 8)
    }
    pub fn send(&mut self, mut input: PlayerInput) -> Result<u64, String> {
        if self.status != "connected" {
            return Err("not_connected".into());
        }
        if !self.input_ready() {
            return Err("input_backpressure".into());
        }
        input.sequence = self
            .sequence
            .checked_add(1)
            .ok_or("input sequence exhausted")?;
        if !input.valid() {
            return Err("invalid_input".into());
        }
        let bytes = serde_json::to_vec(&ClientMessage::Input {
            input: input.clone(),
        })
        .unwrap();
        self.connection
            .as_mut()
            .ok_or("not_connected")?
            .send(&bytes)
            .map_err(|e| e.to_string())?;
        self.prediction.as_mut().ok_or("not_ready")?.push(input)?;
        self.sequence += 1;
        Ok(self.sequence)
    }
    pub fn visible(&self) -> Vec<(u64, ObjectSnapshot)> {
        let Some(latest) = &self.latest else {
            return vec![];
        };
        let alpha = self
            .received
            .map_or(1., |t| (t.elapsed().as_secs_f64() / 0.05).clamp(0., 1.));
        latest
            .objects
            .iter()
            .map(|object| {
                if object.id == latest.player
                    && let Some(prediction) = &self.prediction
                {
                    return (prediction.tick, prediction.actor.clone());
                }
                if let Some(previous) = &self.previous
                    && let Some(old) = previous.objects.iter().find(|o| o.id == object.id)
                {
                    remote_sample(previous.tick, latest.tick, old, object, alpha)
                } else {
                    (latest.tick, object.clone())
                }
            })
            .collect()
    }
}

// Interpolate only continuous samples. Respawns, teleports and action boundaries
// use the newest complete state instead of mixing incompatible animation clocks.
fn remote_sample(
    old_tick: u64,
    tick: u64,
    old: &ObjectSnapshot,
    new: &ObjectSnapshot,
    alpha: f64,
) -> (u64, ObjectSnapshot) {
    let mut visual = new.clone();
    let gap = tick.saturating_sub(old_tick);
    if gap == 0
        || gap > 6
        || (old.health == 0) != (new.health == 0)
        || (old.position.x - new.position.x).hypot(old.position.z - new.position.z) > 2.
    {
        return (tick, visual);
    }
    let elapsed = |a: u16, b: u16| a + ((b - a) as f64 * alpha) as u16;
    visual.action = match (&old.action, &new.action) {
        (WorldAction::Idle, WorldAction::Idle) => WorldAction::Idle,
        (WorldAction::Attack { id: a, elapsed: x }, WorldAction::Attack { id: b, elapsed: y })
            if a == b && y >= x =>
        {
            WorldAction::Attack {
                id: *b,
                elapsed: elapsed(*x, *y),
            }
        }
        (
            WorldAction::Dodge {
                elapsed: x,
                direction: a,
            },
            WorldAction::Dodge {
                elapsed: y,
                direction: b,
            },
        ) if a == b && y >= x => WorldAction::Dodge {
            elapsed: elapsed(*x, *y),
            direction: *b,
        },
        (WorldAction::Dead { respawn_tick: a }, WorldAction::Dead { respawn_tick: b })
            if a == b =>
        {
            new.action.clone()
        }
        _ => return (tick, visual),
    };
    visual.position.x = old.position.x + (new.position.x - old.position.x) * alpha;
    visual.position.z = old.position.z + (new.position.z - old.position.z) * alpha;
    (old_tick + (gap as f64 * alpha) as u64, visual)
}

#[cfg(test)]
mod tests {
    use super::*;
    use arena_arpg_shared::open_world::{OpenWorld, server::WorldServer};

    #[test]
    fn remote_motion_and_attack_share_a_clock_but_respawns_snap() {
        let mut world = OpenWorld::new(CharacterCatalog::builtin());
        let id = world
            .connect(CharacterRecord::new("alice".into(), 100))
            .unwrap();
        let old = world
            .snapshot(id)
            .unwrap()
            .objects
            .into_iter()
            .find(|o| o.id == id)
            .unwrap();
        let mut new = old.clone();
        new.position.x += 0.3;
        let (tick, sample) = remote_sample(100, 103, &old, &new, 0.5);
        assert_eq!(tick, 101);
        assert!((sample.position.x - old.position.x - 0.15).abs() < 1e-9);
        let mut attack = old.clone();
        attack.action = WorldAction::Attack {
            id: 99,
            elapsed: 10,
        };
        new.action = WorldAction::Attack {
            id: 99,
            elapsed: 13,
        };
        let (tick, sample) = remote_sample(100, 103, &attack, &new, 0.5);
        assert_eq!(tick, 101);
        assert!(matches!(
            sample.action,
            WorldAction::Attack { elapsed: 11, .. }
        ));
        // A new attack must not borrow the previous action's tick.
        new.action = WorldAction::Attack {
            id: 102,
            elapsed: 1,
        };
        assert_eq!(remote_sample(100, 103, &attack, &new, 0.5).0, 103);
        let mut dead = old.clone();
        dead.health = 0;
        dead.action = WorldAction::Dead { respawn_tick: 100 };
        let mut respawn = old.clone();
        respawn.position.z -= 30.;
        let (tick, sample) = remote_sample(100, 103, &dead, &respawn, 0.5);
        assert_eq!(tick, 103);
        assert_eq!(sample.position, respawn.position);
        assert_eq!(sample.health, 100);
        assert_eq!(
            remote_sample(100, 103, &old, &respawn, 0.5).1.position,
            respawn.position
        );
    }

    #[test]
    fn faster_client_waits_for_acknowledgements_without_reconnecting() {
        let root = std::env::temp_dir().join(format!(
            "nico-client-clock-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let characters = CharacterCatalog::builtin();
        let mut server = WorldServer::bind(
            "127.0.0.1:0".parse().unwrap(),
            &root,
            OpenWorld::new(characters.clone()),
        )
        .unwrap();
        let mut client =
            WorldClient::new(server.address().unwrap(), "alice".into(), characters).unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while !client.input_ready() {
            assert!(Instant::now() < deadline, "join failed: {:?}", client.error);
            client.poll();
            server.step().unwrap();
            std::thread::sleep(Duration::from_millis(1));
        }
        let mut paused = 0;
        // Deliberately run the client twice as fast as the authoritative clock.
        for tick in 0..300 {
            client.poll();
            assert_eq!(client.epoch, 1);
            assert_eq!(client.status, "connected");
            if client.input_ready() {
                client.send(PlayerInput::default()).unwrap();
            } else {
                paused += 1;
                let sequence = client.sequence;
                assert_eq!(
                    client.send(PlayerInput::default()).unwrap_err(),
                    "input_backpressure"
                );
                assert_eq!(client.sequence, sequence);
            }
            assert!(client.prediction.as_ref().unwrap().pending.len() <= 8);
            if tick % 2 == 0 {
                server.step().unwrap();
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(paused > 60, "test must exercise sustained clock mismatch");
        assert!(client.latest.as_ref().unwrap().acknowledged_input > 60);
        assert!(client.last_disconnect.is_none());
        client.close();
        server.shutdown().unwrap();
        drop(server);
        std::fs::remove_dir_all(root).unwrap();
    }
}
