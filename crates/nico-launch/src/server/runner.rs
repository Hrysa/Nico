//! Fixed-rate native server host, shared by games.

use std::{
    thread,
    time::{Duration, Instant},
};

use nico_ops::HostEndpoint;
use nico_runtime::{App, AppRunner, RuntimeResult};

/// Runs authoritative simulation continuously at a fixed rate.
pub struct FixedRateServerRunner {
    tick_interval: Duration,
    operations: Option<HostEndpoint>,
}

impl FixedRateServerRunner {
    /// Creates a server runner with a non-zero tick interval.
    #[must_use]
    pub fn new(tick_interval: Duration) -> Self {
        assert!(
            !tick_interval.is_zero(),
            "server tick interval must be non-zero"
        );
        Self {
            tick_interval,
            operations: None,
        }
    }

    /// Enables status and orderly-stop control at host-owned tick boundaries.
    ///
    /// Readiness is published after the first successful host tick. A stop or
    /// last-controller disconnect wakes the paced wait without adding a tick.
    #[must_use]
    pub fn with_operations(mut self, operations: HostEndpoint) -> Self {
        self.operations = Some(operations);
        self
    }
}

impl AppRunner for FixedRateServerRunner {
    fn run(mut self, app: &mut App) -> RuntimeResult<()> {
        if let Err(error) = app.start() {
            if let Some(operations) = self.operations.take() {
                operations.finish(Err(error.to_string()));
            }
            return Err(error);
        }
        let mut execution = Ok(());
        let mut completed_steps = 0_u64;

        while !app.exit_requested() {
            if self
                .operations
                .as_mut()
                .is_some_and(HostEndpoint::stop_requested)
            {
                app.request_exit();
                break;
            }
            let tick_started = Instant::now();
            if let Err(error) = app.tick(self.tick_interval) {
                execution = Err(error);
                break;
            }
            completed_steps = completed_steps.saturating_add(1);
            if let Some(operations) = &mut self.operations {
                operations.running(completed_steps);
            }

            if !app.exit_requested() {
                let remaining = self.tick_interval.saturating_sub(tick_started.elapsed());
                if let Some(operations) = &mut self.operations {
                    if operations.wait_for_stop(remaining) {
                        app.request_exit();
                    }
                } else {
                    thread::sleep(remaining);
                }
            }
        }

        if let Some(operations) = &mut self.operations {
            operations.stopping();
        }
        let shutdown = app.shutdown();
        if let Some(operations) = self.operations.take() {
            let result = match (&execution, &shutdown) {
                (Ok(()), Ok(())) => Ok(()),
                (Err(error), Ok(())) | (Ok(()), Err(error)) => Err(error.to_string()),
                (Err(error), Err(shutdown_error)) => {
                    Err(format!("{error}; shutdown also failed: {shutdown_error}"))
                }
            };
            operations.finish(result);
        }
        execution.and(shutdown)
    }
}

#[cfg(test)]
mod tests {
    use std::{sync::mpsc, thread, time::Duration};

    use nico_ops::{HostState, control_channel};
    use nico_runtime::{AppBuilder, AppState, RuntimeError, RuntimeResult, Stage};

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

    #[test]
    fn observing_control_does_not_change_fixed_steps_or_shutdown() -> RuntimeResult<()> {
        let step = Duration::from_nanos(1);
        for controlled in [false, true] {
            let (control, operations) = control_channel();
            let mut builder = AppBuilder::new().with_fixed_step(step);
            builder.insert_resource(Vec::<(&'static str, u64)>::new());
            builder.add_system(Stage::FixedUpdate, "record fixed tick", |context| {
                context
                    .world
                    .resource_mut::<Vec<(&str, u64)>>()?
                    .push(("fixed", context.time.fixed_tick()));
                Ok(())
            });
            builder.add_system(Stage::Update, "record frame", |context| {
                let frame = context.time.frame_number();
                context
                    .world
                    .resource_mut::<Vec<(&str, u64)>>()?
                    .push(("update", frame));
                if frame == 2 {
                    context.request_exit();
                }
                Ok(())
            });
            let observer = control.clone();
            builder.add_system(Stage::Shutdown, "record shutdown", move |context| {
                if controlled {
                    assert_eq!(observer.status().state, HostState::Stopping);
                }
                context
                    .world
                    .resource_mut::<Vec<(&str, u64)>>()?
                    .push(("shutdown", 0));
                Ok(())
            });
            let mut app = builder.build()?;
            let runner = FixedRateServerRunner::new(step);
            let runner = if controlled {
                runner.with_operations(operations)
            } else {
                drop(operations);
                runner
            };
            app.run_with(runner)?;
            assert_eq!(
                app.world().resource::<Vec<(&str, u64)>>()?,
                &[
                    ("fixed", 0),
                    ("update", 0),
                    ("fixed", 1),
                    ("update", 1),
                    ("fixed", 2),
                    ("update", 2),
                    ("shutdown", 0),
                ]
            );
            if controlled {
                assert_eq!(control.status().state, HostState::Stopped);
                assert_eq!(control.status().completed_steps, 3);
            }
        }
        Ok(())
    }

    #[test]
    fn stop_wakes_a_slow_server_without_an_extra_tick() -> RuntimeResult<()> {
        let (control, operations) = control_channel();
        let (ticked, first_tick) = mpsc::channel();
        let (finished, completion) = mpsc::channel();
        let step = Duration::from_secs(30);
        let mut builder = AppBuilder::new().with_fixed_step(step);
        builder.insert_resource(Updates::default());
        builder.add_system(Stage::Update, "notify tick", move |context| {
            context.world.resource_mut::<Updates>()?.0 += 1;
            ticked.send(()).unwrap();
            Ok(())
        });
        let mut app = builder.build()?;
        let worker = thread::spawn(move || {
            let result = app.run_with(FixedRateServerRunner::new(step).with_operations(operations));
            finished
                .send((result, app.world().resource::<Updates>().unwrap().0))
                .unwrap();
        });
        first_tick.recv_timeout(Duration::from_secs(3)).unwrap();
        control.request_stop().unwrap();
        control.request_stop().unwrap();
        let (result, updates) = completion.recv_timeout(Duration::from_secs(3)).unwrap();
        worker.join().unwrap();
        result?;
        assert_eq!(updates, 1);
        assert_eq!(control.status().completed_steps, 1);
        assert_eq!(control.status().state, HostState::Stopped);
        Ok(())
    }

    #[test]
    fn stop_before_run_and_controller_disconnect_skip_ticks() -> RuntimeResult<()> {
        for disconnected in [false, true] {
            let (control, operations) = control_channel();
            if disconnected {
                drop(control);
            } else {
                control.request_stop().unwrap();
            }
            let mut builder = AppBuilder::new();
            builder.insert_resource(Updates::default());
            builder.add_system(Stage::Update, "must not tick", |_| {
                panic!("stop was already requested")
            });
            builder.add_system(Stage::Shutdown, "count shutdown", |context| {
                context.world.resource_mut::<Updates>()?.0 += 1;
                Ok(())
            });
            let mut app = builder.build()?;
            app.run_with(
                FixedRateServerRunner::new(Duration::from_secs(30)).with_operations(operations),
            )?;
            assert_eq!(app.state(), AppState::Stopped);
            assert_eq!(app.world().resource::<Updates>()?.0, 1);
        }
        Ok(())
    }

    #[test]
    fn startup_tick_and_shutdown_failures_publish_final_failure() -> RuntimeResult<()> {
        for failing_stage in [Stage::Startup, Stage::Update, Stage::Shutdown] {
            let (control, operations) = control_channel();
            let mut builder = AppBuilder::new();
            builder.add_system(failing_stage, "intentional failure", |_| {
                Err(RuntimeError::MissingResource("test failure"))
            });
            builder.add_system(Stage::Update, "exit", |context| {
                context.request_exit();
                Ok(())
            });
            let mut app = builder.build()?;
            let result = app.run_with(
                FixedRateServerRunner::new(Duration::from_nanos(1)).with_operations(operations),
            );
            assert!(result.is_err());
            assert_eq!(app.state(), AppState::Stopped);
            assert_eq!(control.status().state, HostState::Failed);
            assert!(!control.status().is_ready());
            assert!(control.status().failure.unwrap().contains("test failure"));
            let expected_steps = u64::from(failing_stage == Stage::Shutdown);
            assert_eq!(control.status().completed_steps, expected_steps);
        }
        Ok(())
    }

    #[test]
    fn tick_failure_does_not_hide_shutdown_failure() -> RuntimeResult<()> {
        let (control, operations) = control_channel();
        let mut builder = AppBuilder::new();
        builder.add_system(Stage::Update, "tick failure", |_| {
            Err(RuntimeError::MissingResource("tick error"))
        });
        builder.add_system(Stage::Shutdown, "shutdown failure", |_| {
            Err(RuntimeError::MissingResource("shutdown error"))
        });
        let mut app = builder.build()?;
        assert!(
            app.run_with(
                FixedRateServerRunner::new(Duration::from_nanos(1)).with_operations(operations),
            )
            .is_err()
        );
        let failure = control.status().failure.unwrap();
        assert!(failure.contains("tick error"));
        assert!(failure.contains("shutdown error"));
        Ok(())
    }
}
