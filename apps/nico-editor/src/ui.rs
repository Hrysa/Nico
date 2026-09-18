use crate::{core::Core, document::Object, operations::Action};
use egui::{Color32, RichText};
use egui_dock::{DockArea, DockState, NodeIndex, TabViewer};
use nico_assets::watch::ImportedAsset;
use nico_winit::{NativeClientResult, editor::EditorApplication};
use std::{path::PathBuf, time::Duration};

#[derive(Clone, Debug, Hash)]
enum Tab {
    Scene,
    Hierarchy,
    Assets,
    Inspector,
}
pub struct Editor {
    runtime: nico_runtime::App,
    dock: DockState<Tab>,
    draft: Option<Object>,
    inspected: Option<Object>,
    image: Option<(PathBuf, u64, egui::TextureHandle)>,
    filter: String,
    confirm_close: bool,
    close_after_save: bool,
    discard_and_close: bool,
}
impl Editor {
    pub fn new(runtime: nico_runtime::App) -> Self {
        let mut dock = DockState::new(vec![Tab::Scene]);
        let [scene, _] = dock.main_surface_mut().split_left(
            NodeIndex::root(),
            0.19,
            vec![Tab::Hierarchy, Tab::Assets],
        );
        dock.main_surface_mut()
            .split_right(scene, 0.73, vec![Tab::Inspector]);
        Self {
            runtime,
            dock,
            draft: None,
            inspected: None,
            image: None,
            filter: String::new(),
            confirm_close: false,
            close_after_save: false,
            discard_and_close: false,
        }
    }
}
fn enqueue(core: &Core, action: Action) {
    let _ = core.queue.lock().unwrap().submit(|id| (id, action));
}
impl EditorApplication for Editor {
    fn ui(&mut self, ui: &mut egui::Ui, texture: egui::TextureId) -> egui::Vec2 {
        ui.ctx().set_theme(egui::ThemePreference::Dark);
        ui.painter()
            .rect_filled(ui.max_rect(), 0., Color32::from_rgb(24, 27, 33));
        let core = self.runtime.world().resource::<Core>().unwrap();
        let selected = core
            .document
            .objects
            .iter()
            .find(|o| Some(o.id) == core.selected);
        if selected != self.inspected.as_ref() {
            self.inspected = selected.cloned();
            self.draft = selected.cloned();
        }
        if self.confirm_close {
            egui::Window::new("Unsaved scene")
                .collapsible(false)
                .resizable(false)
                .show(ui.ctx(), |ui| {
                    ui.label("Save your scene before closing?");
                    ui.horizontal(|ui| {
                        if ui.button("Save and close").clicked() {
                            enqueue(core, Action::Save);
                            self.close_after_save = true;
                        }
                        if ui.button("Discard and close").clicked() {
                            self.discard_and_close = true;
                        }
                        if ui.button("Cancel").clicked() {
                            self.confirm_close = false;
                            self.close_after_save = false;
                        }
                    });
                    if let Some(error) = &core.error {
                        ui.colored_label(Color32::LIGHT_RED, error);
                    }
                });
        }
        ui.horizontal(|ui| {
            ui.label(RichText::new("NICO").strong().size(20.));
            ui.separator();
            for (label, action) in [
                ("Save", Action::Save),
                ("Reload", Action::Reload),
                ("Undo", Action::Undo),
                ("Redo", Action::Redo),
                ("Refresh assets", Action::Refresh),
            ] {
                if ui.button(label).clicked() {
                    enqueue(core, action);
                }
            }
            if core.dirty() {
                ui.label(RichText::new("Unsaved changes").color(Color32::YELLOW));
            }
        });
        let snapshot = core.project.snapshot();
        if let Some(adapter) = &core.adapter {
            ui.label(format!(
                "{} — game-provided world authoring",
                core.definition.manifest.name
            ));
            ui.small(adapter.inspect()["note"].as_str().unwrap_or(""));
        }
        if !core.definition.is_declared() {
            ui.colored_label(
                Color32::YELLOW,
                "Loose content folder: no nico.project.toml. Game code and game-specific world files are not loaded.",
            );
            ui.small("Assets can be browsed and placed manually. Save creates a separate scene.nico.json; it does not update the game's world.");
        }
        ui.horizontal(|ui| {
            ui.small(format!(
                "{} assets · {} imports · {} file events",
                snapshot.assets.len(),
                snapshot.imports,
                snapshot.notifications
            ));
            if let Some(error) = core.error.as_ref().or(snapshot.error.as_ref()) {
                ui.colored_label(Color32::LIGHT_RED, error);
            }
        });
        ui.separator();
        let mut viewer = Viewer {
            core,
            texture,
            viewport: egui::vec2(800., 600.),
            draft: &mut self.draft,
            image: &mut self.image,
            filter: &mut self.filter,
        };
        DockArea::new(&mut self.dock)
            .show_close_buttons(false)
            .show_leaf_close_all_buttons(false)
            .show_inside(ui, &mut viewer);
        let viewport = viewer.viewport;
        if snapshot
            .assets
            .values()
            .any(|a| a.value.is_none() && a.error.is_none() && !a.missing)
        {
            egui::Modal::new(egui::Id::new("asset_import")).show(ui.ctx(), |ui| {
                ui.heading("Importing assets");
                let done = snapshot
                    .assets
                    .values()
                    .filter(|a| a.value.is_some() || a.error.is_some())
                    .count();
                ui.add(
                    egui::ProgressBar::new(done as f32 / snapshot.assets.len().max(1) as f32)
                        .text(format!("{done} / {}", snapshot.assets.len())),
                );
                if let Some(path) = &snapshot.importing {
                    ui.label(path.display().to_string());
                }
            });
        }
        viewport
    }
    fn update(&mut self, elapsed: Duration) -> NativeClientResult<nico_presentation::Scene3d> {
        self.runtime.tick(elapsed.min(Duration::from_millis(250)))?;
        Ok(self
            .runtime
            .world()
            .resource::<nico_presentation::Scene3d>()?
            .clone())
    }
    fn shutdown(&mut self) -> NativeClientResult<()> {
        self.runtime.world_mut().resource_mut::<Core>()?.close();
        self.runtime.shutdown()?;
        Ok(())
    }
    fn request_close(&mut self) -> bool {
        if !self.runtime.world().resource::<Core>().unwrap().dirty() {
            return true;
        }
        self.confirm_close = true;
        false
    }
    fn should_close(&self) -> bool {
        self.discard_and_close
            || (self.close_after_save && !self.runtime.world().resource::<Core>().unwrap().dirty())
    }
}
struct Viewer<'a> {
    core: &'a Core,
    texture: egui::TextureId,
    viewport: egui::Vec2,
    draft: &'a mut Option<Object>,
    image: &'a mut Option<(PathBuf, u64, egui::TextureHandle)>,
    filter: &'a mut String,
}
impl TabViewer for Viewer<'_> {
    type Tab = Tab;
    fn id(&mut self, tab: &mut Tab) -> egui::Id {
        egui::Id::new(tab)
    }
    fn title(&mut self, tab: &mut Tab) -> egui::WidgetText {
        match tab {
            Tab::Scene => "Scene",
            Tab::Hierarchy => "Hierarchy",
            Tab::Assets => "Assets",
            Tab::Inspector => "Inspector",
        }
        .into()
    }
    fn closeable(&mut self, _tab: &mut Tab) -> bool {
        false
    }
    fn ui(&mut self, ui: &mut egui::Ui, tab: &mut Tab) {
        match tab {
            Tab::Scene => {
                if self.core.document.objects.is_empty() {
                    ui.label("No scene objects loaded. Open the Assets tab to inspect or place a GLB model.");
                }
                ui.small("Left-drag: orbit · Middle-drag / Shift+left-drag: pan · Scroll: zoom");
                self.viewport = ui.available_size().max(egui::vec2(1., 1.));
                let response = ui.add(
                    egui::Image::new((self.texture, self.viewport)).sense(egui::Sense::drag()),
                );
                let [mut yaw, mut pitch, mut distance] = self.core.camera;
                let mut changed = false;
                if response.dragged() {
                    let delta = response.drag_delta();
                    if response.dragged_by(egui::PointerButton::Middle)
                        || ui.input(|i| i.modifiers.shift)
                    {
                        let offset = pan_offset(
                            self.core.camera_pan,
                            self.core.camera,
                            delta,
                            self.viewport.y,
                        );
                        enqueue(self.core, Action::Pan { offset });
                    } else {
                        yaw -= delta.x * 0.005;
                        pitch = (pitch + delta.y * 0.005).clamp(-1.4, 1.4);
                        changed = delta != egui::Vec2::ZERO;
                    }
                }
                if response.hovered() {
                    let scroll = ui.input(|i| i.smooth_scroll_delta.y);
                    if scroll != 0. {
                        distance = (distance * (-scroll * 0.002).exp()).clamp(0.1, 1000.);
                        changed = true;
                    }
                }
                if changed {
                    enqueue(
                        self.core,
                        Action::Camera {
                            yaw: yaw.clamp(-100., 100.),
                            pitch,
                            distance,
                        },
                    );
                }
            }
            Tab::Hierarchy => {
                ui.heading("Scene objects");
                if self.core.document.objects.is_empty() {
                    ui.label("Open Assets and add a model to begin.");
                }
                egui::ScrollArea::vertical().show(ui, |ui| {
                    for object in &self.core.document.objects {
                        if ui
                            .selectable_label(self.core.selected == Some(object.id), &object.name)
                            .clicked()
                        {
                            enqueue(self.core, Action::Select { id: object.id });
                            *self.draft = Some(object.clone());
                        }
                    }
                });
            }
            Tab::Assets => {
                ui.heading("Project assets");
                ui.text_edit_singleline(self.filter);
                let catalog = self.core.project.snapshot();
                egui::ScrollArea::vertical().show(ui, |ui| {
                    crate::assets_tree::show(
                        ui,
                        &catalog,
                        self.filter,
                        self.core.inspected_asset.as_deref(),
                        &mut |path, add| {
                            enqueue(
                                self.core,
                                if add {
                                    Action::Add { asset: path }
                                } else {
                                    Action::Inspect { asset: path }
                                },
                            );
                        },
                    );
                });
            }
            Tab::Inspector => {
                egui::ScrollArea::vertical().show(ui, |ui| {
                    if let Some(object) = self
                        .core
                        .document
                        .objects
                        .iter()
                        .find(|o| Some(o.id) == self.core.selected)
                    {
                        if self.draft.as_ref().is_none_or(|d| d.id != object.id) {
                            *self.draft = Some(object.clone());
                        }
                        let draft = self.draft.as_mut().unwrap();
                        ui.heading(&object.name);
                        if let Some(adapter) = &self.core.adapter {
                            ui.label(adapter.transform_help(object.id));
                        }
                        ui.small(object.asset.to_string_lossy());
                        for (label, values) in [
                            ("Position", &mut draft.position),
                            ("Rotation (degrees)", &mut draft.rotation),
                        ] {
                            ui.label(label);
                            ui.horizontal(|ui| {
                                for v in values {
                                    ui.add(
                                        egui::DragValue::new(v)
                                            .speed(0.05)
                                            .range(-10000. ..=10000.),
                                    );
                                }
                            });
                        }
                        ui.label("Scale");
                        ui.add(
                            egui::DragValue::new(&mut draft.scale)
                                .speed(0.01)
                                .range(0.001..=1000.),
                        );
                        if ui.button("Apply transform").clicked() {
                            enqueue(
                                self.core,
                                Action::Transform {
                                    id: draft.id,
                                    position: draft.position,
                                    rotation: draft.rotation,
                                    scale: draft.scale,
                                },
                            );
                        }
                        if ui.button("Reset fields").clicked() {
                            *draft = object.clone();
                        }
                        if ui.button("Remove object").clicked() {
                            enqueue(self.core, Action::Remove { id: object.id });
                        }
                        ui.separator();
                    }
                    if let Some(adapter) = &self.core.adapter {
                        ui.collapsing("Game data", |ui| {
                            ui.monospace(
                                serde_json::to_string_pretty(&adapter.inspect())
                                    .unwrap_or_default(),
                            );
                        });
                    }
                    if let Some(path) = self.core.inspected_asset.as_ref() {
                        let catalog = self.core.project.snapshot();
                        if let Some(entry) = catalog.assets.get(path) {
                            ui.heading("Asset");
                            ui.label(path.to_string_lossy());
                            ui.small(format!("Revision {}", entry.revision));
                            if let Some(error) = &entry.error {
                                ui.colored_label(Color32::LIGHT_RED, error);
                                ui.label("Keeping the last successful import.");
                            }
                            match &entry.value {
                                Some(ImportedAsset::Model(bundle)) => {
                                    ui.label(format!(
                                        "{} nodes · {} meshes · {} animations",
                                        bundle.model.data().nodes.len(),
                                        bundle.model.data().meshes.len(),
                                        bundle.model.data().clips.len()
                                    ));
                                    if ui.button("Add to scene").clicked() {
                                        enqueue(
                                            self.core,
                                            Action::Add {
                                                asset: path.clone(),
                                            },
                                        );
                                    }
                                }
                                Some(ImportedAsset::Texture(texture)) => {
                                    if self
                                        .image
                                        .as_ref()
                                        .is_none_or(|(p, r, _)| p != path || *r != entry.revision)
                                    {
                                        let image = egui::ColorImage::from_rgba_unmultiplied(
                                            [texture.width() as usize, texture.height() as usize],
                                            texture.pixels(),
                                        );
                                        *self.image = Some((
                                            path.clone(),
                                            entry.revision,
                                            ui.ctx().load_texture(
                                                "asset preview",
                                                image,
                                                Default::default(),
                                            ),
                                        ));
                                    }
                                    ui.label(format!("{} × {}", texture.width(), texture.height()));
                                    if let Some((_, _, image)) = self.image {
                                        ui.add(
                                            egui::Image::new(&*image)
                                                .max_width(ui.available_width()),
                                        );
                                    }
                                }
                                None => {
                                    ui.label("Waiting for a successful import.");
                                }
                            }
                        }
                    }
                });
            }
        }
    }
}

fn pan_offset(current: [f32; 3], camera: [f32; 3], delta: egui::Vec2, height: f32) -> [f32; 3] {
    let [yaw, pitch, distance] = camera;
    let right = glam::Vec3::new(yaw.cos(), 0., -yaw.sin());
    let up = glam::Vec3::new(
        -yaw.sin() * pitch.sin(),
        pitch.cos(),
        -yaw.cos() * pitch.sin(),
    );
    let units = 2. * distance * (std::f32::consts::FRAC_PI_3 * 0.5).tan() / height.max(1.);
    (glam::Vec3::from(current) + (-right * delta.x + up * delta.y) * units)
        .clamp(glam::Vec3::splat(-10000.), glam::Vec3::splat(10000.))
        .to_array()
}
#[cfg(test)]
mod input_tests {
    use super::*;
    use std::sync::{Arc, Mutex};
    #[test]
    fn viewport_pointer_events_orbit_left_and_pan_with_middle_or_shift_drag() {
        for (button, shift) in [
            (egui::PointerButton::Primary, false),
            (egui::PointerButton::Middle, false),
            (egui::PointerButton::Primary, true),
        ] {
            let root = tempfile::tempdir().unwrap();
            let queue = Arc::new(Mutex::new(crate::operations::Queue::new(32)));
            let publication = Arc::new(Mutex::new(nico_ops::publication::Publication::default()));
            let mut core = Core::new(root.path(), queue, publication).unwrap();
            core.camera = [0., 0., 10.];
            let context = egui::Context::default();
            let modifiers = egui::Modifiers {
                shift,
                ..Default::default()
            };
            for (i, events) in [
                vec![],
                vec![
                    egui::Event::PointerMoved(egui::pos2(200., 200.)),
                    egui::Event::PointerButton {
                        pos: egui::pos2(200., 200.),
                        button,
                        pressed: true,
                        modifiers,
                    },
                ],
                vec![egui::Event::PointerMoved(egui::pos2(240., 210.))],
            ]
            .into_iter()
            .enumerate()
            {
                let input = egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(
                        egui::Pos2::ZERO,
                        egui::vec2(800., 600.),
                    )),
                    events: std::iter::once(egui::Event::ModifiersChanged(modifiers))
                        .chain(events)
                        .collect(),
                    time: Some(i as f64 / 60.),
                    ..Default::default()
                };
                let mut output = context.run_ui(input, |ui| {
                    let mut viewer = Viewer {
                        core: &core,
                        texture: egui::TextureId::User(1),
                        viewport: egui::vec2(800., 600.),
                        draft: &mut None,
                        image: &mut None,
                        filter: &mut String::new(),
                    };
                    viewer.ui(ui, &mut Tab::Scene);
                });
                output.textures_delta.set.clear();
                core.update();
            }
            if button == egui::PointerButton::Primary && !shift {
                assert!(core.camera[0] < 0., "rightward drag must decrease yaw");
                assert_eq!(core.camera_pan, [0.; 3]);
            } else {
                assert_eq!(core.camera, [0., 0., 10.]);
                assert!(
                    core.camera_pan[0] < 0. && core.camera_pan[1] > 0.,
                    "pan must follow pointer in view plane"
                );
            }
        }
    }
}
