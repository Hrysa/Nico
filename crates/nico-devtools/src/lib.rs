//! Optional diagnostics and runtime-inspection facilities.
//!
//! Devtools observe a running application. They are not an authoring model and
//! do not own game content.

use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage};

/// Minimal state proving that devtools can attach through public runtime APIs.
#[derive(Debug, Default, Eq, PartialEq)]
pub struct DevtoolsState {
    observed_frames: u64,
}

impl DevtoolsState {
    /// Returns the number of runtime frames observed by devtools.
    #[must_use]
    pub const fn observed_frames(&self) -> u64 {
        self.observed_frames
    }
}

/// Registers the review-skeleton devtools capability.
pub struct DevtoolsPlugin;

impl Plugin for DevtoolsPlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.insert_resource(DevtoolsState::default());
        app.add_system(Stage::Update, "nico_devtools::observe", |context| {
            let state = context.world.resource_mut::<DevtoolsState>()?;
            state.observed_frames = state.observed_frames.saturating_add(1);
            Ok(())
        });
        Ok(())
    }
}
