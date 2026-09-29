//! Scene-owned state within a persistent application.
use std::time::Duration;

use nico_ecs::World;

use crate::{RuntimeResult, Stage, Time, events::EventBus, schedule::Schedule};

/// Application-local scene generation. Entity IDs alone cannot identify a scene.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SceneId(pub(crate) u64);

impl SceneId {
    /// Monotonic identity within one application.
    pub fn generation(self) -> u64 {
        self.0
    }
}

/// Prepared scene data. It owns its world, systems, events, and local clock.
/// Build with `AppBuilder::build_scene`, then activate through `App::switch_scene`.
/// External work must use scene-owned services or cancellation handles released during cleanup.
pub struct Scene {
    pub(crate) world: World,
    pub(crate) events: EventBus,
    schedule: Schedule,
    fixed_step: Duration,
    accumulator: Duration,
    elapsed: Duration,
    fixed_elapsed: Duration,
    frame: u64,
    fixed_tick: u64,
    running: bool,
}

impl Scene {
    pub(crate) fn new(
        world: World,
        schedule: Schedule,
        fixed_step: Duration,
        capacity: usize,
    ) -> Self {
        Self {
            world,
            schedule,
            events: EventBus::new(capacity),
            fixed_step,
            accumulator: Duration::ZERO,
            elapsed: Duration::ZERO,
            fixed_elapsed: Duration::ZERO,
            frame: 0,
            fixed_tick: 0,
            running: false,
        }
    }

    /// Prepared world access before activation.
    pub fn world_mut(&mut self) -> &mut World {
        &mut self.world
    }

    pub(crate) fn start(&mut self, root: &mut World, exit: &mut bool) -> RuntimeResult<()> {
        self.running = true;
        if let Err(error) = self.run(Stage::Startup, Time::startup(), root, exit) {
            let _ = self.shutdown(root, exit);
            return Err(error);
        }
        Ok(())
    }

    pub(crate) fn tick(
        &mut self,
        delta: Duration,
        root: &mut World,
        exit: &mut bool,
    ) -> RuntimeResult<()> {
        self.accumulator = self.accumulator.saturating_add(delta);
        while self.accumulator >= self.fixed_step {
            self.accumulator -= self.fixed_step;
            self.fixed_elapsed = self.fixed_elapsed.saturating_add(self.fixed_step);
            self.run(
                Stage::FixedUpdate,
                Time::fixed(
                    self.frame,
                    self.fixed_tick,
                    self.fixed_step,
                    self.fixed_elapsed,
                ),
                root,
                exit,
            )?;
            self.fixed_tick = self.fixed_tick.saturating_add(1);
        }
        self.elapsed = self.elapsed.saturating_add(delta);
        self.run(
            Stage::Update,
            Time::frame(
                self.frame,
                self.fixed_tick,
                delta,
                self.elapsed,
                self.accumulator.as_secs_f64() / self.fixed_step.as_secs_f64(),
            ),
            root,
            exit,
        )?;
        self.frame = self.frame.saturating_add(1);
        Ok(())
    }

    pub(crate) fn shutdown(&mut self, root: &mut World, exit: &mut bool) -> RuntimeResult<()> {
        if !self.running {
            return Ok(());
        }
        self.running = false;
        self.run(
            Stage::Shutdown,
            Time::shutdown(self.frame, self.elapsed),
            root,
            exit,
        )
    }

    fn run(
        &mut self,
        stage: Stage,
        time: Time,
        root: &mut World,
        exit: &mut bool,
    ) -> RuntimeResult<()> {
        self.schedule.run(
            stage,
            &mut self.world,
            Some(root),
            &mut self.events,
            time,
            exit,
        )
    }
}
