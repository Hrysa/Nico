//! Client runtime settings resolved from typed scene entities.
use crate::camera::Camera;
use nico_presentation::SceneLighting;
use nico_presentation_control::camera::{OrbitCamera, OrbitSettings};
use nico_scene::components::{
    AmbientLight, Camera as CameraDefinition, DirectionalLight, OrbitController,
};
use std::io;
pub struct Presentation {
    pub camera: Camera,
    pub lighting: SceneLighting,
}
fn one<T: nico_ecs::Component + Clone>(world: &nico_ecs::World) -> io::Result<T> {
    let mut query = world.query::<&T>();
    let mut values = query.iter();
    let value = values
        .next()
        .ok_or_else(|| io::Error::other("missing scene component"))?
        .clone();
    if values.next().is_some() {
        return Err(io::Error::other("duplicate scene component"));
    }
    Ok(value)
}
impl Presentation {
    pub fn load(content: &arena_arpg_shared::project::ProjectContent) -> io::Result<Self> {
        let camera = one::<CameraDefinition>(&content.entities)?;
        let orbit = one::<OrbitController>(&content.entities)?;
        let follow = one::<arena_arpg_shared::scene::FollowPlayer>(&content.entities)?;
        let sun = one::<DirectionalLight>(&content.entities)?;
        let ambient = one::<AmbientLight>(&content.entities)?;
        let rig = OrbitCamera::new(
            OrbitSettings {
                sensitivity: orbit.sensitivity,
                pitch_limits: orbit.pitch_limits,
                distance: orbit.distance,
                minimum_distance: orbit.minimum_distance,
                radius: orbit.radius,
                collision_margin: orbit.collision_margin,
                restoration_rate: orbit.restoration_rate,
                maximum_delta: orbit.maximum_delta,
                vertical_fov_radians: camera.vertical_fov_radians,
                near: camera.near,
                far: camera.far,
            },
            orbit.yaw,
            orbit.pitch,
        )
        .map_err(io::Error::other)?;
        Ok(Self {
            camera: Camera {
                rig,
                follow_offset: follow.offset,
            },
            lighting: SceneLighting {
                direction: glam::Vec3::from_array(sun.direction).normalize().to_array(),
                radiance: sun.radiance,
                ambient: ambient.radiance,
            },
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scene_camera_and_lights_drive_runtime_settings() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let mut project = nico_scene::Project::open(root).unwrap();
        project.manifest.default_scene = "assets/scenes/meadow.scene.toml".into();
        let mut definition = project.load_scene().unwrap();
        let camera = definition
            .entities
            .iter_mut()
            .find(|e| e.id == "camera")
            .unwrap();
        let mut projection = camera
            .component::<CameraDefinition>("nico.camera")
            .unwrap()
            .unwrap();
        projection.far = 123.;
        projection.vertical_fov_radians = 0.7;
        camera.set_component("nico.camera", &projection).unwrap();
        let mut orbit = camera
            .component::<OrbitController>("nico.orbit")
            .unwrap()
            .unwrap();
        orbit.distance = 4.;
        camera.set_component("nico.orbit", &orbit).unwrap();
        definition
            .entities
            .iter_mut()
            .find(|e| e.id == "sun")
            .unwrap()
            .set_component(
                "nico.directional_light",
                &DirectionalLight {
                    direction: [1., 2., 0.],
                    radiance: [3., 1., 0.5],
                },
            )
            .unwrap();
        let content = arena_arpg_shared::project::ProjectContent::from_scene(
            project,
            definition,
            nico_scene::HostRole::Client,
        )
        .unwrap();
        let mut presentation = Presentation::load(&content).unwrap();
        let view = presentation.camera.view([0., 0.], 0.016);
        assert_eq!(view.far, 123.);
        assert_eq!(view.vertical_fov_radians, 0.7);
        assert_eq!(presentation.camera.rig.distance(), 4.);
        assert_eq!(presentation.lighting.radiance, [3., 1., 0.5]);
        assert!(presentation.lighting.is_valid());
    }
}
