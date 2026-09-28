//! Debug outlines use the same dimensions as combat and physics.
use glam::Vec3;
use nico_assets::{Mesh, MeshVertex};
use std::sync::Arc;

fn lines(segments: impl IntoIterator<Item = (Vec3, Vec3)>) -> Arc<Mesh> {
    let mut vertices = Vec::new();
    let mut indices = Vec::new();
    for (a, b) in segments {
        if a.distance_squared(b) < 1e-12 {
            continue;
        }
        let forward = (b - a).normalize();
        let up = if forward.y.abs() < 0.9 {
            Vec3::Y
        } else {
            Vec3::X
        };
        let side = forward.cross(up).normalize() * 0.012;
        let up = forward.cross(side).normalize() * 0.012;
        let base = vertices.len() as u32;
        for p in [a, b] {
            for offset in [-side - up, side - up, side + up, -side + up] {
                vertices.push(MeshVertex {
                    position: (p + offset).to_array(),
                    uv: [0.5; 2],
                });
            }
        }
        for face in [
            [0, 1, 2, 3],
            [4, 7, 6, 5],
            [0, 4, 5, 1],
            [1, 5, 6, 2],
            [2, 6, 7, 3],
            [3, 7, 4, 0],
        ] {
            indices
                .extend([face[0], face[1], face[2], face[0], face[2], face[3]].map(|i| base + i));
        }
    }
    Arc::new(Mesh::triangles(vertices, indices).expect("valid debug outline"))
}
pub fn capsule(radius: f32, half_height: f32) -> Arc<Mesh> {
    let mut segments = Vec::new();
    // Two vertical silhouettes include the straight sides between rounded ends.
    for axis in 0..2 {
        let point = |angle: f32, center: f32| {
            let horizontal = radius * angle.cos();
            let y = center + radius * angle.sin();
            if axis == 0 {
                Vec3::new(horizontal, y, 0.)
            } else {
                Vec3::new(0., y, horizontal)
            }
        };
        for (start, center) in [(0., half_height), (std::f32::consts::PI, -half_height)] {
            for i in 0..24 {
                let angle = start + i as f32 * std::f32::consts::PI / 24.;
                segments.push((
                    point(angle, center),
                    point(angle + std::f32::consts::PI / 24., center),
                ));
            }
        }
        for angle in [0., std::f32::consts::PI] {
            segments.push((point(angle, -half_height), point(angle, half_height)));
        }
    }
    for y in [-half_height, half_height] {
        let point = |i: u32| {
            let t = i as f32 * std::f32::consts::TAU / 48.;
            Vec3::new(radius * t.cos(), y, radius * t.sin())
        };
        for i in 0..48 {
            segments.push((point(i), point(i + 1)));
        }
    }
    lines(segments)
}
pub fn sector(range: f32, half_angle: f32) -> Arc<Mesh> {
    let point = |i: u32| {
        let angle = -half_angle + 2. * half_angle * i as f32 / 32.;
        Vec3::new(angle.sin() * range, 0., angle.cos() * range)
    };
    lines(
        (0..32)
            .map(|i| (point(i), point(i + 1)))
            .chain([(Vec3::ZERO, point(0)), (Vec3::ZERO, point(32))]),
    )
}
pub fn bounds(size: [f64; 3]) -> Arc<Mesh> {
    let half = Vec3::from_array(size.map(|v| v as f32 * 0.5));
    let point = |bits: u32| {
        Vec3::new(
            if bits & 1 == 0 { -half.x } else { half.x },
            if bits & 2 == 0 { -half.y } else { half.y },
            if bits & 4 == 0 { -half.z } else { half.z },
        )
    };
    lines((0..8).flat_map(|a| {
        (0..3).filter_map(move |axis| {
            let b = a ^ (1 << axis);
            (a < b).then(|| (point(a), point(b)))
        })
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn outlines_follow_authoritative_dimensions() {
        let body = capsule(0.4, 0.5);
        for (axis, expected) in [(0, 0.4), (1, 0.9), (2, 0.4)] {
            let bound = body
                .vertices()
                .iter()
                .map(|v| v.position[axis].abs())
                .fold(0., f32::max);
            assert!((bound - expected).abs() < 0.03);
        }
        let mesh = sector(2., std::f32::consts::FRAC_PI_4);
        assert!(
            mesh.vertices()
                .iter()
                .all(|v| v.position[2] >= -0.02 && Vec3::from_array(v.position).length() <= 2.03)
        );
        let mesh = bounds([2., 4., 6.]);
        for axis in 0..3 {
            let max = mesh
                .vertices()
                .iter()
                .map(|v| v.position[axis].abs())
                .fold(0., f32::max);
            assert!((max - (axis + 1) as f32).abs() < 0.03);
        }
        assert!(
            capsule(1., 0.)
                .vertices()
                .iter()
                .all(|v| (Vec3::from_array(v.position).length() - 1.).abs() < 0.03)
        );
    }
}
