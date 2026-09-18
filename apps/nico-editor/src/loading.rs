//! Open the shell first, then import on a joined worker and adopt at Update.
use crate::{
    core::Core,
    operations::{Published, SharedQueue},
    ui::Editor,
};
use nico_assets::watch::CatalogReader;
use nico_presentation::Scene3d;
use nico_runtime::{AppBuilder, Stage};
use nico_winit::{NativeClientResult, editor::EditorApplication};
use serde_json::json;
use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
    thread::JoinHandle,
    time::Duration,
};

#[derive(Default)]
struct Progress {
    phase: String,
    completed: usize,
    total: usize,
    catalog: Option<CatalogReader>,
}
pub struct LoadingEditor {
    root: PathBuf,
    queue: SharedQueue,
    published: Published,
    registry: Option<nico_authoring::Registry>,
    progress: Arc<Mutex<Progress>>,
    worker: Option<JoinHandle<std::io::Result<Core>>>,
    editor: Option<Editor>,
    first_presented: bool,
    failure: Option<String>,
}
impl LoadingEditor {
    pub fn new(
        root: PathBuf,
        queue: SharedQueue,
        published: Published,
        registry: nico_authoring::Registry,
    ) -> Self {
        Self {
            root,
            queue,
            published,
            registry: Some(registry),
            progress: Arc::new(Mutex::new(Progress {
                phase: "Opening editor".into(),
                ..Default::default()
            })),
            worker: None,
            editor: None,
            first_presented: false,
            failure: None,
        }
    }
    fn start(&mut self) -> std::io::Result<()> {
        let root = self.root.clone();
        let queue = self.queue.clone();
        let published = self.published.clone();
        let registry = self.registry.take().unwrap();
        let progress = self.progress.clone();
        self.progress.lock().unwrap().phase = "Discovering project assets".into();
        self.worker = Some(
            std::thread::Builder::new()
                .name("editor-project-load".into())
                .spawn(move || {
                    let observed = progress.clone();
                    let _observer = nico_assets::progress::observe_progress(move |p| {
                        let mut state = observed.lock().unwrap();
                        state.phase = if p.finished {
                            "Preparing scene".into()
                        } else {
                            p.label
                        };
                        state.completed = p.completed;
                        state.total = if p.finished { 0 } else { p.total };
                    });
                    Core::load(&root, queue, published, &registry, |reader| {
                        progress.lock().unwrap().catalog = Some(reader);
                    })
                })?,
        );
        Ok(())
    }
}
impl EditorApplication for LoadingEditor {
    fn presented(&mut self) {
        self.first_presented = true;
    }
    fn ui(&mut self, ui: &mut egui::Ui, texture: egui::TextureId) -> egui::Vec2 {
        if let Some(editor) = &mut self.editor {
            return editor.ui(ui, texture);
        }
        ui.ctx().set_theme(egui::ThemePreference::Dark);
        ui.heading("NICO");
        ui.label(self.root.display().to_string());
        ui.separator();
        let progress = self.progress.lock().unwrap();
        ui.heading("Assets");
        if let Some(reader) = &progress.catalog {
            let catalog = reader.snapshot();
            egui::ScrollArea::vertical().show(ui, |ui| {
                crate::assets_tree::show(ui, &catalog, "", None, &mut |_, _| {});
            });
        } else {
            ui.label("Waiting for asset discovery…");
        }
        egui::Modal::new(egui::Id::new("project_import")).show(ui.ctx(), |ui| {
            ui.set_min_width(360.);
            if let Some(error) = &self.failure {
                ui.heading("Project could not be opened");
                ui.colored_label(egui::Color32::LIGHT_RED, error);
            } else {
                ui.heading("Importing project");
                ui.label(&progress.phase);
                if progress.total > 0 {
                    ui.add(
                        egui::ProgressBar::new(progress.completed as f32 / progress.total as f32)
                            .text(format!("{} / {}", progress.completed, progress.total)),
                    );
                } else {
                    ui.spinner();
                }
                if let Some(reader) = &progress.catalog {
                    let catalog = reader.snapshot();
                    let ready = catalog
                        .assets
                        .values()
                        .filter(|e| e.value.is_some() || e.error.is_some())
                        .count();
                    ui.label(format!(
                        "Assets: {ready} / {} processed",
                        catalog.assets.len()
                    ));
                    if let Some(path) = &catalog.importing {
                        ui.small(path.display().to_string());
                    }
                }
            }
        });
        egui::vec2(800., 600.)
    }
    fn update(&mut self, elapsed: Duration) -> NativeClientResult<Scene3d> {
        if let Some(editor) = &mut self.editor {
            return editor.update(elapsed);
        }
        if self.first_presented && self.registry.is_some() {
            self.start()?;
        }
        if self.worker.as_ref().is_some_and(|w| w.is_finished()) {
            match self
                .worker
                .take()
                .unwrap()
                .join()
                .unwrap_or_else(|_| Err(std::io::Error::other("project loader panicked")))
            {
                Ok(core) => {
                    let mut builder = AppBuilder::new();
                    builder.insert_resource(core);
                    builder.insert_resource(Scene3d::default());
                    builder.add_system(Stage::Update, "editor::apply_and_extract", |ctx| {
                        let scene = ctx.world.resource_mut::<Core>().unwrap().update();
                        ctx.world.insert_resource(scene);
                        Ok(())
                    });
                    let mut runtime = builder.build()?;
                    runtime.start()?;
                    let mut editor = Editor::new(runtime);
                    let scene = editor.update(elapsed)?;
                    self.editor = Some(editor);
                    return Ok(scene);
                }
                Err(e) => self.failure = Some(e.to_string()),
            }
        }
        let progress = self.progress.lock().unwrap();
        let assets = progress.catalog.as_ref().map(|r| r.snapshot());
        self.published.lock().unwrap().publish(json!({"project":self.root,"loading":self.failure.is_none(),"phase":progress.phase,"completed":progress.completed,"total":progress.total,"assets_discovered":assets.as_ref().map_or(0, |a| a.assets.len()),"error":self.failure,"first_ui_presented":self.first_presented}));
        Ok(Scene3d::default())
    }
    fn shutdown(&mut self) -> NativeClientResult<()> {
        if let Some(editor) = &mut self.editor {
            return editor.shutdown();
        }
        self.queue
            .lock()
            .unwrap()
            .close_with(|(id, _)| json!({"command_id":id,"error":"host_closed"}));
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        self.published.lock().unwrap().close();
        Ok(())
    }
    fn request_close(&mut self) -> bool {
        self.editor.as_mut().is_none_or(|e| e.request_close())
    }
    fn should_close(&self) -> bool {
        self.editor.as_ref().is_some_and(|e| e.should_close())
    }
}

impl Drop for LoadingEditor {
    fn drop(&mut self) {
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn project_loading_waits_for_presented_ui_and_failure_remains_inspectable() {
        let root = tempfile::tempdir().unwrap();
        let queue = Arc::new(Mutex::new(crate::operations::Queue::new(32)));
        let publication = Arc::new(Mutex::new(nico_ops::publication::Publication::default()));
        let mut app = LoadingEditor::new(
            root.path().join("missing"),
            queue.clone(),
            publication.clone(),
            nico_authoring::Registry::default(),
        );
        app.update(Duration::ZERO).unwrap();
        assert!(app.worker.is_none());
        assert!(app.registry.is_some());
        app.presented();
        app.update(Duration::ZERO).unwrap();
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while app.failure.is_none() {
            assert!(std::time::Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(5));
            app.update(Duration::ZERO).unwrap();
        }
        assert!(publication.lock().unwrap().get().unwrap()["error"].is_string());
        app.shutdown().unwrap();
        assert!(queue.lock().unwrap().is_closed());
        assert!(publication.lock().unwrap().is_closed());
    }
}
