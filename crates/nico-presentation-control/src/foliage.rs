//! Owner-thread visual clock and field publication, independent of simulation time.
use nico_presentation::foliage::{InfluenceSnapshot, WorldInfluence};
use std::{sync::Arc, time::Duration};

/// Limits visual time to a finite, reproducible range (about 31 years).
pub const MAX_VISUAL_SECONDS: f64 = 1_000_000_000.;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum VisualTimeCommand {
    SetPaused(bool),
    Seek(f64),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FoliageControlError {
    InvalidTime,
    InvalidFields,
}
impl std::fmt::Display for FoliageControlError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "invalid foliage control: {self:?}")
    }
}
impl std::error::Error for FoliageControlError {}

/// Providers retain fields across seeks, including expired ones, so seeking back
/// restores the same influence. Replacements are validated before publication.
/// Call from the presentation owner; tooling must queue commands to that owner.
pub struct FoliageController {
    paused: bool,
    fields: Vec<WorldInfluence>,
    snapshot: Arc<InfluenceSnapshot>,
}
impl Default for FoliageController {
    fn default() -> Self {
        Self {
            paused: false,
            fields: Vec::new(),
            snapshot: Arc::new(InfluenceSnapshot::new(0., Vec::new()).unwrap()),
        }
    }
}
impl FoliageController {
    pub fn paused(&self) -> bool {
        self.paused
    }
    pub fn time(&self) -> f64 {
        self.snapshot.time()
    }
    pub fn snapshot(&self) -> Arc<InfluenceSnapshot> {
        self.snapshot.clone()
    }

    pub fn apply(&mut self, command: VisualTimeCommand) -> Result<(), FoliageControlError> {
        match command {
            VisualTimeCommand::SetPaused(paused) => {
                self.paused = paused;
                Ok(())
            }
            VisualTimeCommand::Seek(time) => self.publish_time(time),
        }
    }

    /// Elapsed time is supplied by the owner, never read from a global clock.
    /// Pause and zero elapsed preserve Arc identity for editor pixel reuse.
    pub fn advance(&mut self, elapsed: Duration) -> Result<(), FoliageControlError> {
        if self.paused || elapsed.is_zero() {
            return Ok(());
        }
        self.publish_time(self.time() + elapsed.as_secs_f64())
    }

    pub fn replace_fields(
        &mut self,
        fields: Vec<WorldInfluence>,
    ) -> Result<(), FoliageControlError> {
        if fields.len() > nico_presentation::foliage::MAX_WORLD_INFLUENCES {
            return Err(FoliageControlError::InvalidFields);
        }
        let snapshot = InfluenceSnapshot::new(self.time(), fields.clone())
            .ok_or(FoliageControlError::InvalidFields)?;
        self.fields = fields;
        self.snapshot = Arc::new(snapshot);
        Ok(())
    }

    fn publish_time(&mut self, time: f64) -> Result<(), FoliageControlError> {
        if !time.is_finite() || !(0. ..=MAX_VISUAL_SECONDS).contains(&time) {
            return Err(FoliageControlError::InvalidTime);
        }
        if time != self.time() {
            self.snapshot = Arc::new(
                InfluenceSnapshot::new(time, self.fields.clone())
                    .expect("retained fields were validated at replacement"),
            );
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_presentation::{InstanceBounds, foliage::InfluenceKind};
    fn field() -> WorldInfluence {
        WorldInfluence::new(
            1,
            InfluenceKind::RadialBend,
            [0.; 3],
            [1., 0., 0.],
            2.,
            1.,
            0.,
            2.,
        )
        .unwrap()
    }
    #[test]
    fn pause_retains_snapshot_seek_restores_expired_fields_and_failures_retain_last_good() {
        let mut controller = FoliageController::default();
        controller.replace_fields(vec![field()]).unwrap();
        let initial = controller.snapshot();
        controller.advance(Duration::ZERO).unwrap();
        assert!(Arc::ptr_eq(&initial, &controller.snapshot()));
        controller
            .apply(VisualTimeCommand::SetPaused(true))
            .unwrap();
        controller.advance(Duration::from_secs(5)).unwrap();
        assert!(Arc::ptr_eq(&initial, &controller.snapshot()));
        controller.apply(VisualTimeCommand::Seek(3.)).unwrap();
        let bounds = InstanceBounds::new([-1.; 3], [1.; 3]).unwrap();
        assert!(controller.snapshot().for_chunk(bounds).fields().is_empty());
        controller.apply(VisualTimeCommand::Seek(0.)).unwrap();
        assert_eq!(controller.snapshot().for_chunk(bounds).fields().len(), 1);
        let before = controller.snapshot();
        assert_eq!(
            controller.apply(VisualTimeCommand::Seek(f64::NAN)),
            Err(FoliageControlError::InvalidTime)
        );
        assert_eq!(
            controller.replace_fields(vec![field(), field()]),
            Err(FoliageControlError::InvalidFields)
        );
        assert!(Arc::ptr_eq(&before, &controller.snapshot()));
        assert!(controller.paused());
        controller
            .apply(VisualTimeCommand::SetPaused(false))
            .unwrap();
        controller.advance(Duration::from_millis(250)).unwrap();
        assert_eq!(controller.time(), 0.25);
    }
    #[test]
    fn time_overflow_rejects_before_publication() {
        let mut controller = FoliageController::default();
        controller
            .apply(VisualTimeCommand::Seek(MAX_VISUAL_SECONDS))
            .unwrap();
        let snapshot = controller.snapshot();
        assert_eq!(
            controller.advance(Duration::from_secs(1)),
            Err(FoliageControlError::InvalidTime)
        );
        assert!(Arc::ptr_eq(&snapshot, &controller.snapshot()));
    }
}
