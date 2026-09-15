//! Provider-neutral input device state management.
//!
//! Platform adapters feed normalized device events into [`InputManager`]. The
//! manager aggregates physical device state without depending on a windowing,
//! runtime, presentation, or game crate. Client applications map [`InputState`]
//! into game-owned semantic commands.

pub mod fixed;

use std::collections::{HashMap, HashSet};

/// Provider-local identity assigned by an input adapter.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct InputDeviceId(u64);

impl InputDeviceId {
    /// Creates an adapter-local device identity.
    #[must_use]
    pub const fn new(value: u64) -> Self {
        Self(value)
    }
}

/// Broad device class used for normalized aggregation and bindings.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum InputDeviceKind {
    Keyboard,
    Pointer,
    Gamepad,
    Touch,
}

/// Adapter-defined control identity within one device class.
///
/// Control values stay in the application adapter. Shared gameplay receives
/// only commands emitted by the mapper.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct InputControlId(u64);

impl InputControlId {
    /// Creates a control identity selected by an adapter.
    #[must_use]
    pub const fn new(value: u64) -> Self {
        Self(value)
    }
}

/// Normalized event accepted from a keyboard, pointer, gamepad, or touch device.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum InputEvent {
    Connected {
        device: InputDeviceId,
        kind: InputDeviceKind,
    },
    Disconnected {
        device: InputDeviceId,
    },
    Button {
        device: InputDeviceId,
        control: InputControlId,
        pressed: bool,
    },
    /// Persistent normalized scalar value, conventionally in `-1.0..=1.0`.
    Axis {
        device: InputDeviceId,
        control: InputControlId,
        value: f32,
    },
    /// Persistent two-dimensional value such as a stick or touch position.
    Vector {
        device: InputDeviceId,
        control: InputControlId,
        value: [f32; 2],
    },
    /// Frame-local two-dimensional delta such as pointer motion or scrolling.
    Motion {
        device: InputDeviceId,
        control: InputControlId,
        delta: [f32; 2],
    },
}

struct DeviceState {
    kind: InputDeviceKind,
    buttons: HashSet<InputControlId>,
    axes: HashMap<InputControlId, f32>,
    vectors: HashMap<InputControlId, [f32; 2]>,
    motions: HashMap<InputControlId, [f32; 2]>,
}

impl DeviceState {
    fn new(kind: InputDeviceKind) -> Self {
        Self {
            kind,
            buttons: HashSet::new(),
            axes: HashMap::new(),
            vectors: HashMap::new(),
            motions: HashMap::new(),
        }
    }
}

/// Read-only device state supplied to a game-command mapper.
#[derive(Default)]
pub struct InputState {
    devices: HashMap<InputDeviceId, DeviceState>,
    pressed: HashSet<(InputDeviceKind, InputControlId)>,
    released: HashSet<(InputDeviceKind, InputControlId)>,
}

impl InputState {
    /// Returns whether any device of this class holds a button.
    #[must_use]
    pub fn button(&self, kind: InputDeviceKind, control: InputControlId) -> bool {
        self.devices
            .values()
            .any(|device| device.kind == kind && device.buttons.contains(&control))
    }

    /// Returns whether the aggregated button changed from up to down this frame.
    #[must_use]
    pub fn just_pressed(&self, kind: InputDeviceKind, control: InputControlId) -> bool {
        self.pressed.contains(&(kind, control))
    }

    /// Returns whether the aggregated button changed from down to up this frame.
    #[must_use]
    pub fn just_released(&self, kind: InputDeviceKind, control: InputControlId) -> bool {
        self.released.contains(&(kind, control))
    }

    /// Returns the strongest absolute scalar value across devices of a class.
    #[must_use]
    pub fn axis(&self, kind: InputDeviceKind, control: InputControlId) -> f32 {
        self.devices
            .values()
            .filter(|device| device.kind == kind)
            .filter_map(|device| device.axes.get(&control).copied())
            .max_by(|left, right| left.abs().total_cmp(&right.abs()))
            .unwrap_or(0.0)
    }

    /// Returns the strongest two-dimensional value across devices of a class.
    #[must_use]
    pub fn vector(&self, kind: InputDeviceKind, control: InputControlId) -> [f32; 2] {
        self.devices
            .values()
            .filter(|device| device.kind == kind)
            .filter_map(|device| device.vectors.get(&control).copied())
            .max_by(|left, right| length_squared(*left).total_cmp(&length_squared(*right)))
            .unwrap_or([0.0, 0.0])
    }

    /// Returns accumulated frame-local motion across devices of a class.
    #[must_use]
    pub fn motion(&self, kind: InputDeviceKind, control: InputControlId) -> [f32; 2] {
        self.devices
            .values()
            .filter(|device| device.kind == kind)
            .filter_map(|device| device.motions.get(&control))
            .fold([0.0, 0.0], |sum, motion| {
                [sum[0] + motion[0], sum[1] + motion[1]]
            })
    }
}

fn length_squared(value: [f32; 2]) -> f32 {
    value[0].mul_add(value[0], value[1] * value[1])
}

/// Tracks all connected input devices and their aggregate state.
#[derive(Default)]
pub struct InputManager {
    state: InputState,
}

impl InputManager {
    /// Creates an empty manager.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Applies one normalized provider event.
    pub fn handle(&mut self, event: InputEvent) {
        match event {
            InputEvent::Connected { device, kind } => self.connect(device, kind),
            InputEvent::Disconnected { device } => self.disconnect(device),
            InputEvent::Button {
                device,
                control,
                pressed,
            } => self.set_button(device, control, pressed),
            InputEvent::Axis {
                device,
                control,
                value,
            } => {
                if let Some(state) = self.state.devices.get_mut(&device) {
                    state
                        .axes
                        .insert(control, finite_or_zero(value).clamp(-1.0, 1.0));
                }
            }
            InputEvent::Vector {
                device,
                control,
                value,
            } => {
                if let Some(state) = self.state.devices.get_mut(&device) {
                    state.vectors.insert(control, finite_vector(value));
                }
            }
            InputEvent::Motion {
                device,
                control,
                delta,
            } => {
                if let Some(state) = self.state.devices.get_mut(&device) {
                    let delta = finite_vector(delta);
                    let motion = state.motions.entry(control).or_insert([0.0, 0.0]);
                    motion[0] += delta[0];
                    motion[1] += delta[1];
                }
            }
        }
    }

    /// Clears button transitions and motion deltas after the host consumes them.
    pub fn end_frame(&mut self) {
        self.state.pressed.clear();
        self.state.released.clear();
        for device in self.state.devices.values_mut() {
            device.motions.clear();
        }
    }

    /// Releases every held button and resets continuous controls.
    ///
    /// Adapters use this when focus is lost so gameplay cannot retain stuck
    /// controls after the platform stops delivering input.
    pub fn release_all(&mut self) {
        let releases = self
            .state
            .devices
            .values()
            .map(|device| (device.kind, &device.buttons))
            .flat_map(|(kind, buttons)| buttons.iter().map(move |control| (kind, *control)))
            .collect::<Vec<_>>();
        for released in releases {
            self.state.released.insert(released);
            self.state.pressed.remove(&released);
        }
        for device in self.state.devices.values_mut() {
            device.buttons.clear();
            device.axes.clear();
            device.vectors.clear();
            device.motions.clear();
        }
    }

    /// Returns the current state for diagnostics or custom mapping tests.
    #[must_use]
    pub const fn state(&self) -> &InputState {
        &self.state
    }

    fn connect(&mut self, device: InputDeviceId, kind: InputDeviceKind) {
        if self.state.devices.contains_key(&device) {
            self.disconnect(device);
        }
        self.state.devices.insert(device, DeviceState::new(kind));
    }

    fn disconnect(&mut self, device: InputDeviceId) {
        let Some(disconnected) = self.state.devices.remove(&device) else {
            return;
        };
        let kind = disconnected.kind;
        for control in disconnected.buttons {
            if !self.state.button(kind, control) {
                self.state.released.insert((kind, control));
                self.state.pressed.remove(&(kind, control));
            }
        }
    }

    fn set_button(&mut self, device: InputDeviceId, control: InputControlId, down: bool) {
        let Some(kind) = self.state.devices.get(&device).map(|device| device.kind) else {
            return;
        };
        let was_down = self.state.button(kind, control);
        if let Some(state) = self.state.devices.get_mut(&device) {
            if down {
                state.buttons.insert(control);
            } else {
                state.buttons.remove(&control);
            }
        }
        let is_down = self.state.button(kind, control);
        if !was_down && is_down {
            self.state.pressed.insert((kind, control));
            self.state.released.remove(&(kind, control));
        } else if was_down && !is_down {
            self.state.released.insert((kind, control));
            self.state.pressed.remove(&(kind, control));
        }
    }
}

fn finite_or_zero(value: f32) -> f32 {
    if value.is_finite() { value } else { 0.0 }
}

fn finite_vector(value: [f32; 2]) -> [f32; 2] {
    [finite_or_zero(value[0]), finite_or_zero(value[1])]
}

#[cfg(test)]
mod tests {
    use super::{
        InputControlId, InputDeviceId, InputDeviceKind, InputEvent, InputManager, InputState,
    };

    const LEFT: InputControlId = InputControlId::new(1);
    const RIGHT: InputControlId = InputControlId::new(2);
    const FIRE: InputControlId = InputControlId::new(3);
    const STICK: InputControlId = InputControlId::new(4);
    const LOOK: InputControlId = InputControlId::new(5);
    const TOUCH: InputControlId = InputControlId::new(6);

    #[derive(Debug, PartialEq)]
    enum Command {
        Move([f32; 2]),
        Fire,
        Look([f32; 2]),
        Touch([f32; 2]),
    }

    fn manager() -> InputManager {
        InputManager::new()
    }

    fn map_commands(input: &InputState) -> Vec<Command> {
        let keyboard_x = u8::from(input.button(InputDeviceKind::Keyboard, RIGHT)) as f32
            - u8::from(input.button(InputDeviceKind::Keyboard, LEFT)) as f32;
        let stick = input.vector(InputDeviceKind::Gamepad, STICK);
        let mut commands = vec![Command::Move([keyboard_x + stick[0], stick[1]])];
        if input.just_pressed(InputDeviceKind::Gamepad, FIRE) {
            commands.push(Command::Fire);
        }
        commands.push(Command::Look(input.motion(InputDeviceKind::Pointer, LOOK)));
        commands.push(Command::Touch(input.vector(InputDeviceKind::Touch, TOUCH)));
        commands
    }

    #[test]
    fn multiple_device_classes_map_to_game_owned_commands() {
        let mut input = manager();
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(1),
            kind: InputDeviceKind::Keyboard,
        });
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(2),
            kind: InputDeviceKind::Gamepad,
        });
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(3),
            kind: InputDeviceKind::Pointer,
        });
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(4),
            kind: InputDeviceKind::Touch,
        });
        input.handle(InputEvent::Button {
            device: InputDeviceId::new(1),
            control: RIGHT,
            pressed: true,
        });
        input.handle(InputEvent::Vector {
            device: InputDeviceId::new(2),
            control: STICK,
            value: [0.25, -0.5],
        });
        input.handle(InputEvent::Button {
            device: InputDeviceId::new(2),
            control: FIRE,
            pressed: true,
        });
        input.handle(InputEvent::Motion {
            device: InputDeviceId::new(3),
            control: LOOK,
            delta: [4.0, -2.0],
        });
        input.handle(InputEvent::Vector {
            device: InputDeviceId::new(4),
            control: TOUCH,
            value: [120.0, 80.0],
        });

        let commands = map_commands(input.state());
        input.end_frame();

        assert_eq!(
            commands,
            [
                Command::Move([1.25, -0.5]),
                Command::Fire,
                Command::Look([4.0, -2.0]),
                Command::Touch([120.0, 80.0])
            ]
        );
    }

    #[test]
    fn aggregate_button_transitions_wait_for_every_device_to_release() {
        let mut input = manager();
        for device in [InputDeviceId::new(1), InputDeviceId::new(2)] {
            input.handle(InputEvent::Connected {
                device,
                kind: InputDeviceKind::Keyboard,
            });
            input.handle(InputEvent::Button {
                device,
                control: LEFT,
                pressed: true,
            });
        }
        input.end_frame();

        input.handle(InputEvent::Button {
            device: InputDeviceId::new(1),
            control: LEFT,
            pressed: false,
        });
        assert!(!input.state().just_released(InputDeviceKind::Keyboard, LEFT));

        input.handle(InputEvent::Disconnected {
            device: InputDeviceId::new(2),
        });
        assert!(input.state().just_released(InputDeviceKind::Keyboard, LEFT));
    }

    #[test]
    fn focus_loss_releases_buttons_and_clears_continuous_state() {
        let mut input = manager();
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(1),
            kind: InputDeviceKind::Gamepad,
        });
        input.handle(InputEvent::Button {
            device: InputDeviceId::new(1),
            control: FIRE,
            pressed: true,
        });
        input.handle(InputEvent::Axis {
            device: InputDeviceId::new(1),
            control: LEFT,
            value: 0.75,
        });
        input.handle(InputEvent::Vector {
            device: InputDeviceId::new(1),
            control: STICK,
            value: [0.5, 0.5],
        });

        input.release_all();

        assert!(!input.state().button(InputDeviceKind::Gamepad, FIRE));
        assert!(input.state().just_released(InputDeviceKind::Gamepad, FIRE));
        assert_eq!(input.state().axis(InputDeviceKind::Gamepad, LEFT), 0.0);
        assert_eq!(
            input.state().vector(InputDeviceKind::Gamepad, STICK),
            [0.0, 0.0]
        );
    }

    #[test]
    fn motion_is_accumulated_for_one_frame_then_cleared() {
        let mut input = manager();
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(1),
            kind: InputDeviceKind::Pointer,
        });
        for delta in [[1.0, 2.0], [3.0, -1.0]] {
            input.handle(InputEvent::Motion {
                device: InputDeviceId::new(1),
                control: LOOK,
                delta,
            });
        }
        assert_eq!(
            input.state().motion(InputDeviceKind::Pointer, LOOK),
            [4.0, 1.0]
        );

        input.end_frame();

        assert_eq!(
            input.state().motion(InputDeviceKind::Pointer, LOOK),
            [0.0, 0.0]
        );
    }

    #[test]
    fn non_finite_provider_values_are_sanitized() {
        let mut input = manager();
        input.handle(InputEvent::Connected {
            device: InputDeviceId::new(1),
            kind: InputDeviceKind::Gamepad,
        });
        input.handle(InputEvent::Axis {
            device: InputDeviceId::new(1),
            control: LEFT,
            value: f32::NAN,
        });
        input.handle(InputEvent::Vector {
            device: InputDeviceId::new(1),
            control: STICK,
            value: [f32::INFINITY, -0.25],
        });

        assert_eq!(input.state().axis(InputDeviceKind::Gamepad, LEFT), 0.0);
        assert_eq!(
            input.state().vector(InputDeviceKind::Gamepad, STICK),
            [0.0, -0.25]
        );
    }
}
