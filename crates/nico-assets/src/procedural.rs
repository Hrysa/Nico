//! Validated, provider-independent procedural mesh construction.
use crate::{Mesh, MeshVertex};
fn valid_arc(angle: f32, segments: u32) -> bool {
    angle.is_finite()
        && angle > 0.0
        && angle <= std::f32::consts::TAU
        && (1..=4096).contains(&segments)
}
/// Centered box with per-face constant UVs, ordered +Z, -Z, +Y, -Y, +X, -X.
/// Returns None for nonpositive/nonfinite dimensions or invalid UVs.
pub fn cuboid(size: [f32; 3], face_uvs: [[f32; 2]; 6]) -> Option<Mesh> {
    if size.iter().any(|v| !v.is_finite() || *v <= 0.0) {
        return None;
    }
    let mut vertices = Vec::new();
    let mut indices = Vec::new();
    for (face, corners) in [
        [[-1., -1., 1.], [1., -1., 1.], [1., 1., 1.], [-1., 1., 1.]],
        [
            [1., -1., -1.],
            [-1., -1., -1.],
            [-1., 1., -1.],
            [1., 1., -1.],
        ],
        [[-1., 1., 1.], [1., 1., 1.], [1., 1., -1.], [-1., 1., -1.]],
        [
            [-1., -1., -1.],
            [1., -1., -1.],
            [1., -1., 1.],
            [-1., -1., 1.],
        ],
        [[1., -1., 1.], [1., -1., -1.], [1., 1., -1.], [1., 1., 1.]],
        [
            [-1., -1., -1.],
            [-1., -1., 1.],
            [-1., 1., 1.],
            [-1., 1., -1.],
        ],
    ]
    .iter()
    .enumerate()
    {
        let base = vertices.len() as u32;
        for corner in corners {
            vertices.push(MeshVertex {
                position: std::array::from_fn(|i| corner[i] * size[i] * 0.5),
                uv: face_uvs[face],
            });
        }
        indices.extend([base, base + 1, base + 2, base, base + 2, base + 3]);
    }
    Mesh::triangles(vertices, indices)
}
/// Filled XZ sector centered on +Z, with unit outer radius.
pub fn sector(angle: f32, segments: u32, uv: [f32; 2]) -> Option<Mesh> {
    if !valid_arc(angle, segments) {
        return None;
    }
    let mut vertices = vec![MeshVertex {
        position: [0.0; 3],
        uv,
    }];
    let mut indices = Vec::new();
    for i in 0..=segments {
        let a = -angle / 2.0 + angle * i as f32 / segments as f32;
        vertices.push(MeshVertex {
            position: [a.sin(), 0.0, a.cos()],
            uv,
        });
        if i > 0 {
            indices.extend([0, i, i + 1]);
        }
    }
    Mesh::triangles(vertices, indices)
}
/// XZ ring sector centered on +Z, with unit outer radius.
pub fn arc(inner: f32, angle: f32, segments: u32, uv: [f32; 2]) -> Option<Mesh> {
    if !inner.is_finite() || !(0.0..1.0).contains(&inner) || !valid_arc(angle, segments) {
        return None;
    }
    let mut vertices = Vec::new();
    let mut indices = Vec::new();
    for i in 0..=segments {
        let a = -angle / 2.0 + angle * i as f32 / segments as f32;
        for r in [inner, 1.0] {
            vertices.push(MeshVertex {
                position: [a.sin() * r, 0.0, a.cos() * r],
                uv,
            });
        }
    }
    for i in 0..segments {
        let b = i * 2;
        indices.extend([b, b + 1, b + 3, b, b + 3, b + 2]);
    }
    Mesh::triangles(vertices, indices)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn builders_preserve_dimensions_uvs_and_bound_allocations() {
        let mesh = cuboid([2.0, 4.0, 6.0], [[0.2, 0.7]; 6]).unwrap();
        assert_eq!(mesh.indices().len(), 36);
        assert!(mesh.vertices().iter().all(|v| v.position[0].abs() == 1.0
            && v.position[1].abs() == 2.0
            && v.position[2].abs() == 3.0
            && v.uv == [0.2, 0.7]));
        assert!(cuboid([0.0, 1.0, 1.0], [[0.0; 2]; 6]).is_none());
        assert!(sector(1.0, 0, [0.0; 2]).is_none());
        assert!(sector(1.0, 4097, [0.0; 2]).is_none());
        assert!(arc(1.0, 1.0, 32, [0.0; 2]).is_none());
        let mesh = sector(std::f32::consts::PI, 16, [0.0; 2]).unwrap();
        assert_eq!(mesh.indices().len(), 48);
        assert!((mesh.vertices()[1].position[0] + 1.0).abs() < 1e-6);
        let mesh = arc(0.5, std::f32::consts::TAU, 16, [0.0; 2]).unwrap();
        assert!(
            mesh.vertices()
                .iter()
                .all(|v| (0.49999..=1.00001).contains(&v.position[0].hypot(v.position[2])))
        );
    }
}
