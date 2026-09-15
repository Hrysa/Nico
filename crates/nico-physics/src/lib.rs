//! Headless 3D physics integration. Rapier types and handles stay private.
//! Units are meters, seconds and kilograms; rotations are unit XYZW quaternions.
//! A world is owned and mutated by one simulation boundary, never a tooling thread.
mod world;
pub use world::PhysicsWorld;
#[cfg(feature = "runtime")]
mod runtime;
#[cfg(feature = "runtime")]
pub use runtime::{ContactFrame, EntityContact, PhysicsBody, PhysicsEntities, PhysicsPlugin};

/// Process-local identity, scoped to its creating PhysicsWorld. IDs are not reused.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct BodyId(u64);

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Pose {
    pub position: [f64; 3],
    pub orientation: [f64; 4],
}
impl Default for Pose {
    fn default() -> Self {
        Self {
            position: [0.0; 3],
            orientation: [0.0, 0.0, 0.0, 1.0],
        }
    }
}
impl Pose {
    pub fn at(position: [f64; 3]) -> Self {
        Self {
            position,
            ..Self::default()
        }
    }
    pub(crate) fn valid(self) -> bool {
        self.position.iter().all(|x| x.is_finite())
            && self.orientation.iter().all(|x| x.is_finite())
            && (self.orientation.iter().map(|x| x * x).sum::<f64>() - 1.0).abs() < 1e-6
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Shape {
    Ball {
        radius: f64,
    },
    Cuboid {
        half_extents: [f64; 3],
    },
    /// Capsule along local Y; half_height measures the straight segment only.
    Capsule {
        half_height: f64,
        radius: f64,
    },
}
impl Shape {
    pub(crate) fn valid(self) -> bool {
        let positive = |x: f64| x.is_finite() && x > 0.0;
        match self {
            Self::Ball { radius } => positive(radius),
            Self::Cuboid { half_extents } => half_extents.into_iter().all(positive),
            Self::Capsule {
                half_height,
                radius,
            } => half_height.is_finite() && half_height >= 0.0 && positive(radius),
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BodyKind {
    Fixed,
    Dynamic,
    Kinematic,
}

/// Two bodies interact when each membership intersects the other's filter.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CollisionGroups {
    pub membership: u32,
    pub filter: u32,
}
impl Default for CollisionGroups {
    fn default() -> Self {
        Self {
            membership: u32::MAX,
            filter: u32::MAX,
        }
    }
}

/// One collider per body. Remove and reinsert to change shape or material settings;
/// the runtime adapter performs that replacement for edited components.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BodyDesc {
    pub kind: BodyKind,
    pub shape: Shape,
    pub pose: Pose,
    pub velocity: [f64; 3],
    pub friction: f64,
    pub restitution: f64,
    pub density: f64,
    pub sensor: bool,
    pub groups: CollisionGroups,
}
impl BodyDesc {
    pub fn new(kind: BodyKind, shape: Shape, pose: Pose) -> Self {
        Self {
            kind,
            shape,
            pose,
            velocity: [0.0; 3],
            friction: 0.5,
            restitution: 0.0,
            density: 1.0,
            sensor: false,
            groups: CollisionGroups::default(),
        }
    }
    pub(crate) fn valid(self) -> bool {
        self.shape.valid()
            && self.pose.valid()
            && self.velocity.iter().all(|x| x.is_finite())
            && self.friction.is_finite()
            && self.friction >= 0.0
            && self.restitution.is_finite()
            && (0.0..=1.0).contains(&self.restitution)
            && self.density.is_finite()
            && self.density > 0.0
    }
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BodyState {
    pub pose: Pose,
    pub velocity: [f64; 3],
    pub sleeping: bool,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct QueryFilter {
    pub exclude: Option<BodyId>,
    pub groups: CollisionGroups,
    pub include_sensors: bool,
}
#[derive(Clone, Copy, Debug)]
pub struct CastHit {
    pub body: BodyId,
    pub fraction: f64,
    pub normal: [f64; 3],
}

#[derive(Clone, Copy, Debug)]
pub struct CharacterSettings {
    pub offset: f64,
    pub max_slope_angle: f64,
    pub snap_distance: Option<f64>,
}
impl Default for CharacterSettings {
    fn default() -> Self {
        Self {
            offset: 0.001,
            max_slope_angle: std::f64::consts::FRAC_PI_4,
            snap_distance: None,
        }
    }
}
#[derive(Clone, Copy, Debug)]
pub struct CharacterMovement {
    pub translation: [f64; 3],
    pub grounded: bool,
}

/// Touching pair at the most recent simulation boundary, including sensors.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Contact {
    pub bodies: [BodyId; 2],
    pub sensor: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PhysicsError {
    InvalidInput,
    UnknownBody,
    Capacity,
    Closed,
    /// The provider disabled invalid simulation state. Close/recreate the world.
    SimulationFailed,
}
impl std::fmt::Display for PhysicsError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "physics: {self:?}")
    }
}
impl std::error::Error for PhysicsError {}
pub type PhysicsResult<T> = Result<T, PhysicsError>;

#[cfg(test)]
mod tests;
