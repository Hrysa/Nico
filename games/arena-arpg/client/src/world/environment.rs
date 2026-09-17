//! Client-only scenery. Solid placements come from the server's zone definition.
use arena_arpg_shared::open_world::content::ZoneDefinition;
use glam::{Mat4, Quat, Vec3};
use nico_animation::Pose;
use nico_assets::{
    Texture,
    import::{AssetImporter, ImportBudget, ImportContext},
    importers::{PngImporter, PngSettings},
};
use nico_presentation::{MeshInstance, Scene3d};
use nico_presentation_control::model::{ModelBounds, ModelVisual};
use serde::Deserialize;
use std::{collections::BTreeMap, path::Path, sync::Arc};

pub const DEFAULT_VISUAL_WORLD: &str =
    "games/arena-arpg/assets/presentation/worlds/meadow.world-vis.toml";
type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Definition {
    schema_version: u32,
    zone: String,
    models: BTreeMap<String, String>,
    obstacles: BTreeMap<String, Solid>,
    decorations: Vec<Decoration>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Solid {
    model: String,
    /// Omit to fit the whole rock into its collision box; trees specify canopy height.
    height_m: Option<f32>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Decoration {
    model: String,
    position: [f32; 3],
    height_m: f32,
    #[serde(default)]
    yaw_radians: f32,
}
struct Model {
    visual: ModelVisual,
    globals: Vec<Mat4>,
    bounds: ModelBounds,
}
struct Placement {
    draws: Vec<MeshInstance>,
    bounds: ModelBounds,
}
pub struct Environment {
    definition: Definition,
    models: BTreeMap<String, Model>,
    solids: BTreeMap<usize, Placement>,
    decorations: Vec<Placement>,
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
        let mut models = BTreeMap::new();
        let mut source_bytes = 0u64;
        let mut decoded_bytes = 0usize;
        for (name, asset) in &definition.models {
            let path = nico_assets::character::asset_path(
                path.parent().ok_or("world asset directory")?,
                asset,
            )?;
            source_bytes += std::fs::metadata(&path)?.len();
            if source_bytes > 128 * 1024 * 1024 {
                return Err("world model budget exceeded".into());
            }
            let model = crate::character::load(&path)?;
            if !model.data().skins.is_empty() || !model.data().clips.is_empty() {
                return Err("world decorations must be static".into());
            }
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
                let remaining = (256 * 1024 * 1024usize).saturating_sub(decoded_bytes);
                let mut context = ImportContext::new(
                    &image.bytes,
                    ImportBudget {
                        max_input_bytes: 16 * 1024 * 1024,
                        max_decoded_bytes: remaining,
                    },
                    &|| false,
                )?;
                let texture: Texture = PngImporter.import(&mut context, &PngSettings::default())?;
                // All selected inputs are RGBA8; enforce the aggregate decoded budget.
                decoded_bytes += texture.width() as usize * texture.height() as usize * 4;
                textures.push(Some(Arc::new(texture)));
            }
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
                },
            );
        }
        Ok(Self {
            definition,
            models,
            solids: BTreeMap::new(),
            decorations: Vec::new(),
            inspection: serde_json::Value::Null,
        })
    }
    /// Rebuild cached static palettes only when the server supplies a new zone/epoch.
    pub fn bind(&mut self, zone: &ZoneDefinition) -> Result<()> {
        self.solids.clear();
        self.decorations.clear();
        if zone.id != self.definition.zone {
            return Ok(());
        }
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
                self.solids.insert(index, model.place(transform)?);
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
            self.decorations.push(model.place(model.grounded(
                d.position.into(),
                d.height_m,
                d.yaw_radians,
            ))?);
        }
        Ok(())
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
    fn place(&self, transform: Mat4) -> Result<Placement> {
        let mut draws = self.visual.meshes(&self.globals)?;
        for draw in &mut draws {
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
        assert_eq!(environment.solids.len(), 11);
        assert_eq!(environment.decorations.len(), 44);
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
