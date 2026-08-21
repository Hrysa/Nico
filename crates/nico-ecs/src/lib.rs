//! Entity/component storage and typed resources for Nico simulation worlds.
//!
//! Nico owns this world boundary while `hecs` supplies entity allocation,
//! component storage, queries, and deferred structural commands. Provider types
//! are deliberately re-exported rather than hidden behind a second query API.

use std::{
    any::{Any, TypeId, type_name},
    collections::HashMap,
    error::Error,
    fmt,
};

pub use hecs::{CommandBuffer, Component, DynamicBundle, Entity, NoSuchEntity, Query, QueryBorrow};

/// Complete access to the selected ECS provider for advanced queries and tools.
pub use hecs as provider;

/// Marker implemented by values that can be stored as world resources.
pub trait Resource: Any + Send + Sync {}

impl<T: Any + Send + Sync> Resource for T {}

/// Errors produced by Nico world operations outside the ECS provider API.
#[derive(Debug, Eq, PartialEq)]
pub enum WorldError {
    /// A requested typed resource was absent.
    MissingResource(&'static str),
}

impl fmt::Display for WorldError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingResource(resource) => {
                write!(formatter, "resource `{resource}` was not found")
            }
        }
    }
}

impl Error for WorldError {}

/// Result type for Nico-owned world operations.
pub type WorldResult<T> = Result<T, WorldError>;

/// Authoritative simulation world containing ECS entities and typed resources.
#[derive(Default)]
pub struct World {
    entities: hecs::World,
    resources: HashMap<TypeId, Box<dyn Any + Send + Sync>>,
}

impl World {
    /// Creates an empty simulation world.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Returns immutable access to the selected ECS provider world.
    #[must_use]
    pub fn entities(&self) -> &hecs::World {
        &self.entities
    }

    /// Returns mutable access to the selected ECS provider world.
    pub fn entities_mut(&mut self) -> &mut hecs::World {
        &mut self.entities
    }

    /// Spawns an entity immediately and returns its generational identity.
    pub fn spawn(&mut self, components: impl DynamicBundle) -> Entity {
        self.entities.spawn(components)
    }

    /// Reserves an entity identity for use with a deferred command buffer.
    pub fn reserve_entity(&self) -> Entity {
        self.entities.reserve_entity()
    }

    /// Despawns an entity immediately.
    pub fn despawn(&mut self, entity: Entity) -> Result<(), NoSuchEntity> {
        self.entities.despawn(entity)
    }

    /// Returns whether an entity identity is currently alive.
    #[must_use]
    pub fn contains_entity(&self, entity: Entity) -> bool {
        self.entities.contains(entity)
    }

    /// Creates a provider query using immutable world access.
    pub fn query<Q: Query>(&self) -> QueryBorrow<'_, Q> {
        self.entities.query::<Q>()
    }

    /// Inserts a resource, returning the previous value of the same type.
    pub fn insert_resource<R: Resource>(&mut self, resource: R) -> Option<R> {
        self.resources
            .insert(TypeId::of::<R>(), Box::new(resource))
            .and_then(|old| old.downcast::<R>().ok())
            .map(|old| *old)
    }

    /// Returns a shared resource reference.
    pub fn resource<R: Resource>(&self) -> WorldResult<&R> {
        self.resources
            .get(&TypeId::of::<R>())
            .and_then(|resource| resource.downcast_ref())
            .ok_or_else(|| WorldError::MissingResource(type_name::<R>()))
    }

    /// Returns a mutable resource reference.
    pub fn resource_mut<R: Resource>(&mut self) -> WorldResult<&mut R> {
        self.resources
            .get_mut(&TypeId::of::<R>())
            .and_then(|resource| resource.downcast_mut())
            .ok_or_else(|| WorldError::MissingResource(type_name::<R>()))
    }

    /// Removes and returns a resource when present.
    pub fn remove_resource<R: Resource>(&mut self) -> Option<R> {
        self.resources
            .remove(&TypeId::of::<R>())
            .and_then(|resource| resource.downcast::<R>().ok())
            .map(|resource| *resource)
    }

    /// Returns whether a resource of this type is present.
    #[must_use]
    pub fn contains_resource<R: Resource>(&self) -> bool {
        self.resources.contains_key(&TypeId::of::<R>())
    }
}

#[cfg(test)]
mod tests {
    use super::{World, WorldError};

    #[derive(Debug, Eq, PartialEq)]
    struct Position(i32);

    #[test]
    fn resources_can_be_replaced_and_removed() {
        let mut world = World::new();

        assert_eq!(world.insert_resource(3_u32), None);
        assert_eq!(world.insert_resource(7_u32), Some(3));
        assert_eq!(world.resource::<u32>().copied(), Ok(7));
        assert_eq!(world.remove_resource::<u32>(), Some(7));
        assert!(!world.contains_resource::<u32>());
        assert!(matches!(
            world.resource::<u32>(),
            Err(WorldError::MissingResource(_))
        ));
    }

    #[test]
    fn entities_are_queryable_and_generational() {
        let mut world = World::new();
        let first = world.spawn((Position(2),));

        for position in world.query::<&mut Position>().iter() {
            position.0 += 3;
        }
        assert_eq!(world.entities().get::<&Position>(first).unwrap().0, 5);

        world.despawn(first).unwrap();
        let second = world.spawn((Position(9),));
        assert_ne!(first, second);
        assert!(!world.contains_entity(first));
        assert!(world.contains_entity(second));
    }
}
