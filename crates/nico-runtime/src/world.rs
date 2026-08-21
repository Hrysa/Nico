use std::{
    any::{Any, TypeId},
    collections::HashMap,
};

use crate::{RuntimeError, RuntimeResult};

/// Marker implemented by values that can be stored as world resources.
pub trait Resource: Any + Send + Sync {}

impl<T: Any + Send + Sync> Resource for T {}

/// Authoritative simulation state owned by the runtime.
///
/// The first implementation provides typed resources. Entity/component storage
/// will be selected after the ECS design and library discussion.
#[derive(Default)]
pub struct World {
    resources: HashMap<TypeId, Box<dyn Any + Send + Sync>>,
}

impl World {
    /// Creates an empty world.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Inserts a resource, returning the previous value of the same type.
    pub fn insert<R: Resource>(&mut self, resource: R) -> Option<R> {
        self.resources
            .insert(TypeId::of::<R>(), Box::new(resource))
            .and_then(|old| old.downcast::<R>().ok())
            .map(|old| *old)
    }

    /// Returns a shared resource reference.
    pub fn resource<R: Resource>(&self) -> RuntimeResult<&R> {
        self.resources
            .get(&TypeId::of::<R>())
            .and_then(|resource| resource.downcast_ref())
            .ok_or_else(RuntimeError::missing_resource::<R>)
    }

    /// Returns a mutable resource reference.
    pub fn resource_mut<R: Resource>(&mut self) -> RuntimeResult<&mut R> {
        self.resources
            .get_mut(&TypeId::of::<R>())
            .and_then(|resource| resource.downcast_mut())
            .ok_or_else(RuntimeError::missing_resource::<R>)
    }

    /// Removes and returns a resource when present.
    pub fn remove<R: Resource>(&mut self) -> Option<R> {
        self.resources
            .remove(&TypeId::of::<R>())
            .and_then(|resource| resource.downcast::<R>().ok())
            .map(|resource| *resource)
    }

    /// Returns whether a resource of this type is present.
    #[must_use]
    pub fn contains<R: Resource>(&self) -> bool {
        self.resources.contains_key(&TypeId::of::<R>())
    }
}

#[cfg(test)]
mod tests {
    use super::World;

    #[test]
    fn resources_can_be_replaced_and_removed() {
        let mut world = World::new();

        assert_eq!(world.insert(3_u32), None);
        assert_eq!(world.insert(7_u32), Some(3));
        assert_eq!(world.resource::<u32>().copied(), Ok(7));
        assert_eq!(world.remove::<u32>(), Some(7));
        assert!(!world.contains::<u32>());
    }
}
