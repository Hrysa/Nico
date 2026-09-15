use crate::{Arena, StepReport, TickInput};
use nico_runtime::{AppBuilder, Plugin, RuntimeError, RuntimeResult, Stage, events::EventReader};
use std::time::Duration;

/// Nico's rounded 60 Hz fixed-step duration.
pub const FIXED_STEP: Duration = Duration::from_nanos(16_666_667);

/// Host notification consumed at a fixed boundary. Clears submitted input without
/// undoing an attack or dodge already in progress.
#[derive(Clone, Copy, Debug)]
pub struct InputFocusLost;

/// Installs the arena as a runtime-owned resource. Hosts use FIXED_STEP and send
/// TickInput events before fixed updates. Latest input wins within a boundary,
/// except a valid current-run restart takes priority. No input means idle.
pub struct ArenaPlugin;
impl Plugin for ArenaPlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.insert_resource(Arena::default());
        app.insert_resource(StepReport::default());
        let mut reader = EventReader::<TickInput>::new();
        let mut focus_reader = EventReader::<InputFocusLost>::new();
        app.add_system(Stage::FixedUpdate, "arena::simulate", move |context| {
            if context.time.delta() != FIXED_STEP {
                return Err(RuntimeError::System {
                    stage: "FixedUpdate",
                    name: "arena::simulate".into(),
                    message: "arena requires the 60 Hz FIXED_STEP".into(),
                });
            }
            let run_id = context.world.resource::<Arena>()?.snapshot().run_id;
            let mut input = TickInput::idle(run_id);
            for candidate in context.events.read(&mut reader) {
                if !(input.restart && input.run_id == run_id && input.valid()) {
                    input = *candidate;
                }
            }
            let focus_lost = context.events.read(&mut focus_reader).count() != 0;
            if focus_lost {
                let restart = input.restart && input.run_id == run_id && input.valid();
                input = TickInput::idle(run_id);
                input.restart = restart;
            }
            if focus_lost {
                context
                    .world
                    .resource_mut::<Arena>()?
                    .clear_buffered_input();
            }
            #[cfg(feature = "tools")]
            let operations = context
                .world
                .resource::<crate::tools::Operations>()
                .ok()
                .cloned();
            #[cfg(feature = "tools")]
            let report = if let Some(operations) = operations {
                operations.advance(context.world.resource_mut::<Arena>()?, input, focus_lost)
            } else {
                context.world.resource_mut::<Arena>()?.step(input)
            };
            #[cfg(not(feature = "tools"))]
            let report = context.world.resource_mut::<Arena>()?.step(input);
            *context.world.resource_mut::<StepReport>()? = report;
            Ok(())
        });
        app.add_system(Stage::Shutdown, "arena::clear_input", |context| {
            context
                .world
                .resource_mut::<Arena>()?
                .clear_buffered_input();
            Ok(())
        });
        Ok(())
    }
}
