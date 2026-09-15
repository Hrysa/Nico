//! Headless geometric queries and bounded kinematic movement. No runtime or
//! presentation dependencies. Callers supply geometry, radii, and iteration policy.
mod slide;
pub use slide::{Circle, SlideSettings, slide_circle};

/// A finite boom query. `direction` is unit length; a query returns the nearest
/// contact distance in world units, or None. Geometry and filtering are caller-owned.
#[derive(Clone, Copy, Debug)]
pub struct SphereSweep {
    pub origin: [f32; 3],
    pub direction: [f32; 3],
    pub distance: f32,
    pub radius: f32,
}
impl SphereSweep {
    /// Conservative sphere sweep against an axis-aligned box, using slab tests
    /// against radius-expanded bounds. Corners may shorten the boom early.
    /// Starting inside the expanded box returns zero; invalid bounds return None.
    pub fn cast_aabb(&self, min: [f32; 3], max: [f32; 3]) -> Option<f32> {
        let direction_length: f32 = self.direction.iter().map(|v| v * v).sum();
        if !self.distance.is_finite()
            || self.distance < 0.0
            || !self.radius.is_finite()
            || self.radius < 0.0
            || self.origin.iter().any(|v| !v.is_finite())
            || !direction_length.is_finite()
            || (direction_length - 1.0).abs() > 1e-4
        {
            return None;
        }
        let mut enter: f32 = 0.0;
        let mut exit = self.distance;
        for i in 0..3 {
            if !min[i].is_finite() || !max[i].is_finite() || min[i] > max[i] {
                return None;
            }
            let low = min[i] - self.radius;
            let high = max[i] + self.radius;
            if self.direction[i].abs() < 1e-6 {
                if self.origin[i] < low || self.origin[i] > high {
                    return None;
                }
            } else {
                let a = (low - self.origin[i]) / self.direction[i];
                let b = (high - self.origin[i]) / self.direction[i];
                enter = enter.max(a.min(b));
                exit = exit.min(a.max(b));
            }
        }
        (exit >= enter).then_some(enter)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn sweep_handles_parallel_axes_inside_origins_and_finite_reach() {
        let sweep = SphereSweep {
            origin: [0.0; 3],
            direction: [1.0, 0.0, 0.0],
            distance: 4.0,
            radius: 0.2,
        };
        assert_eq!(
            sweep.cast_aabb([2.0, -1.0, -1.0], [2.1, 1.0, 1.0]),
            Some(1.8)
        );
        assert_eq!(sweep.cast_aabb([2.0, 1.0, -1.0], [2.1, 2.0, 1.0]), None);
        assert_eq!(sweep.cast_aabb([5.0, -1.0, -1.0], [6.0, 1.0, 1.0]), None);
        assert_eq!(sweep.cast_aabb([-1.0; 3], [1.0; 3]), Some(0.0));
        assert_eq!(sweep.cast_aabb([1.0; 3], [-1.0; 3]), None);
    }
}
