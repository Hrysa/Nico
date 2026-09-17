use super::*;
fn world() -> OpenWorld {
    OpenWorld::new(CharacterCatalog::builtin())
}
fn player(w: &mut OpenWorld, name: &str, position: Vec2) -> ObjectId {
    let mut record = CharacterRecord::new(name.into(), 100);
    record.position = position;
    w.connect(record).unwrap()
}
fn input(sequence: u64) -> PlayerInput {
    PlayerInput {
        sequence,
        ..Default::default()
    }
}
#[test]
fn independent_players_have_owned_views_and_exclusive_sessions() {
    let mut w = world();
    let a = player(&mut w, "alice", Vec2::new(0., -20.));
    let b = player(&mut w, "bob", Vec2::new(0., -20.));
    assert_ne!(a, b);
    assert_ne!(w.record(a).unwrap().position, w.record(b).unwrap().position);
    assert_eq!(w.snapshot(a).unwrap().objects.len(), 2);
    assert_eq!(
        w.connect(CharacterRecord::new("alice".into(), 100)),
        Err("character_already_connected")
    );
    let saved = w.disconnect(a).unwrap();
    assert_eq!(w.submit(a, input(1)), Err("unknown_player"));
    let reconnected = w.connect(saved).unwrap();
    assert_ne!(reconnected, a);
    assert_eq!(w.snapshot(reconnected).unwrap().acknowledged_input, 0);
}
#[test]
fn queued_packets_cannot_speed_up_movement_and_missing_input_stops() {
    let mut w = world();
    let a = player(&mut w, "alice", Vec2::new(0., -20.));
    for sequence in 1..=INPUT_QUEUE as u64 {
        w.submit(
            a,
            PlayerInput {
                movement: Vec2::new(1., 0.),
                ..input(sequence)
            },
        )
        .unwrap();
    }
    assert_eq!(w.submit(a, input(65)), Err("input_queue_full"));
    assert_eq!(w.submit(a, input(1)), Err("stale_input"));
    w.step();
    assert!((w.record(a).unwrap().position.x - 4. / 60.).abs() < 1e-5);
    assert_eq!(w.snapshot(a).unwrap().acknowledged_input, 1);
    for _ in 1..INPUT_QUEUE {
        w.step();
    }
    let stopped = w.record(a).unwrap().position;
    for _ in 0..60 {
        w.step();
    }
    assert_eq!(w.record(a).unwrap().position, stopped);
    assert_eq!(
        w.submit(
            a,
            PlayerInput {
                movement: Vec2::new(f64::NAN, 0.),
                ..input(65)
            }
        ),
        Err("invalid_input")
    );
    assert_eq!(
        w.submit(
            a,
            PlayerInput {
                movement: Vec2::new(2., 0.),
                ..input(65)
            }
        ),
        Err("invalid_input")
    );
}
#[test]
fn server_combat_awards_one_drop_and_one_player_can_claim_it() {
    let mut catalog = CharacterCatalog::builtin().definitions().clone();
    catalog[0].arena.attacks.primary.damage = 100;
    catalog[0].arena.attacks.primary.windup_ticks = 2;
    let mut w = OpenWorld::new(CharacterCatalog::new(catalog).unwrap());
    let a = player(&mut w, "alice", Vec2::new(0., 0.));
    let b = player(&mut w, "bob", Vec2::new(-1., 0.));
    let monster = w
        .spawn_monster(ObjectKind::Grunt, Vec2::new(0., 2.))
        .unwrap();
    w.submit(
        a,
        PlayerInput {
            attack_yaw: Some(0.),
            ..input(1)
        },
    )
    .unwrap();
    w.step();
    assert_eq!(
        w.objects().iter().find(|o| o.id == monster).unwrap().health,
        60
    );
    w.step();
    assert_eq!(
        w.objects().iter().find(|o| o.id == monster).unwrap().health,
        0
    );
    let drops: Vec<_> = w
        .objects()
        .into_iter()
        .filter(|o| o.kind == ObjectKind::Loot)
        .collect();
    assert_eq!(drops.len(), 1);
    let drop_id = drops[0].id;
    // Place both players within interaction reach, using test-only ECS access.
    w.entities
        .entities()
        .get::<&mut Position>(w.ids[&b])
        .unwrap()
        .0 = Vec2::new(-0.8, 1.);
    w.submit(
        a,
        PlayerInput {
            pickup: Some(drop_id),
            equip: Some(ITEM_SWORD.into()),
            ..input(2)
        },
    )
    .unwrap();
    w.submit(
        b,
        PlayerInput {
            pickup: Some(drop_id),
            ..input(1)
        },
    )
    .unwrap();
    w.step();
    assert_eq!(w.record(a).unwrap().inventory, vec![ITEM_SWORD]);
    assert_eq!(w.record(a).unwrap().equipped.as_deref(), Some(ITEM_SWORD));
    assert_eq!(w.record(a).unwrap().experience, 10);
    assert!(w.record(b).unwrap().inventory.is_empty());
    assert_eq!(
        w.snapshot(b).unwrap().last_error.as_deref(),
        Some("pickup_unavailable")
    );
    let saved = w.disconnect(a).unwrap();
    let new = w.connect(saved.clone()).unwrap();
    assert_eq!(w.record(new).unwrap(), saved);
}
#[test]
fn interest_filter_removes_distant_objects_without_leaking_inventory() {
    let mut w = world();
    let a = player(&mut w, "alice", Vec2::new(0., -20.));
    let b = player(&mut w, "bob", Vec2::new(0., 40.));
    let snapshot = w.snapshot(a).unwrap();
    assert_eq!(snapshot.objects.len(), 1);
    assert_eq!(snapshot.objects[0].id, a);
    w.disconnect(b).unwrap();
    assert_eq!(w.snapshot(a).unwrap().objects.len(), 1);
}
#[test]
fn death_requires_respawn_delay_and_monsters_return_automatically() {
    let mut w = world();
    let a = player(&mut w, "alice", Vec2::new(0., 0.));
    let m = w
        .spawn_monster(ObjectKind::Grunt, Vec2::new(4., 0.))
        .unwrap();
    for id in [a, m] {
        let mut c = w
            .entities
            .entities()
            .get::<&mut Combat>(w.ids[&id])
            .unwrap();
        c.health = 0;
        c.action = WorldAction::Dead { respawn_tick: 3 };
    }
    w.sync_bodies();
    w.submit(
        a,
        PlayerInput {
            respawn: true,
            ..input(1)
        },
    )
    .unwrap();
    w.step();
    assert_eq!(w.record(a).unwrap().health, 0);
    w.step();
    w.step();
    assert_eq!(w.objects().iter().find(|o| o.id == m).unwrap().health, 60);
    assert_eq!(w.record(a).unwrap().health, 0);
    w.submit(
        a,
        PlayerInput {
            respawn: true,
            ..input(2)
        },
    )
    .unwrap();
    w.step();
    assert_eq!(w.record(a).unwrap().health, 100);
    assert_eq!(w.record(a).unwrap().position, Vec2::new(0., -20.));
}
#[test]
fn equip_and_pickup_require_owned_items_and_proximity() {
    let mut w = world();
    let a = player(&mut w, "alice", Vec2::new(0., 0.));
    w.spawn_loot(Vec2::new(0., 10.));
    let drop = w
        .objects()
        .iter()
        .find(|o| o.kind == ObjectKind::Loot)
        .unwrap()
        .id;
    w.submit(
        a,
        PlayerInput {
            pickup: Some(drop),
            equip: Some(ITEM_SWORD.into()),
            ..input(1)
        },
    )
    .unwrap();
    w.step();
    assert!(w.record(a).unwrap().inventory.is_empty());
    assert_eq!(w.record(a).unwrap().equipped, None);
    assert!(w.ids.contains_key(&drop));
}
#[test]
fn authored_zone_loads_obstacles_and_camp_and_rejects_blocked_spawns() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../assets/logic");
    let zone = content::ZoneDefinition::load(&root.join("worlds/meadow.world.toml")).unwrap();
    let item = content::ItemDefinition::load(&root.join("items/iron-sword.item.toml")).unwrap();
    let mut world = OpenWorld::with_content(CharacterCatalog::builtin(), zone, item).unwrap();
    assert_eq!(world.objects().len(), 3);
    assert_eq!(
        world.spawn_monster(ObjectKind::Grunt, Vec2::new(-9., -22.)),
        Err("invalid_spawn")
    );
    let id = player(&mut world, "walker", Vec2::new(-9., -15.));
    for sequence in 1..=120 {
        world
            .submit(
                id,
                PlayerInput {
                    movement: Vec2::new(0., -1.),
                    ..input(sequence)
                },
            )
            .unwrap();
        world.step();
    }
    assert!(
        world.record(id).unwrap().position.z > -18.2,
        "house blocks traversal"
    );
}
