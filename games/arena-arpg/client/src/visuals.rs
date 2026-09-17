use arena_arpg_shared::{Action, RunState, Snapshot};
use nico_assets::{Mesh, Texture};
use nico_presentation::{Camera3d, MeshInstance, Scene2d, Scene3d};
use nico_presentation_control::text::{BitmapFont, rectangle};
use std::sync::Arc;

struct ProceduralMeshes {
    body: Arc<Mesh>,
    head: Arc<Mesh>,
    leg: Arc<Mesh>,
    arm: Arc<Mesh>,
    weapon: Arc<Mesh>,
    horn: Arc<Mesh>,
}
impl ProceduralMeshes {
    fn new(parts: &crate::character::definition::ProceduralParts) -> Self {
        Self {
            body: box_mesh(parts.body.size_m),
            head: box_mesh(parts.head.size_m),
            leg: box_mesh(parts.leg.size_m),
            arm: box_mesh(parts.arm.size_m),
            weapon: box_mesh(parts.weapon.size_m),
            horn: box_mesh(parts.horn.size_m),
        }
    }
}
pub struct Visuals {
    character_meshes: [ProceduralMeshes; 3],
    definitions: [crate::character::definition::VisualDefinition; 3],
    pub imported: [bool; 3],
    white: Arc<Texture>,
    shade: Arc<Texture>,
    font: BitmapFont,
    tile: Arc<Mesh>,
    walls: [Arc<Mesh>; 4],
    body: Arc<Mesh>,
    head: Arc<Mesh>,

    warning: Arc<Mesh>,
    ring: Arc<Mesh>,
    sector: [Arc<Mesh>; 3],
    sector_fill: [Arc<Mesh>; 3],
    bar: Arc<Mesh>,
    last_health: [u16; 4],
    flash: [f32; 4],
    last_run: u64,
    last_wave: u8,
    last_tick: u64,
    last_positions: [arena_arpg_shared::Vec2; 4],
    moving: [bool; 4],
}
impl Visuals {
    #[cfg(test)]
    pub fn new() -> Self {
        Self::configured(
            std::array::from_fn(crate::character::definition::VisualDefinition::builtin),
            &arena_arpg_shared::characters::CharacterCatalog::builtin(),
        )
    }
    pub fn configured(
        definitions: [crate::character::definition::VisualDefinition; 3],
        logic: &arena_arpg_shared::characters::CharacterCatalog,
    ) -> Self {
        let white = Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap());
        let mut pixels = Vec::new();
        for v in [220, 165, 255, 115, 195, 145] {
            pixels.extend_from_slice(&[v, v, v, 255]);
        }
        Self {
            character_meshes: std::array::from_fn(|i| {
                ProceduralMeshes::new(&definitions[i].arena.procedural.parts)
            }),
            definitions,
            imported: [false; 3],
            white,
            shade: Arc::new(Texture::rgba8(6, 1, pixels).unwrap()),
            font: BitmapFont::default(),
            tile: box_mesh([2.0, 0.1, 2.0]),
            walls: arena_arpg_shared::geometry::WALLS
                .map(|wall| box_mesh(wall.size.map(|v| v as f32))),
            body: box_mesh([0.6, 0.7, 0.4]),
            head: box_mesh([0.4, 0.4, 0.4]),

            warning: box_mesh([0.12, 0.3, 0.12]),
            ring: arc_mesh(0.86, std::f32::consts::TAU),
            sector: std::array::from_fn(|i| {
                arc_mesh(
                    0.94,
                    (logic.definitions()[i]
                        .arena
                        .attacks
                        .primary
                        .half_angle_degrees
                        * 2.)
                        .to_radians() as f32,
                )
            }),
            sector_fill: std::array::from_fn(|i| {
                sector_mesh(
                    (logic.definitions()[i]
                        .arena
                        .attacks
                        .primary
                        .half_angle_degrees
                        * 2.)
                        .to_radians() as f32,
                )
            }),
            bar: box_mesh([0.9, 0.08, 0.06]),
            last_health: [0; 4],
            flash: [0.0; 4],
            last_run: 0,
            last_wave: 0,
            last_tick: 0,
            last_positions: [arena_arpg_shared::Vec2::default(); 4],
            moving: [false; 4],
        }
    }
    fn instance(
        &self,
        mesh: &Arc<Mesh>,
        position: [f32; 3],
        yaw: f32,
        scale: f32,
        color: [f32; 4],
    ) -> MeshInstance {
        MeshInstance {
            skin_palette: None,
            mesh: Some(mesh.clone()),
            texture: Some(self.shade.clone()),
            position,
            orientation: nico_presentation::Quaternion::from_rotation_y(yaw),
            scale,
            color,
        }
    }
    pub fn render(
        &mut self,
        state: &Snapshot,
        camera: Camera3d,
        size: [f32; 2],
        captured: bool,
        dt: f32,
    ) -> (Scene3d, Scene2d) {
        if self.last_run != state.run_id || self.last_wave != state.wave {
            self.last_run = state.run_id;
            self.last_wave = state.wave;
            self.last_health = std::array::from_fn(|i| state.actors[i].health);
            self.last_positions = std::array::from_fn(|i| state.actors[i].position);
            self.flash = [0.0; 4];
            self.moving = [false; 4];
        } else if self.last_tick != state.tick {
            self.moving =
                std::array::from_fn(|i| state.actors[i].position != self.last_positions[i]);
            self.last_positions = std::array::from_fn(|i| state.actors[i].position);
        }
        self.last_tick = state.tick;
        let mut scene = Scene3d {
            camera,
            meshes: Vec::new(),
        };
        for x in 0..12 {
            for z in 0..12 {
                let c = if (x + z) % 2 == 0 {
                    [0.065, 0.105, 0.13, 1.0]
                } else {
                    [0.045, 0.075, 0.10, 1.0]
                };
                scene.meshes.push(self.instance(
                    &self.tile,
                    [-11.0 + x as f32 * 2.0, -0.05, -11.0 + z as f32 * 2.0],
                    0.0,
                    0.995,
                    c,
                ));
            }
        }
        for (mesh, wall) in self.walls.iter().zip(arena_arpg_shared::geometry::WALLS) {
            scene.meshes.push(self.instance(
                mesh,
                wall.center.map(|v| v as f32),
                0.0,
                1.0,
                [0.14, 0.22, 0.27, 1.0],
            ));
        }
        for x in [-10.5, 10.5] {
            for z in [-10.5, 10.5] {
                scene.meshes.push(self.instance(
                    &self.body,
                    [x, 0.45, z],
                    0.0,
                    1.0,
                    [0.21, 0.30, 0.35, 1.0],
                ));
                scene.meshes.push(self.instance(
                    &self.head,
                    [x, 1.05, z],
                    0.0,
                    0.8,
                    [1.0, 0.48, 0.07, 1.0],
                ));
            }
        }
        for actor in &state.actors {
            let i = actor.id as usize;
            if actor.health < self.last_health[i] {
                self.flash[i] = 0.18;
            }
            self.last_health[i] = actor.health;
            self.flash[i] = (self.flash[i] - dt).max(0.0);
            let position = [actor.position.x as f32, 0.0, actor.position.z as f32];
            let yaw = (actor.facing.x.atan2(actor.facing.z)) as f32;
            let hero = i == 0;
            let stats = actor.stats();
            let definition_index = match actor.kind {
                arena_arpg_shared::ActorKind::Hero => 0,
                arena_arpg_shared::ActorKind::Grunt => 1,
                arena_arpg_shared::ActorKind::Brute => 2,
            };
            let visual = &self.definitions[definition_index].arena.procedural;
            let character_meshes = &self.character_meshes[definition_index];
            let imminent = !hero
                && state.state == RunState::Playing
                && matches!(actor.action,
                Action::Attack { elapsed, .. } if elapsed >= stats.windup.saturating_sub(12) && elapsed < stats.windup);
            let dead = actor.health == 0;
            let dodge = matches!(actor.action, Action::Dodge { .. });
            let color = if dead {
                [0.16, 0.18, 0.19, 1.0]
            } else if self.flash[i] > 0.0 {
                [1.0; 4]
            } else if dodge {
                [0.4, 0.95, 1.0, 1.0]
            } else {
                visual.color
            };
            let height = if dead {
                0.23
            } else if dodge {
                0.65
            } else {
                1.0
            };
            let body_scale = visual.body_scale;
            let orientation = nico_presentation::Quaternion::from_rotation_y(yaw);
            let local = |x: f32, y: f32, z: f32| {
                nico_presentation_control::coordinates::transform_point(
                    position,
                    orientation,
                    [body_scale, body_scale * height, body_scale],
                    [x, y, z],
                )
            };
            if !self.imported[definition_index] {
                scene.meshes.push(self.instance(
                    &character_meshes.body,
                    local(
                        visual.parts.body.position_m[0],
                        visual.parts.body.position_m[1],
                        visual.parts.body.position_m[2],
                    ),
                    yaw,
                    visual.torso_scale,
                    color,
                ));
                scene.meshes.push(self.instance(
                    &character_meshes.head,
                    local(
                        visual.parts.head.position_m[0],
                        visual.parts.head.position_m[1],
                        visual.parts.head.position_m[2],
                    ),
                    yaw,
                    1.0,
                    if dead || self.flash[i] > 0. || dodge {
                        color
                    } else {
                        visual.head_color
                    },
                ));
            }
            if !dead {
                if !self.imported[definition_index] {
                    let walk = if actor.action == Action::Idle
                        && self.moving[i]
                        && state.state == RunState::Playing
                    {
                        (state.tick as f32 * visual.walk_radians_per_tick + i as f32).sin()
                            * visual.walk_amplitude_m
                    } else {
                        0.0
                    };
                    for sign in [-1.0, 1.0] {
                        scene.meshes.push(self.instance(
                            &character_meshes.leg,
                            local(
                                sign * visual.parts.leg.position_m[0],
                                visual.parts.leg.position_m[1],
                                visual.parts.leg.position_m[2] + walk * sign,
                            ),
                            yaw,
                            1.0,
                            [0.1, 0.19, 0.24, 1.0],
                        ));
                        scene.meshes.push(self.instance(
                            &character_meshes.arm,
                            local(
                                sign * visual.parts.arm.position_m[0],
                                visual.parts.arm.position_m[1],
                                visual.parts.arm.position_m[2],
                            ),
                            yaw,
                            1.0,
                            color,
                        ));
                        if visual.horns {
                            scene.meshes.push(self.instance(
                                &character_meshes.horn,
                                local(
                                    sign * visual.parts.horn.position_m[0],
                                    visual.parts.horn.position_m[1],
                                    visual.parts.horn.position_m[2],
                                ),
                                yaw,
                                1.0,
                                [0.85, 0.68, 0.38, 1.0],
                            ));
                        }
                    }
                    let swing = match actor.action {
                        Action::Attack { elapsed, .. } => {
                            let windup = stats.windup as f32;
                            let t = elapsed as f32;
                            if t < windup {
                                -0.8 * t / windup
                            } else if t < windup + stats.active as f32 {
                                (t - windup) / stats.active as f32 * 1.6 - 0.8
                            } else {
                                let recovery = stats.recovery as f32;
                                0.8 * (1.0 - (t - windup - stats.active as f32) / recovery)
                                    .clamp(0.0, 1.0)
                            }
                        }
                        _ => 0.0,
                    };
                    scene.meshes.push(self.instance(
                        &character_meshes.weapon,
                        local(
                            visual.parts.weapon.position_m[0],
                            visual.parts.weapon.position_m[1],
                            visual.parts.weapon.position_m[2],
                        ),
                        yaw + swing,
                        visual.weapon_scale,
                        if imminent {
                            [1.0, 0.95, 0.45, 1.0]
                        } else {
                            [0.72, 0.91, 0.98, 1.0]
                        },
                    ));
                }
                scene.meshes.push(self.instance(
                    &self.ring,
                    [position[0], 0.02, position[2]],
                    0.0,
                    0.5,
                    if hero {
                        [0.05, 0.75, 0.92, 1.0]
                    } else {
                        [0.8, 0.12, 0.08, 1.0]
                    },
                ));
                if imminent {
                    scene.meshes.push(self.instance(
                        &self.warning,
                        local(0.0, 2.65, 0.0),
                        yaw,
                        1.0,
                        [1.0, 0.95, 0.45, 1.0],
                    ));
                    scene.meshes.push(self.instance(
                        &self.head,
                        local(0.0, 2.4, 0.0),
                        yaw,
                        0.2,
                        [1.0, 0.95, 0.45, 1.0],
                    ));
                }
                if let Action::Attack { elapsed, .. } = actor.action {
                    let windup = stats.windup;
                    if elapsed < windup + stats.active && state.state == RunState::Playing {
                        let color = if elapsed >= windup {
                            [1.0, 0.86, 0.3, 1.0]
                        } else if imminent && (elapsed / 3) % 2 == 0 {
                            [1.0, 0.95, 0.7, 1.0]
                        } else if hero {
                            [0.1, 0.6, 0.8, 1.0]
                        } else {
                            [1.0, 0.23, 0.03, 1.0]
                        };
                        let radius = stats.range as f32;
                        // The dim full sector always shows authoritative reach. The
                        // bright fill grows to the boundary when the strike activates.
                        scene.meshes.push(self.instance(
                            &self.sector_fill[definition_index],
                            [position[0], 0.03, position[2]],
                            yaw,
                            radius,
                            if hero {
                                [0.025, 0.16, 0.21, 1.0]
                            } else {
                                [0.25, 0.035, 0.015, 1.0]
                            },
                        ));
                        let progress = (elapsed as f32 / windup as f32).clamp(0.0, 1.0);
                        if progress > 0.0 {
                            scene.meshes.push(self.instance(
                                &self.sector_fill[definition_index],
                                [position[0], 0.035, position[2]],
                                yaw,
                                radius * progress,
                                color,
                            ));
                        }
                        scene.meshes.push(self.instance(
                            &self.sector[definition_index],
                            [position[0], 0.04, position[2]],
                            yaw,
                            stats.range as f32,
                            color,
                        ));
                    }
                }
                if !hero {
                    let mut bar = self.instance(
                        &self.bar,
                        local(0.0, 2.02, 0.0),
                        0.0,
                        actor.health as f32 / stats.max_health as f32,
                        [0.95, 0.2, 0.12, 1.0],
                    );
                    bar.orientation =
                        nico_presentation_control::coordinates::cylindrical_billboard(
                            camera.orientation,
                            [0.0, 1.0, 0.0],
                        )
                        .unwrap_or(nico_presentation::Quaternion::IDENTITY);
                    scene.meshes.push(bar);
                }
            }
        }
        let mut hud = Scene2d::default();
        // Keep a readable authored layout at small native window sizes.
        let hud_scale = (size[0] / 480.0).min(size[1] / 360.0).clamp(0.01, 1.0);
        let width = size[0] / hud_scale;
        let height = size[1] / hud_scale;
        self.rect(
            &mut hud,
            [24.0, 24.0],
            [320.0, 126.0],
            [0.015, 0.025, 0.04, 0.96],
        );
        self.text(
            &mut hud,
            &format!(
                "ARENA / WAVE {} OF {}",
                state.wave,
                arena_arpg_shared::TOTAL_WAVES
            ),
            [42.0, 40.0],
            1.6,
            [0.45, 0.9, 1.0, 1.0],
        );
        self.text(
            &mut hud,
            &format!(
                "HEALTH {:03} / {}",
                state.actors[0].health,
                state.actors[0].stats().max_health
            ),
            [42.0, 67.0],
            1.6,
            [0.9, 0.95, 1.0, 1.0],
        );
        self.rect(&mut hud, [42.0, 91.0], [280.0, 8.0], [0.1, 0.15, 0.19, 1.0]);
        if state.actors[0].health > 0 {
            self.rect(
                &mut hud,
                [42.0, 91.0],
                [
                    280.0 * state.actors[0].health as f32
                        / state.actors[0].stats().max_health as f32,
                    8.0,
                ],
                [0.04, 0.7, 0.85, 1.0],
            );
        }
        self.text(
            &mut hud,
            &format!(
                "MONSTERS {}   DODGE {}",
                state.monsters_remaining(),
                if state.actors[0].dodge_cooldown == 0 {
                    "READY"
                } else {
                    "WAIT"
                }
            ),
            [42.0, 116.0],
            1.35,
            [0.6, 0.75, 0.85, 1.0],
        );
        self.rect(
            &mut hud,
            [42.0, 136.0],
            [280.0, 5.0],
            [0.1, 0.15, 0.19, 1.0],
        );
        let ready = 1.0
            - state.actors[0]
                .dodge_cooldown
                .min(state.actors[0].definition().arena.dodge.cooldown_ticks) as f32
                / state.actors[0].definition().arena.dodge.cooldown_ticks as f32;
        if ready > 0.0 {
            self.rect(
                &mut hud,
                [42.0, 136.0],
                [280.0 * ready, 5.0],
                [0.25, 0.8, 0.9, 1.0],
            );
        }
        let strip = (width - 48.0).min(850.0);
        self.rect(
            &mut hud,
            [24.0, height - 75.0],
            [strip, 51.0],
            [0.015, 0.025, 0.04, 0.94],
        );
        self.text(
            &mut hud,
            "WASD MOVE  /  MOUSE LOOK  /  LMB ATTACK",
            [40.0, height - 64.0],
            1.4,
            [0.8, 0.9, 0.96, 1.0],
        );
        self.text(
            &mut hud,
            "SPACE DODGE  /  R RESTART  /  ESC RELEASE",
            [40.0, height - 43.0],
            1.3,
            [0.45, 0.65, 0.75, 1.0],
        );
        if !captured && state.state == RunState::Playing {
            self.text(
                &mut hud,
                "CLICK TO CONTROL",
                [(width - 240.0) / 2.0, 40.0],
                2.0,
                [1.0, 0.78, 0.4, 1.0],
            );
        }
        let hero = &state.actors[0];
        let danger = state.state == RunState::Playing && hero.health > 0 && state.actors[1..].iter().any(|a| {
            let stats = a.stats();
            a.health > 0 && (a.position.x - hero.position.x).hypot(a.position.z - hero.position.z) <= stats.range + 0.4
                && matches!(a.action, Action::Attack { elapsed, .. } if elapsed >= stats.windup.saturating_sub(12) && elapsed < stats.windup)
        });
        if danger {
            self.text(
                &mut hud,
                "INCOMING",
                [(width - 162.0) / 2.0, height - 118.0],
                3.0,
                [1.0, 0.9, 0.35, 1.0],
            );
        }
        if state.intermission_ticks > 0 {
            self.rect(
                &mut hud,
                [(width - 420.0) / 2.0, height * 0.32],
                [420.0, 106.0],
                [0.012, 0.02, 0.035, 0.96],
            );
            self.text(
                &mut hud,
                &format!("WAVE {} CLEARED", state.wave),
                [(width - 280.0) / 2.0, height * 0.32 + 22.0],
                2.5,
                [0.35, 1.0, 0.8, 1.0],
            );
            self.text(
                &mut hud,
                &format!("NEXT WAVE IN {}", state.intermission_ticks.div_ceil(60)),
                [(width - 240.0) / 2.0, height * 0.32 + 55.0],
                2.0,
                [0.8, 0.88, 0.95, 1.0],
            );
            self.text(
                &mut hud,
                "RECOVER 40 HEALTH",
                [(width - 192.0) / 2.0, height * 0.32 + 82.0],
                1.6,
                [0.45, 0.9, 1.0, 1.0],
            );
        }
        if state.state != RunState::Playing {
            let x = (width - 380.0) / 2.0;
            let y = height * 0.35;
            self.rect(&mut hud, [x, y], [380.0, 132.0], [0.012, 0.02, 0.035, 0.96]);
            self.text(
                &mut hud,
                if state.state == RunState::Won {
                    "ARENA CLEARED"
                } else {
                    "YOU HAVE FALLEN"
                },
                [x + 32.0, y + 30.0],
                3.0,
                if state.state == RunState::Won {
                    [0.35, 1.0, 0.8, 1.0]
                } else {
                    [1.0, 0.42, 0.28, 1.0]
                },
            );
            self.text(
                &mut hud,
                "PRESS R TO RESTART",
                [x + 55.0, y + 85.0],
                2.0,
                [0.8, 0.88, 0.95, 1.0],
            );
        }
        for quad in &mut hud.hud {
            quad.center = quad.center.map(|v| v * hud_scale);
            quad.size = quad.size.map(|v| v * hud_scale);
        }
        (scene, hud)
    }
    fn rect(&self, hud: &mut Scene2d, p: [f32; 2], size: [f32; 2], color: [f32; 4]) {
        rectangle(hud, p, size, color, self.white.clone());
    }
    fn text(&mut self, hud: &mut Scene2d, text: &str, p: [f32; 2], scale: f32, color: [f32; 4]) {
        self.font.draw(&mut hud.hud, text, p, scale, color);
    }
}
pub(crate) fn box_mesh(size: [f32; 3]) -> Arc<Mesh> {
    let uvs = std::array::from_fn(|face| [(face as f32 + 0.5) / 6.0, 0.5]);
    Arc::new(nico_assets::procedural::cuboid(size, uvs).expect("valid arena cuboid"))
}
fn sector_mesh(angle: f32) -> Arc<Mesh> {
    Arc::new(nico_assets::procedural::sector(angle, 32, [0.4167, 0.5]).expect("valid arena sector"))
}
fn arc_mesh(inner: f32, angle: f32) -> Arc<Mesh> {
    Arc::new(
        nico_assets::procedural::arc(inner, angle, 32, [0.4167, 0.5]).expect("valid arena arc"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn smallest_window_keeps_hud_quads_inside_viewport() {
        let mut visuals = Visuals::new();
        let mut state = arena_arpg_shared::Arena::default().snapshot().clone();
        for outcome in [RunState::Playing, RunState::Won, RunState::Lost] {
            state.state = outcome;
            let (_, hud) =
                visuals.render(&state, Camera3d::default(), [320.0, 240.0], false, 0.016);
            for quad in &hud.hud {
                for i in 0..2 {
                    assert!(quad.center[i] - quad.size[i] / 2.0 >= -0.01);
                    assert!(quad.center[i] + quad.size[i] / 2.0 <= [320.0, 240.0][i] + 0.01);
                }
            }
        }
    }
    #[test]
    fn final_windup_adds_warning_marker_and_pulses_without_changing_reach() {
        let mut state = arena_arpg_shared::Arena::default().snapshot().clone();
        let mut visuals = Visuals::new();
        state.actors[1].action = Action::Attack {
            id: 1,
            elapsed: 17,
            hit_mask: 0,
        };
        let (before, _) = visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        state.actors[1].action = Action::Attack {
            id: 1,
            elapsed: 18,
            hit_mask: 0,
        };
        let (warning, _) = visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        assert_eq!(warning.meshes.len(), before.meshes.len() + 2);
        let sector = warning
            .meshes
            .iter()
            .find(|m| {
                m.mesh.as_ref().is_some_and(|mesh| {
                    visuals
                        .sector
                        .iter()
                        .any(|candidate| Arc::ptr_eq(mesh, candidate))
                })
            })
            .unwrap();
        assert_eq!(sector.scale, 1.8);
        assert_eq!(sector.color, [1.0, 0.95, 0.7, 1.0]);
    }
    #[test]
    fn brute_telegraph_uses_shared_reach_and_longer_windup() {
        let mut state = arena_arpg_shared::Arena::default().snapshot().clone();
        state.actors[1].kind = arena_arpg_shared::ActorKind::Brute;
        state.actors[1].health = 100;
        state.actors[1].action = Action::Attack {
            id: 1,
            elapsed: 30,
            hit_mask: 0,
        };
        let mut visuals = Visuals::new();
        let (scene, _) = visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        let fills: Vec<_> = scene
            .meshes
            .iter()
            .filter(|m| {
                m.mesh.as_ref().is_some_and(|mesh| {
                    visuals
                        .sector_fill
                        .iter()
                        .any(|candidate| Arc::ptr_eq(mesh, candidate))
                })
            })
            .collect();
        assert_eq!(fills.len(), 2);
        assert!((fills[0].scale - 2.4).abs() < 0.0001);
        assert!((fills[1].scale - 1.5).abs() < 0.0001);
        assert_eq!(fills[1].color, [1.0, 0.23, 0.03, 1.0]);
    }
    #[test]
    fn telegraph_keeps_full_reach_fills_with_time_and_disappears_at_result() {
        let mut state = arena_arpg_shared::Arena::default().snapshot().clone();
        let mut visuals = Visuals::new();
        for (elapsed, expected) in [
            (0, vec![1.8]),
            (15, vec![1.8, 0.9]),
            (30, vec![1.8, 1.8]),
            (36, vec![]),
        ] {
            state.actors[1].action = Action::Attack {
                id: 1,
                elapsed,
                hit_mask: 0,
            };
            let (scene, _) =
                visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
            let fills: Vec<_> = scene
                .meshes
                .iter()
                .filter(|m| {
                    m.mesh.as_ref().is_some_and(|mesh| {
                        visuals
                            .sector_fill
                            .iter()
                            .any(|candidate| Arc::ptr_eq(mesh, candidate))
                    })
                })
                .collect();
            assert_eq!(fills.iter().map(|m| m.scale).collect::<Vec<_>>(), expected);
            assert!(
                fills
                    .iter()
                    .all(|m| m.position[0] == -5.0 && m.position[2] == 5.0)
            );
        }
        state.state = RunState::Lost;
        state.actors[1].action = Action::Attack {
            id: 1,
            elapsed: 30,
            hit_mask: 0,
        };
        let (scene, _) = visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        assert!(
            !scene
                .meshes
                .iter()
                .any(|m| m.mesh.as_ref().is_some_and(|mesh| visuals
                    .sector_fill
                    .iter()
                    .any(|candidate| Arc::ptr_eq(mesh, candidate))
                    || visuals
                        .sector
                        .iter()
                        .any(|candidate| Arc::ptr_eq(mesh, candidate))))
        );
    }

    #[test]
    fn stationary_actors_do_not_walk_and_restart_clears_hit_flash() {
        let mut state = arena_arpg_shared::Arena::default().snapshot().clone();
        let mut visuals = Visuals::new();
        let (first, _) = visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        state.tick = 10;
        let (second, _) = visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        let positions = |scene: Scene3d| {
            scene
                .meshes
                .into_iter()
                .map(|m| m.position)
                .collect::<Vec<_>>()
        };
        assert_eq!(positions(first), positions(second));
        state.actors[0].health = 80;
        visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        assert!(visuals.flash[0] > 0.0);
        state.run_id += 1;
        state.actors[0].health = 100;
        visuals.render(&state, Camera3d::default(), [1280., 720.], true, 0.016);
        assert_eq!(visuals.flash, [0.0; 4]);
    }

    #[test]
    fn draw_state_tracks_health_and_result_without_mutating_gameplay() {
        let arena = arena_arpg_shared::Arena::default();
        let state = arena.snapshot().clone();
        let mut visuals = Visuals::new();
        let (scene, hud) =
            visuals.render(&state, Camera3d::default(), [1280.0, 720.0], false, 0.016);
        for wall in arena_arpg_shared::geometry::WALLS {
            assert!(
                scene
                    .meshes
                    .iter()
                    .any(|mesh| mesh.position == wall.center.map(|v| v as f32))
            );
        }
        assert!(!hud.hud.is_empty());
        assert!(
            scene
                .meshes
                .iter()
                .all(|m| m.mesh.is_some() && m.texture.is_some() && m.scale > 0.0)
        );
        assert_eq!(state, arena.snapshot().clone());
        let mut lost = state;
        lost.state = RunState::Lost;
        lost.actors[0].health = 0;
        let (_, terminal) =
            visuals.render(&lost, Camera3d::default(), [1280.0, 720.0], true, 0.016);
        assert!(terminal.hud.len() > hud.hud.len());
    }
}
