//! Runtime-free project and authored scene contracts shared by games and editors.
mod document;
mod project;
pub use document::{Document, Object, relative};
pub use project::{EditorDefinition, Project, ProjectManifest, Targets};

/// Instantiates validated authored objects as ECS components. A caller performs
/// this at its runtime-owned boundary; renderers and game systems consume Object.
/// The returned entity identities allow the caller to unload only its scene.
pub fn instantiate(
    document: &Document,
    world: &mut nico_ecs::World,
) -> std::io::Result<Vec<nico_ecs::Entity>> {
    document.validate()?;
    Ok(document
        .objects
        .iter()
        .map(|object| world.spawn((object.clone(),)))
        .collect())
}
