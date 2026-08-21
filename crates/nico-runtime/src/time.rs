use std::time::Duration;

/// Timing information for one system invocation.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Time {
    frame: u64,
    fixed_tick: u64,
    delta: Duration,
    elapsed: Duration,
    interpolation: f64,
}

impl Time {
    pub(crate) const fn startup() -> Self {
        Self::frame(0, 0, Duration::ZERO, Duration::ZERO, 0.0)
    }

    pub(crate) const fn shutdown(frame: u64, elapsed: Duration) -> Self {
        Self::frame(frame, 0, Duration::ZERO, elapsed, 0.0)
    }

    pub(crate) const fn fixed(
        frame: u64,
        fixed_tick: u64,
        delta: Duration,
        elapsed: Duration,
    ) -> Self {
        Self::frame(frame, fixed_tick, delta, elapsed, 0.0)
    }

    pub(crate) const fn frame(
        frame: u64,
        fixed_tick: u64,
        delta: Duration,
        elapsed: Duration,
        interpolation: f64,
    ) -> Self {
        Self {
            frame,
            fixed_tick,
            delta,
            elapsed,
            interpolation,
        }
    }

    /// Returns the zero-based host frame number.
    #[must_use]
    pub const fn frame_number(self) -> u64 {
        self.frame
    }

    /// Returns the number of fixed ticks that have begun.
    #[must_use]
    pub const fn fixed_tick(self) -> u64 {
        self.fixed_tick
    }

    /// Returns the delta for this invocation.
    #[must_use]
    pub const fn delta(self) -> Duration {
        self.delta
    }

    /// Returns elapsed host time.
    #[must_use]
    pub const fn elapsed(self) -> Duration {
        self.elapsed
    }

    /// Returns the remaining fixed-step fraction for presentation interpolation.
    #[must_use]
    pub const fn interpolation(self) -> f64 {
        self.interpolation
    }
}
