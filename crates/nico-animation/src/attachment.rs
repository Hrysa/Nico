//! Named sockets resolved once against an immutable model.
use crate::{AnimationError, Pose};
use glam::{Mat4, Quat, Vec3};
use nico_assets::model::{Model, Transform};
use std::sync::Arc;

/// A socket follows the evaluated node pose, including blending and retargeting.
/// The offset is in node-local coordinates. It never changes simulation state.
#[derive(Clone, Debug)]
pub struct Attachment {
    model: Arc<Model>,
    node: usize,
    offset: Mat4,
}
impl Attachment {
    /// Names must match exactly and uniquely; ambiguous names are rejected.
    pub fn new(model: Arc<Model>, name: &str, offset: Transform) -> Result<Self, AnimationError> {
        if !offset.is_valid() {
            return Err(AnimationError::new(
                "attachment_offset",
                "invalid transform",
            ));
        }
        let mut matches = model
            .data()
            .nodes
            .iter()
            .enumerate()
            .filter(|(_, n)| n.name == name);
        let node = matches
            .next()
            .map(|(i, _)| i)
            .ok_or_else(|| AnimationError::new("attachment_node", "node not found"))?;
        if matches.next().is_some() {
            return Err(AnimationError::new(
                "attachment_node",
                "ambiguous node name",
            ));
        }
        Ok(Self {
            model,
            node,
            offset: matrix(offset),
        })
    }
    pub fn node(&self) -> usize {
        self.node
    }
    /// Returns placement * animated node hierarchy * socket offset. Retains full
    /// affine scale/shear instead of decomposing to a lossy uniform-scale pose.
    /// Evaluates only the ancestor chain, with no allocation or name lookup.
    pub fn matrix(&self, pose: &Pose<'_>, placement: Mat4) -> Result<Mat4, AnimationError> {
        if !std::ptr::eq(self.model.as_ref(), pose.model()) {
            return Err(AnimationError::new(
                "pose_model",
                "attachment belongs to another model",
            ));
        }
        if !placement.is_finite() || placement.row(3) != glam::Vec4::W {
            return Err(AnimationError::new(
                "attachment_placement",
                "expected finite affine matrix",
            ));
        }
        let mut result = self.offset;
        let mut node = Some(self.node);
        while let Some(i) = node {
            result = matrix(pose.local()[i]) * result;
            node = self.model.parents()[i];
        }
        result = placement * result;
        if !result.is_finite() {
            return Err(AnimationError::new(
                "attachment_overflow",
                "nonfinite socket matrix",
            ));
        }
        Ok(result)
    }
}
fn matrix(t: Transform) -> Mat4 {
    Mat4::from_scale_rotation_translation(
        Vec3::from(t.scale),
        Quat::from_array(t.rotation).normalize(),
        Vec3::from(t.translation),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tests::body;
    #[test]
    fn socket_follows_evaluated_hierarchy_offset_and_instance_placement() {
        let model = body(1., Quat::IDENTITY);
        let offset = Transform {
            translation: [0., 0., 0.5],
            rotation: Quat::from_rotation_x(0.4).to_array(),
            ..Default::default()
        };
        let socket = Attachment::new(model.clone(), "LeftHand", offset).unwrap();
        let mut locals = Pose::rest(&model).local().to_vec();
        locals[0].scale = [2., 1., 0.5];
        locals[3].rotation = Quat::from_rotation_z(0.7).to_array();
        let pose = Pose::from_local(&model, locals).unwrap();
        let placement =
            Mat4::from_rotation_translation(Quat::from_rotation_y(1.), Vec3::new(10., 0., -4.));
        let expected = placement * pose.globals().unwrap()[5] * matrix(offset);
        assert!(
            socket
                .matrix(&pose, placement)
                .unwrap()
                .abs_diff_eq(expected, 1e-5)
        );
        assert_eq!(socket.node(), 5);
        assert!(
            !expected.abs_diff_eq(socket.matrix(&Pose::rest(&model), placement).unwrap(), 1e-4)
        );
    }
    #[test]
    fn rejects_missing_ambiguous_foreign_and_invalid_inputs() {
        let model = body(1., Quat::IDENTITY);
        assert!(Attachment::new(model.clone(), "missing", Transform::default()).is_err());
        let mut duplicate = model.data().clone();
        duplicate.nodes[15].name = "LeftHand".into();
        assert!(
            Attachment::new(
                Arc::new(Model::new(duplicate).unwrap()),
                "LeftHand",
                Transform::default()
            )
            .is_err()
        );
        assert!(
            Attachment::new(
                model.clone(),
                "LeftHand",
                Transform {
                    scale: [0.; 3],
                    ..Default::default()
                }
            )
            .is_err()
        );
        let socket = Attachment::new(model.clone(), "LeftHand", Transform::default()).unwrap();
        let foreign = body(1., Quat::IDENTITY);
        assert!(
            socket
                .matrix(&Pose::rest(&foreign), Mat4::IDENTITY)
                .is_err()
        );
        assert!(
            socket
                .matrix(&Pose::rest(&model), Mat4::perspective_rh(1., 1., 0.1, 100.))
                .is_err()
        );
        assert!(
            socket
                .matrix(
                    &Pose::rest(&model),
                    Mat4::from_translation(Vec3::splat(f32::NAN))
                )
                .is_err()
        );
    }
}
