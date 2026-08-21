//! Host loop for the minimal game's dedicated server.

use std::{
    thread,
    time::{Duration, Instant},
};

use nico_runtime::{App, AppRunner, RuntimeResult};

/// Runs authoritative simulation continuously at a fixed rate.
pub struct FixedRateServerRunner {
    tick_interval: Duration,
}

impl FixedRateServerRunner {
    /// Creates a server runner with a non-zero tick interval.
    #[must_use]
    pub fn new(tick_interval: Duration) -> Self {
        assert!(
            !tick_interval.is_zero(),
            "server tick interval must be non-zero"
        );
        Self { tick_interval }
    }
}

impl AppRunner for FixedRateServerRunner {
    fn run(self, app: &mut App) -> RuntimeResult<()> {
        app.start()?;
        let mut execution = Ok(());

        while !app.exit_requested() {
            let tick_started = Instant::now();
            if let Err(error) = app.tick(self.tick_interval) {
                execution = Err(error);
                break;
            }

            if !app.exit_requested() {
                thread::sleep(self.tick_interval.saturating_sub(tick_started.elapsed()));
            }
        }

        let shutdown = app.shutdown();
        execution.and(shutdown)
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use nico_runtime::{AppBuilder, AppState, RuntimeResult, Stage};

    use super::FixedRateServerRunner;

    #[derive(Default)]
    struct Updates(u64);

    #[test]
    fn runner_ticks_until_a_system_requests_exit() -> RuntimeResult<()> {
        let mut builder = AppBuilder::new();
        builder.insert_resource(Updates::default());
        builder.add_system(Stage::Update, "exit after three ticks", |context| {
            let updates = context.world.resource_mut::<Updates>()?;
            updates.0 += 1;
            if updates.0 == 3 {
                context.request_exit();
            }
            Ok(())
        });
        let mut app = builder.build()?;

        app.run_with(FixedRateServerRunner::new(Duration::from_nanos(1)))?;

        assert_eq!(app.world().resource::<Updates>()?.0, 3);
        assert_eq!(app.state(), AppState::Stopped);
        Ok(())
    }
}
