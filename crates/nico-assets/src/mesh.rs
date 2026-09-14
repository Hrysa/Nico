/// Static mesh vertex. Coordinates are right-handed, Y up; UV origin is top-left.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MeshVertex {
    pub position: [f32; 3],
    pub uv: [f32; 2],
}

/// Immutable indexed triangle geometry, independent of textures and GPU resources.
#[derive(Debug)]
pub struct Mesh {
    vertices: Vec<MeshVertex>,
    indices: Vec<u32>,
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
        Some(Self { vertices, indices })
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
