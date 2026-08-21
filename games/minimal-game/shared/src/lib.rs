//! Authoritative gameplay shared by the minimal game's client and server.

use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage, SystemContext};

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
    Ok(())
}

fn simulate(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    let state = context.world.resource_mut::<GameState>()?;
    state.fixed_updates = state.fixed_updates.saturating_add(1);
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

    use super::{GameState, MinimalGamePlugin};

    #[test]
    fn shared_gameplay_runs_headlessly() -> RuntimeResult<()> {
        let mut app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;

        app.run_for_frames(2, Duration::from_nanos(16_666_667))?;

        let state = app.world().resource::<GameState>()?;
        assert!(state.started());
        assert_eq!(state.fixed_updates(), 2);
        assert_eq!(state.frame_updates(), 2);
        Ok(())
    }
}
