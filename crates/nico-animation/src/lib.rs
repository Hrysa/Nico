//! Pure CPU pose sampling, skin matrices, and explicit humanoid motion conversion.
//! No runtime, renderer, provider, or game-policy dependencies.
use glam::{Mat4, Quat, Vec3};
use nico_assets::model::{Interpolation, Matrix4, Model, TrackValues, Transform};
use std::{borrow::Cow, sync::Arc};
pub mod attachment;
pub mod humanoid;
pub mod playback;

#[cfg(test)]
mod tests;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AnimationError {
    pub code: &'static str,
    pub detail: String,
}
impl AnimationError {
    pub(crate) fn new(code: &'static str, detail: impl Into<String>) -> Self {
        Self {
            code,
            detail: detail.into(),
        }
    }
}
impl std::fmt::Display for AnimationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.code, self.detail)
    }
}
impl std::error::Error for AnimationError {}

#[derive(Clone, Copy, Debug)]
pub enum Playback {
    Clamp,
    Loop,
}

/// A pose is tied to the exact immutable model whose indices it uses.
#[derive(Clone, Debug)]
pub struct Pose<'a> {
    model: &'a Model,
    local: Cow<'a, [Transform]>,
}
impl<'a> Pose<'a> {
    #[must_use]
    pub fn rest(model: &'a Model) -> Self {
        Self {
            model,
            local: Cow::Owned(model.data().nodes.iter().map(|n| n.transform).collect()),
        }
    }
    pub fn from_local(model: &'a Model, local: Vec<Transform>) -> Result<Self, AnimationError> {
        if local.len() != model.data().nodes.len() || local.iter().any(|t| !t.is_valid()) {
            return Err(AnimationError::new("pose", "invalid local transforms"));
        }
        Ok(Self {
            model,
            local: Cow::Owned(local),
        })
    }
    #[must_use]
    pub fn local(&self) -> &[Transform] {
        &self.local
    }
    #[must_use]
    pub const fn model(&self) -> &'a Model {
        self.model
    }
    pub fn globals(&self) -> Result<Vec<Mat4>, AnimationError> {
        let mut globals = Vec::new();
        self.globals_into(&mut globals)?;
        Ok(globals)
    }
    pub fn globals_into(&self, globals: &mut Vec<Mat4>) -> Result<(), AnimationError> {
        globals.resize(self.local.len(), Mat4::IDENTITY);
        for &i in self.model.traversal() {
            let t = self.local[i];
            let local = Mat4::from_scale_rotation_translation(
                Vec3::from(t.scale),
                Quat::from_array(t.rotation).normalize(),
                Vec3::from(t.translation),
            );
            globals[i] = self.model.parents()[i].map_or(local, |p| globals[p] * local);
            if !globals[i].is_finite() {
                return Err(AnimationError::new("pose_overflow", i.to_string()));
            }
        }
        Ok(())
    }
    /// Mesh-local skin matrices: inverse(mesh world) * joint world * inverse bind.
    pub fn skin_matrices(&self, mesh_node: usize) -> Result<Vec<Matrix4>, AnimationError> {
        let node = self
            .model
            .data()
            .nodes
            .get(mesh_node)
            .ok_or_else(|| AnimationError::new("mesh_node", "out of range"))?;
        let skin = node
            .skin
            .ok_or_else(|| AnimationError::new("skin", "node is not skinned"))?;
        let globals = self.globals()?;
        let inverse = globals[mesh_node].inverse();
        if !inverse.is_finite() {
            return Err(AnimationError::new(
                "singular_mesh",
                "cannot invert mesh transform",
            ));
        }
        self.model.data().skins[skin]
            .joints
            .iter()
            .zip(&self.model.data().skins[skin].inverse_bind)
            .map(|(&joint, bind)| {
                let matrix = inverse * globals[joint] * Mat4::from_cols_array_2d(bind);
                if !matrix.is_finite() {
                    return Err(AnimationError::new("skin_overflow", joint.to_string()));
                }
                Ok(matrix.to_cols_array_2d())
            })
            .collect()
    }
}

/// Elapsed seconds start at the clip's earliest key, even when its source starts
/// after zero. Loop wraps at duration; Clamp preserves the final key.
pub fn sample(
    model: &Model,
    clip: usize,
    elapsed: f32,
    playback: Playback,
) -> Result<Pose<'_>, AnimationError> {
    let mut pose = Pose::rest(model);
    sample_local(model, clip, elapsed, playback, pose.local.to_mut())?;
    Ok(pose)
}

fn sample_local(
    model: &Model,
    clip: usize,
    elapsed: f32,
    playback: Playback,
    local: &mut [Transform],
) -> Result<(), AnimationError> {
    if !elapsed.is_finite() || elapsed < 0. {
        return Err(AnimationError::new(
            "time",
            "expected finite nonnegative elapsed seconds",
        ));
    }
    let clip = model
        .data()
        .clips
        .get(clip)
        .ok_or_else(|| AnimationError::new("clip", "out of range"))?;
    let (start, end) = clip.time_range();
    let duration = end - start;
    let time = start
        + match playback {
            Playback::Clamp => elapsed.min(duration),
            Playback::Loop if duration > 0. => elapsed.rem_euclid(duration),
            Playback::Loop => 0.,
        };
    for (target, node) in local.iter_mut().zip(&model.data().nodes) {
        *target = node.transform;
    }
    for track in &clip.tracks {
        let upper = track.times.partition_point(|t| *t <= time);
        let a = upper.saturating_sub(1);
        let b = upper.min(track.times.len() - 1);
        let factor = if a == b || track.interpolation == Interpolation::Step {
            0.
        } else {
            ((time - track.times[a]) / (track.times[b] - track.times[a])).clamp(0., 1.)
        };
        let target = &mut local[track.node];
        match &track.values {
            TrackValues::Translation(v) => {
                target.translation = Vec3::from(v[a]).lerp(Vec3::from(v[b]), factor).to_array()
            }
            TrackValues::Scale(v) => {
                target.scale = Vec3::from(v[a]).lerp(Vec3::from(v[b]), factor).to_array()
            }
            TrackValues::Rotation(v) => {
                target.rotation = Quat::from_array(v[a])
                    .normalize()
                    .slerp(Quat::from_array(v[b]).normalize(), factor)
                    .normalize()
                    .to_array()
            }
        }
    }
    if local.iter().any(|t| !t.is_valid()) {
        return Err(AnimationError::new("pose", "sampled transform is invalid"));
    }
    Ok(())
}

/// Per-instance reusable local pose storage. Sampling and blending retain capacity.
/// Failed evaluation preserves the previous valid pose using reusable scratch storage.
#[derive(Clone, Debug)]
pub struct PoseBuffer {
    model: Arc<Model>,
    local: Vec<Transform>,
    scratch: Vec<Transform>,
}
impl PoseBuffer {
    pub fn new(model: Arc<Model>) -> Self {
        let local: Vec<_> = model.data().nodes.iter().map(|n| n.transform).collect();
        let scratch = local.clone();
        Self {
            model,
            local,
            scratch,
        }
    }
    pub fn pose(&self) -> Pose<'_> {
        Pose {
            model: &self.model,
            local: Cow::Borrowed(&self.local),
        }
    }
    pub fn sample(
        &mut self,
        clip: usize,
        elapsed: f32,
        playback: Playback,
    ) -> Result<(), AnimationError> {
        sample_local(&self.model, clip, elapsed, playback, &mut self.scratch)?;
        std::mem::swap(&mut self.local, &mut self.scratch);
        Ok(())
    }
    pub fn reset(&mut self) {
        for (local, node) in self.local.iter_mut().zip(&self.model.data().nodes) {
            *local = node.transform;
        }
    }
    pub fn copy_from(&mut self, pose: &Pose<'_>) -> Result<(), AnimationError> {
        if !std::ptr::eq(self.model.as_ref(), pose.model()) {
            return Err(AnimationError::new(
                "pose_model",
                "pose belongs to another model",
            ));
        }
        self.local.copy_from_slice(pose.local());
        Ok(())
    }
    pub fn blend(
        &mut self,
        from: &Pose<'_>,
        to: &Pose<'_>,
        weight: f32,
    ) -> Result<(), AnimationError> {
        if !weight.is_finite() || !(0.0..=1.0).contains(&weight) {
            return Err(AnimationError::new(
                "blend_weight",
                "expected weight in 0..1",
            ));
        }
        if !std::ptr::eq(self.model.as_ref(), from.model())
            || !std::ptr::eq(self.model.as_ref(), to.model())
        {
            return Err(AnimationError::new(
                "pose_model",
                "blend poses belong to another model",
            ));
        }
        for ((out, a), b) in self.scratch.iter_mut().zip(from.local()).zip(to.local()) {
            out.translation = Vec3::from(a.translation)
                .lerp(Vec3::from(b.translation), weight)
                .to_array();
            out.scale = Vec3::from(a.scale)
                .lerp(Vec3::from(b.scale), weight)
                .to_array();
            out.rotation = Quat::from_array(a.rotation)
                .slerp(Quat::from_array(b.rotation), weight)
                .normalize()
                .to_array();
        }
        if self.scratch.iter().any(|t| !t.is_valid()) {
            return Err(AnimationError::new("pose", "blended transform is invalid"));
        }
        std::mem::swap(&mut self.local, &mut self.scratch);
        Ok(())
    }
}
