use super::network::WorldClient;
use crate::character::{Character, CharacterAssets, CharacterFrame, definition::VisualDefinition};
use arena_arpg_shared::{
    Action,
    open_world::{ObjectKind, WorldAction},
};
use glam::{Quat, Vec3};
use nico_assets::{Mesh, Texture};
use nico_presentation::{Camera3d, MeshInstance, Scene2d, Scene3d};
use nico_presentation_control::text::{BitmapFont, rectangle};
use std::{collections::BTreeMap, sync::Arc, time::Duration};
pub struct Visuals {
    pub environment: super::environment::Environment,
    assets: [Option<Arc<CharacterAssets>>; 3],
    characters: BTreeMap<u64, Character>,
    definitions: [VisualDefinition; 3],
    epoch: u64,
    white: Arc<Texture>,
    cube: Arc<Mesh>,
    ground: Arc<Mesh>,
    shade: Arc<Texture>,
    obstacles: Vec<Arc<Mesh>>,
    bars: Vec<Arc<Mesh>>,
    sectors: Vec<Arc<Mesh>>,
    positions: BTreeMap<u64, arena_arpg_shared::Vec2>,
    path: Arc<Mesh>,
    parts: Vec<Vec<Arc<Mesh>>>,
    font: BitmapFont,
    pub animation: serde_json::Value,
    pub actor_animations: Vec<serde_json::Value>,
}
fn mesh(size: [f32; 3]) -> Arc<Mesh> {
    Arc::new(
        nico_assets::procedural::cuboid(
            size,
            std::array::from_fn(|i| [(i as f32 + 0.5) / 6., 0.5]),
        )
        .unwrap(),
    )
}
fn draw(
    mesh: Arc<Mesh>,
    texture: Arc<Texture>,
    position: [f32; 3],
    orientation: Quat,
    scale: f32,
    color: [f32; 4],
) -> MeshInstance {
    MeshInstance {
        mesh: Some(mesh),
        texture: Some(texture),
        position,
        orientation,
        scale,
        color,
        skin_palette: None,
    }
}
impl Visuals {
    pub fn new(
        assets: [Option<Arc<CharacterAssets>>; 3],
        definitions: [VisualDefinition; 3],
        environment: super::environment::Environment,
    ) -> Self {
        let parts = definitions
            .iter()
            .map(|d| {
                let p = &d.arena.procedural.parts;
                [&p.body, &p.head, &p.leg, &p.arm, &p.weapon, &p.horn]
                    .iter()
                    .map(|p| mesh(p.size_m))
                    .collect()
            })
            .collect();
        Self {
            environment,
            assets,
            characters: BTreeMap::new(),
            definitions,
            epoch: 0,
            white: Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap()),
            cube: mesh([1.; 3]),
            ground: mesh([128., 0.1, 128.]),
            shade: Arc::new(
                Texture::rgba8(
                    6,
                    1,
                    [220u8, 165, 255, 115, 195, 145]
                        .into_iter()
                        .flat_map(|v| [v, v, v, 255])
                        .collect(),
                )
                .unwrap(),
            ),
            obstacles: vec![],
            bars: (0..=20)
                .map(|i| mesh([0.9 * (i as f32 / 20.).max(0.01), 0.07, 0.07]))
                .collect(),
            sectors: vec![],
            positions: BTreeMap::new(),
            path: mesh([6., 0.02, 44.]),
            parts,
            font: BitmapFont::default(),
            animation: serde_json::Value::Null,
            actor_animations: Vec::new(),
        }
    }
    pub fn render(
        &mut self,
        client: &WorldClient,
        camera: Camera3d,
        size: [f32; 2],
        captured: bool,
        dt: Duration,
    ) -> Result<(Scene3d, Scene2d), String> {
        if self.epoch != client.epoch {
            self.environment
                .bind(&client.zone)
                .map_err(|e| e.to_string())?;
            self.characters.clear();
            self.epoch = client.epoch;
            self.positions.clear();
            self.obstacles = client
                .zone
                .obstacles
                .iter()
                .map(|o| mesh(o.size.map(|x| x as f32)))
                .collect();
            self.sectors = client
                .characters
                .definitions()
                .iter()
                .map(|d| {
                    Arc::new(
                        nico_assets::procedural::sector(
                            (2. * d.arena.attacks.primary.half_angle_degrees).to_radians() as f32,
                            32,
                            [0.5, 0.5],
                        )
                        .unwrap(),
                    )
                })
                .collect();
        }
        let mut scene = Scene3d {
            camera,
            ..Default::default()
        };
        let mut hud = Scene2d::default();
        scene.meshes.push(draw(
            self.ground.clone(),
            self.white.clone(),
            [0., -0.1, 0.],
            Quat::IDENTITY,
            client.zone.half_extent_m as f32 / 64.,
            [0.24, 0.42, 0.24, 1.],
        ));
        scene.meshes.push(draw(
            self.path.clone(),
            self.white.clone(),
            [0., 0., -8.],
            Quat::IDENTITY,
            1.,
            [0.54, 0.48, 0.31, 1.],
        ));
        let projection = camera.view_projection(size[0] / size[1].max(1.));
        // A stationary, non-combatant warden uses the existing procedural body.
        if let Some(q) = &client.zone.quest {
            let p = &self.definitions[0].arena.procedural.parts;
            for (i, offset) in [
                (0, p.body.position_m),
                (1, p.head.position_m),
                (2, p.leg.position_m),
                (3, p.arm.position_m),
            ] {
                for sign in if i >= 2 { &[-1., 1.][..] } else { &[1.][..] } {
                    scene.meshes.push(draw(
                        self.parts[0][i].clone(),
                        self.shade.clone(),
                        [
                            q.warden.x as f32 + offset[0] * sign,
                            offset[1],
                            q.warden.z as f32 + offset[2],
                        ],
                        Quat::IDENTITY,
                        1.,
                        if i == 1 {
                            [0.9, 0.7, 0.5, 1.]
                        } else {
                            [0.9, 0.65, 0.12, 1.]
                        },
                    ));
                }
            }
            scene.meshes.push(draw(
                self.cube.clone(),
                self.white.clone(),
                [q.warden.x as f32, 2.6, q.warden.z as f32],
                Quat::from_rotation_z(0.78),
                0.25,
                [1., 0.85, 0.1, 1.],
            ));
        }
        for (index, obstacle) in client.zone.obstacles.iter().enumerate() {
            if self.environment.obstacle(index, projection, &mut scene) {
                continue;
            }
            scene.meshes.push(draw(
                self.obstacles
                    .get(index)
                    .cloned()
                    .unwrap_or_else(|| mesh(obstacle.size.map(|x| x as f32))),
                self.shade.clone(),
                obstacle.center.map(|x| x as f32),
                Quat::IDENTITY,
                1.,
                obstacle.color,
            ));
        }
        self.actor_animations.clear();
        self.animation = serde_json::Value::Null;
        let mut objects = client.visible();
        let local = client
            .prediction
            .as_ref()
            .map(|p| p.actor.position)
            .unwrap_or(client.zone.settlement);
        objects.sort_by(|a, b| {
            let dist = |o: &arena_arpg_shared::open_world::ObjectSnapshot| {
                (o.position.x - local.x).powi(2) + (o.position.z - local.z).powi(2)
            };
            dist(&a.1).total_cmp(&dist(&b.1))
        });
        self.characters
            .retain(|id, _| objects.iter().any(|(_, o)| o.id == *id));
        for (tick, o) in objects.iter().take(24) {
            // Reserve a complete actor, health bar and telegraph. An imported
            // model may have 32 primitives plus its sword; a fixed threshold
            // based on the default one-primitive hero can overflow the budget.
            let index = match o.kind {
                ObjectKind::Player => 0,
                ObjectKind::Grunt => 1,
                _ => 2,
            };
            let required = if o.kind == ObjectKind::Loot {
                1
            } else {
                self.assets[index]
                    .as_ref()
                    .map_or(11, |a| a.draw_count() + 2)
            };
            if scene.meshes.len() + required > 256 {
                continue;
            }
            if o.kind == ObjectKind::Loot {
                scene.meshes.push(draw(
                    self.cube.clone(),
                    self.white.clone(),
                    [o.position.x as f32, 0.35, o.position.z as f32],
                    Quat::from_rotation_y(*tick as f32 * 0.02),
                    0.45,
                    [0.95, 0.75, 0.12, 1.],
                ));
                continue;
            }
            let def = client.characters.get(o.kind.character());
            let moving = self
                .positions
                .insert(o.id, o.position)
                .is_some_and(|old| (old.x - o.position.x).hypot(old.z - o.position.z) > 0.0001);
            if o.health > 0
                && let WorldAction::Attack { elapsed, .. } = o.action
            {
                let attack = &def.arena.attacks.primary;
                if elapsed < attack.windup_ticks + attack.active_ticks {
                    let index = match o.kind {
                        ObjectKind::Player => 0,
                        ObjectKind::Grunt => 1,
                        _ => 2,
                    };
                    scene.meshes.push(draw(
                        self.sectors[index].clone(),
                        self.white.clone(),
                        [o.position.x as f32, 0.025, o.position.z as f32],
                        Quat::from_rotation_y(o.facing.x.atan2(o.facing.z) as f32),
                        attack.range_m as f32,
                        if o.kind == ObjectKind::Player {
                            [0.1, 0.75, 0.95, 0.4]
                        } else {
                            [0.95, 0.18, 0.1, 0.45]
                        },
                    ));
                }
            }
            let action = match o.action {
                WorldAction::Attack { id, elapsed } => Action::Attack {
                    id,
                    elapsed,
                    hit_mask: 0,
                },
                WorldAction::Dodge { elapsed, direction } => Action::Dodge { elapsed, direction },
                _ => Action::Idle,
            };
            if let Some(assets) = &self.assets[index] {
                let character = self
                    .characters
                    .entry(o.id)
                    .or_insert_with(|| Character::new(assets.clone()));
                let frame = CharacterFrame {
                    epoch: client.epoch,
                    tick: *tick,
                    playing: true,
                    position: o.position,
                    facing: o.facing,
                    health: o.health,
                    action,
                    stats: def.combat_stats(),
                    dodge_duration: def.arena.dodge.duration_ticks,
                };
                let mut draws = character
                    .render_frame(&frame, dt, projection)
                    .map_err(|e| e.to_string())?;
                if o.equipped.is_some()
                    && let Some(weapon) = draws.last_mut()
                {
                    weapon.color = client.item.color;
                }
                scene.meshes.extend(draws);
                self.actor_animations
                    .push(serde_json::json!({"id":o.id,"animation":character.state()}));
                if client.latest.as_ref().is_some_and(|s| s.player == o.id) {
                    self.animation = character.state();
                }
            } else {
                let index = match o.kind {
                    ObjectKind::Player => 0,
                    ObjectKind::Grunt => 1,
                    _ => 2,
                };
                let v = &self.definitions[index].arena.procedural;
                let p = &v.parts;
                let yaw = o.facing.x.atan2(o.facing.z) as f32;
                let rotation = Quat::from_rotation_y(yaw);
                let scale = v.body_scale;
                let origin = Vec3::new(o.position.x as f32, 0., o.position.z as f32);
                let dead = o.health == 0;
                let rotation = if dead {
                    rotation * Quat::from_rotation_z(1.55)
                } else {
                    rotation
                };
                let mut part = |i: usize, offset: [f32; 3], color: [f32; 4], extra: f32| {
                    let position = origin + rotation * Vec3::from(offset) * scale;
                    scene.meshes.push(draw(
                        self.parts[index][i].clone(),
                        self.shade.clone(),
                        position.to_array(),
                        rotation,
                        scale * extra,
                        color,
                    ));
                };
                part(0, p.body.position_m, v.color, v.torso_scale);
                part(1, p.head.position_m, v.head_color, 1.);
                for sign in [-1., 1.] {
                    let mut leg = p.leg.position_m;
                    leg[0] *= sign;
                    if moving && !dead {
                        leg[2] += (*tick as f32 * v.walk_radians_per_tick).sin()
                            * v.walk_amplitude_m
                            * sign;
                    }
                    part(2, leg, v.color, 1.);
                    let mut arm = p.arm.position_m;
                    arm[0] *= sign;
                    part(3, arm, v.color, 1.);
                    if v.horns {
                        let mut horn = p.horn.position_m;
                        horn[0] *= sign;
                        part(5, horn, v.head_color, 1.);
                    }
                }
                part(
                    4,
                    p.weapon.position_m,
                    [0.75, 0.8, 0.85, 1.],
                    v.weapon_scale,
                );
            }
            if o.health > 0 {
                let ratio = f32::from(o.health) / f32::from(o.max_health.max(1));
                scene.meshes.push(draw(
                    self.bars[(ratio * 20.).ceil().clamp(0., 20.) as usize].clone(),
                    self.white.clone(),
                    [o.position.x as f32, 2.3, o.position.z as f32],
                    Quat::IDENTITY,
                    1.,
                    if o.kind == ObjectKind::Player {
                        [0.1, 0.8, 0.9, 1.]
                    } else {
                        [0.9, 0.2, 0.15, 1.]
                    },
                ));
            }
        }
        self.positions
            .retain(|id, _| objects.iter().any(|(_, o)| o.id == *id));
        self.environment.decorate(projection, &mut scene);
        let scale = (size[0] / 800.).clamp(1., 2.5);
        let margin = 12.;
        rectangle(
            &mut hud,
            [margin, margin],
            [340. * scale, 74. * scale],
            [0.07, 0.1, 0.13, 0.9],
            self.white.clone(),
        );
        let title = format!("MEADOW / {} / {}", client.name, client.status);
        self.font.draw(
            &mut hud.hud,
            &title,
            [margin + 8., margin + 8.],
            scale,
            [0.6, 0.95, 1., 1.],
        );
        if let Some(snapshot) = &client.latest
            && let Some(actor) = snapshot.objects.iter().find(|o| o.id == snapshot.player)
        {
            let info = format!(
                "HEALTH {} / {}   XP {}\nPACK {}   {}\n{}",
                actor.health,
                actor.max_health,
                snapshot.experience,
                snapshot.inventory.len(),
                if actor.equipped.is_some() {
                    "IRON SWORD EQUIPPED"
                } else {
                    "STARTER SWORD"
                },
                if actor.health == 0 {
                    "YOU DIED / R TO RESPAWN"
                } else if actor.position.z < -10. {
                    "SETTLEMENT / CAMP TO THE NORTH"
                } else {
                    "MEADOW / MONSTER CAMP"
                }
            );
            self.font.draw(
                &mut hud.hud,
                &info,
                [margin + 8., margin + 22. * scale],
                scale,
                [1.; 4],
            );
        } else {
            self.font.draw(
                &mut hud.hud,
                "WAITING FOR WORLD SERVER",
                [margin + 8., margin + 28. * scale],
                scale,
                [1.; 4],
            );
        }
        if let (Some(q), Some(snapshot)) = (&client.zone.quest, &client.latest) {
            use arena_arpg_shared::open_world::quest::QuestStage;
            let (target, text) = match snapshot.quest.stage {
                QuestStage::Available => {
                    (q.warden, "MEADOW WATCH / SPEAK TO THE WARDEN".to_owned())
                }
                QuestStage::Active => (
                    q.camp,
                    format!("MEADOW WATCH / CAMP MONSTERS {}/3", snapshot.quest.kills),
                ),
                QuestStage::Ready => (q.warden, "MEADOW WATCH / RETURN FOR YOUR REWARD".to_owned()),
                QuestStage::Completed => (
                    q.warden,
                    "MEADOW WATCH COMPLETE / +50 XP AND IRON SWORD".to_owned(),
                ),
            };
            let distance = (target.x - local.x).hypot(target.z - local.z);
            let nearby = (q.warden.x - local.x).hypot(q.warden.z - local.z) <= 2.5;
            let hint = if nearby {
                "E TALK / GOLD MARKER: WARDEN"
            } else {
                "GOLD MARKER: SETTLEMENT WARDEN"
            };
            rectangle(
                &mut hud,
                [margin, 100. * scale],
                [390. * scale, 58. * scale],
                [0.07, 0.1, 0.13, 0.9],
                self.white.clone(),
            );
            self.font.draw(
                &mut hud.hud,
                &format!("{text}\nOBJECTIVE {distance:.0}M\n{hint}"),
                [margin + 8., 108. * scale],
                scale,
                [1., 0.9, 0.5, 1.],
            );
        }
        let help = "WASD MOVE / MOUSE LOOK / LMB ATTACK / SPACE DODGE\nE TALK/PICK UP / F EQUIP / R RESPAWN / Q RECONNECT / ESC RELEASE";
        rectangle(
            &mut hud,
            [margin, (size[1] - 38. * scale).max(0.)],
            [size[0] - 2. * margin, 34. * scale],
            [0.07, 0.1, 0.13, 0.9],
            self.white.clone(),
        );
        self.font.draw(
            &mut hud.hud,
            help,
            [margin + 8., (size[1] - 32. * scale).max(0.)],
            scale,
            [1.; 4],
        );
        if !captured {
            self.font.draw(
                &mut hud.hud,
                "CLICK TO CONTROL",
                [(size[0] - 96. * scale) / 2., 16.],
                scale,
                [1., 0.9, 0.5, 1.],
            );
        }
        if client.error.is_some() {
            self.font.draw(
                &mut hud.hud,
                "CONNECTION LOST / RETRYING",
                [margin + 8., 160. * scale],
                scale,
                [1., 0.5, 0.4, 1.],
            );
        }
        Ok((scene, hud))
    }
}
