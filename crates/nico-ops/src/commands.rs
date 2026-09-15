//! Bounded inbound command bookkeeping. Callers own synchronization, readiness,
//! payload/outcome semantics, and execution boundaries; no runtime or transport.
use std::{
    collections::VecDeque,
    ops::{Index, IndexMut},
};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SubmitError {
    Closed,
    Busy,
    InvalidSlot,
    IdExhausted,
}
/// Fixed command lanes plus bounded terminal history. Taking a request does not
/// finish it: the owner may update and return it to its lane at the same boundary.
/// Closing rejects new submissions; the owner must finalize all pending requests.
pub struct CommandBook<R, O, const S: usize> {
    slots: [Option<R>; S],
    history: VecDeque<O>,
    retention: usize,
    last_id: u64,
    closed: bool,
}
impl<R, O, const S: usize> CommandBook<R, O, S> {
    pub fn new(retention: usize) -> Self {
        Self {
            slots: std::array::from_fn(|_| None),
            history: VecDeque::new(),
            retention,
            last_id: 0,
            closed: false,
        }
    }
    pub fn submit(
        &mut self,
        slot: usize,
        request: impl FnOnce(u64) -> R,
    ) -> Result<u64, SubmitError> {
        if self.closed {
            return Err(SubmitError::Closed);
        }
        let lane = self.slots.get_mut(slot).ok_or(SubmitError::InvalidSlot)?;
        if lane.is_some() {
            return Err(SubmitError::Busy);
        }
        let id = self
            .last_id
            .checked_add(1)
            .ok_or(SubmitError::IdExhausted)?;
        *lane = Some(request(id));
        self.last_id = id;
        Ok(id)
    }
    pub fn pending(&self) -> impl Iterator<Item = &R> {
        self.slots.iter().flatten()
    }
    pub fn history(&self) -> &VecDeque<O> {
        &self.history
    }
    pub fn record(&mut self, outcome: O) {
        if self.retention == 0 {
            return;
        }
        if self.history.len() == self.retention {
            self.history.pop_front();
        }
        self.history.push_back(outcome);
    }
    pub fn clear_history(&mut self) {
        self.history.clear();
    }
    pub fn last_id(&self) -> u64 {
        self.last_id
    }
    pub fn is_closed(&self) -> bool {
        self.closed
    }
    pub fn close(&mut self) {
        self.closed = true;
    }
}
impl<R, O, const S: usize> Index<usize> for CommandBook<R, O, S> {
    type Output = Option<R>;
    fn index(&self, index: usize) -> &Self::Output {
        &self.slots[index]
    }
}
impl<R, O, const S: usize> IndexMut<usize> for CommandBook<R, O, S> {
    fn index_mut(&mut self, index: usize) -> &mut Self::Output {
        &mut self.slots[index]
    }
}
/// Ordered inbound requests sharing the lane book's IDs, history and close state.
/// Popping releases capacity; the caller executes/finalizes popped work before
/// closing at its runtime boundary. Synchronize submission and close externally.
pub struct FifoCommands<R, O, const N: usize> {
    book: CommandBook<R, O, N>,
    order: VecDeque<usize>,
}
impl<R, O, const N: usize> FifoCommands<R, O, N> {
    pub fn new(retention: usize) -> Self {
        Self {
            book: CommandBook::new(retention),
            order: VecDeque::new(),
        }
    }
    pub fn submit(&mut self, request: impl FnOnce(u64) -> R) -> Result<u64, SubmitError> {
        if self.book.is_closed() {
            return Err(SubmitError::Closed);
        }
        let slot = (0..N)
            .find(|&i| self.book[i].is_none())
            .ok_or(SubmitError::Busy)?;
        let id = self.book.submit(slot, request)?;
        self.order.push_back(slot);
        Ok(id)
    }
    pub fn pop(&mut self) -> Option<R> {
        let slot = self.order.pop_front()?;
        self.book[slot].take()
    }
    pub fn record(&mut self, outcome: O) {
        self.book.record(outcome);
    }
    pub fn history(&self) -> &VecDeque<O> {
        self.book.history()
    }
    pub fn is_closed(&self) -> bool {
        self.book.is_closed()
    }
    /// Stop acceptance and give every still-queued request a terminal outcome.
    pub fn close_with(&mut self, mut cancel: impl FnMut(R) -> O) {
        self.book.close();
        while let Some(request) = self.pop() {
            self.record(cancel(request));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fifo_reused_lanes_preserve_submission_order_and_close_finalizes_queue() {
        let mut queue = FifoCommands::<u64, u64, 2>::new(3);
        assert_eq!(queue.submit(|id| id), Ok(1));
        assert_eq!(queue.submit(|id| id), Ok(2));
        assert_eq!(queue.submit(|id| id), Err(SubmitError::Busy));
        let first = queue.pop().unwrap();
        queue.record(first);
        assert_eq!(queue.submit(|id| id), Ok(3));
        queue.close_with(|id| id);
        assert_eq!(
            queue.history().iter().copied().collect::<Vec<_>>(),
            [1, 2, 3]
        );
        assert_eq!(queue.submit(|id| id), Err(SubmitError::Closed));
        assert!(queue.pop().is_none());
    }
    #[test]
    fn lanes_preserve_ids_bounded_history_and_shutdown_finalization() {
        let mut book = CommandBook::<u64, u64, 2>::new(2);
        assert_eq!(book.submit(0, |id| id), Ok(1));
        assert_eq!(book.submit(0, |id| id), Err(SubmitError::Busy));
        assert_eq!(book.submit(1, |id| id), Ok(2));
        assert_eq!(book.submit(2, |id| id), Err(SubmitError::InvalidSlot));
        book.close();
        assert_eq!(book.submit(0, |id| id), Err(SubmitError::Closed));
        for lane in 0..2 {
            let request = book[lane].take().unwrap();
            book.record(request);
        }
        book.record(3);
        assert_eq!(book.history().iter().copied().collect::<Vec<_>>(), [2, 3]);
        assert_eq!(book.pending().count(), 0);
        let mut book = CommandBook::<u64, (), 1>::new(0);
        book.last_id = u64::MAX;
        assert_eq!(book.submit(0, |id| id), Err(SubmitError::IdExhausted));
        assert!(book[0].is_none());
    }
}
