//! Local device-input abstraction.
//!
//! Gameplay should consume semantic commands rather than these device events,
//! allowing a server to obtain equivalent commands from the network.

/// Stable identity of a physical input control within a provider.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct ControlId(pub u32);

/// State transition for a digital control.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ButtonState {
    /// The control became active.
    Pressed,
    /// The control became inactive.
    Released,
}

/// Normalized local-device event.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum InputEvent {
    /// A digital control changed state.
    Button {
        /// Provider-defined stable control identity.
        control: ControlId,
        /// New state.
        state: ButtonState,
    },
    /// A one-dimensional analog control changed value.
    Axis {
        /// Provider-defined stable control identity.
        control: ControlId,
        /// Normalized value when the device supports normalization.
        value: f32,
    },
    /// A pointer moved by a relative amount.
    PointerMotion { x: f64, y: f64 },
}

/// Source of normalized local-device input.
pub trait InputSource: Send {
    /// Appends currently pending events in provider order.
    fn drain_events(&mut self, output: &mut Vec<InputEvent>);
}
