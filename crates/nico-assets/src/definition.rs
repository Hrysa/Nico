//! Shared parsing for validated authored TOML definitions.
use serde::de::DeserializeOwned;
use std::{error::Error, io::Read, path::Path};

pub use nico_definition_derive::Definition;

/// Maximum bytes accepted from one definition file.
pub const MAX_DEFINITION_BYTES: u64 = 1024 * 1024;
/// Error returned by parsing, reading, or validating a definition.
pub type DefinitionError = Box<dyn Error + Send + Sync>;
/// Result returned by definition operations.
pub type DefinitionResult<T> = Result<T, DefinitionError>;

/// Validation rules supplied by the owner of an authored model.
///
/// ```
/// use nico_assets::definition::DefinitionValidation;
///
/// #[derive(serde::Deserialize, nico_assets::definition::Definition)]
/// struct Count { value: u8 }
///
/// impl DefinitionValidation for Count {
///     type Error = &'static str;
///
///     fn validate(&self) -> Result<(), Self::Error> {
///         if self.value > 0 { Ok(()) } else { Err("value must be positive") }
///     }
/// }
///
/// assert!(Count::parse("value = 0").is_err());
/// assert_eq!(Count::parse("value = 2").unwrap().value, 2);
/// ```
pub trait DefinitionValidation {
    /// The model's validation error, convertible to the shared loading error.
    type Error: Into<DefinitionError>;

    /// Checks the parsed model without changing it.
    fn validate(&self) -> Result<(), Self::Error>;
}

/// An authored TOML model with explicit validation rules.
pub trait Definition: DeserializeOwned + DefinitionValidation + Sized {
    /// Parses TOML and checks the result before returning it.
    fn parse_text(text: &str) -> DefinitionResult<Self> {
        let value: Self = toml::from_str(text)?;
        value.validate().map_err(Into::into)?;
        Ok(value)
    }

    /// Reads a bounded TOML file, then parses and checks it.
    fn load_file(path: &Path) -> DefinitionResult<Self> {
        let text = read_definition(path)?;
        Self::parse_text(&text).map_err(|error| format!("{}: {error}", path.display()).into())
    }
}

/// Reads a bounded UTF-8 definition file.
pub fn read_definition(path: &Path) -> DefinitionResult<String> {
    let mut text = String::new();
    std::fs::File::open(path)?
        .take(MAX_DEFINITION_BYTES + 1)
        .read_to_string(&mut text)?;
    if text.len() as u64 > MAX_DEFINITION_BYTES {
        return Err(format!("{}: definition exceeds 1 MiB", path.display()).into());
    }
    Ok(text)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;

    #[derive(Deserialize)]
    struct Example {
        value: u8,
    }

    impl Definition for Example {}

    impl DefinitionValidation for Example {
        type Error = DefinitionError;

        fn validate(&self) -> DefinitionResult<()> {
            if self.value > 0 {
                Ok(())
            } else {
                Err("value must be positive".into())
            }
        }
    }

    #[test]
    fn parse_checks_each_definition() {
        assert_eq!(Example::parse_text("value = 3").unwrap().value, 3);
        assert!(Example::parse_text("value = 0").is_err());
        assert!(Example::parse_text("value = 'bad'").is_err());
    }

    #[test]
    fn file_load_checks_values_and_rejects_oversized_input() {
        let root = tempfile::tempdir().unwrap();
        let path = root.path().join("example.toml");
        std::fs::write(&path, "value = 2").unwrap();
        assert_eq!(Example::load_file(&path).unwrap().value, 2);
        std::fs::write(&path, "value = 0").unwrap();
        assert!(Example::load_file(&path).is_err());
        std::fs::write(&path, vec![b' '; MAX_DEFINITION_BYTES as usize + 1]).unwrap();
        assert!(Example::load_file(&path).is_err());
    }
}
