/// Static mesh vertex. Coordinates are right-handed, Y up; UV origin is top-left.
#[cfg_attr(feature = "import-cache", derive(serde::Serialize, serde::Deserialize))]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MeshVertex {
    pub position: [f32; 3],
    pub uv: [f32; 2],
}

/// Four normalized joint influences per vertex. Indices address a draw's palette.
#[cfg_attr(feature = "import-cache", derive(serde::Serialize, serde::Deserialize))]
#[derive(Clone, Copy, Debug)]
pub struct SkinWeights {
    pub joints: [u16; 4],
    pub weights: [f32; 4],
}

/// Immutable indexed triangle geometry, independent of textures and GPU resources.
#[derive(Debug)]
pub struct Mesh {
    vertices: Vec<MeshVertex>,
    normals: Vec<[f32; 3]>,
    indices: Vec<u32>,
    skin: Option<Vec<SkinWeights>>,
    joint_count: usize,
    centroid: [f32; 3],
    skin_centroid_weights: Vec<[f32; 4]>,
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
        let normals = generated_normals(&vertices, &indices);
        let mut center = [0_f64; 3];
        for &index in &indices {
            for (sum, position) in center.iter_mut().zip(vertices[index as usize].position) {
                *sum += f64::from(position);
            }
        }
        let centroid = center.map(|v| (v / indices.len() as f64) as f32);
        Some(Self {
            vertices,
            normals,
            indices,
            skin: None,
            joint_count: 0,
            centroid,
            skin_centroid_weights: Vec::new(),
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
        let mut centers = vec![[0_f64; 4]; joint_count];
        for &index in &mesh.indices {
            let index = index as usize;
            let position = mesh.vertices[index].position;
            for (&joint, &weight) in skin[index].joints.iter().zip(&skin[index].weights) {
                for (sum, coordinate) in centers[usize::from(joint)].iter_mut().zip([
                    position[0],
                    position[1],
                    position[2],
                    1.,
                ]) {
                    *sum += f64::from(coordinate) * f64::from(weight);
                }
            }
        }
        mesh.skin_centroid_weights = centers
            .into_iter()
            .map(|c| c.map(|v| (v / mesh.indices.len() as f64) as f32))
            .collect();
        mesh.skin = Some(skin);
        mesh.joint_count = joint_count;
        Some(mesh)
    }
    /// Mean of indexed vertex positions; unused vertices do not affect sorting.
    #[must_use]
    pub fn centroid(&self) -> [f32; 3] {
        self.centroid
    }
    /// Per-joint homogeneous weighted centroids. Summing palette[j] * value[j]
    /// yields the mean of skinned indexed positions without visiting all vertices.
    /// Empty for static geometry. Values include their mean influence in W.
    #[must_use]
    pub fn skin_centroid_weights(&self) -> &[[f32; 4]] {
        &self.skin_centroid_weights
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
    /// Replaces generated normals with authored object-space normals. Values are
    /// normalized; zero, nonfinite, or mismatched attributes are rejected.
    pub fn with_normals(mut self, normals: Vec<[f32; 3]>) -> Option<Self> {
        if normals.len() != self.vertices.len() {
            return None;
        }
        self.normals = normals.into_iter().map(normalize).collect::<Option<_>>()?;
        Some(self)
    }
    /// Unit object-space normals, one per vertex. Without authored normals,
    /// triangles contribute area-weighted normals to their shared vertices.
    /// Split vertices at hard edges. Degenerate/unused vertices use +Y.
    #[must_use]
    pub fn normals(&self) -> &[[f32; 3]] {
        &self.normals
    }
    #[must_use]
    pub fn indices(&self) -> &[u32] {
        &self.indices
    }
}

fn normalize(value: [f32; 3]) -> Option<[f32; 3]> {
    let value = value.map(f64::from);
    let length = value.iter().map(|v| v * v).sum::<f64>().sqrt();
    (length.is_finite() && length > 0.).then(|| value.map(|v| (v / length) as f32))
}

fn generated_normals(vertices: &[MeshVertex], indices: &[u32]) -> Vec<[f32; 3]> {
    // Accumulate in f64 so finite f32 geometry cannot overflow cross products.
    let mut normals = vec![[0_f64; 3]; vertices.len()];
    for triangle in indices.as_chunks::<3>().0 {
        let [a, b, c] = [triangle[0], triangle[1], triangle[2]]
            .map(|i| vertices[i as usize].position.map(f64::from));
        let u = std::array::from_fn::<_, 3, _>(|i| b[i] - a[i]);
        let v = std::array::from_fn::<_, 3, _>(|i| c[i] - a[i]);
        let cross = [
            u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0],
        ];
        for &index in triangle {
            for (sum, contribution) in normals[index as usize].iter_mut().zip(cross) {
                *sum += contribution;
            }
        }
    }
    normals
        .into_iter()
        .map(|n| {
            let length = n.iter().map(|v| v * v).sum::<f64>().sqrt();
            if length > 0. {
                n.map(|v| (v / length) as f32)
            } else {
                [0., 1., 0.]
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    fn triangle() -> Mesh {
        Mesh::triangles(
            [[0., 0., 0.], [1., 0., 0.], [0., 1., 0.]]
                .map(|position| MeshVertex {
                    position,
                    uv: [0.; 2],
                })
                .to_vec(),
            vec![0, 1, 2],
        )
        .unwrap()
    }
    #[test]
    fn indexed_centroids_ignore_unused_vertices_and_track_skin_influences() {
        let vertices = [[0., 0., 0.], [2., 0., 0.], [0., 2., 0.], [100., 100., 100.]]
            .map(|position| MeshVertex {
                position,
                uv: [0.; 2],
            })
            .to_vec();
        let weights = [0.5, 0.25, 1., 0.]
            .map(|w| SkinWeights {
                joints: [0, 1, 0, 0],
                weights: [w, 1. - w, 0., 0.],
            })
            .to_vec();
        let mesh = Mesh::skinned_triangles(vertices, vec![0, 1, 2], weights, 2).unwrap();
        assert_eq!(mesh.centroid(), [2. / 3., 2. / 3., 0.]);
        let centers = mesh.skin_centroid_weights();
        let animated = [
            centers[0][0] + centers[1][0],
            centers[0][1] + centers[1][1],
            centers[0][2] + centers[1][2] + centers[1][3] * 4.,
        ];
        for (actual, expected) in animated.into_iter().zip([2. / 3., 2. / 3., 5. / 3.]) {
            assert!((actual - expected).abs() < 1e-6);
        }
        assert!((centers[0][3] + centers[1][3] - 1.).abs() < 1e-6);
    }
    #[test]
    fn generated_normals_follow_winding_and_remain_finite_for_large_geometry() {
        assert_eq!(triangle().normals(), &[[0., 0., 1.]; 3]);
        let vertices = triangle()
            .vertices()
            .iter()
            .map(|v| MeshVertex {
                position: v.position.map(|x| x * f32::MAX),
                ..*v
            })
            .collect();
        let reversed = Mesh::triangles(vertices, vec![2, 1, 0]).unwrap();
        assert_eq!(reversed.normals(), &[[0., 0., -1.]; 3]);
        let degenerate = Mesh::triangles(triangle().vertices().to_vec(), vec![0, 0, 0]).unwrap();
        assert_eq!(degenerate.normals(), &[[0., 1., 0.]; 3]);
    }
    #[test]
    fn authored_normals_are_normalized_and_invalid_attributes_rejected() {
        assert_eq!(
            triangle()
                .with_normals(vec![[2., 0., 0.]; 3])
                .unwrap()
                .normals(),
            &[[1., 0., 0.]; 3]
        );
        for normals in [
            vec![[0.; 3]; 3],
            vec![[f32::NAN; 3]; 3],
            vec![[f32::INFINITY; 3]; 3],
            vec![[1.; 3]; 2],
        ] {
            assert!(triangle().with_normals(normals).is_none());
        }
    }
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
