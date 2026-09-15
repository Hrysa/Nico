//! Frame-to-fixed-step input delivery, without runtime or semantic bindings.
/// Held axes replace older values; deltas sum and button edges coalesce until
/// consumed. Edges are booleans: multiple presses before a tick produce one edge.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct InputFrame<const H: usize, const D: usize, const E: usize> {
    pub held: [f32; H],
    pub deltas: [f32; D],
    pub pressed: [bool; E],
}
impl<const H: usize, const D: usize, const E: usize> Default for InputFrame<H, D, E> {
    fn default() -> Self {
        Self {
            held: [0.0; H],
            deltas: [0.0; D],
            pressed: [false; E],
        }
    }
}
#[derive(Default)]
pub struct FixedInput<const H: usize, const D: usize, const E: usize> {
    pending: InputFrame<H, D, E>,
    fresh: bool,
}
impl<const H: usize, const D: usize, const E: usize> FixedInput<H, D, E> {
    pub fn push(&mut self, frame: InputFrame<H, D, E>) {
        self.pending.held = frame.held;
        for i in 0..D {
            self.pending.deltas[i] += frame.deltas[i];
        }
        for i in 0..E {
            self.pending.pressed[i] |= frame.pressed[i];
        }
        self.fresh = true;
    }
    pub fn has_pending_frame(&self) -> bool {
        self.fresh
    }
    /// Consume transient input once. Subsequent catch-up ticks retain held axes.
    pub fn take(&mut self) -> InputFrame<H, D, E> {
        let frame = self.pending;
        self.pending.deltas = [0.0; D];
        self.pending.pressed = [false; E];
        self.fresh = false;
        frame
    }
    /// Caller chooses when focus changes or gameplay resets require cancellation.
    pub fn clear(&mut self) {
        self.pending = InputFrame::default();
        self.fresh = false;
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn frames_accumulate_until_consumption_then_only_held_input_repeats() {
        let mut input = FixedInput::<2, 2, 2>::default();
        input.push(InputFrame {
            held: [1.0, 0.0],
            deltas: [2.0, 3.0],
            pressed: [true, false],
        });
        input.push(InputFrame {
            held: [0.0, 1.0],
            deltas: [-1.0, 4.0],
            pressed: [false, true],
        });
        assert!(input.has_pending_frame());
        assert_eq!(
            input.take(),
            InputFrame {
                held: [0.0, 1.0],
                deltas: [1.0, 7.0],
                pressed: [true, true]
            }
        );
        assert!(!input.has_pending_frame());
        assert_eq!(
            input.take(),
            InputFrame {
                held: [0.0, 1.0],
                ..Default::default()
            }
        );
        input.push(InputFrame {
            pressed: [true, true],
            ..Default::default()
        });
        input.clear();
        assert_eq!(input.take(), InputFrame::default());
    }
}
