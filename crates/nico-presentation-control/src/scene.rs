//! Shared extraction for editor previews and authored game scenes.
use crate::model::ModelVisual;
use glam::{EulerRot, Quat};
use nico_assets::model_loading::ImportedModel;
use nico_presentation::MeshInstance;
use nico_scene::Object;
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

/// Apply a validated scene object's uniform scale and XYZ degree rotation.
pub fn place_meshes(meshes: &[MeshInstance], object: &Object) -> Vec<MeshInstance> {
    meshes
        .iter()
        .cloned()
        .map(|mut mesh| {
            mesh.position = object.position;
            mesh.orientation = Quat::from_euler(
                EulerRot::XYZ,
                object.rotation[0].to_radians(),
                object.rotation[1].to_radians(),
                object.rotation[2].to_radians(),
            );
            mesh.scale = object.scale;
            mesh
        })
        .collect()
}
