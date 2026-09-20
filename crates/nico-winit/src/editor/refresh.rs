//! Reuse static authoring pixels without comparing asset buffers or skipping updates.
use nico_presentation::Scene3d;
use std::{sync::Arc, time::Duration};

pub(super) const IDLE_INTERVAL: Duration = Duration::from_millis(100);
const FRAME_INTERVAL: Duration = Duration::from_millis(16);

pub(super) fn repaint_delay(requested: Duration) -> Duration {
    // Poll application workers/commands even when egui has no repaint pending.
    requested.clamp(FRAME_INTERVAL, IDLE_INTERVAL)
}

#[derive(Default)]
pub(super) struct ViewportCache(Option<(Scene3d, [f32; 2])>);
impl ViewportCache {
    pub(super) fn changed(&self, scene: &Scene3d, size: [f32; 2]) -> bool {
        self.0
            .as_ref()
            .is_none_or(|(previous, old_size)| *old_size != size || !same_scene(previous, scene))
    }
    pub(super) fn store(&mut self, scene: Scene3d, size: [f32; 2]) {
        self.0 = Some((scene, size));
    }
}

fn same_resource<T>(a: &Option<Arc<T>>, b: &Option<Arc<T>>) -> bool {
    match (a, b) {
        (Some(a), Some(b)) => Arc::ptr_eq(a, b),
        (None, None) => true,
        _ => false,
    }
}

fn same_scene(a: &Scene3d, b: &Scene3d) -> bool {
    // Exhaustive destructuring makes new scene fields require an explicit decision.
    let Scene3d {
        camera: ac,
        lighting: al,
        meshes: am,
    } = a;
    let Scene3d {
        camera: bc,
        lighting: bl,
        meshes: bm,
    } = b;
    let nico_presentation::Camera3d {
        position,
        orientation,
        vertical_fov_radians,
        near,
        far,
    } = ac;
    let nico_presentation::SceneLighting {
        direction,
        radiance,
        ambient,
    } = al;
    *position == bc.position
        && *orientation == bc.orientation
        && *vertical_fov_radians == bc.vertical_fov_radians
        && *near == bc.near
        && *far == bc.far
        && *direction == bl.direction
        && *radiance == bl.radiance
        && *ambient == bl.ambient
        && am.len() == bm.len()
        && am.iter().zip(bm).all(|(a, b)| {
            let nico_presentation::MeshInstance {
                mirrored,
                material,
                mesh,
                skin_palette,
                texture,
                position,
                orientation,
                scale,
                color,
            } = a;
            *mirrored == b.mirrored
                && *position == b.position
                && *orientation == b.orientation
                && *scale == b.scale
                && *color == b.color
                && same_resource(material, &b.material)
                && same_resource(mesh, &b.mesh)
                && same_resource(skin_palette, &b.skin_palette)
                && same_resource(texture, &b.texture)
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_presentation::MeshInstance;

    #[test]
    fn idle_polls_continue_and_animation_requests_are_bounded() {
        assert_eq!(repaint_delay(Duration::MAX), IDLE_INTERVAL);
        assert_eq!(repaint_delay(Duration::ZERO), FRAME_INTERVAL);
        assert_eq!(
            repaint_delay(Duration::from_millis(40)),
            Duration::from_millis(40)
        );
    }

    #[test]
    fn viewport_reuses_identical_snapshots_and_invalidates_visual_changes() {
        let mut cache = ViewportCache::default();
        let mut scene = Scene3d::default();
        scene.meshes.push(MeshInstance {
            mirrored: false,
            material: None,
            mesh: None,
            skin_palette: None,
            texture: None,
            position: [0.; 3],
            orientation: nico_presentation::Quaternion::IDENTITY,
            scale: 1.,
            color: [1.; 4],
        });
        let size = [800., 600.];
        assert!(cache.changed(&scene, size));
        cache.store(scene.clone(), size);
        assert!(!cache.changed(&scene.clone(), size));
        assert!(cache.changed(&scene, [801., 600.]));
        for change in [
            |s: &mut Scene3d| s.camera.position[0] += 1.,
            |s: &mut Scene3d| s.camera.far += 1.,
            |s: &mut Scene3d| s.lighting.ambient[0] += 1.,
            |s: &mut Scene3d| s.meshes[0].position[0] += 1.,
            |s: &mut Scene3d| s.meshes[0].color[0] += 1.,
            |s: &mut Scene3d| s.meshes[0].mirrored = true,
            |s: &mut Scene3d| s.meshes.clear(),
            |s: &mut Scene3d| s.meshes[0].skin_palette = Some(Arc::new(vec![])),
        ] {
            let mut changed = scene.clone();
            change(&mut changed);
            assert!(cache.changed(&changed, size));
        }
        scene.meshes[0].skin_palette = Some(Arc::new(vec![]));
        cache.store(scene.clone(), size);
        assert!(!cache.changed(&scene, size));
        // Replacement resources must invalidate even when their data compares equal.
        scene.meshes[0].skin_palette = Some(Arc::new(vec![]));
        assert!(cache.changed(&scene, size));
    }
}
