//! Minimal-game input bindings and semantic command mapping.

use minimal_game_shared::{MovementVector, PlayerCommand};
use nico_input::{InputControlId, InputDeviceKind, InputState};
use nico_winit::keyboard;

const GAMEPAD_MOVEMENT: InputControlId = InputControlId::new(1);

pub(crate) fn map_player_input(input: &InputState, output: &mut Vec<PlayerCommand>) {
    let left = input.button(InputDeviceKind::Keyboard, keyboard::A)
        || input.button(InputDeviceKind::Keyboard, keyboard::LEFT);
    let right = input.button(InputDeviceKind::Keyboard, keyboard::D)
        || input.button(InputDeviceKind::Keyboard, keyboard::RIGHT);
    let up = input.button(InputDeviceKind::Keyboard, keyboard::W)
        || input.button(InputDeviceKind::Keyboard, keyboard::UP);
    let down = input.button(InputDeviceKind::Keyboard, keyboard::S)
        || input.button(InputDeviceKind::Keyboard, keyboard::DOWN);
    let gamepad = input.vector(InputDeviceKind::Gamepad, GAMEPAD_MOVEMENT);
    let x = u8::from(right) as f32 - u8::from(left) as f32 + gamepad[0];
    let y = u8::from(up) as f32 - u8::from(down) as f32 + gamepad[1];
    output.push(PlayerCommand::move_in(MovementVector::normalized(x, y)));
}

#[cfg(test)]
mod tests {
    use nico_input::{InputDeviceId, InputEvent, InputManager};

    use super::*;

    #[test]
    fn device_state_maps_to_normalized_game_movement_commands() {
        let mut input = InputManager::new();
        let keyboard_device = InputDeviceId::new(1);
        input.handle(InputEvent::Connected {
            device: keyboard_device,
            kind: InputDeviceKind::Keyboard,
        });
        for control in [keyboard::W, keyboard::D] {
            input.handle(InputEvent::Button {
                device: keyboard_device,
                control,
                pressed: true,
            });
        }

        let mut commands = Vec::new();
        map_player_input(input.state(), &mut commands);
        let movement = commands[0].movement();
        let diagonal = 1.0_f32 / 2.0_f32.sqrt();
        assert!((movement.x() - diagonal).abs() < f32::EPSILON);
        assert!((movement.y() - diagonal).abs() < f32::EPSILON);

        input.release_all();
        let gamepad = InputDeviceId::new(2);
        input.handle(InputEvent::Connected {
            device: gamepad,
            kind: InputDeviceKind::Gamepad,
        });
        input.handle(InputEvent::Vector {
            device: gamepad,
            control: GAMEPAD_MOVEMENT,
            value: [-0.25, 0.5],
        });
        commands.clear();
        map_player_input(input.state(), &mut commands);

        assert_eq!(commands[0].movement().x(), -0.25);
        assert_eq!(commands[0].movement().y(), 0.5);
    }
}
