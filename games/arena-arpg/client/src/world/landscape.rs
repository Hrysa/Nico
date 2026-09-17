//! Meadow dressing prepared once per zone. Walkable ground remains server-flat.
use arena_arpg_shared::open_world::content::ZoneDefinition;
use glam::{Quat, Vec3};
use nico_assets::{MaterialTexture, Mesh, MeshVertex, PbrMaterial, Texture};
use nico_presentation::{Camera3d, MeshInstance, Scene3d, SceneLighting};
use nico_presentation_control::model::ModelBounds;
use std::sync::Arc;

pub struct Landscape {
    ground: MeshInstance,
    hills: MeshInstance,
    sky: MeshInstance,
    grass: Vec<(ModelBounds, MeshInstance)>,
}
fn instance(mesh: Mesh, material: Arc<PbrMaterial>) -> MeshInstance {
    MeshInstance {
        mesh: Some(Arc::new(mesh)),
        material: Some(material),
        texture: None,
        skin_palette: None,
        mirrored: false,
        position: [0.; 3],
        orientation: Quat::IDENTITY,
        scale: 1.,
        color: [1.; 4],
    }
}
fn material(texture: Texture, emissive: bool) -> Arc<PbrMaterial> {
    let mut texture = MaterialTexture::new(Arc::new(texture));
    texture.wrap_t = nico_assets::model::WrapMode::Clamp;
    Arc::new(if emissive {
        PbrMaterial {
            base_color: [0., 0., 0., 1.],
            emissive: [1.; 3],
            emissive_texture: Some(texture),
            double_sided: true,
            ..Default::default()
        }
    } else {
        PbrMaterial {
            base_color_texture: Some(texture),
            double_sided: true,
            ..Default::default()
        }
    })
}
fn path_x(z: f32) -> f32 {
    1.8 * (z * 0.09).sin() + 0.6 * (z * 0.23).sin()
}
fn noise(x: f32, z: f32) -> f32 {
    ((x * 0.39 + z * 0.21).sin() + (z * 0.73 - x * 0.17).cos()) * 0.25 + 0.5
}
// Independently hashed lattice values avoid the directional bands of sine waves.
// Smooth interpolation keeps texture filtering from exposing cell boundaries.
fn ground_noise(x: f32, z: f32, seed: u32) -> f32 {
    let hash = |x: i32, z: i32| {
        let mut v =
            (x as u32).wrapping_mul(0x9e3779b9) ^ (z as u32).wrapping_mul(0x85ebca6b) ^ seed;
        v ^= v >> 16;
        v = v.wrapping_mul(0x7feb352d);
        v ^= v >> 15;
        v = v.wrapping_mul(0x846ca68b);
        v ^= v >> 16;
        (v >> 8) as f32 / 16777215.
    };
    let ix = x.floor() as i32;
    let iz = z.floor() as i32;
    let smooth = |t: f32| t * t * t * (t * (t * 6. - 15.) + 10.);
    let u = smooth(x - x.floor());
    let v = smooth(z - z.floor());
    let mix = |a: f32, b: f32, t: f32| a + (b - a) * t;
    mix(
        mix(hash(ix, iz), hash(ix + 1, iz), u),
        mix(hash(ix, iz + 1), hash(ix + 1, iz + 1), u),
        v,
    )
}
fn random(seed: &mut u32) -> f32 {
    *seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
    (*seed >> 8) as f32 / 16777216.
}
fn vertex(position: [f32; 3], uv: [f32; 2]) -> MeshVertex {
    MeshVertex { position, uv }
}
impl Landscape {
    pub fn new(zone: &ZoneDefinition) -> Self {
        let h = zone.half_extent_m as f32;
        let mut pixels = Vec::with_capacity(1024 * 1024 * 4);
        for y in 0..1024 {
            for x in 0..1024 {
                let wx = (x as f32 / 1023. * 2. - 1.) * h;
                let wz = (y as f32 / 1023. * 2. - 1.) * h;
                let n = ground_noise(wx * 0.20, wz * 0.20, 721);
                let distance = (wx - path_x(wz)).abs();
                let path = ((3.2 + (n - 0.5) * 0.65 - distance) * 1.8).clamp(0., 1.);
                let grass = [92. + n * 27., 135. + n * 34., 29. + n * 18.];
                let earth = [181. + n * 27., 159. + n * 23., 92. + n * 24.];
                // Low-contrast irregular mottling, with detail resolved by the
                // 1024-pixel ground map rather than near-Nyquist wave patterns.
                let grain = 0.94
                    + 0.08 * ground_noise(wx * 0.83 + wz * 0.37, wz * 0.83 - wx * 0.37, 193)
                    + 0.04 * ground_noise(wx * 2.1, wz * 2.1, 947);
                // Authored ground shading around static obstacles, not a dynamic shadow map.
                let shade = zone
                    .obstacles
                    .iter()
                    .map(|o| {
                        let rx = (o.size[0] as f32 * 0.65).max(2.);
                        let rz = (o.size[2] as f32 * 0.65).max(2.);
                        let d = ((wx - o.center[0] as f32 - 0.8) / rx).powi(2)
                            + ((wz - o.center[2] as f32 - 0.4) / rz).powi(2);
                        (1. - d * 0.5).clamp(0., 1.) * 0.30
                    })
                    .fold(0_f32, f32::max);
                pixels.extend((0..3).map(|i| {
                    ((grass[i] * (1. - path) + earth[i] * path) * grain * (1. - shade)) as u8
                }));
                pixels.push(255);
            }
        }
        let ground = instance(
            Mesh::triangles(
                vec![
                    vertex([-h, -0.015, -h], [0., 0.]),
                    vertex([-h, -0.015, h], [0., 1.]),
                    vertex([h, -0.015, h], [1., 1.]),
                    vertex([h, -0.015, -h], [1., 0.]),
                ],
                vec![0, 1, 2, 0, 2, 3],
            )
            .unwrap(),
            material(Texture::rgba8(1024, 1024, pixels).unwrap(), false),
        );
        // Distant rolling silhouette lies entirely outside the playable square.
        let mut vertices = Vec::new();
        let mut indices = Vec::new();
        for ring in 0..4 {
            for i in 0..=96 {
                let angle = i as f32 / 96. * std::f32::consts::TAU;
                let radius = h / angle.cos().abs().max(angle.sin().abs()) + ring as f32 * 13.;
                let height = if ring == 0 {
                    -0.015
                } else {
                    6. + 8. * noise(angle * 12., ring as f32 * 4.) + 6. * (angle * 3.).sin().abs()
                };
                vertices.push(vertex(
                    [angle.cos() * radius, height, angle.sin() * radius],
                    [0.5, ring as f32 / 3.],
                ));
                if ring > 0 && i > 0 {
                    let a = (ring * 97 + i) as u32;
                    indices.extend([a, a - 1, a - 98, a, a - 98, a - 97]);
                }
            }
        }
        let hills = instance(
            Mesh::triangles(vertices, indices).unwrap(),
            material(
                Texture::rgba8(
                    1,
                    4,
                    vec![
                        104, 145, 52, 255, 139, 166, 76, 255, 161, 183, 101, 255, 179, 196, 123,
                        255,
                    ],
                )
                .unwrap(),
                false,
            ),
        );
        let mut sky_pixels = Vec::new();
        for y in 0..128 {
            let t = y as f32 / 127.;
            for x in 0..256 {
                let u = x as f32 / 256. * std::f32::consts::TAU;
                let wisps = (u * 5. + t * 37.).sin() * 0.45
                    + (u * 11. - t * 65.).cos() * 0.3
                    + (u * 19. + t * 113.).sin() * 0.25;
                let cloud =
                    ((wisps - 0.27) * 2.).clamp(0., 1.) * (t * 12.).min(1.) * (1. - t).sqrt();
                let sky = [185. - t * 109., 222. - t * 37., 224. + t * 16.];
                sky_pixels.extend(sky.map(|v| (v * (1. - cloud) + 249. * cloud) as u8));
                sky_pixels.push(255);
            }
        }
        let mut vertices = Vec::new();
        let mut indices = Vec::new();
        for lat in 0..=16 {
            for lon in 0..=48 {
                let a = lon as f32 / 48. * std::f32::consts::TAU;
                let e = -std::f32::consts::FRAC_PI_2 + lat as f32 / 16. * std::f32::consts::PI;
                vertices.push(vertex(
                    [e.cos() * a.cos(), e.sin(), e.cos() * a.sin()],
                    [lon as f32 / 48., e.sin().max(0.)],
                ));
                if lat > 0 && lon > 0 {
                    let a = (lat * 49 + lon) as u32;
                    indices.extend([a, a - 1, a - 50, a, a - 50, a - 49]);
                }
            }
        }
        let sky = instance(
            Mesh::triangles(vertices, indices).unwrap(),
            material(Texture::rgba8(256, 128, sky_pixels).unwrap(), true),
        );
        let palette = material(
            Texture::rgba8(
                6,
                1,
                vec![
                    107, 164, 25, 255, 139, 186, 37, 255, 161, 192, 42, 255, 184, 170, 40, 255,
                    207, 173, 42, 255, 96, 145, 29, 255,
                ],
            )
            .unwrap(),
            false,
        );
        let mut grass = Vec::new();
        let mut seed = 27491;
        for cz in 0..8 {
            for cx in 0..8 {
                let mut vertices = Vec::new();
                let mut indices = Vec::new();
                let minx = -h + cx as f32 * h / 4.;
                let minz = -h + cz as f32 * h / 4.;
                for _ in 0..1800 {
                    let x = minx + random(&mut seed) * h / 4.;
                    let z = minz + random(&mut seed) * h / 4.;
                    if (x - path_x(z)).abs() < 3.3
                        || zone.obstacles.iter().any(|o| {
                            (x - o.center[0] as f32).abs() < o.size[0] as f32 * 0.5 + 0.3
                                && (z - o.center[2] as f32).abs() < o.size[2] as f32 * 0.5 + 0.3
                        })
                    {
                        continue;
                    }
                    let height = 0.18 + random(&mut seed) * 0.48;
                    let autumn = x > 7. && z > -16.;
                    let color = if autumn {
                        3 + (random(&mut seed) * 2.) as usize
                    } else {
                        (random(&mut seed) * 3.) as usize
                    };
                    let uv = [(color as f32 + 0.5) / 6., 0.5];
                    for blade in 0..3 {
                        let angle = random(&mut seed) * std::f32::consts::TAU + blade as f32;
                        let side = Vec3::new(angle.cos(), 0., angle.sin()) * 0.075;
                        let p = Vec3::new(x, 0., z);
                        let bend = Vec3::new(angle.sin(), 0., -angle.cos()) * height * 0.42;
                        let middle = p + Vec3::Y * height * 0.55 + bend * 0.25;
                        let tip = p + Vec3::Y * height + bend;
                        let start = vertices.len() as u32;
                        vertices.extend([
                            vertex((p - side).to_array(), uv),
                            vertex((p + side).to_array(), uv),
                            vertex((middle - side * 0.55).to_array(), uv),
                            vertex((middle + side * 0.55).to_array(), uv),
                            vertex(tip.to_array(), uv),
                        ]);
                        indices.extend([
                            start,
                            start + 1,
                            start + 2,
                            start + 1,
                            start + 3,
                            start + 2,
                            start + 2,
                            start + 3,
                            start + 4,
                        ]);
                    }
                }
                if !indices.is_empty() {
                    grass.push((
                        ModelBounds {
                            min: [minx - 0.4, 0., minz - 0.4],
                            max: [minx + h / 4. + 0.4, 0.8, minz + h / 4. + 0.4],
                        },
                        instance(Mesh::triangles(vertices, indices).unwrap(), palette.clone()),
                    ));
                }
            }
        }
        Self {
            ground,
            hills,
            sky,
            grass,
        }
    }
    pub fn backdrop(&self, camera: Camera3d, scene: &mut Scene3d) {
        scene.lighting = SceneLighting {
            direction: [-0.4364358, 0.8728716, -0.2182179],
            radiance: [2.4, 2.25, 1.95],
            ambient: [0.48, 0.55, 0.60],
        };
        let mut sky = self.sky.clone();
        sky.position = camera.position;
        sky.scale = camera.far * 0.92;
        scene
            .meshes
            .extend([sky, self.ground.clone(), self.hills.clone()]);
    }
    pub fn decorate(&self, projection: Option<glam::Mat4>, scene: &mut Scene3d) {
        for (bounds, draw) in &self.grass {
            if scene.meshes.len() < 256 && projection.is_none_or(|p| bounds.intersects_clip(p)) {
                scene.meshes.push(draw.clone());
            }
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn meadow_ground_matches_collision_plane_and_grass_respects_draw_budget() {
        let zone = ZoneDefinition::load(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../assets/logic/worlds/meadow.world.toml"),
        )
        .unwrap();
        let landscape = Landscape::new(&zone);
        assert!(
            landscape.hills.mesh.as_ref().unwrap().vertices()[..97]
                .iter()
                .all(|v| (v.position[1] + 0.015).abs() < 1e-6)
        );
        assert!(
            landscape
                .ground
                .mesh
                .as_ref()
                .unwrap()
                .vertices()
                .iter()
                .all(|v| (v.position[1] + 0.015).abs() < 1e-6)
        );
        for (_, draw) in &landscape.grass {
            assert!(
                draw.mesh
                    .as_ref()
                    .unwrap()
                    .vertices()
                    .iter()
                    .all(|v| (v.position[0] - path_x(v.position[2])).abs() > 2.9)
            );
        }
        let mut scene = Scene3d::default();
        landscape.backdrop(scene.camera, &mut scene);
        assert!(scene.lighting.is_valid());
        scene.meshes.resize(255, landscape.ground.clone());
        landscape.decorate(None, &mut scene);
        assert_eq!(scene.meshes.len(), 256);
    }
}
