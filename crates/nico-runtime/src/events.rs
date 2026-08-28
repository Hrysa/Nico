//! Typed, bounded events for deterministic runtime communication.
//!
//! Events are broadcast facts. Every [`EventReader`] owns an independent
//! cursor, so reading does not remove an event for other consumers. Systems
//! stage writes through [`SystemEvents`]; the scheduler commits those writes
//! only when the producing system succeeds.

use std::{
    any::{Any, TypeId},
    collections::{HashMap, VecDeque},
    marker::PhantomData,
};

/// Default number of retained events for each event type.
pub const DEFAULT_EVENT_CAPACITY: usize = 1_024;

/// Marker implemented by values that may be published as runtime events.
pub trait Event: Any + Send + Sync {}

impl<T: Any + Send + Sync> Event for T {}

/// Independent read position for one consumer of an event type.
///
/// A new reader observes all events of this type that are still retained. Keep
/// the reader alive between system invocations to receive each event once.
#[derive(Clone, Debug)]
pub struct EventReader<T> {
    next_sequence: u64,
    marker: PhantomData<fn() -> T>,
}

impl<T> Default for EventReader<T> {
    fn default() -> Self {
        Self {
            next_sequence: 0,
            marker: PhantomData,
        }
    }
}

impl<T> EventReader<T> {
    /// Creates a reader positioned at the beginning of retained history.
    #[must_use]
    pub const fn new() -> Self {
        Self {
            next_sequence: 0,
            marker: PhantomData,
        }
    }
}

/// Events available to one reader during a single read operation.
///
/// This iterator borrows the event bus. [`Self::missed`] reports events that
/// were evicted before this reader observed them.
pub struct EventRead<'a, T> {
    events: Option<&'a VecDeque<T>>,
    index: usize,
    end: usize,
    missed: u64,
}

impl<T> EventRead<'_, T> {
    /// Returns how many events were dropped because this reader fell behind.
    #[must_use]
    pub const fn missed(&self) -> u64 {
        self.missed
    }

    /// Returns whether no retained events are available.
    #[must_use]
    pub const fn is_empty(&self) -> bool {
        self.index == self.end
    }

    /// Returns the number of retained events available.
    #[must_use]
    pub const fn len(&self) -> usize {
        self.end - self.index
    }
}

impl<'a, T> Iterator for EventRead<'a, T> {
    type Item = &'a T;

    fn next(&mut self) -> Option<Self::Item> {
        if self.index == self.end {
            return None;
        }

        let index = self.index;
        self.index += 1;
        self.events.and_then(|events| events.get(index))
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        let remaining = self.len();
        (remaining, Some(remaining))
    }
}

impl<T> ExactSizeIterator for EventRead<'_, T> {}

struct EventStream<T> {
    first_sequence: u64,
    next_sequence: u64,
    values: VecDeque<T>,
}

impl<T> Default for EventStream<T> {
    fn default() -> Self {
        Self {
            first_sequence: 0,
            next_sequence: 0,
            values: VecDeque::new(),
        }
    }
}

/// Bounded storage for every typed runtime event stream.
///
/// Capacity applies independently to each event type. When a stream is full,
/// publishing another event evicts its oldest value. Lagging readers learn the
/// exact number of evicted values through [`EventRead::missed`].
pub struct EventBus {
    capacity: usize,
    streams: HashMap<TypeId, Box<dyn Any + Send + Sync>>,
}

impl Default for EventBus {
    fn default() -> Self {
        Self::new(DEFAULT_EVENT_CAPACITY)
    }
}

impl EventBus {
    pub(crate) fn new(capacity: usize) -> Self {
        debug_assert!(capacity > 0);
        Self {
            capacity,
            streams: HashMap::new(),
        }
    }

    /// Returns the maximum retained event count for each event type.
    #[must_use]
    pub const fn capacity(&self) -> usize {
        self.capacity
    }

    /// Publishes an event immediately.
    ///
    /// Application hosts use this before a runtime tick. Systems use
    /// [`SystemEvents::send`] so failed systems cannot leak partial output.
    pub(crate) fn send<T: Event>(&mut self, event: T) {
        let stream = self
            .streams
            .entry(TypeId::of::<T>())
            .or_insert_with(|| Box::<EventStream<T>>::default())
            .downcast_mut::<EventStream<T>>()
            .expect("an event TypeId must map to its own typed stream");

        if stream.values.len() == self.capacity {
            stream.values.pop_front();
            stream.first_sequence = stream
                .first_sequence
                .checked_add(1)
                .expect("event sequence exhausted");
        }

        stream.values.push_back(event);
        stream.next_sequence = stream
            .next_sequence
            .checked_add(1)
            .expect("event sequence exhausted");
    }

    /// Reads all retained events not previously observed by this reader.
    pub fn read<'a, T: Event>(&'a self, reader: &mut EventReader<T>) -> EventRead<'a, T> {
        let Some(stream) = self
            .streams
            .get(&TypeId::of::<T>())
            .and_then(|stream| stream.downcast_ref::<EventStream<T>>())
        else {
            return EventRead {
                events: None,
                index: 0,
                end: 0,
                missed: 0,
            };
        };

        let first_available = reader
            .next_sequence
            .clamp(stream.first_sequence, stream.next_sequence);
        let missed = stream.first_sequence.saturating_sub(reader.next_sequence);
        let index = usize::try_from(first_available - stream.first_sequence)
            .expect("retained event offset must fit in usize");
        reader.next_sequence = stream.next_sequence;

        EventRead {
            events: Some(&stream.values),
            index,
            end: stream.values.len(),
            missed,
        }
    }
}

trait PendingEvent: Send {
    fn commit(self: Box<Self>, events: &mut EventBus);
}

struct TypedPendingEvent<T>(T);

impl<T: Event> PendingEvent for TypedPendingEvent<T> {
    fn commit(self: Box<Self>, events: &mut EventBus) {
        events.send(self.0);
    }
}

#[derive(Default)]
pub(crate) struct PendingEvents {
    values: Vec<Box<dyn PendingEvent>>,
}

impl PendingEvents {
    fn send<T: Event>(&mut self, event: T) {
        self.values.push(Box::new(TypedPendingEvent(event)));
    }

    pub(crate) fn commit(self, events: &mut EventBus) {
        for event in self.values {
            event.commit(events);
        }
    }
}

/// Event access supplied to one runtime system invocation.
pub struct SystemEvents<'a> {
    committed: &'a EventBus,
    pending: &'a mut PendingEvents,
}

impl<'a> SystemEvents<'a> {
    pub(crate) fn new(committed: &'a EventBus, pending: &'a mut PendingEvents) -> Self {
        Self { committed, pending }
    }

    /// Stages an event for publication after the current system succeeds.
    pub fn send<T: Event>(&mut self, event: T) {
        self.pending.send(event);
    }

    /// Reads committed events not previously observed by this reader.
    ///
    /// Events staged by this same system are not visible until it returns
    /// successfully.
    pub fn read<T: Event>(&self, reader: &mut EventReader<T>) -> EventRead<'_, T> {
        self.committed.read(reader)
    }
}

#[cfg(test)]
mod tests {
    use super::{EventBus, EventReader};

    #[test]
    fn readers_are_independent() {
        let mut events = EventBus::new(4);
        let mut first = EventReader::<u32>::new();
        let mut second = EventReader::<u32>::new();

        events.send(7_u32);
        events.send(9_u32);

        assert_eq!(events.read(&mut first).copied().collect::<Vec<_>>(), [7, 9]);
        assert_eq!(events.read(&mut first).count(), 0);
        assert_eq!(
            events.read(&mut second).copied().collect::<Vec<_>>(),
            [7, 9]
        );
    }

    #[test]
    fn lagging_reader_is_told_about_evicted_events() {
        let mut events = EventBus::new(2);
        let mut reader = EventReader::<u32>::new();

        events.send(1_u32);
        events.send(2_u32);
        events.send(3_u32);

        let read = events.read(&mut reader);
        assert_eq!(read.missed(), 1);
        assert_eq!(read.copied().collect::<Vec<_>>(), [2, 3]);
    }
}
