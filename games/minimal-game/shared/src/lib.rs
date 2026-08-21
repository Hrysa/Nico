//! Authoritative gameplay shared by the minimal game's client and server.

use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage, SystemContext};

/// Position of an active simulated entity in minimal-game world units.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Position {
    x: f32,
}

impl Position {
    /// Returns the horizontal world position.
    #[must_use]
    pub const fn x(self) -> f32 {
        self.x
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct Velocity {
    units_per_second: f32,
}

/// Authoritative state shared by client and server.
#[derive(Debug, Default, Eq, PartialEq)]
pub struct GameState {
    started: bool,
    fixed_updates: u64,
    frame_updates: u64,
}

impl GameState {
    /// Returns whether startup completed.
    #[must_use]
    pub const fn started(&self) -> bool {
        self.started
    }

    /// Returns the number of authoritative fixed updates.
    #[must_use]
    pub const fn fixed_updates(&self) -> u64 {
        self.fixed_updates
    }

    /// Returns the number of host-frame updates.
    #[must_use]
    pub const fn frame_updates(&self) -> u64 {
        self.frame_updates
    }
}

/// Registers authoritative gameplay in either a client or server runtime.
pub struct MinimalGamePlugin;

impl Plugin for MinimalGamePlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.insert_resource(GameState::default());
        app.add_system(Stage::Startup, "minimal_game::setup", setup);
        app.add_system(Stage::FixedUpdate, "minimal_game::simulate", simulate);
        app.add_system(Stage::Update, "minimal_game::update", update);
        Ok(())
    }
}

fn setup(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    context.world.resource_mut::<GameState>()?.started = true;
    context.commands.spawn((
        Position::default(),
        Velocity {
            units_per_second: 1.0,
        },
    ));
    Ok(())
}

fn simulate(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    let state = context.world.resource_mut::<GameState>()?;
    state.fixed_updates = state.fixed_updates.saturating_add(1);

    let delta = context.time.delta().as_secs_f32();
    for (position, velocity) in context.world.query::<(&mut Position, &Velocity)>().iter() {
        position.x += velocity.units_per_second * delta;
    }
    Ok(())
}

fn update(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    let state = context.world.resource_mut::<GameState>()?;
    state.frame_updates = state.frame_updates.saturating_add(1);
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use nico_runtime::{AppBuilder, RuntimeResult};

    use super::{GameState, MinimalGamePlugin, Position};

    #[test]
    fn shared_gameplay_runs_headlessly() -> RuntimeResult<()> {
        let mut app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;

        app.run_for_frames(2, Duration::from_nanos(16_666_667))?;

        let state = app.world().resource::<GameState>()?;
        assert!(state.started());
        assert_eq!(state.fixed_updates(), 2);
        assert_eq!(state.frame_updates(), 2);

        let positions = app
            .world()
            .query::<&Position>()
            .iter()
            .map(|position| position.x())
            .collect::<Vec<_>>();
        assert_eq!(positions.len(), 1);
        assert!((positions[0] - 0.033_333_335).abs() < f32::EPSILON);
        Ok(())
    }
}
