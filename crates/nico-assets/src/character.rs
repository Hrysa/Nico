//! Runtime-free character asset descriptors. Games compose these into their own schemas.
//! Loading a descriptor does not spawn an entity or select gameplay behavior.
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
};
pub type DefinitionResult<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;
/// Reusable authoritative descriptors; a game supplies its own behavior and spawning.
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CharacterCore {
    pub id: String,
    pub collision: Collision,
}
/// A local collider shape descriptor. Placement and filtering belong to the consumer.
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Collision {
    pub shape: CollisionShape,
    pub radius_m: f64,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CollisionShape {
    Ball,
}
/// Shared visual content. Animation keys have no built-in gameplay meaning.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct VisualCore {
    pub character: String,
    pub model: Option<ModelDefinition>,
    #[serde(default)]
    pub profiles: BTreeMap<String, Profile>,
    #[serde(default)]
    pub pose: Vec<PoseOverride>,
    #[serde(default)]
    pub sockets: BTreeMap<String, Socket>,
    #[serde(default)]
    pub animations: BTreeMap<String, Animation>,
}
/// A model reference with humanoid mapping and reference-height normalization.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ModelDefinition {
    pub asset: String,
    pub target_height_m: f32,
    pub floor_offset_m: f32,
    pub profile: String,
}
/// An explicit humanoid mapping in nico-animation canonical bone order.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Profile {
    /// Canonical Bone::ALL order, documented in the format specification.
    pub bones: Vec<String>,
    pub motion_root: String,
}
/// A bone-local reference rotation guarded by its expected direct parent.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct PoseOverride {
    pub bone: String,
    pub parent: String,
    pub rotation_xyzw: [f32; 4],
}
/// An attachment frame in source bone-local units; rotations use XYZW order.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Socket {
    pub bone: String,
    pub translation_model: [f32; 3],
    pub rotation_xyzw: [f32; 4],
}
/// One named source clip. Games choose when and how to play it.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Animation {
    pub asset: String,
    pub clip: String,
    pub profile: String,
}
fn check(ok: bool, field: &str) -> DefinitionResult<()> {
    if ok {
        Ok(())
    } else {
        Err(format!("invalid {field}").into())
    }
}
fn identity(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 128
        && id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || b"._-".contains(&c))
}
impl CharacterCore {
    /// Checks descriptor values and references; does not read or import asset files.
    pub fn validate(&self) -> DefinitionResult<()> {
        check(identity(&self.id), "core.id")?;
        check(
            self.collision.radius_m.is_finite() && self.collision.radius_m > 0.,
            "core.collision.radius_m",
        )
    }
}
impl VisualCore {
    /// Checks descriptor values and references; does not read or import asset files.
    pub fn validate(&self) -> DefinitionResult<()> {
        check(identity(&self.character), "core.character")?;
        let quaternion = |q: [f32; 4]| {
            q.iter().all(|v| v.is_finite())
                && (q.iter().map(|v| v * v).sum::<f32>() - 1.).abs() < 0.001
        };
        check(
            self.pose.len() <= 256 && self.sockets.len() <= 64 && self.profiles.len() <= 16,
            "content count limit",
        )?;
        for (name, p) in &self.profiles {
            check(
                !matches!(name.as_str(), "mixamo" | "rpg")
                    && p.bones.len() == 22
                    && !p.motion_root.is_empty()
                    && p.bones.iter().all(|n| !n.is_empty()),
                "profiles",
            )?;
        }
        for (i, p) in self.pose.iter().enumerate() {
            check(
                !p.bone.is_empty()
                    && !p.parent.is_empty()
                    && quaternion(p.rotation_xyzw)
                    && !self.pose[..i].iter().any(|other| other.bone == p.bone),
                "pose",
            )?;
        }
        for s in self.sockets.values() {
            check(
                !s.bone.is_empty()
                    && s.translation_model
                        .iter()
                        .all(|v| v.is_finite() && v.abs() <= 10000.)
                    && quaternion(s.rotation_xyzw),
                "sockets",
            )?;
        }
        if let Some(model) = &self.model {
            asset_path(Path::new("."), &model.asset)?;
            check(
                model.target_height_m.is_finite()
                    && model.target_height_m > 0.
                    && model.target_height_m <= 100.
                    && model.floor_offset_m.is_finite()
                    && model.floor_offset_m.abs() <= 10.,
                "core.model dimensions",
            )?;
            self.validate_profile(&model.profile)?;
        }
        check(self.animations.len() <= 256, "core.animations count")?;
        for (name, a) in &self.animations {
            check(
                !name.is_empty() && !a.clip.is_empty(),
                "core.animations name/clip",
            )?;
            asset_path(Path::new("."), &a.asset)?;
            self.validate_profile(&a.profile)?;
        }
        Ok(())
    }
    fn validate_profile(&self, name: &str) -> DefinitionResult<()> {
        check(
            matches!(name, "mixamo" | "rpg") || self.profiles.contains_key(name),
            "core.profile reference",
        )
    }
}
/// File references are relative to the definition directory; no traversal or URIs.
pub fn asset_path(root: &Path, asset: &str) -> DefinitionResult<PathBuf> {
    use std::path::Component;
    let path = Path::new(asset);
    if asset.is_empty()
        || asset.contains(':')
        || path
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
    {
        return Err(format!("invalid relative asset path: {asset}").into());
    }
    Ok(root.join(path))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn engine_core_loads_without_game_rules() {
        let source = "id = 'example.worker'\n[collision]\nshape = 'ball'\nradius_m = 0.5\n";
        let core: CharacterCore = toml::from_str(source).unwrap();
        core.validate().unwrap();
        assert!(
            toml::from_str::<CharacterCore>(&format!("{source}[dodge]\nduration_ticks = 18"))
                .is_err()
        );
        let mut invalid = core;
        invalid.collision.radius_m = f64::NAN;
        assert!(invalid.validate().is_err());
        invalid.collision.radius_m = -1.;
        assert!(invalid.validate().is_err());
    }
    #[test]
    fn visual_library_has_no_required_game_actions() {
        let source = r#"
character = "example.worker"
[model]
asset = "worker.glb"
target_height_m = 1.8
floor_offset_m = 0.0
profile = "mixamo"
[animations.wave_to_friend]
asset = "greetings.glb"
clip = "Wave"
profile = "mixamo"
"#;
        let core: VisualCore = toml::from_str(source).unwrap();
        core.validate().unwrap();
        assert_eq!(core.animations.len(), 1);
        for invalid in [
            source.replace("greetings.glb", "../greetings.glb"),
            source.replace("clip = \"Wave\"", "clip = \"\""),
            source.replace("mixamo", "unknown"),
        ] {
            assert!(
                toml::from_str::<VisualCore>(&invalid)
                    .unwrap()
                    .validate()
                    .is_err()
            );
        }
        assert!(toml::from_str::<VisualCore>(&format!("{source}contact_seconds = 0.2")).is_err());
    }
}
