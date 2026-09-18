pub use nico_scene::{Document, Object, relative};
#[derive(Clone, Default)]
pub struct History {
    undo: Vec<Document>,
    redo: Vec<Document>,
}
impl History {
    pub fn record(&mut self, document: Document) {
        if self.undo.len() == 64 {
            self.undo.remove(0);
        }
        self.undo.push(document);
        self.redo.clear();
    }
    pub fn undo(&mut self, document: &mut Document) {
        if let Some(old) = self.undo.pop() {
            self.redo.push(std::mem::replace(document, old));
        }
    }
    pub fn redo(&mut self, document: &mut Document) {
        if let Some(new) = self.redo.pop() {
            self.undo.push(std::mem::replace(document, new));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, path::Path};
    #[test]
    fn saved_scene_reopens_and_can_be_replaced_without_temp_files() {
        let root = tempfile::tempdir().unwrap();
        let mut d = Document::default();
        d.save(root.path()).unwrap();
        assert_eq!(Document::load(root.path()).unwrap(), d);
        d.objects.push(Object {
            id: 1,
            asset: "model.glb".into(),
            name: "Model".into(),
            position: [1., 2., 3.],
            rotation: [0.; 3],
            scale: 2.,
        });
        d.save(root.path()).unwrap();
        assert_eq!(Document::load(root.path()).unwrap(), d);
        assert_eq!(fs::read_dir(root.path()).unwrap().count(), 1);
    }
    #[test]
    fn undo_redo_preserves_document_and_new_edits_drop_redo() {
        let mut h = History::default();
        let mut d = Document::default();
        h.record(d.clone());
        d.objects.push(Object {
            id: 1,
            asset: "model.glb".into(),
            name: "Model".into(),
            position: [0.; 3],
            rotation: [0.; 3],
            scale: 1.,
        });
        let edited = d.clone();
        h.undo(&mut d);
        assert!(d.objects.is_empty());
        h.redo(&mut d);
        assert_eq!(d, edited);
        h.undo(&mut d);
        h.record(d.clone());
        h.redo(&mut d);
        assert!(d.objects.is_empty());
    }
    #[test]
    fn scene_rejects_escape_duplicate_identity_and_nonfinite_transform() {
        assert!(!relative(Path::new("../outside.glb")));
        let o = Object {
            id: 1,
            asset: "model.glb".into(),
            name: "Model".into(),
            position: [0.; 3],
            rotation: [0.; 3],
            scale: 1.,
        };
        let mut d = Document {
            version: 1,
            objects: vec![o.clone(), o],
        };
        assert!(d.validate().is_err());
        d.objects.pop();
        d.objects[0].position[0] = f32::NAN;
        assert!(d.validate().is_err());
    }
}
