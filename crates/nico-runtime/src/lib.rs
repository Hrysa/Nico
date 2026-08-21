//! Headless simulation kernel for Nico applications.
//!
//! This crate has no dependency on windows, rendering, audio, UI, or local
//! input. Clients, servers, tests, and tools all drive the same [`App`].

mod error;
mod host;
mod schedule;
mod time;
mod world;

pub use error::{RuntimeError, RuntimeResult};
pub use host::AppRunner;
pub use schedule::Stage;
pub use time::Time;
pub use world::{Resource, World};

use std::time::Duration;

use schedule::Schedule;

/// Current state of an [`App`].
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum AppState {
    /// The app is configured but has not started.
    #[default]
    Created,
    /// The app is accepting simulation ticks.
    Running,
    /// Shutdown systems are executing.
    Stopping,
    /// The app has completed shutdown.
    Stopped,
}

/// Read/write state supplied to a system invocation.
pub struct SystemContext<'a> {
    /// The authoritative simulation world.
    pub world: &'a mut World,
    /// Timing values for this invocation.
    pub time: Time,
    exit_requested: &'a mut bool,
}

impl SystemContext<'_> {
    /// Requests an orderly stop after the current host frame.
    pub fn request_exit(&mut self) {
        *self.exit_requested = true;
    }

    /// Returns whether this or an earlier system requested exit.
    #[must_use]
    pub const fn exit_requested(&self) -> bool {
        *self.exit_requested
    }
}

/// Adds a cohesive capability to an [`AppBuilder`].
pub trait Plugin: Send + Sync + 'static {
    /// Registers the plugin's resources and systems.
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()>;
}

/// Configures an application before it starts.
pub struct AppBuilder {
    world: World,
    schedule: Schedule,
    plugins: Vec<Box<dyn Plugin>>,
    fixed_step: Duration,
}

impl Default for AppBuilder {
    fn default() -> Self {
        Self {
            world: World::new(),
            schedule: Schedule::new(),
            plugins: Vec::new(),
            fixed_step: Duration::from_nanos(16_666_667),
        }
    }
}

impl AppBuilder {
    /// Creates an empty application configuration.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds a plugin. Plugins are built in insertion order by [`Self::build`].
    #[must_use]
    pub fn add_plugin(mut self, plugin: impl Plugin) -> Self {
        self.plugins.push(Box::new(plugin));
        self
    }

    /// Sets the authoritative simulation step.
    #[must_use]
    pub const fn with_fixed_step(mut self, fixed_step: Duration) -> Self {
        self.fixed_step = fixed_step;
        self
    }

    /// Inserts or replaces a typed world resource.
    pub fn insert_resource<R: Resource>(&mut self, resource: R) -> Option<R> {
        self.world.insert(resource)
    }

    /// Registers a named system in a lifecycle stage.
    pub fn add_system<F>(&mut self, stage: Stage, name: impl Into<String>, system: F)
    where
        F: FnMut(&mut SystemContext<'_>) -> RuntimeResult<()> + Send + 'static,
    {
        self.schedule.add(stage, name.into(), system);
    }

    /// Builds the configured application.
    pub fn build(mut self) -> RuntimeResult<App> {
        if self.fixed_step.is_zero() {
            return Err(RuntimeError::InvalidFixedStep);
        }

        for plugin in std::mem::take(&mut self.plugins) {
            plugin.build(&mut self)?;
        }

        Ok(App {
            state: AppState::Created,
            world: self.world,
            schedule: self.schedule,
            fixed_step: self.fixed_step,
            accumulator: Duration::ZERO,
            elapsed: Duration::ZERO,
            fixed_elapsed: Duration::ZERO,
            frame: 0,
            fixed_tick: 0,
            exit_requested: false,
        })
    }
}

/// A deterministic, presentation-independent application runtime.
pub struct App {
    state: AppState,
    world: World,
    schedule: Schedule,
    fixed_step: Duration,
    accumulator: Duration,
    elapsed: Duration,
    fixed_elapsed: Duration,
    frame: u64,
    fixed_tick: u64,
    exit_requested: bool,
}

impl App {
    /// Returns the current lifecycle state.
    #[must_use]
    pub const fn state(&self) -> AppState {
        self.state
    }

    /// Returns the simulation world.
    #[must_use]
    pub const fn world(&self) -> &World {
        &self.world
    }

    /// Returns mutable access to the simulation world.
    pub const fn world_mut(&mut self) -> &mut World {
        &mut self.world
    }

    /// Requests an orderly stop from the host loop.
    pub fn request_exit(&mut self) {
        self.exit_requested = true;
    }

    /// Returns whether application or system code requested exit.
    #[must_use]
    pub const fn exit_requested(&self) -> bool {
        self.exit_requested
    }

    /// Gives control of this application to a host-owned runner.
    pub fn run_with<R: AppRunner>(&mut self, runner: R) -> RuntimeResult<()> {
        runner.run(self)
    }

    /// Runs startup systems and begins accepting ticks.
    pub fn start(&mut self) -> RuntimeResult<()> {
        self.require_state(AppState::Created)?;
        tracing::info!(
            fixed_step_seconds = self.fixed_step.as_secs_f64(),
            "runtime starting"
        );
        self.state = AppState::Running;
        if let Err(error) = self.run_stage(Stage::Startup, Time::startup()) {
            tracing::error!(error = %error, "runtime startup failed");
            self.state = AppState::Stopping;
            let _ = self.run_stage(Stage::Shutdown, Time::shutdown(0, Duration::ZERO));
            self.state = AppState::Stopped;
            return Err(error);
        }
        tracing::info!("runtime started");
        Ok(())
    }

    /// Advances the simulation by one host frame.
    pub fn tick(&mut self, delta: Duration) -> RuntimeResult<()> {
        self.require_state(AppState::Running)?;
        tracing::trace!(
            frame = self.frame,
            delta_seconds = delta.as_secs_f64(),
            "runtime tick"
        );
        self.accumulator = self.accumulator.saturating_add(delta);

        while self.accumulator >= self.fixed_step {
            self.accumulator -= self.fixed_step;
            self.fixed_elapsed = self.fixed_elapsed.saturating_add(self.fixed_step);
            let time = Time::fixed(
                self.frame,
                self.fixed_tick,
                self.fixed_step,
                self.fixed_elapsed,
            );
            self.run_stage(Stage::FixedUpdate, time)?;
            self.fixed_tick = self.fixed_tick.saturating_add(1);
        }

        self.elapsed = self.elapsed.saturating_add(delta);
        let interpolation = self.accumulator.as_secs_f64() / self.fixed_step.as_secs_f64();
        let time = Time::frame(
            self.frame,
            self.fixed_tick,
            delta,
            self.elapsed,
            interpolation,
        );
        self.run_stage(Stage::Update, time)?;
        self.frame = self.frame.saturating_add(1);
        Ok(())
    }

    /// Runs shutdown systems exactly once.
    pub fn shutdown(&mut self) -> RuntimeResult<()> {
        self.require_state(AppState::Running)?;
        tracing::info!(frame = self.frame, "runtime stopping");
        self.state = AppState::Stopping;
        let result = self.run_stage(Stage::Shutdown, Time::shutdown(self.frame, self.elapsed));
        self.state = AppState::Stopped;
        tracing::info!("runtime stopped");
        result
    }

    /// Executes a deterministic number of equally sized headless frames.
    ///
    /// Shutdown is attempted even if a frame system returns an error.
    pub fn run_for_frames(&mut self, frames: u64, delta: Duration) -> RuntimeResult<()> {
        self.start()?;
        let mut execution = Ok(());

        for _ in 0..frames {
            if self.exit_requested {
                break;
            }
            if let Err(error) = self.tick(delta) {
                execution = Err(error);
                break;
            }
        }

        let shutdown = self.shutdown();
        execution.and(shutdown)
    }

    fn require_state(&self, expected: AppState) -> RuntimeResult<()> {
        if self.state == expected {
            Ok(())
        } else {
            Err(RuntimeError::InvalidState {
                expected,
                actual: self.state,
            })
        }
    }

    fn run_stage(&mut self, stage: Stage, time: Time) -> RuntimeResult<()> {
        let span = tracing::trace_span!(
            "runtime_stage",
            ?stage,
            frame = time.frame_number(),
            fixed_tick = time.fixed_tick()
        );
        let _entered = span.enter();
        self.schedule
            .run(stage, &mut self.world, time, &mut self.exit_requested)
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::{AppBuilder, AppState, Plugin, RuntimeResult, Stage, SystemContext};

    #[derive(Default)]
    struct Trace {
        entries: Vec<&'static str>,
    }

    struct TestPlugin;

    impl Plugin for TestPlugin {
        fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
            app.insert_resource(Trace::default());
            app.add_system(Stage::Startup, "startup", |context| {
                context
                    .world
                    .resource_mut::<Trace>()?
                    .entries
                    .push("startup");
                Ok(())
            });
            app.add_system(Stage::FixedUpdate, "fixed", |context| {
                context.world.resource_mut::<Trace>()?.entries.push("fixed");
                Ok(())
            });
            app.add_system(Stage::Update, "update", |context| {
                context
                    .world
                    .resource_mut::<Trace>()?
                    .entries
                    .push("update");
                Ok(())
            });
            app.add_system(Stage::Shutdown, "shutdown", |context| {
                context
                    .world
                    .resource_mut::<Trace>()?
                    .entries
                    .push("shutdown");
                Ok(())
            });
            Ok(())
        }
    }

    #[test]
    fn headless_run_has_deterministic_stage_order() -> RuntimeResult<()> {
        let mut app = AppBuilder::new()
            .with_fixed_step(Duration::from_millis(10))
            .add_plugin(TestPlugin)
            .build()?;

        app.run_for_frames(2, Duration::from_millis(15))?;

        assert_eq!(app.state(), AppState::Stopped);
        assert_eq!(
            app.world().resource::<Trace>()?.entries,
            [
                "startup", "fixed", "update", "fixed", "fixed", "update", "shutdown"
            ]
        );
        Ok(())
    }

    #[test]
    fn update_receives_host_frame_timing() -> RuntimeResult<()> {
        #[derive(Default)]
        struct Observed(Vec<(u64, Duration)>);

        let mut builder = AppBuilder::new();
        builder.insert_resource(Observed::default());
        builder.add_system(
            Stage::Update,
            "observe",
            |context: &mut SystemContext<'_>| {
                context
                    .world
                    .resource_mut::<Observed>()?
                    .0
                    .push((context.time.frame_number(), context.time.delta()));
                Ok(())
            },
        );
        let mut app = builder.build()?;

        app.run_for_frames(3, Duration::from_millis(20))?;

        assert_eq!(
            app.world().resource::<Observed>()?.0,
            [
                (0, Duration::from_millis(20)),
                (1, Duration::from_millis(20)),
                (2, Duration::from_millis(20))
            ]
        );
        Ok(())
    }

    #[test]
    fn headless_run_shuts_down_after_a_system_failure() -> RuntimeResult<()> {
        #[derive(Default)]
        struct ShutdownObserved(bool);

        let mut builder = AppBuilder::new();
        builder.insert_resource(ShutdownObserved::default());
        builder.add_system(Stage::Update, "fail", |_context| {
            Err(super::RuntimeError::MissingResource("intentional failure"))
        });
        builder.add_system(Stage::Shutdown, "observe shutdown", |context| {
            context.world.resource_mut::<ShutdownObserved>()?.0 = true;
            Ok(())
        });
        let mut app = builder.build()?;

        assert!(app.run_for_frames(1, Duration::from_millis(16)).is_err());
        assert_eq!(app.state(), AppState::Stopped);
        assert!(app.world().resource::<ShutdownObserved>()?.0);
        assert!(app.shutdown().is_err(), "shutdown must execute only once");
        Ok(())
    }

    #[test]
    fn startup_failure_runs_shutdown_and_stops() -> RuntimeResult<()> {
        #[derive(Default)]
        struct ShutdownObserved(bool);

        let mut builder = AppBuilder::new();
        builder.insert_resource(ShutdownObserved::default());
        builder.add_system(Stage::Startup, "fail startup", |_context| {
            Err(super::RuntimeError::MissingResource("intentional failure"))
        });
        builder.add_system(Stage::Shutdown, "observe shutdown", |context| {
            context.world.resource_mut::<ShutdownObserved>()?.0 = true;
            Ok(())
        });
        let mut app = builder.build()?;

        assert!(app.start().is_err());
        assert_eq!(app.state(), AppState::Stopped);
        assert!(app.world().resource::<ShutdownObserved>()?.0);
        Ok(())
    }

    #[test]
    fn fixed_time_tracks_simulation_time_and_remainder() -> RuntimeResult<()> {
        #[derive(Default)]
        struct Observed {
            fixed_elapsed: Vec<Duration>,
            interpolation: f64,
        }

        let mut builder = AppBuilder::new().with_fixed_step(Duration::from_millis(10));
        builder.insert_resource(Observed::default());
        builder.add_system(Stage::FixedUpdate, "observe fixed", |context| {
            let elapsed = context.time.elapsed();
            context
                .world
                .resource_mut::<Observed>()?
                .fixed_elapsed
                .push(elapsed);
            Ok(())
        });
        builder.add_system(Stage::Update, "observe remainder", |context| {
            context.world.resource_mut::<Observed>()?.interpolation = context.time.interpolation();
            Ok(())
        });
        let mut app = builder.build()?;

        app.run_for_frames(1, Duration::from_millis(25))?;

        let observed = app.world().resource::<Observed>()?;
        assert_eq!(
            observed.fixed_elapsed,
            [Duration::from_millis(10), Duration::from_millis(20)]
        );
        assert!((observed.interpolation - 0.5).abs() < f64::EPSILON);
        Ok(())
    }

    #[test]
    fn system_exit_request_stops_a_bounded_run_early() -> RuntimeResult<()> {
        #[derive(Default)]
        struct Updates(u64);

        let mut builder = AppBuilder::new();
        builder.insert_resource(Updates::default());
        builder.add_system(Stage::Update, "exit after two frames", |context| {
            let updates = context.world.resource_mut::<Updates>()?;
            updates.0 += 1;
            if updates.0 == 2 {
                context.request_exit();
            }
            Ok(())
        });
        let mut app = builder.build()?;

        app.run_for_frames(10, Duration::from_millis(16))?;

        assert_eq!(app.world().resource::<Updates>()?.0, 2);
        assert!(app.exit_requested());
        assert_eq!(app.state(), AppState::Stopped);
        Ok(())
    }
}
