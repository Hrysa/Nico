//! Bounded runtime adapter for registered CPU importers. Install before consumers.
//!
//! The store is mutated only by runtime code. One worker reads trusted local
//! content; cancellation is cooperative and shutdown joins any active read/decode.
//! Paths are relative to a host-selected content root, not a filesystem sandbox.

use std::{
    collections::BTreeMap,
    io,
    path::Path,
    sync::{Arc, Mutex, Weak},
    thread::{self, JoinHandle},
    time::Duration,
};

use nico_runtime::{
    AppBuilder, Stage,
    events::EventReader,
    services::{
        RequestPollError, ServiceBackend, ServiceCompletion, ServiceError, ServiceRequestHandle,
        ServiceRuntime, service_channel,
    },
};

use crate::{AssetId, AssetLease, Handle};
#[cfg(any(feature = "png-import", feature = "gltf-import"))]
use std::path::PathBuf;

pub use crate::Texture;

pub use crate::asset_error::{AssetError, AssetLimits};
/// Ready describes CPU availability only; it makes no GPU readiness claim.
#[derive(Debug)]
pub enum AssetState<T> {
    Loading,
    Ready(Arc<T>),
    Failed(AssetError),
}

pub type TextureError = AssetError;
pub type MeshError = AssetError;
pub type TextureLimits = AssetLimits;
pub type MeshLimits = AssetLimits;
pub type TextureState = AssetState<Texture>;
pub type MeshState = AssetState<crate::Mesh>;
pub type TextureStore = AssetStore<Texture>;
pub type MeshStore = AssetStore<crate::Mesh>;

/// Resident-entry capacity. Source and decoder budgets belong to catalog entries.
#[derive(Clone, Copy, Debug)]
pub struct StoreLimits {
    pub max_assets: usize,
}
impl Default for StoreLimits {
    fn default() -> Self {
        Self { max_assets: 64 }
    }
}

#[cfg(any(feature = "png-import", feature = "gltf-import"))]
fn install_builtin<I: crate::import::AssetImporter>(
    app: &mut AppBuilder,
    root: impl AsRef<Path>,
    assets: impl IntoIterator<Item = (AssetId, PathBuf)>,
    limits: AssetLimits,
    importer: I,
    settings: I::Settings,
) -> io::Result<()> {
    use crate::import::{ImportBudget, ImportRegistry};
    if limits.max_assets == 0
        || limits.max_file_bytes == 0
        || limits.max_decoded_bytes == 0
        || limits.max_dimension == 0
        || limits.max_vertices == 0
        || limits.max_indices == 0
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "asset limits must be nonzero",
        ));
    }
    let invalid = |error| io::Error::new(io::ErrorKind::InvalidInput, error);
    importer.validate_settings(&settings).map_err(invalid)?;
    let mut registry = ImportRegistry::new();
    let token = registry.register(importer).map_err(invalid)?;
    for (id, path) in assets {
        registry
            .asset(
                id,
                path,
                &token,
                settings.clone(),
                ImportBudget {
                    max_input_bytes: limits.max_file_bytes,
                    max_decoded_bytes: limits.max_decoded_bytes,
                },
            )
            .map_err(invalid)?;
    }
    AssetStore::install_with_importers(
        app,
        root,
        registry,
        StoreLimits {
            max_assets: limits.max_assets,
        },
    )
}

#[cfg(feature = "png-import")]
impl TextureStore {
    /// Convenience installation using the built-in PNG importer and legacy limits.
    pub fn install(
        app: &mut AppBuilder,
        root: impl AsRef<Path>,
        assets: impl IntoIterator<Item = (AssetId, PathBuf)>,
        limits: TextureLimits,
    ) -> io::Result<()> {
        install_builtin(
            app,
            root,
            assets,
            limits,
            crate::importers::PngImporter,
            crate::importers::PngSettings {
                max_dimension: limits.max_dimension,
            },
        )
    }
}

#[cfg(feature = "gltf-import")]
impl MeshStore {
    /// Convenience installation using the restricted static GLB importer.
    pub fn install(
        app: &mut AppBuilder,
        root: impl AsRef<Path>,
        assets: impl IntoIterator<Item = (AssetId, PathBuf)>,
        limits: MeshLimits,
    ) -> io::Result<()> {
        install_builtin(
            app,
            root,
            assets,
            limits,
            crate::importers::StaticGlbImporter,
            crate::importers::StaticGlbSettings {
                max_vertices: limits.max_vertices,
                max_indices: limits.max_indices,
            },
        )
    }
}

struct Entry<T = Texture> {
    owners: Weak<()>,
    generation: u64,
    state: AssetState<T>,
}
struct LoadRequest<T = Texture> {
    entry: crate::import::ImportEntry<T>,
    result: Arc<Mutex<Option<LoadResult<T>>>>,
}
type LoadResult<T = Texture> = Result<Arc<T>, AssetError>;
// Events carry notification only; the active request owns the transferable pixels.
struct LoadCompletion<T = Texture>(std::marker::PhantomData<fn() -> T>);
impl<T> LoadCompletion<T> {
    fn new() -> Self {
        Self(std::marker::PhantomData)
    }
}
type Runtime<T = Texture> = ServiceRuntime<LoadRequest<T>, LoadCompletion<T>>;
type Backend<T = Texture> = ServiceBackend<LoadRequest<T>, LoadCompletion<T>>;

struct ActiveLoad<T = Texture> {
    asset: AssetId,
    generation: u64,
    request: ServiceRequestHandle,
    result: Arc<Mutex<Option<LoadResult<T>>>>,
}

/// Runtime-owned asset store. Install once per app before consumer systems.
///
/// Requests share entries. The final lease permits retirement at the next update.
/// Cloning a ready asset's `Arc` explicitly pins CPU data beyond store retirement.
/// GPU consumers must independently retain resources needed by snapshots/submissions.
pub struct AssetStore<T: Send + Sync + 'static> {
    catalog: BTreeMap<AssetId, crate::import::ImportEntry<T>>,
    entries: BTreeMap<AssetId, Entry<T>>,
    limits: StoreLimits,
    next_generation: u64,
    active: Option<ActiveLoad<T>>,
    service: Runtime<T>,
    worker: Option<JoinHandle<()>>,
    closed: bool,
}

impl<T: Send + Sync + 'static> AssetStore<T> {
    /// Catalog provenance remains inspectable independently of residency/readiness.
    #[must_use]
    pub fn source(&self, handle: Handle<T>) -> Option<&crate::import::ImportSource> {
        self.catalog.get(&handle.id()).map(|entry| &entry.source)
    }
    /// Enumerates configured identities and provenance for structured inspection.
    pub fn sources(&self) -> impl Iterator<Item = (AssetId, &crate::import::ImportSource)> {
        self.catalog.iter().map(|(id, entry)| (*id, &entry.source))
    }
    /// Installs a frozen registry with one worker for this output type.
    /// Importers are trusted code; cancellation is cooperative and shutdown joins them.
    pub fn install_with_importers(
        app: &mut AppBuilder,
        root: impl AsRef<Path>,
        registry: crate::import::ImportRegistry<T>,
        limits: StoreLimits,
    ) -> io::Result<()> {
        let store = Self::create(root.as_ref(), registry, limits)?;
        Self::attach(app, store)
    }

    fn attach(app: &mut AppBuilder, store: Self) -> io::Result<()> {
        // A rejected store drops and joins its worker without changing the installed store.
        if app
            .insert_resource(Installed::<T>(std::marker::PhantomData))
            .is_some()
        {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "asset store already installed",
            ));
        }
        let service = store.service.clone();
        app.insert_resource(store);
        let name = format!("assets::{}", std::any::type_name::<T>());
        app.add_service(&name, service);
        let mut reader = EventReader::<ServiceCompletion<LoadCompletion<T>>>::new();
        app.add_system(Stage::Update, format!("{name}::resolve"), move |context| {
            let completions = context.events.read(&mut reader);
            let store = context.world.resource_mut::<Self>()?;
            store.reconcile();
            if completions.missed() != 0 {
                store.fail_active(AssetError::WorkerUnavailable);
            }
            for completion in completions {
                store.publish(completion);
            }
            store.dispatch();
            Ok(())
        });
        app.add_system(Stage::Shutdown, format!("{name}::shutdown"), |context| {
            context.world.resource_mut::<Self>()?.shutdown();
            Ok(())
        });
        Ok(())
    }

    fn create(
        root: &Path,
        registry: crate::import::ImportRegistry<T>,
        limits: StoreLimits,
    ) -> io::Result<Self> {
        if limits.max_assets == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "asset capacity must be nonzero",
            ));
        }
        let catalog = registry.into_entries(root);
        let (service, backend) = service_channel(1).expect("nonzero capacity");
        let worker = thread::Builder::new()
            .name(format!("assets::{}", std::any::type_name::<T>()))
            .spawn(move || worker::<T>(backend))?;
        Ok(Self {
            catalog,
            entries: BTreeMap::new(),
            limits,
            next_generation: 0,
            active: None,
            service,
            worker: Some(worker),
            closed: false,
        })
    }

    /// Acquires shared ownership. Loading begins at the next update boundary.
    pub fn request(&mut self, handle: Handle<T>) -> Result<AssetLease<T>, AssetError> {
        if self.closed {
            return Err(AssetError::Closed);
        }
        let id = handle.id();
        if !self.catalog.contains_key(&id) {
            return Err(AssetError::UnknownAsset);
        }
        if let Some(entry) = self.entries.get_mut(&id) {
            let ownership = entry.owners.upgrade().unwrap_or_else(|| Arc::new(()));
            entry.owners = Arc::downgrade(&ownership);
            return Ok(AssetLease { handle, ownership });
        }
        if self.entries.len() >= self.limits.max_assets {
            return Err(AssetError::Capacity);
        }
        let ownership = Arc::new(());
        let generation = self.generation();
        self.entries.insert(
            id,
            Entry {
                owners: Arc::downgrade(&ownership),
                generation,
                state: AssetState::Loading,
            },
        );
        Ok(AssetLease { handle, ownership })
    }

    /// Resolves identity without retaining it. Released or unknown handles return None.
    #[must_use]
    pub fn state(&self, handle: Handle<T>) -> Option<&AssetState<T>> {
        self.entries.get(&handle.id()).map(|entry| &entry.state)
    }

    /// Retries a failed entry explicitly; existing leases observe the new state.
    pub fn retry(&mut self, handle: Handle<T>) -> Result<(), AssetError> {
        if self.closed {
            return Err(AssetError::Closed);
        }
        let entry = self
            .entries
            .get(&handle.id())
            .ok_or(AssetError::UnknownAsset)?;
        if !matches!(entry.state, AssetState::Failed(_)) {
            return Err(AssetError::NotFailed);
        }
        let generation = self.generation();
        let entry = self.entries.get_mut(&handle.id()).expect("entry exists");
        entry.generation = generation;
        entry.state = AssetState::Loading;
        Ok(())
    }

    fn generation(&mut self) -> u64 {
        self.next_generation = self
            .next_generation
            .checked_add(1)
            .expect("asset generation exhausted");
        self.next_generation
    }

    fn reconcile(&mut self) {
        self.entries
            .retain(|_, entry| entry.owners.strong_count() != 0);
        if let Some(active) = &self.active
            && !self
                .entries
                .get(&active.asset)
                .is_some_and(|e| e.generation == active.generation)
        {
            active.request.cancel();
        }
    }

    fn publish(&mut self, completion: &ServiceCompletion<LoadCompletion<T>>) {
        if self.closed
            || !self
                .active
                .as_ref()
                .is_some_and(|a| a.request.id() == completion.request_id())
        {
            return;
        }
        let active = self.active.take().expect("matched active request");
        let result = match completion.result() {
            Ok(_) => active
                .result
                .lock()
                .expect("completion lock poisoned")
                .take()
                .unwrap_or(Err(AssetError::WorkerUnavailable)),
            Err(ServiceError::Cancelled) => Err(AssetError::Cancelled),
            Err(ServiceError::Failed(_)) => Err(AssetError::WorkerUnavailable),
        };
        if let Some(entry) = self.entries.get_mut(&active.asset)
            && entry.generation == active.generation
        {
            entry.state = match result {
                Ok(texture) => AssetState::Ready(texture),
                Err(error) => AssetState::Failed(error),
            };
        }
    }

    fn fail_active(&mut self, error: AssetError) {
        if let Some(active) = self.active.take() {
            active.request.cancel();
            if let Some(entry) = self.entries.get_mut(&active.asset)
                && entry.generation == active.generation
            {
                entry.state = AssetState::Failed(error);
            }
        }
    }

    fn dispatch(&mut self) {
        if self.closed {
            return;
        }
        if self.service.is_closed() || self.worker.as_ref().is_some_and(JoinHandle::is_finished) {
            self.fail_active(AssetError::WorkerUnavailable);
            for entry in self.entries.values_mut() {
                if matches!(entry.state, AssetState::Loading) {
                    entry.state = AssetState::Failed(AssetError::WorkerUnavailable);
                }
            }
            return;
        }
        if self.active.is_some() {
            return;
        }
        if let Some((&id, entry)) = self
            .entries
            .iter_mut()
            .find(|(_, e)| matches!(e.state, AssetState::Loading))
        {
            let result = Arc::new(Mutex::new(None));
            match self.service.submit(LoadRequest {
                entry: self.catalog[&id].clone(),
                result: result.clone(),
            }) {
                Ok(request) => {
                    self.active = Some(ActiveLoad {
                        asset: id,
                        generation: entry.generation,
                        request,
                        result,
                    })
                }
                Err(_) => entry.state = AssetState::Failed(AssetError::WorkerUnavailable),
            }
        }
    }

    /// Closes requests, cancels active work, joins the worker, and releases entries.
    /// Active local file I/O or decoding may finish before the join returns.
    pub fn shutdown(&mut self) {
        self.closed = true;
        if let Some(active) = self.active.take() {
            active.request.cancel();
        }
        self.service.close();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        self.entries.clear();
    }
}

struct Installed<T>(std::marker::PhantomData<fn() -> T>);

impl<T: Send + Sync + 'static> Drop for AssetStore<T> {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn worker<T: Send + Sync + 'static>(backend: Backend<T>) {
    loop {
        match backend.try_next() {
            Ok(request) => {
                let (context, request) = request.into_parts();
                let result = if context.is_cancelled() {
                    Err(AssetError::Cancelled)
                } else {
                    request.entry.load(&|| context.is_cancelled()).map(Arc::new)
                };
                let result = if context.is_cancelled() {
                    Err(AssetError::Cancelled)
                } else {
                    result
                };
                // Exactly one request is outstanding until its completion is consumed.
                // Thus this completion queue cannot fill under the installed protocol.
                *request.result.lock().expect("completion lock poisoned") = Some(result);
                if backend
                    .complete(context, Ok(LoadCompletion::new()))
                    .is_err()
                {
                    break;
                }
            }
            Err(RequestPollError::Empty) => thread::sleep(Duration::from_millis(2)),
            Err(RequestPollError::Closed) => break,
        }
    }
}

#[cfg(all(test, feature = "png-import", feature = "gltf-import"))]
mod tests;
