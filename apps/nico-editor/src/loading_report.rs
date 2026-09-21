//! Bounded operation diagnostics, independent of the editor runtime publication.
//! These are loading progress and elapsed wall times, not CPU profiling data.
use nico_assets::progress::{ProgressObserver, ProgressUpdate, observe_progress};
use serde_json::{Value, json};
use std::{
    path::Path,
    sync::{Arc, Mutex},
    time::Instant,
};

const PHASE_LIMIT: usize = 32;
#[derive(Clone, Default)]
pub struct LoadingReport(Arc<Mutex<Reports>>);
#[derive(Default)]
struct Reports {
    catalog: Option<nico_assets::watch::CatalogReader>,
    startup: Option<Record>,
    refresh: Option<Record>,
    first_scene_presented_ms: Option<f64>,
}
struct Record {
    project: String,
    command_id: Option<u64>,
    started: Instant,
    elapsed_ms: Option<f64>,
    error: Option<String>,
    phases: Vec<ProgressUpdate>,
    current: Option<ProgressUpdate>,
    updated: Instant,
    truncated: bool,
}
impl Record {
    fn json(&self) -> Value {
        let mut current = self.current.clone();
        if let Some(p) = &mut current {
            p.elapsed_ms += self.updated.elapsed().as_secs_f64() * 1000.;
        }
        json!({"project":self.project,"command_id":self.command_id,
            "state":if self.elapsed_ms.is_none() { "running" } else if self.error.is_some() { "failed" } else { "complete" },
            "elapsed_ms":self.elapsed_ms.unwrap_or_else(|| self.started.elapsed().as_secs_f64() * 1000.),
            "error":self.error,"phases":self.phases,"current":current,"truncated":self.truncated})
    }
}
impl LoadingReport {
    pub fn snapshot(&self) -> Value {
        let reports = self.0.lock().unwrap();
        let catalog = reports.catalog.as_ref().map(|r| {
            let s = r.snapshot();
            json!({"sources":s.assets.len(),"metadata_cached":s.assets.values().filter(|a| a.cached).count(),
                "cpu_loaded":s.assets.values().filter(|a| a.value.is_some()).count(),
                "failed":s.assets.values().filter(|a| a.error.is_some()).count(),
                "content_loads":s.imports,"scans":s.scans,"current":s.importing,"error":s.error})
        });
        json!({"schema_version":1,"process_id":std::process::id(),
            "timing":"elapsed_wall_time_not_cpu_or_gpu",
            "startup":reports.startup.as_ref().map(Record::json),
            "refresh":reports.refresh.as_ref().map(Record::json),
            "first_scene_presented_ms":reports.first_scene_presented_ms,
            "catalog":catalog,
            "phase_limit":PHASE_LIMIT})
    }
    pub fn catalog(&self, reader: nico_assets::watch::CatalogReader) {
        self.0.lock().unwrap().catalog = Some(reader);
    }
    pub fn begin(&self, project: &Path, command_id: Option<u64>) -> LoadingOperation {
        self.begin_observed(project, command_id, |_| {})
    }
    pub fn begin_observed(
        &self,
        project: &Path,
        command_id: Option<u64>,
        observe: impl Fn(ProgressUpdate) + Send + Sync + 'static,
    ) -> LoadingOperation {
        let startup = command_id.is_none();
        let record = Record {
            project: project.display().to_string(),
            command_id,
            started: Instant::now(),
            elapsed_ms: None,
            error: None,
            phases: Vec::new(),
            current: None,
            updated: Instant::now(),
            truncated: false,
        };
        {
            let mut reports = self.0.lock().unwrap();
            if startup {
                reports.startup = Some(record);
                reports.first_scene_presented_ms = None;
            } else {
                reports.refresh = Some(record);
            }
        }
        let report = self.clone();
        let observer = observe_progress(move |update| {
            observe(update.clone());
            let mut reports = report.0.lock().unwrap();
            let record = if startup {
                &mut reports.startup
            } else {
                &mut reports.refresh
            };
            let Some(record) = record else {
                return;
            };
            record.updated = Instant::now();
            if update.finished {
                record.current = None;
                if record.phases.len() < PHASE_LIMIT {
                    record.phases.push(update);
                } else {
                    record.truncated = true;
                }
            } else {
                record.current = Some(update);
            }
        });
        LoadingOperation {
            report: self.clone(),
            startup,
            observer: Some(observer),
            finished: false,
        }
    }
    pub fn scene_presented(&self) {
        let mut reports = self.0.lock().unwrap();
        if reports.first_scene_presented_ms.is_none() {
            reports.first_scene_presented_ms = reports
                .startup
                .as_ref()
                .filter(|s| s.elapsed_ms.is_some() && s.error.is_none())
                .map(|s| s.started.elapsed().as_secs_f64() * 1000.);
        }
    }
}
pub struct LoadingOperation {
    report: LoadingReport,
    startup: bool,
    observer: Option<ProgressObserver>,
    finished: bool,
}
impl LoadingOperation {
    pub fn finish(mut self, error: Option<String>) {
        self.complete(error);
    }
    fn complete(&mut self, error: Option<String>) {
        self.observer.take();
        let mut reports = self.report.0.lock().unwrap();
        let record = if self.startup {
            &mut reports.startup
        } else {
            &mut reports.refresh
        };
        if let Some(record) = record {
            record.elapsed_ms = Some(record.started.elapsed().as_secs_f64() * 1000.);
            // Preserve the unfinished phase with its last counts on failure.
            if let Some(mut current) = record.current.take() {
                current.elapsed_ms += record.updated.elapsed().as_secs_f64() * 1000.;
                if record.phases.len() < PHASE_LIMIT {
                    record.phases.push(current);
                } else {
                    record.truncated = true;
                }
            }
            record.error = error;
        }
        self.finished = true;
    }
}
impl Drop for LoadingOperation {
    fn drop(&mut self) {
        if !self.finished {
            self.complete(Some("loading operation interrupted".into()));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_assets::progress::ImportProgress;
    #[test]
    fn report_survives_completion_and_keeps_startup_separate_from_refresh() {
        let report = LoadingReport::default();
        let operation = report.begin(Path::new("project"), None);
        let phase = ImportProgress::new("scene preparation", 1).unwrap();
        assert_eq!(
            report.snapshot()["startup"]["current"]["label"],
            "scene preparation"
        );
        phase.complete_one();
        phase.finish();
        operation.finish(None);
        report.scene_presented();
        let before = report.snapshot();
        assert_eq!(before["startup"]["state"], "complete");
        assert!(before["first_scene_presented_ms"].is_number());
        let operation = report.begin(Path::new("project"), Some(7));
        let phase = ImportProgress::new("models", 2).unwrap();
        drop(phase);
        operation.finish(Some("bad model".into()));
        let after = report.snapshot();
        assert_eq!(after["startup"], before["startup"]);
        assert_eq!(after["refresh"]["command_id"], 7);
        assert_eq!(after["refresh"]["state"], "failed");
        assert_eq!(after["refresh"]["phases"][0]["finished"], false);
    }
    #[test]
    fn report_bounds_history_and_marks_interrupted_work() {
        let report = LoadingReport::default();
        let operation = report.begin(Path::new("project"), None);
        for _ in 0..PHASE_LIMIT + 1 {
            ImportProgress::new("phase", 0).unwrap().finish();
        }
        drop(operation);
        let snapshot = report.snapshot();
        assert_eq!(
            snapshot["startup"]["phases"].as_array().unwrap().len(),
            PHASE_LIMIT
        );
        assert_eq!(snapshot["startup"]["truncated"], true);
        assert_eq!(snapshot["startup"]["state"], "failed");
    }
}
