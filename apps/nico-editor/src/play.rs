//! UI composition of the engine-owned play worker; no child processes live here.
use crate::{core::Core, operations::Action};
use nico_launch::play::{PlayProfile, PlaySession as Worker};
use serde_json::{Value, json};

#[derive(Default)]
pub struct PlaySession {
    worker: Option<Worker>,
    restart: Option<PlayProfile>,
    panel: bool,
    bridge: String,
    endpoint: String,
    token_file: String,
    release: bool,
    error: Option<String>,
}
impl PlaySession {
    pub fn update(&mut self, core: &mut Core) {
        if let Some(start) = core.play_requested.take() {
            self.error = None;
            if start {
                self.panel = true;
                self.bridge = core.play_profile.bridge.to_string();
                self.endpoint = core.play_profile.editor_endpoint.to_string();
                self.token_file = core.play_profile.endpoint_token_file.display().to_string();
                self.release = core.play_profile.release;
                if core.restart_requested {
                    core.restart_requested = false;
                    self.restart = Some(core.play_profile.clone());
                    if let Some(worker) = &self.worker {
                        worker.stop();
                    }
                } else if let Err(error) = self.start(core.play_profile.clone()) {
                    self.error = Some(error.to_string());
                }
            } else {
                self.restart = None;
                if let Some(worker) = &self.worker {
                    worker.stop();
                }
            }
        }
        let state = self.snapshot();
        if state["active"] != true
            && let Some(profile) = self.restart.take()
            && let Err(error) = self.start(profile)
        {
            self.error = Some(error.to_string());
        }
        core.play_state = self.snapshot();
        core.playing = core.play_state["active"] == true || self.restart.is_some();
        core.play_error = self
            .error
            .clone()
            .or_else(|| core.play_state["error"].as_str().map(str::to_owned));
    }
    fn start(&mut self, profile: PlayProfile) -> std::io::Result<()> {
        if self.worker.is_none() {
            self.worker = Some(Worker::new()?);
        }
        self.worker.as_ref().unwrap().start(profile)?;
        Ok(())
    }
    fn snapshot(&self) -> Value {
        self.worker
            .as_ref()
            .map_or_else(|| json!({"phase":"idle","active":false}), Worker::snapshot)
    }
    pub fn shutdown(&mut self) {
        self.restart = None;
        if let Some(worker) = &mut self.worker {
            worker.shutdown();
        }
    }
    pub fn ui(&mut self, ui: &mut egui::Ui, core: &Core) {
        if ui.button("Play profile").clicked() {
            self.panel = true;
            self.bridge = core.play_profile.bridge.to_string();
            self.endpoint = core.play_profile.editor_endpoint.to_string();
            self.token_file = core.play_profile.endpoint_token_file.display().to_string();
            self.release = core.play_profile.release;
        }
        egui::Window::new("Client / Server Play")
            .open(&mut self.panel)
            .show(ui.ctx(), |ui| {
                ui.label(format!(
                    "{} • client/server profile",
                    core.definition.manifest.name
                ));
                ui.add_enabled_ui(!core.playing, |ui| {
                    ui.label("Game registration endpoint");
                    ui.text_edit_singleline(&mut self.bridge);
                    ui.label("Editor RPC endpoint");
                    ui.text_edit_singleline(&mut self.endpoint);
                    ui.label("Endpoint token file");
                    ui.text_edit_singleline(&mut self.token_file);
                    ui.checkbox(&mut self.release, "Release build");
                    if ui.button("Apply profile").clicked() {
                        match (self.bridge.parse(), self.endpoint.parse()) {
                            (Ok(bridge), Ok(editor_endpoint)) => {
                                let action = Action::ConfigurePlay {
                                    bridge,
                                    editor_endpoint,
                                    endpoint_token_file: self.token_file.clone().into(),
                                    release: self.release,
                                };
                                let _ = core.queue.lock().unwrap().submit(|id| (id, action));
                                self.error = None;
                            }
                            _ => self.error = Some("Enter valid endpoint addresses".into()),
                        }
                    }
                });
                ui.horizontal(|ui| {
                    for (label, action) in [
                        ("Restart saved", Action::Restart),
                        ("Save and restart", Action::SaveAndRestart),
                    ] {
                        if ui.button(label).clicked() {
                            let _ = core.queue.lock().unwrap().submit(|id| (id, action));
                        }
                    }
                });
                ui.label(format!(
                    "Phase: {}",
                    core.play_state["phase"].as_str().unwrap_or("idle")
                ));
                for role in ["server", "client"] {
                    let host = &core.play_state[role];
                    if host.is_object() {
                        ui.label(format!(
                            "{role}: PID {} • connected {} • ready {}",
                            host["pid"], host["connected"], host["ready"]
                        ));
                        if let Some(id) = host["instance_id"].as_str() {
                            ui.monospace(id);
                        }
                    }
                }
                for field in ["content_revision", "server_data"] {
                    if let Some(value) = core.play_state[field].as_str() {
                        ui.label(field);
                        ui.monospace(value);
                    }
                }
                if let Some(error) = &core.play_error {
                    ui.colored_label(egui::Color32::LIGHT_RED, error);
                }
            });
    }
}
