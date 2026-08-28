//! Typed, bounded bridge between runtime systems and asynchronous services.
//!
//! A runtime owns [`ServiceRuntime`]; a host-selected backend owns the matching
//! [`ServiceBackend`]. Requests and completions cross bounded standard-library
//! channels, so neither side depends on an async executor. The runtime publishes
//! completions as typed events from an ordinary system, keeping [`World`] on the
//! runtime thread.

use std::{
    error::Error,
    fmt,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{Receiver, SyncSender, TryRecvError, TrySendError, sync_channel},
    },
};

use nico_ecs::{Entity, World};

use crate::events::{Event, SystemEvents};

/// Default capacity of each side of a service channel.
pub const DEFAULT_SERVICE_CAPACITY: usize = 256;

/// Stable identity of one request within a service channel.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ServiceRequestId(u64);

impl ServiceRequestId {
    /// Returns the channel-local integer representation used for diagnostics.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// Error reported by a service operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ServiceError {
    /// The runtime cancelled the request before consuming its completion.
    Cancelled,
    /// The selected backend failed the operation.
    Failed(String),
}

impl fmt::Display for ServiceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Cancelled => formatter.write_str("service request was cancelled"),
            Self::Failed(message) => formatter.write_str(message),
        }
    }
}

impl Error for ServiceError {}

/// Failure to enqueue an owned value on a bounded service channel.
#[derive(Debug, Eq, PartialEq)]
pub enum ServiceQueueError<T> {
    /// The bounded queue has no remaining capacity.
    Full(T),
    /// The service channel has closed.
    Closed(T),
}

impl<T> ServiceQueueError<T> {
    /// Returns the value that could not be enqueued.
    #[must_use]
    pub fn into_inner(self) -> T {
        match self {
            Self::Full(value) | Self::Closed(value) => value,
        }
    }
}

/// Failure to create a service channel.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct InvalidServiceCapacity;

impl fmt::Display for InvalidServiceCapacity {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("service channel capacity must be non-zero")
    }
}

impl Error for InvalidServiceCapacity {}

/// Failure to enqueue a completion on its bounded ingress queue.
///
/// A rejected completion is dropped. Backends that require retry guarantees
/// must implement that policy outside this ingress boundary.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompletionSubmitError {
    /// The bounded completion queue has no remaining capacity.
    Full,
    /// The runtime closed or dropped the service channel.
    Closed,
}

struct ChannelState {
    next_request_id: AtomicU64,
    closed: AtomicBool,
}

impl ChannelState {
    fn new() -> Self {
        Self {
            next_request_id: AtomicU64::new(1),
            closed: AtomicBool::new(false),
        }
    }

    fn next_request_id(&self) -> ServiceRequestId {
        let id = self.next_request_id.fetch_add(1, Ordering::Relaxed);
        assert!(id != u64::MAX, "service request identity exhausted");
        ServiceRequestId(id)
    }
}

/// Handle retained by runtime code to identify or cancel one request.
#[derive(Clone)]
pub struct ServiceRequestHandle {
    id: ServiceRequestId,
    cancelled: Arc<AtomicBool>,
}

impl ServiceRequestHandle {
    /// Returns this request's identity.
    #[must_use]
    pub const fn id(&self) -> ServiceRequestId {
        self.id
    }

    /// Prevents a successful late completion from being published.
    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
    }

    /// Returns whether cancellation has been requested.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::Acquire)
    }
}

/// Context carried from submission through backend completion.
pub struct ServiceRequestContext {
    id: ServiceRequestId,
    target: Option<Entity>,
    cancelled: Arc<AtomicBool>,
}

impl ServiceRequestContext {
    /// Returns this request's identity.
    #[must_use]
    pub const fn id(&self) -> ServiceRequestId {
        self.id
    }

    /// Returns the entity this request targets, when any.
    #[must_use]
    pub const fn target(&self) -> Option<Entity> {
        self.target
    }

    /// Returns whether runtime code cancelled this request.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::Acquire)
    }
}

/// Owned request received by a service backend.
pub struct BackendRequest<R> {
    context: ServiceRequestContext,
    payload: R,
}

impl<R> BackendRequest<R> {
    /// Returns this request's identity.
    #[must_use]
    pub const fn id(&self) -> ServiceRequestId {
        self.context.id()
    }

    /// Returns the target entity, when any.
    #[must_use]
    pub const fn target(&self) -> Option<Entity> {
        self.context.target()
    }

    /// Returns whether runtime code cancelled this request.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.context.is_cancelled()
    }

    /// Borrows the domain-owned request value.
    #[must_use]
    pub const fn payload(&self) -> &R {
        &self.payload
    }

    /// Splits the request into the context required for completion and its
    /// domain-owned value.
    #[must_use]
    pub fn into_parts(self) -> (ServiceRequestContext, R) {
        (self.context, self.payload)
    }
}

struct QueuedCompletion<C> {
    context: ServiceRequestContext,
    result: Result<C, ServiceError>,
}

/// Typed completion published by [`ServiceRuntime::publish_completions`].
#[derive(Debug, Eq, PartialEq)]
pub struct ServiceCompletion<C> {
    request_id: ServiceRequestId,
    target: Option<Entity>,
    result: Result<C, ServiceError>,
}

impl<C> ServiceCompletion<C> {
    /// Returns the completed request's identity.
    #[must_use]
    pub const fn request_id(&self) -> ServiceRequestId {
        self.request_id
    }

    /// Returns the still-live entity targeted by the request, when any.
    #[must_use]
    pub const fn target(&self) -> Option<Entity> {
        self.target
    }

    /// Borrows the backend result.
    pub const fn result(&self) -> &Result<C, ServiceError> {
        &self.result
    }

    /// Consumes the completion and returns the backend result.
    pub fn into_result(self) -> Result<C, ServiceError> {
        self.result
    }
}

/// Counts the results of one deterministic completion drain.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct CompletionDrainReport {
    published: usize,
    stale_targets: usize,
}

impl CompletionDrainReport {
    /// Returns the number of completion events staged for publication.
    #[must_use]
    pub const fn published(self) -> usize {
        self.published
    }

    /// Returns the number discarded because their generational entity target
    /// was no longer alive.
    #[must_use]
    pub const fn stale_targets(self) -> usize {
        self.stale_targets
    }
}

/// Runtime side of one typed service channel.
///
/// Clone this value when separate systems submit requests and publish
/// completions. Only one system should call [`Self::publish_completions`].
pub struct ServiceRuntime<R, C> {
    request_sender: SyncSender<BackendRequest<R>>,
    completion_receiver: Arc<Mutex<Receiver<QueuedCompletion<C>>>>,
    state: Arc<ChannelState>,
}

impl<R, C> Clone for ServiceRuntime<R, C> {
    fn clone(&self) -> Self {
        Self {
            request_sender: self.request_sender.clone(),
            completion_receiver: Arc::clone(&self.completion_receiver),
            state: Arc::clone(&self.state),
        }
    }
}

impl<R: Send + 'static, C: Event> ServiceRuntime<R, C> {
    /// Submits an owned domain request without an entity target.
    pub fn submit(&self, payload: R) -> Result<ServiceRequestHandle, ServiceQueueError<R>> {
        self.submit_inner(None, payload)
    }

    /// Submits an owned domain request targeting a generational entity.
    ///
    /// Its completion is discarded if that exact entity is no longer alive
    /// when the runtime drains it.
    pub fn submit_for(
        &self,
        target: Entity,
        payload: R,
    ) -> Result<ServiceRequestHandle, ServiceQueueError<R>> {
        self.submit_inner(Some(target), payload)
    }

    fn submit_inner(
        &self,
        target: Option<Entity>,
        payload: R,
    ) -> Result<ServiceRequestHandle, ServiceQueueError<R>> {
        if self.is_closed() {
            return Err(ServiceQueueError::Closed(payload));
        }

        let handle = ServiceRequestHandle {
            id: self.state.next_request_id(),
            cancelled: Arc::new(AtomicBool::new(false)),
        };
        let request = BackendRequest {
            context: ServiceRequestContext {
                id: handle.id,
                target,
                cancelled: Arc::clone(&handle.cancelled),
            },
            payload,
        };

        match self.request_sender.try_send(request) {
            Ok(()) => Ok(handle),
            Err(TrySendError::Full(request)) => Err(ServiceQueueError::Full(request.payload)),
            Err(TrySendError::Disconnected(request)) => {
                self.state.closed.store(true, Ordering::Release);
                Err(ServiceQueueError::Closed(request.payload))
            }
        }
    }

    /// Drains completions in backend submission order and stages typed events.
    ///
    /// Call this from a runtime system. Successful output becomes visible to the
    /// next scheduled system. Cancelled requests publish `Cancelled`; stale
    /// entity-targeted completions are discarded.
    pub fn publish_completions(
        &self,
        world: &World,
        events: &mut SystemEvents<'_>,
    ) -> CompletionDrainReport {
        let mut report = CompletionDrainReport::default();
        let receiver = self
            .completion_receiver
            .lock()
            .expect("service completion receiver lock poisoned");

        loop {
            let completion = match receiver.try_recv() {
                Ok(completion) => completion,
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    self.state.closed.store(true, Ordering::Release);
                    break;
                }
            };

            if completion
                .context
                .target
                .is_some_and(|target| !world.contains_entity(target))
            {
                report.stale_targets += 1;
                continue;
            }

            let result = if completion.context.is_cancelled() {
                Err(ServiceError::Cancelled)
            } else {
                completion.result
            };
            events.send(ServiceCompletion {
                request_id: completion.context.id,
                target: completion.context.target,
                result,
            });
            report.published += 1;
        }

        report
    }

    /// Closes the channel to new requests and late completions.
    pub fn close(&self) {
        self.state.closed.store(true, Ordering::Release);
    }

    /// Returns whether the service channel has closed.
    #[must_use]
    pub fn is_closed(&self) -> bool {
        self.state.closed.load(Ordering::Acquire)
    }
}

/// Outcome of polling the backend side for one request.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RequestPollError {
    /// No request is currently queued.
    Empty,
    /// The runtime closed or dropped the service channel.
    Closed,
}

/// Host-selected backend side of one typed service channel.
///
/// A deterministic test backend can poll this value manually. A native adapter
/// may move it onto its executor or worker thread.
pub struct ServiceBackend<R, C> {
    request_receiver: Receiver<BackendRequest<R>>,
    completion_sender: SyncSender<QueuedCompletion<C>>,
    state: Arc<ChannelState>,
}

/// Runtime and backend endpoints created together for one typed service.
pub type ServiceChannel<R, C> = (ServiceRuntime<R, C>, ServiceBackend<R, C>);

impl<R: Send + 'static, C: Event> ServiceBackend<R, C> {
    /// Polls the next request without blocking.
    pub fn try_next(&self) -> Result<BackendRequest<R>, RequestPollError> {
        if self.state.closed.load(Ordering::Acquire) {
            return Err(RequestPollError::Closed);
        }

        match self.request_receiver.try_recv() {
            Ok(request) => Ok(request),
            Err(TryRecvError::Empty) => Err(RequestPollError::Empty),
            Err(TryRecvError::Disconnected) => {
                self.state.closed.store(true, Ordering::Release);
                Err(RequestPollError::Closed)
            }
        }
    }

    /// Enqueues an owned result for deterministic runtime-thread publication.
    pub fn complete(
        &self,
        context: ServiceRequestContext,
        result: Result<C, ServiceError>,
    ) -> Result<(), CompletionSubmitError> {
        if self.state.closed.load(Ordering::Acquire) {
            return Err(CompletionSubmitError::Closed);
        }

        let completion = QueuedCompletion { context, result };
        match self.completion_sender.try_send(completion) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(_)) => Err(CompletionSubmitError::Full),
            Err(TrySendError::Disconnected(_)) => {
                self.state.closed.store(true, Ordering::Release);
                Err(CompletionSubmitError::Closed)
            }
        }
    }
}

/// Creates a typed, bounded runtime/backend service channel.
///
/// Request and completion queues each retain at most `capacity` values.
pub fn service_channel<R: Send + 'static, C: Event>(
    capacity: usize,
) -> Result<ServiceChannel<R, C>, InvalidServiceCapacity> {
    if capacity == 0 {
        return Err(InvalidServiceCapacity);
    }

    let (request_sender, request_receiver) = sync_channel(capacity);
    let (completion_sender, completion_receiver) = sync_channel(capacity);
    let state = Arc::new(ChannelState::new());
    Ok((
        ServiceRuntime {
            request_sender,
            completion_receiver: Arc::new(Mutex::new(completion_receiver)),
            state: Arc::clone(&state),
        },
        ServiceBackend {
            request_receiver,
            completion_sender,
            state,
        },
    ))
}

#[cfg(test)]
mod tests {
    use std::{thread, time::Duration};

    use crate::{
        AppBuilder, RuntimeResult, Stage,
        events::EventReader,
        services::{
            CompletionSubmitError, RequestPollError, ServiceCompletion, ServiceError,
            ServiceQueueError, service_channel,
        },
    };

    #[derive(Debug, Eq, PartialEq)]
    struct LoadRequest(&'static str);

    #[derive(Clone, Debug, Eq, PartialEq)]
    struct LoadedValue(u32);

    #[derive(Default)]
    struct Observed {
        values: Vec<Result<u32, ServiceError>>,
        stale_targets: usize,
    }

    #[test]
    fn manually_controlled_backend_delivers_completions_in_order() -> RuntimeResult<()> {
        let (runtime, backend) = service_channel::<LoadRequest, LoadedValue>(4).unwrap();
        let bridge = runtime.clone();
        let mut reader = EventReader::<ServiceCompletion<LoadedValue>>::new();
        let mut builder = AppBuilder::new();
        builder.insert_resource(Observed::default());
        builder.add_system(
            Stage::Update,
            "publish service completions",
            move |context| {
                bridge.publish_completions(context.world, &mut context.events);
                Ok(())
            },
        );
        builder.add_system(
            Stage::Update,
            "observe service completions",
            move |context| {
                let values = context
                    .events
                    .read(&mut reader)
                    .map(|completion| {
                        completion
                            .result()
                            .as_ref()
                            .map(|value| value.0)
                            .map_err(Clone::clone)
                    })
                    .collect();
                context.world.resource_mut::<Observed>()?.values = values;
                Ok(())
            },
        );
        let mut app = builder.build()?;
        app.start()?;

        let first_handle = runtime.submit(LoadRequest("first")).unwrap();
        let second_handle = runtime.submit(LoadRequest("second")).unwrap();
        let first = backend.try_next().unwrap();
        let second = backend.try_next().unwrap();
        assert_eq!(first.id(), first_handle.id());
        assert_eq!(second.id(), second_handle.id());
        assert_eq!(first.payload(), &LoadRequest("first"));
        assert_eq!(second.payload(), &LoadRequest("second"));
        let (first_context, _) = first.into_parts();
        let (second_context, _) = second.into_parts();
        backend.complete(first_context, Ok(LoadedValue(3))).unwrap();
        backend
            .complete(
                second_context,
                Err(ServiceError::Failed("offline".to_owned())),
            )
            .unwrap();

        app.tick(Duration::from_millis(16))?;

        assert_eq!(
            app.world().resource::<Observed>()?.values,
            [Ok(3), Err(ServiceError::Failed("offline".to_owned()))]
        );
        app.shutdown()
    }

    #[test]
    fn backend_can_deliver_a_completion_from_another_thread() -> RuntimeResult<()> {
        let (runtime, backend) = service_channel::<LoadRequest, LoadedValue>(1).unwrap();
        let mut reader = EventReader::<ServiceCompletion<LoadedValue>>::new();
        let mut builder = AppBuilder::new();
        builder.insert_resource(Observed::default());
        builder.add_service("threaded_test", runtime.clone());
        builder.add_system(
            Stage::Update,
            "observe threaded completion",
            move |context| {
                let values = context
                    .events
                    .read(&mut reader)
                    .map(|completion| {
                        completion
                            .result()
                            .as_ref()
                            .map(|value| value.0)
                            .map_err(Clone::clone)
                    })
                    .collect();
                context.world.resource_mut::<Observed>()?.values = values;
                Ok(())
            },
        );
        let mut app = builder.build()?;
        app.start()?;
        runtime.submit(LoadRequest("threaded")).unwrap();

        thread::spawn(move || {
            let request = backend.try_next().unwrap();
            let (context, payload) = request.into_parts();
            assert_eq!(payload, LoadRequest("threaded"));
            backend.complete(context, Ok(LoadedValue(9))).unwrap();
        })
        .join()
        .unwrap();

        app.tick(Duration::from_millis(16))?;
        assert_eq!(app.world().resource::<Observed>()?.values, [Ok(9)]);
        app.shutdown()
    }

    #[test]
    fn cancellation_and_stale_entity_targets_are_checked_on_runtime_thread() -> RuntimeResult<()> {
        let (runtime, backend) = service_channel::<LoadRequest, LoadedValue>(4).unwrap();
        let bridge = runtime.clone();
        let mut reader = EventReader::<ServiceCompletion<LoadedValue>>::new();
        let mut builder = AppBuilder::new();
        builder.insert_resource(Observed::default());
        builder.add_system(
            Stage::Update,
            "publish service completions",
            move |context| {
                let report = bridge.publish_completions(context.world, &mut context.events);
                context.world.resource_mut::<Observed>()?.stale_targets = report.stale_targets();
                Ok(())
            },
        );
        builder.add_system(
            Stage::Update,
            "observe service completions",
            move |context| {
                let values = context
                    .events
                    .read(&mut reader)
                    .map(|completion| {
                        completion
                            .result()
                            .as_ref()
                            .map(|value| value.0)
                            .map_err(Clone::clone)
                    })
                    .collect();
                context.world.resource_mut::<Observed>()?.values = values;
                Ok(())
            },
        );
        let mut app = builder.build()?;
        let stale = app.world_mut().spawn(());
        app.start()?;

        let cancelled = runtime.submit(LoadRequest("cancelled")).unwrap();
        cancelled.cancel();
        let cancelled_request = backend.try_next().unwrap();
        assert!(cancelled_request.is_cancelled());
        let (cancelled_context, _) = cancelled_request.into_parts();
        backend
            .complete(cancelled_context, Ok(LoadedValue(1)))
            .unwrap();

        runtime.submit_for(stale, LoadRequest("stale")).unwrap();
        let stale_request = backend.try_next().unwrap();
        let (stale_context, _) = stale_request.into_parts();
        app.world_mut().despawn(stale).unwrap();
        backend.complete(stale_context, Ok(LoadedValue(2))).unwrap();

        app.tick(Duration::from_millis(16))?;

        let observed = app.world().resource::<Observed>()?;
        assert_eq!(observed.values, [Err(ServiceError::Cancelled)]);
        assert_eq!(observed.stale_targets, 1);
        app.shutdown()
    }

    #[test]
    fn bounded_queues_report_overload_and_close_rejects_late_work() {
        let (runtime, backend) = service_channel::<LoadRequest, LoadedValue>(1).unwrap();

        runtime.submit(LoadRequest("first")).unwrap();
        assert!(matches!(
            runtime.submit(LoadRequest("overflow")),
            Err(ServiceQueueError::Full(LoadRequest("overflow")))
        ));

        let first = backend.try_next().unwrap();
        let (context, _) = first.into_parts();
        backend.complete(context, Ok(LoadedValue(1))).unwrap();

        runtime.submit(LoadRequest("second")).unwrap();
        let second = backend.try_next().unwrap();
        let (context, _) = second.into_parts();
        assert!(matches!(
            backend.complete(context, Ok(LoadedValue(2))),
            Err(CompletionSubmitError::Full)
        ));

        runtime.close();
        assert!(matches!(
            runtime.submit(LoadRequest("late")),
            Err(ServiceQueueError::Closed(LoadRequest("late")))
        ));
        assert!(matches!(backend.try_next(), Err(RequestPollError::Closed)));
    }

    #[test]
    fn registered_service_closes_during_app_shutdown() -> RuntimeResult<()> {
        let (runtime, backend) = service_channel::<LoadRequest, LoadedValue>(1).unwrap();
        let mut builder = AppBuilder::new();
        builder.add_service("test_service", runtime.clone());
        let mut app = builder.build()?;
        app.start()?;

        runtime.submit(LoadRequest("pending")).unwrap();
        let request = backend.try_next().unwrap();
        let (context, _) = request.into_parts();

        app.shutdown()?;

        assert!(runtime.is_closed());
        assert_eq!(
            backend.complete(context, Ok(LoadedValue(1))),
            Err(CompletionSubmitError::Closed)
        );
        Ok(())
    }

    #[test]
    fn zero_capacity_is_rejected() {
        assert!(service_channel::<LoadRequest, LoadedValue>(0).is_err());
    }
}
