use std::{any::type_name, error::Error, fmt};

use crate::AppState;

/// Errors reported by the headless runtime.
#[derive(Debug, Eq, PartialEq)]
pub enum RuntimeError {
    /// A lifecycle operation was invoked in the wrong state.
    InvalidState {
        /// State required by the operation.
        expected: AppState,
        /// State observed by the operation.
        actual: AppState,
    },
    /// The fixed simulation step was zero.
    InvalidFixedStep,
    /// A requested typed resource was absent.
    MissingResource(&'static str),
    /// A system failed while executing.
    System {
        /// Lifecycle stage containing the system.
        stage: &'static str,
        /// Registered system name.
        name: String,
        /// Original failure description.
        message: String,
    },
}

impl RuntimeError {
    pub(crate) fn missing_resource<R: 'static>() -> Self {
        Self::MissingResource(type_name::<R>())
    }
}

impl fmt::Display for RuntimeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidState { expected, actual } => {
                write!(
                    formatter,
                    "expected app state {expected:?}, found {actual:?}"
                )
            }
            Self::InvalidFixedStep => formatter.write_str("fixed simulation step must be non-zero"),
            Self::MissingResource(resource) => {
                write!(formatter, "resource `{resource}` was not found")
            }
            Self::System {
                stage,
                name,
                message,
            } => write!(
                formatter,
                "system `{name}` failed during {stage}: {message}"
            ),
        }
    }
}

impl Error for RuntimeError {}

/// Result type used by runtime APIs and systems.
pub type RuntimeResult<T> = Result<T, RuntimeError>;
