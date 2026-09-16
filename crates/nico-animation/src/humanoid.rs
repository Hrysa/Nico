//! Canonical body motion with explicit, userland-configurable rig profiles.
use crate::{AnimationError, Pose, PoseBuffer};
use glam::{Mat4, Quat, Vec3};
use nico_assets::model::Model;
use std::{
    collections::{BTreeMap, BTreeSet},
    sync::Arc,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Ord, PartialOrd)]
pub enum Bone {
    Hips,
    Spine,
    Chest,
    UpperChest,
    Neck,
    Head,
    LeftShoulder,
    LeftUpperArm,
    LeftLowerArm,
    LeftHand,
    RightShoulder,
    RightUpperArm,
    RightLowerArm,
    RightHand,
    LeftUpperLeg,
    LeftLowerLeg,
    LeftFoot,
    LeftToes,
    RightUpperLeg,
    RightLowerLeg,
    RightFoot,
    RightToes,
}
impl Bone {
    pub const ALL: [Self; 22] = [
        Self::Hips,
        Self::Spine,
        Self::Chest,
        Self::UpperChest,
        Self::Neck,
        Self::Head,
        Self::LeftShoulder,
        Self::LeftUpperArm,
        Self::LeftLowerArm,
        Self::LeftHand,
        Self::RightShoulder,
        Self::RightUpperArm,
        Self::RightLowerArm,
        Self::RightHand,
        Self::LeftUpperLeg,
        Self::LeftLowerLeg,
        Self::LeftFoot,
        Self::LeftToes,
        Self::RightUpperLeg,
        Self::RightLowerLeg,
        Self::RightFoot,
        Self::RightToes,
    ];
    fn required(self) -> bool {
        !matches!(
            self,
            Self::Chest
                | Self::UpperChest
                | Self::Neck
                | Self::LeftShoulder
                | Self::RightShoulder
                | Self::LeftToes
                | Self::RightToes
        )
    }
    fn parent(self) -> Option<Self> {
        use Bone::*;
        Some(match self {
            Hips => return None,
            Spine | LeftUpperLeg | RightUpperLeg => Hips,
            Chest => Spine,
            UpperChest => Chest,
            Neck | LeftShoulder | RightShoulder => UpperChest,
            Head => Neck,
            LeftUpperArm => LeftShoulder,
            LeftLowerArm => LeftUpperArm,
            LeftHand => LeftLowerArm,
            RightUpperArm => RightShoulder,
            RightLowerArm => RightUpperArm,
            RightHand => RightLowerArm,
            LeftLowerLeg => LeftUpperLeg,
            LeftFoot => LeftLowerLeg,
            LeftToes => LeftFoot,
            RightLowerLeg => RightUpperLeg,
            RightFoot => RightLowerLeg,
            RightToes => RightFoot,
        })
    }
}

/// Indexed overrides resolve duplicate or absent source names without guessing.
#[derive(Clone, Debug)]
pub enum BoneBinding {
    Name(String),
    Node(usize),
}
impl From<String> for BoneBinding {
    fn from(name: String) -> Self {
        Self::Name(name)
    }
}
impl From<&str> for BoneBinding {
    fn from(name: &str) -> Self {
        Self::Name(name.into())
    }
}

/// Explicit bone mapping, reference orientation, and root-motion source.
/// Modify or construct this in userland; no importer changes are required.
#[derive(Clone, Debug)]
pub struct HumanoidProfile {
    pub bones: BTreeMap<Bone, BoneBinding>,
    pub motion_root: Option<BoneBinding>,
    /// Unit quaternion mapping model-world directions to canonical Y-up/Z-forward.
    pub model_to_canonical: [f32; 4],
}
impl Default for HumanoidProfile {
    fn default() -> Self {
        Self {
            bones: BTreeMap::new(),
            motion_root: None,
            model_to_canonical: [0., 0., 0., 1.],
        }
    }
}
impl HumanoidProfile {
    #[must_use]
    pub fn mixamo() -> Self {
        let names = [
            "Hips",
            "Spine",
            "Spine1",
            "Spine2",
            "Neck",
            "Head",
            "LeftShoulder",
            "LeftArm",
            "LeftForeArm",
            "LeftHand",
            "RightShoulder",
            "RightArm",
            "RightForeArm",
            "RightHand",
            "LeftUpLeg",
            "LeftLeg",
            "LeftFoot",
            "LeftToeBase",
            "RightUpLeg",
            "RightLeg",
            "RightFoot",
            "RightToeBase",
        ];
        Self {
            bones: Bone::ALL
                .into_iter()
                .zip(names.map(|n| format!("mixamorig:{n}").into()))
                .collect(),
            motion_root: Some("mixamorig:Hips".into()),
            ..Default::default()
        }
    }
    #[must_use]
    pub fn rpg() -> Self {
        let names = [
            "B_Pelvis",
            "B_Spine",
            "B_Spine1",
            "B_Spine2",
            "B_Neck",
            "B_Head",
            "B_L_Clavicle",
            "B_L_UpperArm",
            "B_L_Forearm",
            "B_L_Hand",
            "B_R_Clavicle",
            "B_R_UpperArm",
            "B_R_Forearm",
            "B_R_Hand",
            "B_L_Thigh",
            "B_L_Calf",
            "B_L_Foot",
            "B_L_Toe0",
            "B_R_Thigh",
            "B_R_Calf",
            "B_R_Foot",
            "B_R_Toe0",
        ];
        Self {
            bones: Bone::ALL
                .into_iter()
                .zip(names.map(BoneBinding::from))
                .collect(),
            motion_root: Some("Motion".into()),
            ..Default::default()
        }
    }
}
#[derive(Clone, Debug)]
pub struct MappingReport {
    pub mapped: BTreeMap<Bone, usize>,
    pub missing_optional: Vec<Bone>,
    pub unmapped_nodes: Vec<usize>,
}
/// Compiled, shareable rig mapping. Holds the immutable model alive and resolves
/// names, hierarchy and reference corrections once, outside playback.
pub struct HumanoidRig {
    model: Arc<Model>,
    reference: Vec<nico_assets::model::Transform>,
    by_node: Vec<Option<Bone>>,
    report: MappingReport,
    basis: Quat,
    rotations: Vec<Quat>,
    globals: Vec<Mat4>,
    leg_length: f32,
    motion_root: usize,
}
/// World-space rotation deltas in a common reference frame, plus translations
/// measured in source leg lengths. Created from a validated rig, not raw indices.
pub struct HumanoidMotion {
    rotations: [Option<Quat>; 22],
    hips: Vec3,
    root: Vec3,
}
/// Reusable scratch per evaluator, not shared mutable rig state.
#[derive(Clone, Debug, Default)]
pub struct HumanoidWorkspace {
    rotations: Vec<Quat>,
    globals: Vec<Mat4>,
}
impl HumanoidMotion {
    #[must_use]
    pub fn root_displacement(&self) -> [f32; 3] {
        self.root.to_array()
    }
}
#[derive(Clone, Copy, Debug)]
pub enum RootMotion {
    InPlace,
    Preserve,
}

fn error(code: &'static str, detail: impl Into<String>) -> AnimationError {
    AnimationError::new(code, detail)
}
fn find(model: &Model, binding: &BoneBinding) -> Result<Option<usize>, AnimationError> {
    let name = match binding {
        BoneBinding::Node(index) => {
            return if *index < model.data().nodes.len() {
                Ok(Some(*index))
            } else {
                Err(error("bone_index", index.to_string()))
            };
        }
        BoneBinding::Name(name) => name.as_str(),
    };
    let mut found = model
        .data()
        .nodes
        .iter()
        .enumerate()
        .filter(|(_, n)| n.name == name)
        .map(|(i, _)| i);
    let first = found.next();
    if found.next().is_some() {
        return Err(error("ambiguous_bone", name));
    }
    Ok(first)
}
fn ancestor(model: &Model, parent: usize, child: usize) -> bool {
    let mut current = Some(child);
    while let Some(n) = current {
        if n == parent {
            return true;
        }
        current = model.parents()[n];
    }
    false
}
fn rotations(pose: &Pose<'_>) -> Result<Vec<Quat>, AnimationError> {
    let mut out = Vec::new();
    rotations_into(pose, &mut out)?;
    Ok(out)
}
fn rotations_into(pose: &Pose<'_>, out: &mut Vec<Quat>) -> Result<(), AnimationError> {
    out.resize(pose.local().len(), Quat::IDENTITY);
    for &i in pose.model().traversal() {
        let t = pose.local()[i];
        let scale = Vec3::from(t.scale);
        if scale.min_element() <= 0.
            || scale.max_element() - scale.min_element() > scale.max_element() * 1e-4
        {
            return Err(error(
                "humanoid_scale",
                format!("node {i}: positive uniform scale required"),
            ));
        }
        let rotation = Quat::from_array(t.rotation).normalize();
        out[i] = pose.model().parents()[i]
            .map_or(rotation, |p| out[p] * rotation)
            .normalize();
    }
    Ok(())
}
impl HumanoidRig {
    /// Immutable model retained by this compiled mapping.
    pub fn model(&self) -> &Arc<Model> {
        &self.model
    }
    pub fn new(model: Arc<Model>, profile: HumanoidProfile) -> Result<Self, AnimationError> {
        let local = Pose::rest(&model).local().to_vec();
        Self::from_reference(model, local, profile)
    }
    /// Supply a calibrated reference pose when bind pose alone is unsuitable.
    pub fn from_reference(
        model: Arc<Model>,
        local: Vec<nico_assets::model::Transform>,
        profile: HumanoidProfile,
    ) -> Result<Self, AnimationError> {
        let model_arc = model;
        let reference = Pose::from_local(&model_arc, local)?;
        let model = reference.model();
        let basis = Quat::from_array(profile.model_to_canonical);
        if !basis.is_finite() || (basis.length_squared() - 1.).abs() > 1e-3 {
            return Err(error("humanoid_basis", "expected unit quaternion"));
        }
        let mut mapped = BTreeMap::new();
        let mut missing_optional = Vec::new();
        let mut used = BTreeSet::new();
        for bone in Bone::ALL {
            let node = profile
                .bones
                .get(&bone)
                .map(|name| find(model, name))
                .transpose()?
                .flatten();
            if let Some(node) = node {
                if !used.insert(node) {
                    return Err(error("duplicate_bone", format!("{bone:?}")));
                }
                mapped.insert(bone, node);
            } else if bone.required() {
                return Err(error("missing_bone", format!("{bone:?}")));
            } else {
                missing_optional.push(bone);
            }
        }
        for (&bone, &node) in &mapped {
            let mut parent = bone.parent();
            while let Some(p) = parent {
                if let Some(&index) = mapped.get(&p) {
                    if !ancestor(model, index, node) {
                        return Err(error(
                            "bone_hierarchy",
                            format!("{bone:?} must descend from {p:?}"),
                        ));
                    }
                    break;
                }
                parent = p.parent();
            }
        }
        let hips = mapped[&Bone::Hips];
        let motion_root = match profile.motion_root {
            Some(binding) => find(model, &binding)?
                .ok_or_else(|| error("motion_root", format!("{binding:?}")))?,
            None => hips,
        };
        if !ancestor(model, motion_root, hips) {
            return Err(error("motion_root", "must be an ancestor of hips"));
        }
        let rotations = rotations(&reference)?;
        let globals = reference.globals()?;
        let position = |b| globals[mapped[&b]].transform_point3(Vec3::ZERO);
        let leg_length = ((position(Bone::LeftUpperLeg) - position(Bone::LeftLowerLeg)).length()
            + (position(Bone::LeftLowerLeg) - position(Bone::LeftFoot)).length()
            + (position(Bone::RightUpperLeg) - position(Bone::RightLowerLeg)).length()
            + (position(Bone::RightLowerLeg) - position(Bone::RightFoot)).length())
            * 0.5;
        if !leg_length.is_finite() || leg_length < 1e-6 {
            return Err(error("limb_length", "degenerate reference legs"));
        }
        let report = MappingReport {
            mapped,
            missing_optional,
            unmapped_nodes: (0..model.data().nodes.len())
                .filter(|i| !used.contains(i))
                .collect(),
        };
        let mut by_node = vec![None; model.data().nodes.len()];
        for (&bone, &node) in &report.mapped {
            by_node[node] = Some(bone);
        }
        Ok(Self {
            reference: reference.local().to_vec(),
            model: model_arc,
            by_node,
            report,
            basis: basis.normalize(),
            rotations,
            globals,
            leg_length,
            motion_root,
        })
    }
    #[must_use]
    pub const fn report(&self) -> &MappingReport {
        &self.report
    }
    #[must_use]
    pub const fn leg_length(&self) -> f32 {
        self.leg_length
    }
    pub fn capture(&self, pose: &Pose<'_>) -> Result<HumanoidMotion, AnimationError> {
        self.capture_into(pose, &mut HumanoidWorkspace::default())
    }
    pub fn capture_into(
        &self,
        pose: &Pose<'_>,
        workspace: &mut HumanoidWorkspace,
    ) -> Result<HumanoidMotion, AnimationError> {
        if !std::ptr::eq(self.model.as_ref(), pose.model()) {
            return Err(error("pose_model", "pose belongs to another model"));
        }
        for (t, r) in pose.local().iter().zip(self.reference.as_slice()) {
            if (Vec3::from(t.scale) - Vec3::from(r.scale))
                .abs()
                .max_element()
                > Vec3::from(r.scale).abs().max_element() * 1e-4
            {
                return Err(error(
                    "animated_scale",
                    "humanoid conversion preserves target reference scale",
                ));
            }
        }
        rotations_into(pose, &mut workspace.rotations)?;
        pose.globals_into(&mut workspace.globals)?;
        let current = &workspace.rotations;
        let globals = &workspace.globals;
        let displacement = |node: usize| {
            self.basis
                * (globals[node].transform_point3(Vec3::ZERO)
                    - self.globals[node].transform_point3(Vec3::ZERO))
                / self.leg_length
        };
        let hips = displacement(self.report.mapped[&Bone::Hips]);
        let root = displacement(self.motion_root);
        if !hips.is_finite() || !root.is_finite() {
            return Err(error("motion_overflow", "nonfinite displacement"));
        }
        let mut rotations = [None; 22];
        for (&bone, &node) in &self.report.mapped {
            rotations[bone as usize] = Some(
                (self.basis
                    * current[node]
                    * self.rotations[node].conjugate()
                    * self.basis.conjugate())
                .normalize(),
            );
        }
        Ok(HumanoidMotion {
            rotations,
            hips,
            root,
        })
    }
    /// Retains target limb lengths/helper joints. Only hips receive translated
    /// motion; unmapped fingers/twist joints retain their reference local pose.
    pub fn apply(
        &self,
        motion: &HumanoidMotion,
        policy: RootMotion,
    ) -> Result<Pose<'_>, AnimationError> {
        let mut output = PoseBuffer::new(self.model.clone());
        self.apply_into(
            motion,
            policy,
            &mut output,
            &mut HumanoidWorkspace::default(),
        )?;
        Pose::from_local(&self.model, output.local)
    }
    pub fn apply_into(
        &self,
        motion: &HumanoidMotion,
        policy: RootMotion,
        output: &mut PoseBuffer,
        workspace: &mut HumanoidWorkspace,
    ) -> Result<(), AnimationError> {
        if !Arc::ptr_eq(&self.model, &output.model) {
            return Err(error("pose_model", "output belongs to another model"));
        }
        let model = self.model.as_ref();
        output.scratch.copy_from_slice(&self.reference);
        let local = &mut output.scratch;
        workspace.globals.resize(local.len(), Mat4::IDENTITY);
        workspace.rotations.resize(local.len(), Quat::IDENTITY);
        let globals = &mut workspace.globals;
        let world_rotations = &mut workspace.rotations;
        let displacement = match policy {
            RootMotion::Preserve => motion.hips,
            RootMotion::InPlace => motion.hips - Vec3::new(motion.root.x, 0., motion.root.z),
        };
        for &n in model.traversal() {
            let parent = model.parents()[n];
            let parent_rotation = parent.map_or(Quat::IDENTITY, |p| world_rotations[p]);
            let parent_matrix = parent.map_or(Mat4::IDENTITY, |p| globals[p]);
            if let Some(bone) = self.by_node[n].as_ref() {
                if let Some(delta) = motion.rotations[*bone as usize].as_ref() {
                    let desired =
                        (self.basis.conjugate() * *delta * self.basis * self.rotations[n])
                            .normalize();
                    local[n].rotation = (parent_rotation.conjugate() * desired)
                        .normalize()
                        .to_array();
                }
                if *bone == Bone::Hips {
                    let desired = self.globals[n].transform_point3(Vec3::ZERO)
                        + self.basis.conjugate() * (displacement * self.leg_length);
                    local[n].translation =
                        parent_matrix.inverse().transform_point3(desired).to_array();
                }
            }
            world_rotations[n] =
                (parent_rotation * Quat::from_array(local[n].rotation)).normalize();
            globals[n] = parent_matrix
                * Mat4::from_scale_rotation_translation(
                    Vec3::from(local[n].scale),
                    Quat::from_array(local[n].rotation),
                    Vec3::from(local[n].translation),
                );
            if !globals[n].is_finite() {
                return Err(error("retarget_overflow", n.to_string()));
            }
        }
        if local.iter().any(|t| !t.is_valid()) {
            return Err(error("pose", "retargeted transform is invalid"));
        }
        std::mem::swap(&mut output.local, &mut output.scratch);
        Ok(())
    }
}
