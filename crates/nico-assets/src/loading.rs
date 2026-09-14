//! Bounded native PNG and mesh-only GLB loading. Install before asset consumers.
//!
//! The store is mutated only by runtime code. One worker reads trusted local
//! content; cancellation is cooperative and shutdown joins any active read/decode.
//! Paths are relative to a host-selected content root, not a filesystem sandbox.

use std::{
    collections::BTreeMap,
    fmt,
    fs::File,
    io::{self, Cursor, Read},
    path::{Component, Path, PathBuf},
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

pub use crate::Texture;

/// Structured loading failures. Failed entries remain inspectable until released.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AssetError {
    UnknownAsset,
    Capacity,
    Closed,
    NotFailed,
    Io(String),
    InvalidPng(String),
    InvalidMesh(String),
    UnsupportedMesh,
    UnsupportedPng,
    LimitExceeded,
    Cancelled,
    WorkerUnavailable,
}

impl fmt::Display for AssetError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}
impl std::error::Error for AssetError {}

/// Ready describes CPU availability only; it makes no GPU readiness claim.
#[derive(Debug)]
pub enum AssetState<T> {
    Loading,
    Ready(Arc<T>),
    Failed(AssetError),
}

/// Per-store bounds, including input and decoded allocations for each asset.
#[derive(Clone, Copy, Debug)]
pub struct AssetLimits {
    pub max_assets: usize,
    pub max_file_bytes: usize,
    pub max_decoded_bytes: usize,
    pub max_dimension: u32,
    pub max_vertices: usize,
    pub max_indices: usize,
}

impl Default for AssetLimits {
    fn default() -> Self {
        Self {
            max_assets: 64,
            max_file_bytes: 16 * 1024 * 1024,
            max_decoded_bytes: 64 * 1024 * 1024,
            max_dimension: 4096,
            max_vertices: 250_000,
            max_indices: 750_000,
        }
    }
}

pub type TextureError = AssetError;
pub type MeshError = AssetError;
pub type TextureLimits = AssetLimits;
pub type MeshLimits = AssetLimits;
pub type TextureState = AssetState<Texture>;
pub type MeshState = AssetState<crate::Mesh>;
pub type TextureStore = AssetStore<Texture>;
pub type MeshStore = AssetStore<crate::Mesh>;

mod mesh;
mod sealed {
    pub trait Sealed {}
    impl Sealed for super::Texture {}
    impl Sealed for crate::Mesh {}
}
/// Built-in decoder contract; implementations are sealed to supported asset types.
pub trait DecodeAsset: sealed::Sealed + Send + Sync + 'static {
    const SERVICE: &'static str;
    fn decode(path: &Path, limits: AssetLimits) -> Result<Arc<Self>, AssetError>;
}
impl DecodeAsset for Texture {
    const SERVICE: &'static str = "texture_assets";
    fn decode(path: &Path, limits: AssetLimits) -> Result<Arc<Self>, AssetError> {
        decode_png(path, limits)
    }
}
impl DecodeAsset for crate::Mesh {
    const SERVICE: &'static str = "mesh_assets";
    fn decode(path: &Path, limits: AssetLimits) -> Result<Arc<Self>, AssetError> {
        mesh::decode(path, limits)
    }
}
struct Entry<T = Texture> {
    owners: Weak<()>,
    generation: u64,
    state: AssetState<T>,
}
struct LoadRequest<T = Texture> {
    path: PathBuf,
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
pub struct AssetStore<T: DecodeAsset> {
    catalog: BTreeMap<AssetId, PathBuf>,
    entries: BTreeMap<AssetId, Entry<T>>,
    limits: AssetLimits,
    next_generation: u64,
    active: Option<ActiveLoad<T>>,
    service: Runtime<T>,
    worker: Option<JoinHandle<()>>,
    closed: bool,
}

impl<T: DecodeAsset> AssetStore<T> {
    /// Registers immutable asset paths, starts a decoder worker, and installs update
    /// and shutdown systems. Duplicate IDs, non-relative paths, zero limits, and a
    /// second installation are rejected. Content paths are trusted host configuration.
    pub fn install(
        app: &mut AppBuilder,
        root: impl AsRef<Path>,
        assets: impl IntoIterator<Item = (AssetId, PathBuf)>,
        limits: AssetLimits,
    ) -> io::Result<()> {
        let store = Self::create(root.as_ref(), assets, limits)?;
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
        app.add_service(T::SERVICE, service);
        let mut reader = EventReader::<ServiceCompletion<LoadCompletion<T>>>::new();
        app.add_system(
            Stage::Update,
            format!("{}::resolve", T::SERVICE),
            move |context| {
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
            },
        );
        app.add_system(
            Stage::Shutdown,
            format!("{}::shutdown", T::SERVICE),
            |context| {
                context.world.resource_mut::<Self>()?.shutdown();
                Ok(())
            },
        );
        Ok(())
    }

    fn create(
        root: &Path,
        assets: impl IntoIterator<Item = (AssetId, PathBuf)>,
        limits: AssetLimits,
    ) -> io::Result<Self> {
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
        let mut catalog = BTreeMap::new();
        for (id, path) in assets {
            if path.as_os_str().is_empty()
                || path
                    .components()
                    .any(|c| !matches!(c, Component::Normal(_)))
            {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "asset paths must contain only relative normal components",
                ));
            }
            if catalog.insert(id, root.join(path)).is_some() {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "duplicate asset ID",
                ));
            }
        }
        let (service, backend) = service_channel(1).expect("nonzero capacity");
        let worker = thread::Builder::new()
            .name(T::SERVICE.into())
            .spawn(move || worker::<T>(backend, limits))?;
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
                path: self.catalog[&id].clone(),
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

impl<T: DecodeAsset> Drop for AssetStore<T> {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn worker<T: DecodeAsset>(backend: Backend<T>, limits: AssetLimits) {
    loop {
        match backend.try_next() {
            Ok(request) => {
                let (context, request) = request.into_parts();
                let result = if context.is_cancelled() {
                    Err(AssetError::Cancelled)
                } else {
                    T::decode(&request.path, limits)
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

fn decode_png(path: &Path, limits: TextureLimits) -> LoadResult {
    let file = File::open(path).map_err(|e| TextureError::Io(e.to_string()))?;
    let mut bytes = Vec::new();
    file.take((limits.max_file_bytes as u64).saturating_add(1))
        .read_to_end(&mut bytes)
        .map_err(|e| TextureError::Io(e.to_string()))?;
    if bytes.len() > limits.max_file_bytes {
        return Err(TextureError::LimitExceeded);
    }
    decode_bytes(bytes, limits)
}

fn decode_bytes(bytes: Vec<u8>, limits: TextureLimits) -> LoadResult {
    let mut decoder = png::Decoder::new(Cursor::new(bytes));
    decoder.set_limits(png::Limits {
        bytes: limits.max_decoded_bytes,
    });
    decoder.set_transformations(png::Transformations::EXPAND | png::Transformations::STRIP_16);
    let mut reader = decoder
        .read_info()
        .map_err(|e| TextureError::InvalidPng(e.to_string()))?;
    let info = reader.info();
    if info.animation_control.is_some() {
        return Err(TextureError::UnsupportedPng);
    }
    let (width, height) = (info.width, info.height);
    let rgba_len = (width as usize)
        .checked_mul(height as usize)
        .and_then(|n| n.checked_mul(4))
        .ok_or(TextureError::LimitExceeded)?;
    if width > limits.max_dimension
        || height > limits.max_dimension
        || rgba_len > limits.max_decoded_bytes
    {
        return Err(TextureError::LimitExceeded);
    }
    let size = reader
        .output_buffer_size()
        .ok_or(TextureError::LimitExceeded)?;
    if size > limits.max_decoded_bytes {
        return Err(TextureError::LimitExceeded);
    }
    let mut decoded = vec![0; size];
    let output = reader
        .next_frame(&mut decoded)
        .map_err(|e| TextureError::InvalidPng(e.to_string()))?;
    reader
        .finish()
        .map_err(|e| TextureError::InvalidPng(e.to_string()))?;
    let mut pixels = Vec::with_capacity(rgba_len);
    let channels = output.color_type.samples();
    for pixel in decoded[..output.buffer_size()].chunks_exact(channels) {
        match output.color_type {
            png::ColorType::Grayscale => {
                pixels.extend_from_slice(&[pixel[0], pixel[0], pixel[0], 255])
            }
            png::ColorType::GrayscaleAlpha => {
                pixels.extend_from_slice(&[pixel[0], pixel[0], pixel[0], pixel[1]])
            }
            png::ColorType::Rgb => pixels.extend_from_slice(&[pixel[0], pixel[1], pixel[2], 255]),
            png::ColorType::Rgba => pixels.extend_from_slice(pixel),
            png::ColorType::Indexed => return Err(TextureError::UnsupportedPng),
        }
    }
    Ok(Arc::new(Texture {
        width,
        height,
        pixels,
    }))
}

#[cfg(test)]
mod tests;
