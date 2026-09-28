//! Shared extraction for editor previews and authored game scenes.
use crate::model::ModelVisual;
use glam::{EulerRot, Quat};
use nico_assets::model_loading::ImportedModel;
use nico_presentation::MeshInstance;
use nico_scene::components::Transform;
use std::io;

/// Extract the model's rest pose, including authored node transforms and skins.
pub fn rest_meshes(bundle: &ImportedModel) -> io::Result<Vec<MeshInstance>> {
    let visual = ModelVisual::new(bundle.model.clone(), bundle.textures.clone())
        .map_err(io::Error::other)?;
    let globals = nico_animation::Pose::rest(&bundle.model)
        .globals()
        .map_err(io::Error::other)?;
    visual.meshes(&globals).map_err(io::Error::other)
}

/// Apply a validated transform's uniform scale and XYZ radian rotation.
pub fn place_meshes(meshes: &[MeshInstance], object: &Transform) -> Vec<MeshInstance> {
    meshes
        .iter()
        .cloned()
        .map(|mut mesh| {
            mesh.position = object.position.map(|v| v as f32);
            mesh.orientation = Quat::from_euler(
                EulerRot::XYZ,
                object.rotation_radians[0],
                object.rotation_radians[1],
                object.rotation_radians[2],
            );
            mesh.scale = object.scale;
            mesh
        })
        .collect()
}
