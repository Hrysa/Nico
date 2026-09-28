//! Runtime-free project and authored scene contracts shared by games and editors.
pub mod components;
pub mod content;
mod project;
mod scene;
pub use scene::{
    ComponentRegistry, ComponentScope, EntityDefinition, HostRole, PreparedScene, SceneComponent,
    SceneDefinition, SceneIdentity,
};
pub fn relative(path: &std::path::Path) -> bool {
    !path.as_os_str().is_empty()
        && path
            .components()
            .all(|c| matches!(c, std::path::Component::Normal(_)))
}
pub use project::{AuthoringDefinition, Project, ProjectManifest, Targets};
