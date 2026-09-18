use crate::{
    document::{Document, History, Object, relative},
    operations::{Action, Published, SharedQueue},
};
use glam::Vec3;
use nico_assets::watch::{ImportedAsset, WatchedProject};
use nico_presentation::{Camera3d, MeshInstance, Scene3d};
use nico_presentation_control::scene::{place_meshes, rest_meshes};
use serde_json::json;
use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
};

pub struct Core {
    pub project: WatchedProject,
    pub definition: nico_scene::Project,
    pub adapter: Option<Box<dyn nico_authoring::Session>>,
    pub document: Document,
    saved: Document,
    history: History,
    pub selected: Option<u64>,
    pub inspected_asset: Option<PathBuf>,
    last_object_id: u64,
    pub camera: [f32; 3],
    pub camera_pan: [f32; 3],
    pub error: Option<String>,
    pub queue: SharedQueue,
    pub publication: Published,
    draws: BTreeMap<PathBuf, (u64, Vec<MeshInstance>)>,
}

impl Core {
    #[cfg(test)]
    pub fn new(root: &Path, queue: SharedQueue, publication: Published) -> std::io::Result<Self> {
        Self::with_adapters(
            root,
            queue,
            publication,
            &nico_authoring::Registry::default(),
        )
    }
    #[cfg(test)]
    pub fn with_adapters(
        root: &Path,
        queue: SharedQueue,
        publication: Published,
        registry: &nico_authoring::Registry,
    ) -> std::io::Result<Self> {
        Self::load(root, queue, publication, registry, |_| {})
    }
    pub fn load(
        root: &Path,
        queue: SharedQueue,
        publication: Published,
        registry: &nico_authoring::Registry,
        discovered: impl FnOnce(nico_assets::watch::CatalogReader),
    ) -> std::io::Result<Self> {
        let definition = nico_scene::Project::open(root)?;
        let project =
            WatchedProject::open_roots(definition.root(), &definition.manifest.asset_roots)?;
        discovered(project.reader());
        let adapter = registry.open(&definition)?;
        let camera = adapter
            .as_ref()
            .map_or([0.5, 0.35, 8.], |a| a.initial_camera());
        let document = if let Some(adapter) = &adapter {
            adapter.document()
        } else {
            definition.load_scene()?
        };
        Ok(Self {
            project,
            definition,
            adapter,
            last_object_id: document.objects.iter().map(|o| o.id).max().unwrap_or(0),
            saved: document.clone(),
            document,
            history: History::default(),
            selected: None,
            inspected_asset: None,
            camera,
            camera_pan: [0.; 3],
            error: None,
            queue,
            publication,
            draws: BTreeMap::new(),
        })
    }
    pub fn dirty(&self) -> bool {
        self.document != self.saved
    }
    fn apply(&mut self, action: Action) -> Result<(), String> {
        let previous = self.document.clone();
        let history = self.history.clone();
        let selected = self.selected;
        self.apply_inner(action)?;
        if self.document != previous
            && let Some(adapter) = &mut self.adapter
            && let Err(e) = adapter.replace(&self.document)
        {
            self.document = previous;
            self.history = history;
            self.selected = selected;
            return Err(e.to_string());
        }
        Ok(())
    }
    fn apply_inner(&mut self, action: Action) -> Result<(), String> {
        match action {
            Action::Inspect { asset } => {
                if !relative(&asset) || !self.project.snapshot().assets.contains_key(&asset) {
                    return Err("unknown project asset".into());
                }
                self.inspected_asset = Some(asset);
            }
            Action::Save => {
                if let Some(adapter) = &mut self.adapter {
                    adapter.save().map_err(|e| e.to_string())?;
                } else {
                    self.definition
                        .save_scene(&self.document)
                        .map_err(|e| e.to_string())?;
                }
                self.saved = self.document.clone();
            }
            Action::Reload => {
                if self.dirty() {
                    return Err("unsaved edits; save or undo them before reload".into());
                }
                let next = if let Some(adapter) = &mut self.adapter {
                    adapter.reload().map_err(|e| e.to_string())?;
                    self.history = History::default();
                    adapter.document()
                } else {
                    self.definition.load_scene().map_err(|e| e.to_string())?
                };
                if self.adapter.is_none() {
                    self.history.record(self.document.clone());
                }
                self.document = next;
                self.saved = self.document.clone();
            }
            Action::Refresh => {
                if let Some(adapter) = &mut self.adapter {
                    adapter.refresh_assets().map_err(|e| e.to_string())?;
                }
                self.project.refresh();
            }
            Action::Undo => self.history.undo(&mut self.document),
            Action::Redo => self.history.redo(&mut self.document),
            Action::Select { id } => {
                if !self.document.objects.iter().any(|o| o.id == id) {
                    return Err("unknown object".into());
                }
                self.selected = Some(id);
                self.camera_pan = [0.; 3];
            }
            Action::Pan { offset } => {
                if offset.iter().any(|v| !v.is_finite() || v.abs() > 10000.) {
                    return Err("invalid camera pan".into());
                }
                self.camera_pan = offset;
            }
            Action::Camera {
                yaw,
                pitch,
                distance,
            } => {
                if !yaw.is_finite()
                    || yaw.abs() > 100.
                    || !pitch.is_finite()
                    || pitch.abs() > 1.4
                    || !(0.1..=1000.).contains(&distance)
                {
                    return Err("invalid camera".into());
                }
                self.camera = [yaw, pitch, distance];
            }
            action => {
                let mut next = self.document.clone();
                let mut selected = self.selected;
                match action {
                    Action::Add { asset } => {
                        if !relative(&asset) {
                            return Err("asset must be project relative".into());
                        }
                        let snapshot = self.project.snapshot();
                        if !snapshot
                            .assets
                            .get(&asset)
                            .is_some_and(|e| matches!(e.value, Some(ImportedAsset::Model(_))))
                        {
                            return Err("model is not ready".into());
                        }
                        let id = self
                            .last_object_id
                            .checked_add(1)
                            .ok_or("object IDs exhausted")?;
                        let name = asset.file_stem().unwrap().to_string_lossy().into_owned();
                        next.objects.push(Object {
                            id,
                            asset,
                            name,
                            position: [0.; 3],
                            rotation: [0.; 3],
                            scale: 1.,
                        });
                        selected = Some(id);
                    }
                    Action::Transform {
                        id,
                        position,
                        rotation,
                        scale,
                    } => {
                        let object = next
                            .objects
                            .iter_mut()
                            .find(|o| o.id == id)
                            .ok_or("unknown object")?;
                        object.position = position;
                        object.rotation = rotation;
                        object.scale = scale;
                    }
                    Action::Remove { id } => {
                        let index = next
                            .objects
                            .iter()
                            .position(|o| o.id == id)
                            .ok_or("unknown object")?;
                        next.objects.remove(index);
                    }
                    _ => unreachable!(),
                }
                next.validate().map_err(|e| e.to_string())?;
                self.selected = selected;
                if next != self.document {
                    self.history.record(self.document.clone());
                    self.document = next;
                }
            }
        }
        self.last_object_id = self.last_object_id.max(
            self.document
                .objects
                .iter()
                .map(|o| o.id)
                .max()
                .unwrap_or(0),
        );
        if !self
            .document
            .objects
            .iter()
            .any(|o| Some(o.id) == self.selected)
        {
            self.selected = None;
        }
        Ok(())
    }
    pub fn update(&mut self) -> Scene3d {
        loop {
            let command = self.queue.lock().unwrap().pop();
            let Some((id, action)) = command else {
                break;
            };
            let error = self.apply(action).err();
            self.error = error.clone();
            self.queue
                .lock()
                .unwrap()
                .record(json!({"command_id":id,"error":error}));
        }
        let catalog = self.project.snapshot();
        for object in self
            .document
            .objects
            .iter()
            .filter(|_| self.adapter.is_none())
        {
            let Some(entry) = catalog.assets.get(&object.asset) else {
                continue;
            };
            if self
                .draws
                .get(&object.asset)
                .is_some_and(|(revision, _)| *revision == entry.revision)
            {
                continue;
            }
            if let Some(ImportedAsset::Model(bundle)) = &entry.value {
                let result = rest_meshes(bundle);
                match result {
                    Ok(draws) => {
                        self.draws
                            .insert(object.asset.clone(), (entry.revision, draws));
                    }
                    Err(error) => self.error = Some(error.to_string()),
                }
            }
        }
        self.draws
            .retain(|path, _| self.document.objects.iter().any(|o| &o.asset == path));
        let [yaw, pitch, distance] = self.camera;
        let target = self
            .document
            .objects
            .iter()
            .find(|o| Some(o.id) == self.selected)
            .map_or(Vec3::ZERO, |o| Vec3::from(o.position))
            + Vec3::from(self.camera_pan);
        let offset = Vec3::new(
            yaw.sin() * pitch.cos(),
            pitch.sin(),
            yaw.cos() * pitch.cos(),
        ) * distance;
        let mut scene = Scene3d {
            camera: Camera3d::looking_at(
                (target + offset).to_array(),
                target.to_array(),
                [0., 1., 0.],
            )
            .unwrap(),
            ..Default::default()
        };
        scene.camera.far = 5000.;
        for object in self
            .document
            .objects
            .iter()
            .filter(|_| self.adapter.is_none())
        {
            if let Some((_, meshes)) = self.draws.get(&object.asset) {
                if scene.meshes.len() + meshes.len() > 256 {
                    self.error = Some("scene exceeds 256 rendered primitives".into());
                    break;
                }
                scene.meshes.extend(place_meshes(meshes, object));
            }
        }
        if let Some(adapter) = &mut self.adapter {
            scene = adapter.render(scene.camera);
        }
        self.publication.lock().unwrap().publish(json!({
            "loading":false,
            "authoring":self.adapter.as_ref().map(|a| a.inspect()),
            "project":self.project.root(), "manifest":self.definition.manifest, "dirty":self.dirty(), "objects":self.document.objects,
            "selected":self.selected,"inspected_asset":self.inspected_asset,"camera":self.camera,"camera_pan":self.camera_pan,"draws":scene.meshes.len(),"error":self.error,
            "imports":{"current":catalog.importing,"pending":catalog.assets.values().filter(|a| a.value.is_none() && a.error.is_none() && !a.missing).count(),"scans":catalog.scans,"attempts":catalog.imports,"notifications":catalog.notifications,"error":catalog.error},
            "assets":catalog.assets.values().map(|e| json!({"path":e.path,"revision":e.revision,"ready":e.value.is_some(),"missing":e.missing,"error":e.error})).collect::<Vec<_>>(),
            "command_results":self.queue.lock().unwrap().history()
        }));
        scene
    }
    pub fn close(&mut self) {
        self.queue
            .lock()
            .unwrap()
            .close_with(|(id, _)| json!({"command_id":id,"error":"host_closed"}));
        self.update();
        self.publication.lock().unwrap().close();
    }
}

#[cfg(test)]
mod tests {
    #[test]
    #[ignore = "manual elapsed-time measurement using the real Arena project and import cache"]
    fn arena_project_open_measurement() {
        use std::time::{Duration, Instant};
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../games/arena-arpg");
        let definition = nico_scene::Project::open(&root).unwrap();
        let start = Instant::now();
        let catalog =
            nico_assets::watch::WatchedProject::open_roots(&root, &definition.manifest.asset_roots)
                .unwrap();
        loop {
            let snapshot = catalog.snapshot();
            assert!(snapshot.error.is_none(), "{:?}", snapshot.error);
            if snapshot.scans > 0
                && snapshot.importing.is_none()
                && snapshot.assets.len() == 13
                && snapshot
                    .assets
                    .values()
                    .all(|a| a.value.is_some() || a.error.is_some())
            {
                assert_eq!(snapshot.assets.len(), 13);
                assert!(
                    snapshot
                        .assets
                        .values()
                        .all(|a| a.value.is_some() && a.error.is_none())
                );
                break;
            }
            assert!(start.elapsed() < Duration::from_secs(120));
            std::thread::sleep(Duration::from_millis(10));
        }
        eprintln!("catalog elapsed: {:?}", start.elapsed());
        drop(catalog);
        let start = Instant::now();
        let environment = arena_arpg_presentation::environment::Environment::load(
            &root.join("assets/presentation/worlds/meadow.world-vis.toml"),
        )
        .unwrap();
        eprintln!("environment load elapsed: {:?}", start.elapsed());
        // Measure the real adapter separately, including zone/landscape preparation.
        drop(environment);
        let start = Instant::now();
        let adapter = arena_arpg_presentation::authoring::open(&definition).unwrap();
        eprintln!("authoring adapter elapsed: {:?}", start.elapsed());
        assert_eq!(adapter.document().objects.len(), 99);
        drop(adapter);
        let start = Instant::now();
        let mut registry = nico_authoring::Registry::default();
        registry
            .register(
                arena_arpg_presentation::authoring::ADAPTER,
                arena_arpg_presentation::authoring::open,
            )
            .unwrap();
        let queue = std::sync::Arc::new(std::sync::Mutex::new(crate::operations::Queue::new(32)));
        let publication = std::sync::Arc::new(std::sync::Mutex::new(
            nico_ops::publication::Publication::default(),
        ));
        let core = super::Core::with_adapters(&root, queue, publication, &registry).unwrap();
        eprintln!("project ready elapsed: {:?}", start.elapsed());
        assert_eq!(core.document.objects.len(), 99);
    }

    use super::*;
    use std::sync::{Arc, Mutex};
    #[test]
    fn queued_edits_are_atomic_undoable_and_shutdown_finalizes_work() {
        let root = tempfile::tempdir().unwrap();
        let queue = Arc::new(Mutex::new(crate::operations::Queue::new(32)));
        let published = Arc::new(Mutex::new(nico_ops::publication::Publication::default()));
        let mut core = Core::new(root.path(), queue.clone(), published.clone()).unwrap();
        core.document.objects.push(Object {
            id: 1,
            asset: "missing.glb".into(),
            name: "Object".into(),
            position: [0.; 3],
            rotation: [0.; 3],
            scale: 1.,
        });
        queue
            .lock()
            .unwrap()
            .submit(|id| {
                (
                    id,
                    Action::Transform {
                        id: 1,
                        position: [2.; 3],
                        rotation: [0.; 3],
                        scale: 2.,
                    },
                )
            })
            .unwrap();
        assert_eq!(core.document.objects[0].scale, 1.);
        core.update();
        assert_eq!(core.document.objects[0].scale, 2.);
        let good = core.document.clone();
        assert!(
            core.apply(Action::Transform {
                id: 1,
                position: [f32::NAN; 3],
                rotation: [0.; 3],
                scale: 2.
            })
            .is_err()
        );
        assert_eq!(core.document, good);
        core.apply(Action::Undo).unwrap();
        assert_eq!(core.document.objects[0].scale, 1.);
        core.apply(Action::Redo).unwrap();
        assert_eq!(core.document, good);
        assert!(core.apply(Action::Reload).is_err());
        core.apply(Action::Save).unwrap();
        assert!(!core.dirty());
        queue
            .lock()
            .unwrap()
            .submit(|id| (id, Action::Remove { id: 1 }))
            .unwrap();
        core.close();
        assert_eq!(core.document, good);
        assert!(queue.lock().unwrap().is_closed());
        assert!(published.lock().unwrap().is_closed());
        assert_eq!(
            queue.lock().unwrap().history().back().unwrap()["error"],
            "host_closed"
        );
    }
}
