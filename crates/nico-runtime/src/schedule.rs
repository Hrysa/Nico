use crate::{RuntimeError, RuntimeResult, SystemContext, Time, World};

/// Ordered lifecycle stages supported by the minimal runtime.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Stage {
    /// Executes once when the app starts.
    Startup,
    /// Executes zero or more times per host frame at a fixed timestep.
    FixedUpdate,
    /// Executes once per host frame.
    Update,
    /// Executes once when the app shuts down.
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
        time: Time,
        exit_requested: &mut bool,
    ) -> RuntimeResult<()> {
        let mut context = SystemContext {
            world,
            time,
            exit_requested,
        };
        for system in &mut self.stages[stage.index()] {
            if let Err(error) = (system.run)(&mut context) {
                return Err(RuntimeError::System {
                    stage: stage.name(),
                    name: system.name.clone(),
                    message: error.to_string(),
                });
            }
        }
        Ok(())
    }
}
