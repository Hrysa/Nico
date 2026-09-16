//! Headless inspection of user-supplied GLBs without installing a runtime.
use nico_assets::{
    import::{AssetImporter, ImportBudget, ImportContext},
    importers::{ModelGlbImporter, ModelGlbSettings},
};
use std::{fs::File, io::Read};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let paths: Vec<_> = std::env::args_os().skip(1).collect();
    if paths.is_empty() {
        return Err("usage: inspect_model FILE.glb ...".into());
    }
    for path in paths {
        let mut bytes = Vec::new();
        File::open(&path)?
            .take(64 * 1024 * 1024 + 1)
            .read_to_end(&mut bytes)?;
        let mut context = ImportContext::new(
            &bytes,
            ImportBudget {
                max_input_bytes: 64 * 1024 * 1024,
                max_decoded_bytes: 128 * 1024 * 1024,
            },
            &|| false,
        )?;
        let model = ModelGlbImporter.import(
            &mut context,
            &ModelGlbSettings {
                allow_material_fallback: true,
                ..Default::default()
            },
        )?;
        let d = model.data();
        println!(
            "{}",
            serde_json::json!({"file":path.to_string_lossy(),"nodes":d.nodes.len(),"meshes":d.meshes.len(),"materials":d.materials.len(),"images":d.images.len(),"skins":d.skins.iter().map(|s|s.joints.len()).collect::<Vec<_>>(),"clips":d.clips.iter().map(|c|serde_json::json!({"name":c.name,"range":c.time_range(),"tracks":c.tracks.len()})).collect::<Vec<_>>(),"omitted_extensions":d.omitted_extensions})
        );
    }
    Ok(())
}
