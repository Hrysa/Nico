//! Composed scene assets. Games register typed components before loading entities.
use nico_assets::definition::{DefinitionError, DefinitionValidation};
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use std::{
    any::TypeId,
    collections::{BTreeMap, BTreeSet},
    io,
    path::{Path, PathBuf},
};

/// Stable authored identity; runtime entity IDs are local to each loaded scene.
#[derive(Clone, Debug, PartialEq)]
pub struct SceneIdentity {
    pub id: String,
    pub name: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EntityDefinition {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub scope: ComponentScope,
    pub components: BTreeMap<String, toml::Value>,
}
impl EntityDefinition {
    pub fn component<T: DeserializeOwned>(&self, name: &str) -> io::Result<Option<T>> {
        self.components
            .get(name)
            .map(|v| v.clone().try_into().map_err(io::Error::other))
            .transpose()
    }
    pub fn set_component<T: Serialize>(&mut self, name: &str, value: &T) -> io::Result<()> {
        self.components.insert(
            name.into(),
            toml::Value::try_from(value).map_err(io::Error::other)?,
        );
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, nico_assets::definition::Definition)]
#[serde(deny_unknown_fields)]
pub struct SceneDefinition {
    pub schema_version: u32,
    pub entities: Vec<EntityDefinition>,
}
impl Default for SceneDefinition {
    fn default() -> Self {
        Self {
            schema_version: 1,
            entities: vec![],
        }
    }
}
impl SceneDefinition {
    /// Save a validated scene with an atomic file replacement.
    pub fn save_file(&self, path: &Path) -> io::Result<()> {
        use std::io::Write;
        self.validate()
            .map_err(|e| io::Error::other(e.to_string()))?;
        let temporary = path.with_extension(format!("scene-{}.tmp", std::process::id()));
        let mut file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        let result = (|| {
            file.write_all(
                toml::to_string_pretty(self)
                    .map_err(io::Error::other)?
                    .as_bytes(),
            )?;
            file.sync_all()?;
            drop(file);
            std::fs::rename(&temporary, path)
        })();
        if result.is_err() {
            let _ = std::fs::remove_file(temporary);
        }
        result
    }
}
impl DefinitionValidation for SceneDefinition {
    type Error = DefinitionError;
    fn validate(&self) -> Result<(), Self::Error> {
        if self.schema_version != 1 || self.entities.len() > 4096 {
            return Err("invalid scene version or entity count".into());
        }
        let mut ids = BTreeSet::new();
        for entity in &self.entities {
            if !identifier(&entity.id)
                || !ids.insert(&entity.id)
                || entity.name.len() > 256
                || entity.components.is_empty()
                || entity.components.len() > 64
            {
                return Err(format!("invalid scene entity: {}", entity.id).into());
            }
            if entity.components.keys().any(|key| !identifier(key)) {
                return Err("invalid component key".into());
            }
        }
        Ok(())
    }
}
fn identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HostRole {
    Client,
    Server,
}
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ComponentScope {
    #[default]
    Shared,
    Client,
    Server,
}
impl ComponentScope {
    fn includes(self, role: HostRole) -> bool {
        matches!(
            (self, role),
            (Self::Shared, _) | (Self::Client, HostRole::Client) | (Self::Server, HostRole::Server)
        )
    }
}

/// A decoded component validates before any destination world changes.
pub trait SceneComponent: DeserializeOwned + Send + Sync + 'static {
    fn validate(&self) -> io::Result<()>;
    /// Project-relative assets. Directories are allowed for authored catalogs.
    fn assets(&self) -> Vec<PathBuf> {
        vec![]
    }
    fn entity_references(&self) -> Vec<String> {
        vec![]
    }
}
type Decode = Box<
    dyn Fn(
            &toml::Value,
            &mut nico_ecs::provider::EntityBuilder,
        ) -> io::Result<(Vec<PathBuf>, Vec<String>)>
        + Send
        + Sync,
>;
struct Registration {
    scope: ComponentScope,
    type_id: TypeId,
    decode: Decode,
}
#[derive(Default)]
pub struct ComponentRegistry {
    entries: BTreeMap<String, Registration>,
}
impl ComponentRegistry {
    pub fn register<T: SceneComponent>(
        &mut self,
        name: &str,
        scope: ComponentScope,
    ) -> io::Result<()> {
        if !identifier(name)
            || self.entries.contains_key(name)
            || self
                .entries
                .values()
                .any(|r| r.type_id == TypeId::of::<T>())
        {
            return Err(io::Error::other("duplicate component name or Rust type"));
        }
        self.entries.insert(
            name.into(),
            Registration {
                scope,
                type_id: TypeId::of::<T>(),
                decode: Box::new(|value, builder| {
                    let component: T = value.clone().try_into().map_err(io::Error::other)?;
                    component.validate()?;
                    let assets = component.assets();
                    let references = component.entity_references();
                    builder.add(component);
                    Ok((assets, references))
                }),
            },
        );
        Ok(())
    }
    /// Validate every component, including components excluded from this host role.
    /// Resolve references before spawning anything. Unknown components are errors.
    pub fn prepare(
        &self,
        scene: &SceneDefinition,
        role: HostRole,
        resolve: impl Fn(&Path) -> io::Result<PathBuf>,
    ) -> io::Result<PreparedScene> {
        scene
            .validate()
            .map_err(|e| io::Error::other(e.to_string()))?;
        let ids: BTreeSet<_> = scene.entities.iter().map(|e| e.id.as_str()).collect();
        let mut entities = Vec::new();
        for entity in &scene.entities {
            let mut builder = nico_ecs::provider::EntityBuilder::new();
            let mut included = false;
            for (name, value) in &entity.components {
                let registration = self.entries.get(name).ok_or_else(|| {
                    io::Error::other(format!("{}: unknown component {name}", entity.id))
                })?;
                let mut excluded = nico_ecs::provider::EntityBuilder::new();
                let keep = entity.scope.includes(role) && registration.scope.includes(role);
                let (assets, references) =
                    (registration.decode)(value, if keep { &mut builder } else { &mut excluded })
                        .map_err(|e| io::Error::other(format!("{}.{name}: {e}", entity.id)))?;
                for asset in assets {
                    resolve(&asset)?;
                }
                for target in references {
                    if !ids.contains(target.as_str()) {
                        return Err(io::Error::other(format!(
                            "{}: missing target {target}",
                            entity.id
                        )));
                    }
                }
                included |= keep;
            }
            if included {
                builder.add(SceneIdentity {
                    id: entity.id.clone(),
                    name: entity.name.clone(),
                });
                entities.push(builder);
            }
        }
        Ok(PreparedScene { entities })
    }
}
pub struct PreparedScene {
    entities: Vec<nico_ecs::provider::EntityBuilder>,
}
impl PreparedScene {
    /// Call at the owning world's setup or update boundary. Returned IDs belong to this scene only.
    pub fn instantiate(mut self, world: &mut nico_ecs::World) -> Vec<nico_ecs::Entity> {
        self.entities
            .iter_mut()
            .map(|builder| world.spawn(builder.build()))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[derive(Deserialize)]
    struct Number {
        value: u32,
    }
    impl SceneComponent for Number {
        fn validate(&self) -> io::Result<()> {
            if self.value > 0 {
                Ok(())
            } else {
                Err(io::Error::other("zero"))
            }
        }
    }
    #[test]
    fn typed_components_validate_before_instantiation_and_respect_host_scope() {
        let mut registry = ComponentRegistry::default();
        registry
            .register::<Number>("number", ComponentScope::Client)
            .unwrap();
        let mut scene=SceneDefinition::parse("schema_version=1\n[[entities]]\nid='one'\nname='One'\n[entities.components.number]\nvalue=3\n").unwrap();
        let mut world = nico_ecs::World::new();
        let ids = registry
            .prepare(&scene, HostRole::Client, |p| Ok(p.into()))
            .unwrap()
            .instantiate(&mut world);
        assert_eq!(world.entities().get::<&Number>(ids[0]).unwrap().value, 3);
        assert!(
            registry
                .prepare(&scene, HostRole::Server, |p| Ok(p.into()))
                .unwrap()
                .instantiate(&mut world)
                .is_empty()
        );
        scene.entities[0].components.get_mut("number").unwrap()["value"] = toml::Value::Integer(0);
        assert!(
            registry
                .prepare(&scene, HostRole::Server, |p| Ok(p.into()))
                .is_err()
        );
        assert_eq!(world.entities().len(), 1);
        scene.entities[0]
            .components
            .insert("unknown".into(), toml::Value::Table(Default::default()));
        assert!(
            registry
                .prepare(&scene, HostRole::Client, |p| Ok(p.into()))
                .is_err()
        );
    }
}
