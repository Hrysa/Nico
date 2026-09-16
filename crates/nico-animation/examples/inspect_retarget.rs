//! Headless RPG-to-Mixamo inspection using local assets; no game process is touched.
use glam::{Mat4, Vec3};
use nico_animation::{
    Playback, Pose,
    humanoid::{HumanoidProfile, HumanoidRig, RootMotion},
    sample,
};
use nico_assets::{
    import::{AssetImporter, ImportBudget, ImportContext},
    importers::{ModelGlbImporter, ModelGlbSettings},
    model::Model,
};
use std::sync::Arc;
use std::{fs::File, io::Read, path::Path};

fn load(path: &Path) -> Result<Arc<Model>, Box<dyn std::error::Error>> {
    let mut bytes = Vec::new();
    File::open(path)?
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
    Ok(Arc::new(ModelGlbImporter.import(
        &mut context,
        &ModelGlbSettings {
            allow_material_fallback: true,
            ..Default::default()
        },
    )?))
}
fn points(pose: &Pose<'_>) -> Result<Vec<Vec3>, Box<dyn std::error::Error>> {
    let model = pose.model();
    let globals = pose.globals()?;
    let mut points = Vec::new();
    for (i, node) in model.data().nodes.iter().enumerate() {
        let Some(mesh) = node.mesh else {
            continue;
        };
        let palette = if node.skin.is_some() {
            pose.skin_matrices(i)?
                .iter()
                .map(Mat4::from_cols_array_2d)
                .collect::<Vec<_>>()
        } else {
            Vec::new()
        };
        for p in &model.data().meshes[mesh].primitives {
            for v in &p.vertices {
                let position = if p.skinned {
                    v.joints
                        .iter()
                        .zip(v.weights)
                        .fold(Vec3::ZERO, |a, (&joint, weight)| {
                            a + palette[joint as usize].transform_point3(Vec3::from(v.position))
                                * weight
                        })
                } else {
                    Vec3::from(v.position)
                };
                let point = globals[i].transform_point3(position);
                if !point.is_finite() {
                    return Err("nonfinite skinned position".into());
                }
                points.push(point);
            }
        }
    }
    Ok(points)
}
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let paths: Vec<_> = std::env::args_os().skip(1).collect();
    if paths.len() < 2 {
        return Err("usage: inspect_retarget MIXAMO.glb RPG.glb ...".into());
    }
    let target = load(Path::new(&paths[0]))?;
    let target_rig = HumanoidRig::new(target.clone(), HumanoidProfile::mixamo())?;
    let rest = points(&Pose::rest(&target))?;
    for path in &paths[1..] {
        let source = load(Path::new(path))?;
        let rig = HumanoidRig::new(source.clone(), HumanoidProfile::rpg())?;
        for (clip_index, clip) in source.data().clips.iter().enumerate() {
            let (start, end) = clip.time_range();
            let mut low = Vec3::splat(f32::INFINITY);
            let mut high = Vec3::splat(f32::NEG_INFINITY);
            let mut max_change = 0f32;
            let mut max_root = 0f32;
            for frame in 0..=8 {
                let pose = sample(
                    &source,
                    clip_index,
                    (end - start) * frame as f32 / 8.,
                    Playback::Clamp,
                )?;
                let motion = rig.capture(&pose)?;
                max_root = max_root.max(Vec3::from(motion.root_displacement()).length());
                let target_pose = target_rig.apply(&motion, RootMotion::InPlace)?;
                for (point, reference) in points(&target_pose)?.iter().zip(&rest) {
                    low = low.min(*point);
                    high = high.max(*point);
                    max_change = max_change.max(point.distance(*reference));
                }
            }
            let mut hand_peaks = serde_json::Map::new();
            for name in ["mixamorig:LeftHand", "mixamorig:RightHand"] {
                if let Some(index) = target
                    .data()
                    .nodes
                    .iter()
                    .position(|node| node.name == name)
                {
                    let mut best = (f32::NEG_INFINITY, 0., Mat4::IDENTITY);
                    for frame in 0..=120 {
                        let time = (end - start) * frame as f32 / 120.;
                        let pose = sample(&source, clip_index, time, Playback::Clamp)?;
                        let motion = rig.capture(&pose)?;
                        let matrix =
                            target_rig.apply(&motion, RootMotion::InPlace)?.globals()?[index];
                        if matrix.w_axis.z > best.0 {
                            best = (matrix.w_axis.z, time, matrix);
                        }
                    }
                    hand_peaks.insert(name.into(), serde_json::json!({"time":best.1,"position":best.2.w_axis.truncate().to_array(),"matrix":best.2.to_cols_array_2d()}));
                }
            }
            println!(
                "{}",
                serde_json::json!({"hand_forward_peaks":hand_peaks,"file":path.to_string_lossy(),"clip":clip_index,"source_bones":rig.report().mapped.len(),"target_bones":target_rig.report().mapped.len(),"samples":9,"vertices_per_sample":rest.len(),"bounds_min":low.to_array(),"bounds_max":high.to_array(),"max_vertex_change_from_bind":max_change,"max_normalized_root_motion":max_root,"source_leg_length":rig.leg_length(),"target_leg_length":target_rig.leg_length()})
            );
        }
    }
    Ok(())
}
