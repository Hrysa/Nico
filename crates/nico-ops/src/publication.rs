//! Owned snapshot storage with explicit freshness. Embed under the caller's lock
//! to publish payload and command outcomes atomically. No world or transport access.
use std::time::{Duration, Instant};
pub struct Publication<T> {
    value: Option<T>,
    sequence: u64,
    at: Option<Instant>,
    closed: bool,
}
impl<T> Default for Publication<T> {
    fn default() -> Self {
        Self {
            value: None,
            sequence: 0,
            at: None,
            closed: false,
        }
    }
}
impl<T> Publication<T> {
    /// Returns false after close or if the sequence space is exhausted.
    pub fn publish(&mut self, value: T) -> bool {
        let Some(next) = self.sequence.checked_add(1) else {
            return false;
        };
        if self.closed {
            return false;
        }
        self.value = Some(value);
        self.sequence = next;
        self.at = Some(Instant::now());
        true
    }
    pub fn get(&self) -> Option<&T> {
        self.value.as_ref()
    }
    pub fn sequence(&self) -> u64 {
        self.sequence
    }
    pub fn age(&self) -> Option<Duration> {
        self.at.map(|at| at.elapsed())
    }
    pub fn is_closed(&self) -> bool {
        self.closed
    }
    /// Retains the final payload and its original publication time.
    pub fn close(&mut self) {
        self.closed = true;
    }
}
#[cfg(feature = "mcp")]
impl Publication<serde_json::Value> {
    /// Clone an object payload and add reserved publication metadata fields.
    pub fn json(&self) -> Option<serde_json::Value> {
        let mut value = self.get()?.clone();
        let object = value.as_object_mut()?;
        object.insert("snapshot_sequence".into(), self.sequence().into());
        object.insert(
            "snapshot_age_ms".into(),
            (self.age()?.as_millis().min(u64::MAX as u128) as u64).into(),
        );
        object.insert("closed".into(), self.is_closed().into());
        Some(value)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reading_and_closing_do_not_refresh_stale_snapshots() {
        let mut published = Publication::default();
        assert!(published.get().is_none());
        assert!(published.publish(vec![1]));
        published.at = Some(Instant::now() - Duration::from_secs(2));
        let copy = published.get().unwrap().clone();
        assert!(published.age().unwrap() >= Duration::from_secs(2));
        published.close();
        assert!(!published.publish(vec![2]));
        assert_eq!(published.get().unwrap(), &copy);
        assert_eq!(published.sequence(), 1);
        assert!(published.age().unwrap() >= Duration::from_secs(2));
    }
}
