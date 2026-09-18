//! Client-only scenery. Solid placements come from the server's zone definition.
use arena_arpg_shared::open_world::content::ZoneDefinition;
use glam::{Mat4, Quat, Vec3};
use nico_animation::Pose;
use nico_assets::{
    Texture,
    import::ImportBudget,
    importers::{PngImporter, PngSettings},
};
use nico_presentation::{Camera3d, MeshInstance, Scene3d};
use nico_presentation_control::model::{ModelBounds, ModelVisual};
use serde::{Deserialize, Serialize};
use std::{collections::BTreeMap, path::Path, sync::Arc};

pub const DEFAULT_VISUAL_WORLD: &str =
    "games/arena-arpg/assets/presentation/worlds/meadow.world-vis.toml";
type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct Definition {
    pub(crate) schema_version: u32,
    pub(crate) zone: String,
    pub(crate) models: BTreeMap<String, String>,
    pub(crate) obstacles: BTreeMap<String, Solid>,
    pub(crate) decorations: Vec<Decoration>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct Solid {
    pub(crate) model: String,
    /// Omit to fit the whole rock into its collision box; trees specify canopy height.
    pub(crate) height_m: Option<f32>,
    #[serde(default)]
    pub(crate) autumn: bool,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct Decoration {
    pub(crate) model: String,
    pub(crate) position: [f32; 3],
    pub(crate) height_m: f32,
    #[serde(default)]
    pub(crate) yaw_radians: f32,
    #[serde(default)]
    pub(crate) autumn: bool,
}
struct Model {
    visual: ModelVisual,
    globals: Vec<Mat4>,
    bounds: ModelBounds,
    foliage: Vec<Arc<Texture>>,
}
struct Placement {
    draws: Vec<MeshInstance>,
    bounds: ModelBounds,
}
pub struct Environment {
    pub(crate) definition: Definition,
    models: BTreeMap<String, Model>,
    solids: BTreeMap<usize, Placement>,
    decorations: Vec<Placement>,
    landscape: Option<super::landscape::Landscape>,
    pub inspection: serde_json::Value,
}
fn height_valid(height: f32) -> bool {
    height.is_finite() && (0.05..=20.).contains(&height)
}
impl Environment {
    pub fn load(path: &Path) -> Result<Self> {
        let definition: Definition =
            toml::from_str(&arena_arpg_shared::characters::read_definition(path)?)?;
        if definition.schema_version != 1
            || definition.zone.is_empty()
            || definition.zone.len() > 64
            || definition.models.is_empty()
            || definition.models.len() > 16
            || definition.obstacles.len() > 32
            || definition.decorations.len() > 128
        {
            return Err("invalid world visual definition".into());
        }
        for solid in definition.obstacles.values() {
            if !definition.models.contains_key(&solid.model)
                || solid.height_m.is_some_and(|h| !height_valid(h))
            {
                return Err("invalid world obstacle visual".into());
            }
        }
        for d in &definition.decorations {
            if !definition.models.contains_key(&d.model)
                || !height_valid(d.height_m)
                || d.position.iter().any(|x| !x.is_finite() || x.abs() > 128.)
                || !d.yaw_radians.is_finite()
            {
                return Err("invalid world decoration".into());
            }
        }
        let progress = nico_assets::progress::ImportProgress::new(
            "environment models",
            definition.models.len(),
        )?;
        let mut sources = Vec::new();
        let mut models = BTreeMap::new();
        let mut source_bytes = 0u64;
        let mut decoded_bytes = 0usize;
        let mut images: BTreeMap<Vec<u8>, Arc<Texture>> = BTreeMap::new();
        for (name, asset) in &definition.models {
            let path = nico_assets::character::asset_path(
                path.parent().ok_or("world asset directory")?,
                asset,
            )?;
            source_bytes += std::fs::metadata(&path)?.len();
            if source_bytes > 128 * 1024 * 1024 {
                return Err("world model budget exceeded".into());
            }
            let model = crate::load_model(&path)?;
            if !model.data().skins.is_empty() || !model.data().clips.is_empty() {
                return Err("world decorations must be static".into());
            }
            sources.push((name.clone(), path, model));
            progress.complete_one();
        }
        progress.finish();
        let texture_count = sources
            .iter()
            .map(|(_, _, model)| {
                (0..model.data().textures.len())
                    .filter(|i| {
                        model
                            .data()
                            .materials
                            .iter()
                            .any(|m| m.texture_indices().contains(&Some(*i)))
                    })
                    .count()
            })
            .sum();
        let progress =
            nico_assets::progress::ImportProgress::new("environment textures", texture_count)?;
        for (name, path, model) in sources {
            let mut textures = Vec::new();
            for (index, texture) in model.data().textures.iter().enumerate() {
                if !model
                    .data()
                    .materials
                    .iter()
                    .any(|m| m.texture_indices().contains(&Some(index)))
                {
                    textures.push(None);
                    continue;
                }
                let image = &model.data().images[texture.image];
                if let Some(texture) = images.get(&image.bytes) {
                    textures.push(Some(texture.clone()));
                    progress.reuse_one();
                    continue;
                }
                let remaining = (256 * 1024 * 1024usize).saturating_sub(decoded_bytes);
                let texture: Texture = nico_assets::cache::load_bytes(
                    &path,
                    &format!("image/{}", texture.image),
                    &image.bytes,
                    &PngImporter,
                    &PngSettings::default(),
                    ImportBudget {
                        max_input_bytes: 16 * 1024 * 1024,
                        max_decoded_bytes: remaining,
                    },
                    &|| false,
                )?;
                // All selected inputs are RGBA8; enforce the aggregate decoded budget.
                decoded_bytes += texture.width() as usize * texture.height() as usize * 4;
                let texture = Arc::new(texture);
                images.insert(image.bytes.clone(), texture.clone());
                textures.push(Some(texture));
                progress.complete_one();
            }
            let foliage = model
                .data()
                .materials
                .iter()
                .filter(|m| m.name.to_lowercase().contains("leaves"))
                .filter_map(|m| m.base_color_texture.and_then(|i| textures[i].clone()))
                .collect();
            let globals = Pose::rest(&model).globals()?;
            let visual = ModelVisual::new(model, textures)?;
            let bounds = visual.bounds(&globals, Mat4::IDENTITY)?;
            if (Vec3::from(bounds.max) - Vec3::from(bounds.min)).min_element() <= 0. {
                return Err("empty world model bounds".into());
            }
            models.insert(
                name.clone(),
                Model {
                    visual,
                    globals,
                    bounds,
                    foliage,
                },
            );
        }
        progress.finish();
        Ok(Self {
            definition,
            models,
            solids: BTreeMap::new(),
            decorations: Vec::new(),
            landscape: None,
            inspection: serde_json::Value::Null,
        })
    }
    /// Rebuild cached static palettes only when the server supplies a new zone/epoch.
    pub fn bind(&mut self, zone: &ZoneDefinition) -> Result<()> {
        let mut solids = BTreeMap::new();
        let mut decorations = Vec::new();
        if zone.id != self.definition.zone {
            self.solids.clear();
            self.decorations.clear();
            self.landscape = None;
            return Ok(());
        }
        let landscape = Some(super::landscape::Landscape::new(zone));
        for (index, obstacle) in zone.obstacles.iter().enumerate() {
            if let Some(binding) = self.definition.obstacles.get(&obstacle.id) {
                let model = &self.models[&binding.model];
                let min = Vec3::from(model.bounds.min);
                let max = Vec3::from(model.bounds.max);
                let center = Vec3::from(obstacle.center.map(|x| x as f32));
                let size = Vec3::from(obstacle.size.map(|x| x as f32));
                let transform = if let Some(height) = binding.height_m {
                    model.grounded(center - Vec3::Y * size.y * 0.5, height, 0.)
                } else {
                    Mat4::from_translation(center)
                        * Mat4::from_scale(size / (max - min))
                        * Mat4::from_translation(-(min + max) * 0.5)
                };
                solids.insert(index, model.place(transform, binding.autumn)?);
            }
        }
        for d in &self.definition.decorations {
            // Outside-zone content never appears when a smaller authored zone is served.
            if d.position[0].abs() as f64 >= zone.half_extent_m
                || d.position[2].abs() as f64 >= zone.half_extent_m
            {
                continue;
            }
            let model = &self.models[&d.model];
            decorations.push(model.place(
                model.grounded(d.position.into(), d.height_m, d.yaw_radians),
                d.autumn,
            )?);
        }
        self.solids = solids;
        self.decorations = decorations;
        self.landscape = landscape;
        Ok(())
    }
    pub fn backdrop(&self, camera: Camera3d, scene: &mut Scene3d) -> bool {
        if let Some(landscape) = &self.landscape {
            landscape.backdrop(camera, scene);
            true
        } else {
            false
        }
    }
    pub fn obstacle(&self, index: usize, projection: Option<Mat4>, scene: &mut Scene3d) -> bool {
        let Some(placement) = self.solids.get(&index) else {
            return false;
        };
        placement.submit(projection, scene);
        true
    }
    /// Decorations consume only the budget left after all actors and solid scenery.
    pub fn decorate(&mut self, projection: Option<Mat4>, scene: &mut Scene3d) {
        let before = scene.meshes.len();
        for placement in &self.decorations {
            placement.submit(projection, scene);
        }
        if let Some(landscape) = &self.landscape {
            landscape.decorate(projection, scene);
        }
        self.inspection = serde_json::json!({"zone":self.definition.zone,"loaded_models":self.models.len(),"solid_models":self.solids.len(),"decorations":self.decorations.len(),"decoration_draws":scene.meshes.len()-before});
    }
}
impl Model {
    fn grounded(&self, position: Vec3, height: f32, yaw: f32) -> Mat4 {
        let min = Vec3::from(self.bounds.min);
        let max = Vec3::from(self.bounds.max);
        Mat4::from_scale_rotation_translation(
            Vec3::splat(height / (max.y - min.y)),
            Quat::from_rotation_y(yaw),
            position,
        ) * Mat4::from_translation(-Vec3::new(
            (min.x + max.x) * 0.5,
            min.y,
            (min.z + max.z) * 0.5,
        ))
    }
    fn place(&self, transform: Mat4, autumn: bool) -> Result<Placement> {
        let mut draws = self.visual.meshes(&self.globals)?;
        for draw in &mut draws {
            // Retain foliage alpha/normal maps and bark color; warm only leaf draws.
            if autumn
                && draw
                    .material
                    .as_ref()
                    .and_then(|m| m.base_color_texture.as_ref())
                    .is_some_and(|t| {
                        self.foliage
                            .iter()
                            .any(|image| Arc::ptr_eq(image, &t.image))
                    })
            {
                draw.color = [6., 0.65, 0.045, 1.];
            }
            let palette = draw
                .skin_palette
                .as_ref()
                .ok_or("missing static model palette")?;
            draw.skin_palette = Some(Arc::new(
                palette
                    .iter()
                    .map(|m| (transform * Mat4::from_cols_array_2d(m)).to_cols_array_2d())
                    .collect(),
            ));
        }
        Ok(Placement {
            draws,
            bounds: self.visual.bounds(&self.globals, transform)?,
        })
    }
}
impl Placement {
    fn submit(&self, projection: Option<Mat4>, scene: &mut Scene3d) {
        if scene.meshes.len() + self.draws.len() <= 256
            && projection.is_none_or(|p| self.bounds.intersects_clip(p))
        {
            scene.meshes.extend(self.draws.iter().cloned());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn environment() -> Environment {
        Environment::load(
            &Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../assets/presentation/worlds/meadow.world-vis.toml"),
        )
        .unwrap()
    }
    fn zone() -> ZoneDefinition {
        ZoneDefinition::load(
            &Path::new(env!("CARGO_MANIFEST_DIR")).join("../assets/logic/worlds/meadow.world.toml"),
        )
        .unwrap()
    }
    #[test]
    fn scenery_uses_server_obstacle_bounds_and_rebinds_after_zone_changes() {
        let mut environment = environment();
        let mut zone = zone();
        environment.bind(&zone).unwrap();
        assert_eq!(environment.solids.len(), 13);
        assert_eq!(
            environment.decorations.len(),
            environment.definition.decorations.len()
        );
        for index in [2, 3] {
            let obstacle = &zone.obstacles[index];
            let bounds = environment.solids[&index].bounds;
            for axis in 0..3 {
                assert!(
                    (f64::from(bounds.min[axis])
                        - (obstacle.center[axis] - obstacle.size[axis] / 2.))
                        .abs()
                        < 0.005
                );
                assert!(
                    (f64::from(bounds.max[axis])
                        - (obstacle.center[axis] + obstacle.size[axis] / 2.))
                        .abs()
                        < 0.005
                );
            }
        }
        for index in 4..zone.obstacles.len() {
            let bounds = environment.solids[&index].bounds;
            assert!(bounds.min[1].abs() < 0.005);
            assert!(
                ((bounds.min[0] + bounds.max[0]) * 0.5 - zone.obstacles[index].center[0] as f32)
                    .abs()
                    < 0.005
            );
        }
        zone.obstacles[2].center[0] += 2.;
        environment.bind(&zone).unwrap();
        assert!((environment.solids[&2].bounds.min[0] + 15.).abs() < 0.005);
        zone.id = "another-zone".into();
        environment.bind(&zone).unwrap();
        assert!(environment.solids.is_empty() && environment.decorations.is_empty());
    }
    #[test]
    fn repeated_tree_images_are_shared_and_autumn_preserves_bark() {
        let environment = environment();
        let tree = &environment.models["tree"];
        let canopy = &environment.models["canopy"];
        assert!(Arc::ptr_eq(&tree.foliage[0], &canopy.foliage[0]));
        let summer = canopy.place(Mat4::IDENTITY, false).unwrap();
        let autumn = canopy.place(Mat4::IDENTITY, true).unwrap();
        let mut leaves = 0;
        let mut bark = 0;
        for (summer, autumn) in summer.draws.iter().zip(&autumn.draws) {
            assert!(Arc::ptr_eq(
                summer.material.as_ref().unwrap(),
                autumn.material.as_ref().unwrap()
            ));
            if autumn.color == summer.color {
                bark += 1;
            } else {
                leaves += 1;
                assert_eq!(autumn.color[3], 1.);
            }
        }
        assert!(leaves > 0 && bark > 0);
    }
    #[test]
    fn decorative_draws_share_geometry_and_cannot_exceed_remaining_budget() {
        let mut environment = environment();
        environment.bind(&zone()).unwrap();
        let placement = &environment.decorations[0];
        let mut scene = Scene3d::default();
        placement.submit(None, &mut scene);
        assert!(Arc::ptr_eq(
            scene.meshes[0].mesh.as_ref().unwrap(),
            placement.draws[0].mesh.as_ref().unwrap()
        ));
        assert!(Arc::ptr_eq(
            scene.meshes[0].skin_palette.as_ref().unwrap(),
            placement.draws[0].skin_palette.as_ref().unwrap()
        ));
        scene.meshes = vec![scene.meshes[0].clone(); 255];
        environment.decorate(None, &mut scene);
        assert_eq!(scene.meshes.len(), 256);
    }
}
