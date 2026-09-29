use nico_ecs::{CommandBuffer, World};

use crate::{
    RuntimeError, RuntimeResult, SystemContext, Time,
    events::{EventBus, PendingEvents, SystemEvents},
};

/// Ordered lifecycle stages supported by the minimal runtime.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Stage {
    /// Executes once when the owning application or scene starts.
    Startup,
    /// Executes zero or more times per host frame at a fixed timestep.
    FixedUpdate,
    /// Executes once per host frame.
    Update,
    /// Executes once when the owning application stops or its scene unloads.
    Shutdown,
}

impl Stage {
    const fn name(self) -> &'static str {
        match self {
            Self::Startup => "Startup",
            Self::FixedUpdate => "FixedUpdate",
            Self::Update => "Update",
            Self::Shutdown => "Shutdown",
        }
    }

    const fn index(self) -> usize {
        match self {
            Self::Startup => 0,
            Self::FixedUpdate => 1,
            Self::Update => 2,
            Self::Shutdown => 3,
        }
    }
}

type SystemFn = dyn FnMut(&mut SystemContext<'_>) -> RuntimeResult<()> + Send;

struct System {
    name: String,
    run: Box<SystemFn>,
}

pub(crate) struct Schedule {
    stages: [Vec<System>; 4],
}

impl Schedule {
    pub(crate) fn new() -> Self {
        Self {
            stages: std::array::from_fn(|_| Vec::new()),
        }
    }

    pub(crate) fn add<F>(&mut self, stage: Stage, name: String, system: F)
    where
        F: FnMut(&mut SystemContext<'_>) -> RuntimeResult<()> + Send + 'static,
    {
        self.stages[stage.index()].push(System {
            name,
            run: Box::new(system),
        });
    }

    pub(crate) fn run(
        &mut self,
        stage: Stage,
        world: &mut World,
        mut app_world: Option<&mut World>,
        events: &mut EventBus,
        time: Time,
        exit_requested: &mut bool,
    ) -> RuntimeResult<()> {
        let mut failure = None;
        for system in &mut self.stages[stage.index()] {
            let mut commands = CommandBuffer::new();
            let mut pending_events = PendingEvents::default();
            let mut context = SystemContext {
                world,
                app_world: app_world.as_deref_mut(),
                commands: &mut commands,
                events: SystemEvents::new(events, &mut pending_events),
                time,
                exit_requested,
            };
            let span = tracing::trace_span!("runtime_system", system = %system.name);
            let _entered = span.enter();
            if let Err(error) = (system.run)(&mut context) {
                let error = RuntimeError::System {
                    stage: stage.name(),
                    name: system.name.clone(),
                    message: error.to_string(),
                };
                if stage != Stage::Shutdown {
                    return Err(error);
                }
                failure.get_or_insert(error);
                continue;
            }
            context.commands.run_on(context.world.entities_mut());
            pending_events.commit(events);
        }
        failure.map_or(Ok(()), Err)
    }
}
