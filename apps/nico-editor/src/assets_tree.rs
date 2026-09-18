//! Source hierarchy is available before decoded content is ready.
use nico_assets::watch::{CatalogSnapshot, ImportedAsset};
use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
};

#[derive(Default)]
struct Node {
    children: BTreeMap<String, Node>,
    source: Option<PathBuf>,
}
fn tree<'a>(paths: impl IntoIterator<Item = &'a PathBuf>, filter: &str) -> Node {
    let mut root = Node::default();
    let filter = filter.to_lowercase();
    for path in paths {
        if !path.to_string_lossy().to_lowercase().contains(&filter) {
            continue;
        }
        let mut node = &mut root;
        for component in path.components() {
            node = node
                .children
                .entry(component.as_os_str().to_string_lossy().into_owned())
                .or_default();
        }
        node.source = Some(path.clone());
    }
    root
}
pub fn show(
    ui: &mut egui::Ui,
    catalog: &CatalogSnapshot,
    filter: &str,
    selected: Option<&Path>,
    action: &mut dyn FnMut(PathBuf, bool),
) {
    branch(
        ui,
        &tree(catalog.assets.keys(), filter),
        Path::new(""),
        catalog,
        selected,
        action,
    );
}
fn branch(
    ui: &mut egui::Ui,
    node: &Node,
    parent: &Path,
    catalog: &CatalogSnapshot,
    selected: Option<&Path>,
    action: &mut dyn FnMut(PathBuf, bool),
) {
    for (name, child) in node.children.iter().filter(|(_, n)| n.source.is_none()) {
        let path = parent.join(name);
        egui::CollapsingHeader::new(name)
            .id_salt(&path)
            .default_open(true)
            .show(ui, |ui| branch(ui, child, &path, catalog, selected, action));
    }
    for (name, child) in node.children.iter().filter(|(_, n)| n.source.is_some()) {
        let path = child.source.as_ref().unwrap();
        let entry = &catalog.assets[path];
        let suffix = if entry.missing {
            " (missing)"
        } else if entry.error.is_some() {
            " (failed)"
        } else if catalog.importing.as_ref() == Some(path) {
            " (importing…)"
        } else if entry.value.is_none() {
            " (queued)"
        } else {
            ""
        };
        let mut label = egui::RichText::new(format!("{name}{suffix}"));
        if entry.error.is_some() {
            label = label.color(egui::Color32::LIGHT_RED);
        }
        ui.push_id(path, |ui| {
            let response = ui.selectable_label(selected == Some(path.as_path()), label);
            if response.double_clicked() && matches!(entry.value, Some(ImportedAsset::Model(_))) {
                action(path.clone(), true);
            } else if response.clicked() {
                action(path.clone(), false);
            }
            if let Some(error) = &entry.error {
                response.on_hover_text(error);
            }
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_tree_keeps_same_named_files_distinct_and_search_retains_ancestors() {
        let paths = [
            PathBuf::from("assets/a/cube.glb"),
            PathBuf::from("assets/b/cube.glb"),
            PathBuf::from("assets/a/image.png"),
        ];
        let all = tree(&paths, "");
        assert_eq!(all.children["assets"].children["a"].children.len(), 2);
        assert_ne!(
            all.children["assets"].children["a"].children["cube.glb"].source,
            all.children["assets"].children["b"].children["cube.glb"].source
        );
        let filtered = tree(&paths, "image.PNG");
        assert_eq!(filtered.children["assets"].children.len(), 1);
        assert_eq!(
            filtered.children["assets"].children["a"].children["image.png"]
                .source
                .as_ref(),
            Some(&paths[2])
        );
    }
}
