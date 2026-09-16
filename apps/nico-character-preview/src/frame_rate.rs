//! Wall-clock Update cadence, not CPU execution time or GPU presentation rate.
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug)]
struct Reading {
    samples: u64,
    elapsed: Duration,
    min: Duration,
    max: Duration,
}

#[derive(Default)]
pub struct FrameRate {
    previous: Option<Instant>,
    pending: Option<Reading>,
    published: Option<Reading>,
}

impl FrameRate {
    /// Publish once at least one second of complete Update intervals is available.
    /// Long intervals remain visible, including stalls or time spent suspended.
    pub fn observe(&mut self, now: Instant) {
        let Some(previous) = self.previous.replace(now) else {
            return;
        };
        let delta = now.duration_since(previous);
        if delta.is_zero() {
            return;
        }
        let reading = self.pending.get_or_insert(Reading {
            samples: 0,
            elapsed: Duration::ZERO,
            min: delta,
            max: delta,
        });
        reading.samples += 1;
        reading.elapsed += delta;
        reading.min = reading.min.min(delta);
        reading.max = reading.max.max(delta);
        if reading.elapsed >= Duration::from_secs(1) {
            self.published = self.pending.take();
        }
    }

    pub fn hud(&self) -> String {
        self.published.map_or_else(
            || "UPDATE FPS WARMING UP".into(),
            |r| {
                format!(
                    "UPDATE FPS {:.0}  AVG {:.0} MS  MAX {:.0} MS",
                    r.samples as f64 / r.elapsed.as_secs_f64(),
                    r.elapsed.as_secs_f64() * 1000. / r.samples as f64,
                    r.max.as_secs_f64() * 1000.,
                )
            },
        )
    }

    pub fn json(&self) -> serde_json::Value {
        self.published.map_or(serde_json::Value::Null, |r| {
            serde_json::json!({
                "update_fps": r.samples as f64 / r.elapsed.as_secs_f64(),
                "mean_frame_ms": r.elapsed.as_secs_f64() * 1000. / r.samples as f64,
                "min_frame_ms": r.min.as_secs_f64() * 1000.,
                "max_frame_ms": r.max.as_secs_f64() * 1000.,
                "sample_count": r.samples,
                "window_seconds": r.elapsed.as_secs_f64(),
            })
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reports_elapsed_weighted_fps_and_interval_extremes() {
        let mut rate = FrameRate::default();
        let mut now = Instant::now();
        rate.observe(now);
        assert!(rate.json().is_null());
        for _ in 0..25 {
            now += Duration::from_millis(10);
            rate.observe(now);
            now += Duration::from_millis(30);
            rate.observe(now);
        }
        let r = rate.json();
        assert_eq!(r["update_fps"], 50.);
        assert_eq!(r["mean_frame_ms"], 20.);
        assert_eq!(r["min_frame_ms"], 10.);
        assert_eq!(r["max_frame_ms"], 30.);
        assert_eq!(r["sample_count"], 50);
        assert_eq!(r["window_seconds"], 1.);
    }

    #[test]
    fn ignores_zero_intervals_and_reports_stalls_in_the_next_window() {
        let mut rate = FrameRate::default();
        let now = Instant::now();
        rate.observe(now);
        rate.observe(now);
        assert!(rate.json().is_null());
        rate.observe(now + Duration::from_secs(2));
        assert_eq!(rate.json()["update_fps"], 0.5);
        assert_eq!(rate.json()["max_frame_ms"], 2000.);
        rate.observe(now + Duration::from_secs(3));
        assert_eq!(rate.json()["sample_count"], 1);
        assert_eq!(rate.json()["max_frame_ms"], 1000.);
    }
}
