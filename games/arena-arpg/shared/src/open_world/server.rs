//! Authoritative game session adapter over engine-owned native transport.
//! Call `step` at the fixed runtime boundary; shutdown saves before sockets close.
use super::{
    CharacterRecord, MAX_PLAYERS, ObjectId, OpenWorld,
    persistence::CharacterStore,
    protocol::{ClientMessage, PROTOCOL_VERSION, ServerMessage},
};
use nico_net::{ConnectionId, Event, Server};
use std::{collections::BTreeMap, io, net::SocketAddr, path::Path};
struct Session {
    player: Option<ObjectId>,
    deadline: u64,
    closing: bool,
}
pub struct WorldServer {
    pub world: OpenWorld,
    transport: Server,
    store: CharacterStore,
    sessions: BTreeMap<ConnectionId, Session>,
    pub last_save_tick: u64,
}
impl WorldServer {
    pub fn bind(address: SocketAddr, directory: &Path, world: OpenWorld) -> io::Result<Self> {
        if !address.ip().is_loopback() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "local world milestone requires loopback bind",
            ));
        }
        Ok(Self {
            world,
            transport: Server::bind(address, MAX_PLAYERS)?,
            store: CharacterStore::open(directory)?,
            sessions: BTreeMap::new(),
            last_save_tick: 0,
        })
    }
    pub fn address(&self) -> io::Result<SocketAddr> {
        self.transport.local_addr()
    }
    pub fn sessions(&self) -> Vec<(ConnectionId, Option<ObjectId>)> {
        self.sessions
            .iter()
            .map(|(&id, s)| (id, s.player))
            .collect()
    }
    fn send(&mut self, id: ConnectionId, message: &ServerMessage) -> io::Result<()> {
        self.transport
            .send(id, &serde_json::to_vec(message).map_err(io::Error::other)?)
    }
    fn disconnect(&mut self, id: ConnectionId) -> io::Result<()> {
        // Persist before despawning. If saving fails, report failure and retain the
        // authoritative record so shutdown can retry; never silently lose it.
        if let Some(player) = self.sessions.get(&id).and_then(|s| s.player) {
            let record = self.world.record(player).map_err(io::Error::other)?;
            self.store.save(&record)?;
            self.world.disconnect(player).map_err(io::Error::other)?;
        }
        self.sessions.remove(&id);
        self.transport.disconnect(id);
        Ok(())
    }
    fn reject(&mut self, id: ConnectionId, code: &str) -> io::Result<()> {
        if self
            .send(id, &ServerMessage::Error { code: code.into() })
            .is_err()
        {
            return self.disconnect(id);
        }
        if let Some(session) = self.sessions.get_mut(&id) {
            session.closing = true;
        }
        Ok(())
    }
    fn message(&mut self, id: ConnectionId, bytes: &[u8]) -> io::Result<()> {
        let Some(session) = self.sessions.get(&id) else {
            return Ok(());
        };
        if session.closing {
            return Ok(());
        }
        let player = session.player;
        match serde_json::from_slice::<ClientMessage>(bytes) {
            Ok(ClientMessage::Hello { version, character }) if player.is_none() => {
                if version != PROTOCOL_VERSION {
                    return self.reject(id, "protocol_version_mismatch");
                }
                if CharacterRecord::new(character.clone(), 1)
                    .validate()
                    .is_err()
                {
                    return self.reject(id, "invalid_character_identity");
                }
                // One unreadable character must not stop other players' world.
                // Never replace a failed load with a fresh record.
                let record = match self.store.load(&character) {
                    Ok(record) => record,
                    Err(_) => return self.reject(id, "character_load_failed"),
                }
                .unwrap_or_else(|| {
                    let mut record = CharacterRecord::new(
                        character,
                        self.world
                            .characters()
                            .get(crate::ActorKind::Hero)
                            .arena
                            .stats
                            .max_health,
                    );
                    record.position = self.world.zone.settlement;
                    record
                });
                match self.world.connect(record) {
                    Ok(player) => {
                        self.sessions.get_mut(&id).unwrap().player = Some(player);
                        let message = ServerMessage::Welcome {
                            version: PROTOCOL_VERSION,
                            player,
                            zone: self.world.zone.clone(),
                            item: self.world.item.clone(),
                            characters: Box::new(self.world.characters().definitions().clone()),
                        };
                        if self.send(id, &message).is_err() {
                            self.disconnect(id)?;
                        }
                    }
                    Err(code) => self.reject(id, code)?,
                }
            }
            Ok(ClientMessage::Input { input }) if player.is_some() => {
                if let Err(code) = self.world.submit(player.unwrap(), input) {
                    self.reject(id, code)?;
                }
            }
            _ => self.reject(id, "invalid_message")?,
        }
        Ok(())
    }
    pub fn step(&mut self) -> io::Result<()> {
        // Poll flushes errors queued on the previous boundary before removal.
        let closing: Vec<_> = self
            .sessions
            .iter()
            .filter(|(_, s)| s.closing)
            .map(|(&id, _)| id)
            .collect();
        let events = self.transport.poll()?;
        for id in closing {
            self.disconnect(id)?;
        }
        for event in events {
            match event {
                Event::Connected { connection, .. } => {
                    self.sessions.insert(
                        connection,
                        Session {
                            player: None,
                            deadline: self.world.tick() + 300,
                            closing: false,
                        },
                    );
                }
                Event::Message { connection, bytes } => self.message(connection, &bytes)?,
                Event::Disconnected { connection, .. } => self.disconnect(connection)?,
            }
        }
        let expired: Vec<_> = self
            .sessions
            .iter()
            .filter(|(_, s)| s.player.is_none() && self.world.tick() >= s.deadline)
            .map(|(&id, _)| id)
            .collect();
        for id in expired {
            self.disconnect(id)?;
        }
        self.world.step();
        // Full nearby-entity snapshots at 20 Hz. A missing entity means it left
        // interest or despawned; no stale client object is retained indefinitely.
        if self.world.tick().is_multiple_of(3) {
            let players: Vec<_> = self
                .sessions
                .iter()
                .filter_map(|(&id, s)| s.player.map(|p| (id, p)))
                .collect();
            for (id, player) in players {
                let snapshot = self.world.snapshot(player).map_err(io::Error::other)?;
                if self
                    .send(id, &ServerMessage::Snapshot { snapshot })
                    .is_err()
                {
                    self.disconnect(id)?;
                }
            }
        }
        if self.world.tick().is_multiple_of(300) {
            self.save()?;
        }
        Ok(())
    }
    pub fn save(&mut self) -> io::Result<()> {
        for (_, record) in self.world.records() {
            self.store.save(&record)?;
        }
        self.last_save_tick = self.world.tick();
        Ok(())
    }
    pub fn shutdown(&mut self) -> io::Result<()> {
        self.save()?;
        let ids: Vec<_> = self.sessions.keys().copied().collect();
        for id in ids {
            self.disconnect(id)?;
        }
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Vec2, characters::CharacterCatalog, open_world::PlayerInput};
    use nico_net::Connection;
    use std::time::Duration;
    fn message(peer: &mut Connection, m: ClientMessage) {
        peer.send(&serde_json::to_vec(&m).unwrap()).unwrap();
    }
    fn pump(
        server: &mut WorldServer,
        peers: &mut [&mut Connection],
        ticks: usize,
    ) -> Vec<ServerMessage> {
        let mut messages = vec![];
        for _ in 0..ticks {
            for peer in &mut *peers {
                for frame in peer.poll().unwrap() {
                    messages.push(serde_json::from_slice(&frame).unwrap());
                }
            }
            server.step().unwrap();
            std::thread::sleep(Duration::from_millis(1));
        }
        messages
    }
    #[test]
    fn rejected_sessions_do_not_stop_healthy_players_or_overwrite_corrupt_saves() {
        let root = std::env::temp_dir().join(format!(
            "nico-world-rejections-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let mut server = WorldServer::bind(
            "127.0.0.1:0".parse().unwrap(),
            &root,
            OpenWorld::new(CharacterCatalog::builtin()),
        )
        .unwrap();
        std::fs::write(root.join("character-broken.toml"), "corrupt").unwrap();
        let mut healthy =
            Connection::connect(server.address().unwrap(), Duration::from_secs(1)).unwrap();
        message(
            &mut healthy,
            ClientMessage::Hello {
                version: PROTOCOL_VERSION,
                character: "alice".into(),
            },
        );
        pump(&mut server, &mut [&mut healthy], 10);
        for (version, name, expected) in [
            (99, "bob", "protocol_version_mismatch"),
            (PROTOCOL_VERSION, "alice", "character_already_connected"),
            (PROTOCOL_VERSION, "broken", "character_load_failed"),
        ] {
            let mut peer =
                Connection::connect(server.address().unwrap(), Duration::from_secs(1)).unwrap();
            message(
                &mut peer,
                ClientMessage::Hello {
                    version,
                    character: name.into(),
                },
            );
            let mut rejection = None;
            for _ in 0..30 {
                if let Ok(frames) = peer.poll() {
                    for frame in frames {
                        if let ServerMessage::Error { code } =
                            serde_json::from_slice(&frame).unwrap()
                        {
                            rejection = Some(code);
                        }
                    }
                }
                healthy.poll().unwrap();
                server.step().unwrap();
                if rejection.is_some() {
                    break;
                }
                std::thread::sleep(Duration::from_millis(1));
            }
            assert_eq!(rejection.as_deref(), Some(expected));
            assert_eq!(server.world.records().len(), 1);
        }
        assert_eq!(
            std::fs::read_to_string(root.join("character-broken.toml")).unwrap(),
            "corrupt"
        );
        server.shutdown().unwrap();
        drop(server);
        drop(healthy);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn failed_disconnect_save_retains_authoritative_record_for_retry() {
        let root = std::env::temp_dir().join(format!(
            "nico-world-save-failure-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let mut server = WorldServer::bind(
            "127.0.0.1:0".parse().unwrap(),
            &root,
            OpenWorld::new(CharacterCatalog::builtin()),
        )
        .unwrap();
        let mut peer =
            Connection::connect(server.address().unwrap(), Duration::from_secs(1)).unwrap();
        message(
            &mut peer,
            ClientMessage::Hello {
                version: PROTOCOL_VERSION,
                character: "alice".into(),
            },
        );
        pump(&mut server, &mut [&mut peer], 10);
        let (connection, player) = server.sessions()[0];
        let player = player.unwrap();
        let record = server.world.record(player).unwrap();
        // A directory at the temporary filename deterministically prevents writing.
        let pending = root.join("character-alice.pending");
        std::fs::create_dir(&pending).unwrap();
        assert!(server.disconnect(connection).is_err());
        assert_eq!(server.world.record(player).unwrap(), record);
        assert_eq!(server.sessions()[0].1, Some(player));
        std::fs::remove_dir(&pending).unwrap();
        server.disconnect(connection).unwrap();
        assert!(server.world.records().is_empty());
        assert_eq!(server.store.load("alice").unwrap(), Some(record));
        server.shutdown().unwrap();
        drop(server);
        drop(peer);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn two_network_players_share_authority_and_reconnect_after_server_restart() {
        let root = std::env::temp_dir().join(format!(
            "nico-world-network-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let mut server = WorldServer::bind(
            "127.0.0.1:0".parse().unwrap(),
            &root,
            OpenWorld::new(CharacterCatalog::builtin()),
        )
        .unwrap();
        let mut a = Connection::connect(server.address().unwrap(), Duration::from_secs(1)).unwrap();
        let mut b = Connection::connect(server.address().unwrap(), Duration::from_secs(1)).unwrap();
        message(
            &mut a,
            ClientMessage::Hello {
                version: PROTOCOL_VERSION,
                character: "alice".into(),
            },
        );
        message(
            &mut b,
            ClientMessage::Hello {
                version: PROTOCOL_VERSION,
                character: "bob".into(),
            },
        );
        let messages = pump(&mut server, &mut [&mut a, &mut b], 20);
        assert_eq!(
            messages
                .iter()
                .filter(|m| matches!(m, ServerMessage::Welcome { .. }))
                .count(),
            2
        );
        assert!(
            messages.iter().any(
                |m| matches!(m,ServerMessage::Snapshot{snapshot} if snapshot.objects.len()==2)
            )
        );
        message(
            &mut a,
            ClientMessage::Input {
                input: PlayerInput {
                    sequence: 1,
                    movement: Vec2::new(1., 0.),
                    ..Default::default()
                },
            },
        );
        pump(&mut server, &mut [&mut a, &mut b], 10);
        let saved = server
            .world
            .records()
            .into_iter()
            .find(|(_, r)| r.name == "alice")
            .unwrap()
            .1;
        assert!(saved.position.x > 0.);
        drop(a);
        pump(&mut server, &mut [&mut b], 10);
        assert_eq!(server.world.records().len(), 1);
        server.shutdown().unwrap();
        drop(server);
        drop(b);
        let mut server = WorldServer::bind(
            "127.0.0.1:0".parse().unwrap(),
            &root,
            OpenWorld::new(CharacterCatalog::builtin()),
        )
        .unwrap();
        let mut a = Connection::connect(server.address().unwrap(), Duration::from_secs(1)).unwrap();
        message(
            &mut a,
            ClientMessage::Hello {
                version: PROTOCOL_VERSION,
                character: "alice".into(),
            },
        );
        pump(&mut server, &mut [&mut a], 20);
        assert_eq!(server.world.records()[0].1, saved);
        server.shutdown().unwrap();
        drop(server);
        drop(a);
        std::fs::remove_dir_all(root).unwrap();
    }
}
