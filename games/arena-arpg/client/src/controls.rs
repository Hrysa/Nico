use crate::camera::Camera;
use arena_arpg_shared::{Arena, InputFocusLost, TickInput, Vec2};
use nico_input::{
    InputDeviceKind as Kind, InputState,
    fixed::{FixedInput, InputFrame},
};
use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage, events::EventReader};
use nico_winit::{NativeWindowState, WindowFocusLost, keyboard, pointer};
#[derive(Clone, Copy, Default)]
pub struct FrameInput {
    movement: [f32; 2],
    look: [f32; 2],
    attack: bool,
    dodge: bool,
    restart: bool,
}
pub fn map_input(input: &InputState, output: &mut Vec<FrameInput>) {
    let button = |key| input.button(Kind::Keyboard, key);
    output.push(FrameInput {
        movement: [
            (button(keyboard::D) as u8 as f32) - (button(keyboard::A) as u8 as f32),
            (button(keyboard::W) as u8 as f32) - (button(keyboard::S) as u8 as f32),
        ],
        look: input.motion(Kind::Pointer, pointer::MOTION),
        attack: input.just_pressed(Kind::Pointer, pointer::LEFT),
        dodge: input.just_pressed(Kind::Keyboard, keyboard::SPACE),
        restart: input.just_pressed(Kind::Keyboard, keyboard::R),
    });
}
#[derive(Default)]
struct Held {
    input: FixedInput<2, 2, 3>,
    run: u64,
    wave: u8,
    suppress: bool,
}
pub struct ControlsPlugin;
impl Plugin for ControlsPlugin {
    fn build(&self, builder: &mut AppBuilder) -> RuntimeResult<()> {
        builder.insert_resource(Camera::default());
        builder.insert_resource(Held::default());
        let mut frames = EventReader::<FrameInput>::new();
        let mut focus = EventReader::<WindowFocusLost>::new();
        builder.add_system(Stage::FixedUpdate, "arena_client::input", move |ctx| {
            let run = ctx.world.resource::<Arena>()?.snapshot().run_id;
            let wave = ctx.world.resource::<Arena>()?.snapshot().wave;
            let window = ctx
                .world
                .resource::<NativeWindowState>()
                .cloned()
                .unwrap_or_default();
            let lost = ctx.events.read(&mut focus).count() != 0;
            let held = ctx.world.resource_mut::<Held>()?;
            for frame in ctx.events.read(&mut frames) {
                held.input.push(InputFrame {
                    held: frame.movement,
                    deltas: frame.look,
                    pressed: [frame.attack, frame.dodge, frame.restart],
                });
            }
            let has_frame = held.input.has_pending_frame();
            let sample = held.input.take();
            let mut input = FrameInput {
                movement: sample.held,
                look: sample.deltas,
                attack: sample.pressed[0],
                dodge: sample.pressed[1],
                restart: sample.pressed[2],
            };
            let reset = held.run != 0 && (held.run != run || held.wave != wave);
            if reset || lost {
                held.suppress = true;
                held.input.clear();
                input = FrameInput::default();
            }
            held.run = run;
            held.wave = wave;
            if has_frame && !reset && !lost && input.movement == [0.0; 2] {
                held.suppress = false;
            }
            if held.suppress {
                input.movement = [0.0; 2];
            }
            if !window.focused || !window.pointer_captured {
                held.input.clear();
                input.movement = [0.0; 2];
                input.attack = false;
                input.dodge = false;
                input.look = [0.0; 2];
            }
            let movement = input.movement;
            if lost {
                ctx.events.send(InputFocusLost);
            }
            let camera = ctx.world.resource_mut::<Camera>()?;
            camera.orbit(input.look);
            let yaw = (camera.rig.yaw() as f64).clamp(-std::f64::consts::PI, std::f64::consts::PI);
            let x = movement[0] as f64;
            let z = movement[1] as f64;
            let length = (x * x + z * z).sqrt().max(1.0);
            let rotated = nico_presentation_control::coordinates::rotate_on_floor(
                yaw,
                [-x / length, z / length],
            );
            let direction = Vec2::new(rotated[0].clamp(-1.0, 1.0), rotated[1].clamp(-1.0, 1.0));
            let mut command = TickInput::idle(run);
            command.movement = direction;
            command.restart = input.restart;
            if input.attack {
                command.attack_yaw = Some(yaw);
            }
            if input.dodge {
                command.dodge = Some(if x == 0.0 && z == 0.0 {
                    ctx.world.resource::<Arena>()?.snapshot().actors[0].facing
                } else {
                    direction
                });
            }
            ctx.events.send(command);
            Ok(())
        });
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use arena_arpg_shared::{ArenaPlugin, FIXED_STEP};
    fn app() -> nico_runtime::App {
        let mut app = AppBuilder::new()
            .add_plugin(ControlsPlugin)
            .add_plugin(ArenaPlugin)
            .build()
            .unwrap();
        app.world_mut().insert_resource(NativeWindowState {
            focused: true,
            pointer_captured: true,
            ..Default::default()
        });
        app.start().unwrap();
        app
    }
    #[test]
    fn camera_relative_input_persists_across_catchup_ticks() {
        let mut app = app();
        app.send_event(FrameInput {
            movement: [1.0, 0.0],
            ..Default::default()
        });
        app.tick(FIXED_STEP * 3).unwrap();
        let s = app.world().resource::<Arena>().unwrap().snapshot();
        assert!((s.actors[0].position.x + 0.2).abs() < 1e-10);
        assert_eq!(s.actors[0].position.z, -6.0);
        app.send_event(FrameInput::default());
        app.tick(FIXED_STEP).unwrap();
        assert!(
            (app.world().resource::<Arena>().unwrap().snapshot().actors[0]
                .position
                .x
                + 0.2)
                .abs()
                < 1e-10
        );
        app.shutdown().unwrap();
    }
    #[test]
    fn restart_requires_held_movement_to_be_released() {
        let mut app = app();
        app.send_event(FrameInput {
            movement: [0.0, 1.0],
            restart: true,
            ..Default::default()
        });
        app.tick(FIXED_STEP).unwrap();
        for _ in 0..3 {
            app.send_event(FrameInput {
                movement: [0.0, 1.0],
                ..Default::default()
            });
            app.tick(FIXED_STEP).unwrap();
        }
        assert_eq!(
            app.world().resource::<Arena>().unwrap().snapshot().actors[0]
                .position
                .z,
            -6.0
        );
        app.send_event(FrameInput::default());
        app.tick(FIXED_STEP).unwrap();
        app.send_event(FrameInput {
            movement: [0.0, 1.0],
            ..Default::default()
        });
        app.tick(FIXED_STEP).unwrap();
        assert!(
            app.world().resource::<Arena>().unwrap().snapshot().actors[0]
                .position
                .z
                > -6.0
        );
        app.shutdown().unwrap();
    }
    #[test]
    fn focus_loss_clears_motion_and_held_attack_is_an_edge() {
        let mut app = app();
        app.send_event(FrameInput {
            movement: [0.0, 1.0],
            attack: true,
            ..Default::default()
        });
        app.tick(FIXED_STEP).unwrap();
        app.send_event(WindowFocusLost);
        app.tick(FIXED_STEP * 40).unwrap();
        let s = app.world().resource::<Arena>().unwrap().snapshot();
        assert_eq!(s.actors[0].position.z, -6.0);
        assert_eq!(s.actors[0].action, arena_arpg_shared::Action::Idle);
        app.shutdown().unwrap();
    }
}

#[cfg(test)]
mod boundary_tests {
    use super::*;
    use arena_arpg_shared::{ArenaPlugin, FIXED_STEP, StepReport};
    #[test]
    fn rounded_camera_pi_and_diagonal_input_produce_valid_combat() {
        let mut app = AppBuilder::new()
            .add_plugin(ControlsPlugin)
            .add_plugin(ArenaPlugin)
            .build()
            .unwrap();
        app.world_mut().insert_resource(NativeWindowState {
            focused: true,
            pointer_captured: true,
            ..Default::default()
        });
        app.start().unwrap();
        app.world_mut()
            .resource_mut::<Camera>()
            .unwrap()
            .rig
            .set_angles(std::f32::consts::PI, 0.48);
        app.send_event(FrameInput {
            movement: [1.0, 1.0],
            attack: true,
            ..Default::default()
        });
        app.tick(FIXED_STEP).unwrap();
        assert_eq!(
            app.world().resource::<StepReport>().unwrap().rejection,
            None
        );
        assert!(matches!(
            app.world().resource::<Arena>().unwrap().snapshot().actors[0].action,
            arena_arpg_shared::Action::Attack { .. }
        ));
        app.shutdown().unwrap();
    }
}
