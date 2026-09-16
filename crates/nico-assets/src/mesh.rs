/// Static mesh vertex. Coordinates are right-handed, Y up; UV origin is top-left.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MeshVertex {
    pub position: [f32; 3],
    pub uv: [f32; 2],
}

/// Four normalized joint influences per vertex. Indices address a draw's palette.
#[derive(Clone, Copy, Debug)]
pub struct SkinWeights {
    pub joints: [u16; 4],
    pub weights: [f32; 4],
}

/// Immutable indexed triangle geometry, independent of textures and GPU resources.
#[derive(Debug)]
pub struct Mesh {
    vertices: Vec<MeshVertex>,
    indices: Vec<u32>,
    skin: Option<Vec<SkinWeights>>,
    joint_count: usize,
}

impl Mesh {
    /// Validates nonempty triangles, finite attributes, and in-range indices.
    pub fn triangles(vertices: Vec<MeshVertex>, indices: Vec<u32>) -> Option<Self> {
        if vertices.is_empty()
            || indices.is_empty()
            || !indices.len().is_multiple_of(3)
            || vertices
                .iter()
                .any(|v| v.position.iter().chain(&v.uv).any(|x| !x.is_finite()))
            || indices.iter().any(|&i| i as usize >= vertices.len())
        {
            return None;
        }
        Some(Self {
            vertices,
            indices,
            skin: None,
            joint_count: 0,
        })
    }
    /// Validates immutable skin geometry once. Draw palettes must have at least
    /// `joint_count` entries. Model-space skinning supports up to 256 joints.
    pub fn skinned_triangles(
        vertices: Vec<MeshVertex>,
        indices: Vec<u32>,
        skin: Vec<SkinWeights>,
        joint_count: usize,
    ) -> Option<Self> {
        let mut mesh = Self::triangles(vertices, indices)?;
        if skin.len() != mesh.vertices.len()
            || !(1..=256).contains(&joint_count)
            || skin.iter().any(|v| {
                v.joints.iter().any(|&j| usize::from(j) >= joint_count)
                    || v.weights.iter().any(|w| !w.is_finite() || *w < 0.)
                    || (v.weights.iter().sum::<f32>() - 1.).abs() > 1e-3
            })
        {
            return None;
        }
        mesh.skin = Some(skin);
        mesh.joint_count = joint_count;
        Some(mesh)
    }
    #[must_use]
    pub fn skin(&self) -> Option<&[SkinWeights]> {
        self.skin.as_deref()
    }
    #[must_use]
    pub fn joint_count(&self) -> usize {
        self.joint_count
    }
    #[must_use]
    pub fn vertices(&self) -> &[MeshVertex] {
        &self.vertices
    }
    #[must_use]
    pub fn indices(&self) -> &[u32] {
        &self.indices
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn skin_geometry_rejects_bad_weights_joint_indices_and_count_mismatches() {
        let vertices = vec![
            MeshVertex {
                position: [0.; 3],
                uv: [0.; 2]
            };
            3
        ];
        let valid = SkinWeights {
            joints: [0; 4],
            weights: [1., 0., 0., 0.],
        };
        assert!(
            Mesh::skinned_triangles(vertices.clone(), vec![0, 1, 2], vec![valid; 3], 1).is_some()
        );
        for (skin, count) in [
            (vec![valid; 2], 1),
            (vec![valid; 3], 0),
            (vec![valid; 3], 257),
            (
                vec![
                    SkinWeights {
                        joints: [1; 4],
                        ..valid
                    };
                    3
                ],
                1,
            ),
            (
                vec![
                    SkinWeights {
                        weights: [0.; 4],
                        ..valid
                    };
                    3
                ],
                1,
            ),
            (
                vec![
                    SkinWeights {
                        weights: [f32::NAN, 0., 0., 0.],
                        ..valid
                    };
                    3
                ],
                1,
            ),
            (
                vec![
                    SkinWeights {
                        weights: [1.5, -0.5, 0., 0.],
                        ..valid
                    };
                    3
                ],
                1,
            ),
        ] {
            assert!(
                Mesh::skinned_triangles(vertices.clone(), vec![0, 1, 2], skin, count).is_none()
            );
        }
    }
}
