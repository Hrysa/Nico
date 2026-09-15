//! Shared arena authoring, used by collision and client presentation.
pub const HALF_EXTENT: f64 = 12.0;
pub const WALL_THICKNESS: f64 = 0.4;
pub const WALL_HEIGHT: f64 = 0.9;
pub const ACTOR_RADIUS: f64 = 0.4;
pub const INNER_FACE: f64 = HALF_EXTENT - WALL_THICKNESS / 2.0;
pub const CENTER_LIMIT: f64 = INNER_FACE - ACTOR_RADIUS;
#[derive(Clone, Copy, Debug)]
pub struct Wall {
    pub center: [f64; 3],
    pub size: [f64; 3],
}
impl Wall {
    pub fn bounds(self) -> [[f32; 3]; 2] {
        [
            std::array::from_fn(|i| (self.center[i] - self.size[i] / 2.0) as f32),
            std::array::from_fn(|i| (self.center[i] + self.size[i] / 2.0) as f32),
        ]
    }
}
pub const WALLS: [Wall; 4] = [
    Wall {
        center: [0.0, WALL_HEIGHT / 2.0, -HALF_EXTENT],
        size: [
            HALF_EXTENT * 2.0 + WALL_THICKNESS,
            WALL_HEIGHT,
            WALL_THICKNESS,
        ],
    },
    Wall {
        center: [0.0, WALL_HEIGHT / 2.0, HALF_EXTENT],
        size: [
            HALF_EXTENT * 2.0 + WALL_THICKNESS,
            WALL_HEIGHT,
            WALL_THICKNESS,
        ],
    },
    Wall {
        center: [-HALF_EXTENT, WALL_HEIGHT / 2.0, 0.0],
        size: [
            WALL_THICKNESS,
            WALL_HEIGHT,
            HALF_EXTENT * 2.0 + WALL_THICKNESS,
        ],
    },
    Wall {
        center: [HALF_EXTENT, WALL_HEIGHT / 2.0, 0.0],
        size: [
            WALL_THICKNESS,
            WALL_HEIGHT,
            HALF_EXTENT * 2.0 + WALL_THICKNESS,
        ],
    },
];

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actor_contact_matches_authored_wall_inner_face() {
        let wall = WALLS[3];
        assert_eq!(wall.center[0] - wall.size[0] / 2.0, INNER_FACE);
        assert!((CENTER_LIMIT + ACTOR_RADIUS - INNER_FACE).abs() < 1e-12);
        let p = crate::collision::slide(
            crate::Vec2::new(0.0, 0.0),
            crate::Vec2::new(100.0, 0.0),
            &[],
        );
        assert!(p.x + ACTOR_RADIUS <= INNER_FACE);
        assert!(INNER_FACE - p.x - ACTOR_RADIUS < 0.001);
    }
}
