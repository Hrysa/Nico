use nico_ops::bridge::{EditorAction, EditorSession};
use serde_json::json;

pub struct DebugPanel {
    pub session: EditorSession,
    open: bool,
    observed_connection: bool,
    address: String,
    token_file: String,
    credential_file: String,
    instance: String,
    api_version: String,
    tool: String,
    arguments: String,
    error: Option<String>,
    result_id: u64,
    result_next: usize,
    result_total: usize,
    result_text: String,
    capture_destination: String,
}

impl DebugPanel {
    pub fn new(session: EditorSession) -> Self {
        Self {
            session,
            open: false,
            observed_connection: false,
            address: "127.0.0.1:47632".into(),
            token_file: String::new(),
            credential_file: String::new(),
            instance: String::new(),
            api_version: String::new(),
            tool: "status".into(),
            arguments: "{}".into(),
            error: None,
            result_id: 0,
            result_next: 0,
            result_total: 0,
            result_text: String::new(),
            capture_destination: String::new(),
        }
    }

    fn submit(&mut self, action: EditorAction) {
        self.error = self
            .session
            .submit(action)
            .err()
            .map(|error| error.to_string());
    }

    fn result_page(&mut self, id: u64, offset: usize) {
        match self.session.result_page(id, offset, 16384) {
            Ok(page) => {
                self.result_id = id;
                self.result_next = page["next_offset"].as_u64().unwrap_or(0) as usize;
                self.result_total = page["total_bytes"].as_u64().unwrap_or(0) as usize;
                let fragment = page["json_fragment"].as_str().unwrap_or("");
                self.result_text = if offset == 0 && page["complete"] == true {
                    serde_json::from_str::<serde_json::Value>(fragment)
                        .ok()
                        .and_then(|value| serde_json::to_string_pretty(&value).ok())
                        .unwrap_or_else(|| fragment.into())
                } else {
                    fragment.into()
                };
            }
            Err(error) => self.error = Some(error.to_string()),
        }
    }

    pub fn ui(&mut self, ui: &mut egui::Ui) {
        if ui.button("Attach / Inspect").clicked() {
            self.open = true;
        }
        let connected = self.session.snapshot()["connected"] == true;
        if connected && !self.observed_connection {
            self.open = true;
        }
        self.observed_connection = connected;
        let mut open = self.open;
        egui::Window::new("Running game inspection").open(&mut open).default_width(580.).show(ui.ctx(), |ui| {
            let snapshot = self.session.snapshot();
            ui.label(format!("Bridge connected: {} · Attached: {} · Pending: {}", snapshot["connected"], snapshot["attached_instance"].as_str().unwrap_or("none"), snapshot["pending"]));
            ui.label("Detach leaves the game running. Inspection shows runtime state; it does not edit your authored scene.");
            ui.horizontal(|ui| { ui.label("Editor endpoint"); ui.text_edit_singleline(&mut self.address); });
            ui.horizontal(|ui| { ui.label("Endpoint token file"); ui.text_edit_singleline(&mut self.token_file); });
            ui.horizontal(|ui| {
                if ui.button("Connect").clicked() {
                    match self.address.parse() {
                        Ok(address) => self.submit(EditorAction::Connect { address, token_file:self.token_file.clone().into() }),
                        Err(_) => self.error = Some("Enter a loopback address and port".into()),
                    }
                }
                if ui.button("Discover").clicked() { self.submit(EditorAction::Discover); }
                if ui.button("Disconnect").clicked() { self.submit(EditorAction::Disconnect); }
            });
            if let Some(outcomes) = snapshot["outcomes"].as_array()
                && let Some(instances) = outcomes.iter().rev().find_map(|outcome| outcome["result"]["structuredContent"]["instances"].as_array()) {
                    egui::ComboBox::from_label("Discovered instances").selected_text(if self.instance.is_empty() { "Choose a connected instance" } else { &self.instance }).show_ui(ui, |ui| {
                        for instance in instances {
                            if instance["connected"] != true { continue; }
                            let Some(id) = instance["instance_id"].as_str() else { continue; };
                            let label = format!("{} · {} · PID {} · {}", instance["game"].as_str().unwrap_or("?"), instance["role"].as_str().unwrap_or("?"), instance["pid"], id);
                            if ui.selectable_label(self.instance == id, label).clicked() {
                                self.instance = id.into();
                                self.api_version = instance["api_version"].as_str().unwrap_or("").into();
                            }
                        }
                    });
            }
            ui.horizontal(|ui| { ui.label("Instance ID"); ui.text_edit_singleline(&mut self.instance); });
            ui.horizontal(|ui| { ui.label("API version"); ui.text_edit_singleline(&mut self.api_version); });
            ui.horizontal(|ui| { ui.label("Host credential file"); ui.text_edit_singleline(&mut self.credential_file); });
            ui.horizontal(|ui| {
                if ui.button("Attach").clicked() { self.submit(EditorAction::Attach { instance_id:self.instance.clone(), api_version:self.api_version.clone(), credential_file:self.credential_file.clone().into() }); }
                if ui.button("Detach").clicked() { self.submit(EditorAction::Detach); }
                if ui.button("Inspect tool catalog").clicked() { self.submit(EditorAction::Catalog); }
                if ui.button("Status").clicked() { self.submit(EditorAction::Call { tool_name:"status".into(), arguments:Default::default() }); }
            });
            if ui.button("Inspect entities").clicked() { self.submit(EditorAction::Call { tool_name:"debug_entities".into(), arguments:json!({"limit":8}).as_object().unwrap().clone() }); }
            if let Some(outcomes) = snapshot["outcomes"].as_array()
                && let Some(page) = outcomes.iter().rev().find_map(|outcome| {
                    let value = &outcome["result"]["structuredContent"];
                    (snapshot["attached_instance"].is_string() && outcome["instance_id"] == snapshot["attached_instance"] && value.get("entities").is_some() && value.get("snapshot_id").is_some()).then_some(value)
                }) {
                ui.label(format!("Entity snapshot {} · tick {} · generation {}", page["revision"], page["tick"], page["generation"]));
                if let Some(entities) = page["entities"].as_array() {
                    for entity in entities {
                        if ui.button(format!("Inspect entity {}", entity["reference"]["entity_id"].as_str().unwrap_or("?"))).clicked() {
                            self.submit(EditorAction::Call { tool_name:"debug_entity".into(),arguments:json!({"snapshot_id":page["snapshot_id"],"reference":entity["reference"]}).as_object().unwrap().clone() });
                        }
                    }
                }
                if !page["next_cursor"].is_null() && ui.button("Next entities").clicked() {
                    self.submit(EditorAction::Call { tool_name:"debug_entities".into(),arguments:json!({"limit":8,"cursor":page["next_cursor"]}).as_object().unwrap().clone() });
                }
            }
            ui.horizontal(|ui| {
                ui.label("Capture PNG destination");
                ui.text_edit_singleline(&mut self.capture_destination);
                if ui.add_enabled(!self.capture_destination.is_empty(), egui::Button::new("Capture to file")).clicked() {
                    self.submit(EditorAction::Capture { destination:self.capture_destination.clone().into() });
                }
            });
            ui.separator();
            ui.label("Call a registered operation (host permissions apply)");
            ui.text_edit_singleline(&mut self.tool);
            ui.text_edit_multiline(&mut self.arguments);
            if ui.button("Send").clicked() {
                match serde_json::from_str::<serde_json::Map<String, serde_json::Value>>(&self.arguments) {
                    Ok(arguments) => self.submit(EditorAction::Call { tool_name:self.tool.clone(), arguments }),
                    Err(_) => self.error = Some("Arguments must be a JSON object".into()),
                }
            }
            if let Some(error) = &self.error { ui.colored_label(egui::Color32::LIGHT_RED, error); }
            ui.label("Replies are sampled separately from rendered frames. Command acceptance is not application or process exit.");
            let result = snapshot["outcomes"].as_array().and_then(|items| items.last()).cloned().unwrap_or(json!({}));
            if let Some(id) = result["command_id"].as_u64() && id != self.result_id { self.result_page(id, 0); }
            ui.horizontal(|ui| {
                ui.label(format!("Reply {} · bytes through {} of {}", self.result_id, self.result_next, self.result_total));
                if self.result_total > 16384 {
                    if ui.button("First page").clicked() { self.result_page(self.result_id, 0); }
                    if ui.add_enabled(self.result_next < self.result_total, egui::Button::new("Next page")).clicked() { self.result_page(self.result_id, self.result_next); }
                }
            });
            egui::ScrollArea::vertical().max_height(300.).show(ui, |ui| {
                ui.monospace(&self.result_text);
            });
        });
        self.open = open;
    }
}
