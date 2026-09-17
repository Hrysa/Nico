use super::*;
use serde_json::{Value, json};

fn fixture() -> (Value, Vec<u8>) {
    let mut blob = Vec::new();
    let mut views = Vec::new();
    let mut accessors = Vec::new();
    let mut floats =
        |values: &[f32], kind: &str, count: usize, min: Option<Value>, max: Option<Value>| {
            let offset = blob.len();
            for v in values {
                blob.extend_from_slice(&v.to_le_bytes());
            }
            views.push(json!({"buffer":0,"byteOffset":offset,"byteLength":values.len()*4}));
            let mut a =
                json!({"bufferView":views.len()-1,"componentType":5126,"count":count,"type":kind});
            if let Some(v) = min {
                a["min"] = v;
            }
            if let Some(v) = max {
                a["max"] = v;
            }
            accessors.push(a);
            accessors.len() - 1
        };
    let pos = floats(
        &[0., 0., 0., 1., 0., 0., 0., 1., 0.],
        "VEC3",
        3,
        Some(json!([0, 0, 0])),
        Some(json!([1, 1, 0])),
    );
    let weights = floats(
        &[1., 0., 0., 0., 1., 0., 0., 0., 1., 0., 0., 0.],
        "VEC4",
        3,
        None,
        None,
    );
    let times = floats(&[1., 2.], "SCALAR", 2, Some(json!([1])), Some(json!([2])));
    let translations = floats(&[0., 0., 0., 0., 0., 2.], "VEC3", 2, None, None);
    let offset = blob.len();
    blob.extend([0u8; 12]);
    views.push(json!({"buffer":0,"byteOffset":offset,"byteLength":12}));
    let joints = accessors.len();
    accessors
        .push(json!({"bufferView":views.len()-1,"componentType":5121,"count":3,"type":"VEC4"}));
    let offset = blob.len();
    blob.extend([0, 1, 2, 0]);
    views.push(json!({"buffer":0,"byteOffset":offset,"byteLength":3}));
    let indices = accessors.len();
    accessors
        .push(json!({"bufferView":views.len()-1,"componentType":5121,"count":3,"type":"SCALAR"}));
    (
        json!({"asset":{"version":"2.0"},"buffers":[{"byteLength":blob.len()}],"bufferViews":views,"accessors":accessors,
        "nodes":[{"name":"Root","children":[1,2]},{"name":"Joint"},{"mesh":0,"skin":0}],"skins":[{"joints":[1],"skeleton":0}],
        "meshes":[{"primitives":[{"attributes":{"POSITION":pos,"JOINTS_0":joints,"WEIGHTS_0":weights},"indices":indices}]}],
        "animations":[{"name":"move","samplers":[{"input":times,"output":translations}],"channels":[{"sampler":0,"target":{"node":1,"path":"translation"}}]}],
        "scenes":[{"nodes":[0]}],"scene":0}),
        blob,
    )
}
fn pack(doc: &Value, blob: &[u8]) -> Vec<u8> {
    let mut json = serde_json::to_vec(doc).unwrap();
    while !json.len().is_multiple_of(4) {
        json.push(b' ');
    }
    let mut bytes = b"glTF".to_vec();
    bytes.extend(2u32.to_le_bytes());
    bytes.extend(((28 + json.len() + blob.len()) as u32).to_le_bytes());
    bytes.extend((json.len() as u32).to_le_bytes());
    bytes.extend(b"JSON");
    bytes.extend(json);
    bytes.extend((blob.len() as u32).to_le_bytes());
    bytes.extend(b"BIN\0");
    bytes.extend(blob);
    bytes
}
fn decode(doc: &Value, blob: &[u8]) -> Result<Model, ImportError> {
    let bytes = pack(doc, blob);
    ModelGlbImporter.import(
        &mut ImportContext::new(&bytes, Default::default(), &|| false).unwrap(),
        &Default::default(),
    )
}
#[test]
fn imports_hierarchy_skin_and_clip_without_runtime() {
    let (doc, blob) = fixture();
    let model = decode(&doc, &blob).unwrap();
    assert_eq!(model.parents(), [None, Some(0), Some(0)]);
    assert_eq!(model.data().skins[0].inverse_bind, [IDENTITY]);
    assert_eq!(model.data().clips[0].time_range(), (1., 2.));
    assert_eq!(
        model.data().meshes[0].primitives[0].vertices[0].weights,
        [1., 0., 0., 0.]
    );
}
#[test]
fn rejects_invalid_graphs_binding_times_and_binary_ranges() {
    let mutations: Vec<fn(&mut Value)> = vec![
        |d| d["nodes"][1]["children"] = json!([0]),
        |d| d["nodes"][0]["children"] = json!([1, 1, 2]),
        |d| d["skins"][0]["joints"] = json!([99]),
        |d| d["accessors"][0]["byteOffset"] = json!(u64::MAX),
        |d| d["bufferViews"][0]["byteStride"] = json!(1),
        |d| d["accessors"][2]["count"] = json!(1),
        |d| {
            d["animations"][0]["channels"]
                .as_array_mut()
                .unwrap()
                .push(json!({"sampler":0,"target":{"node":1,"path":"translation"}}))
        },
        |d| d["nodes"][1]["rotation"] = json!([0, 0, 0, 0]),
        |d| d["nodes"][1]["matrix"] = json!([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]),
        |d| d["animations"][0]["samplers"][0]["interpolation"] = json!("CUBICSPLINE"),
        |d| d["extensionsRequired"] = json!(["KHR_draco_mesh_compression"]),
    ];
    for mutation in mutations {
        let (mut doc, blob) = fixture();
        mutation(&mut doc);
        assert!(decode(&doc, &blob).is_err());
    }
    for (view, offset, value) in [(0, 0, f32::NAN), (1, 0, -1.), (2, 4, 1.)] {
        let (doc, mut blob) = fixture();
        let start = doc["bufferViews"][view]["byteOffset"].as_u64().unwrap() as usize + offset;
        blob[start..start + 4].copy_from_slice(&value.to_le_bytes());
        assert!(decode(&doc, &blob).is_err());
    }
    let (doc, mut blob) = fixture();
    let start = doc["bufferViews"][4]["byteOffset"].as_u64().unwrap() as usize;
    blob[start] = 2;
    assert_eq!(decode(&doc, &blob).unwrap_err().code(), "vertex_joint");
}
#[test]
fn importer_limits_and_optional_material_fallback_are_explicit() {
    let (mut doc, blob) = fixture();
    let bytes = pack(&doc, &blob);
    for settings in [
        ModelGlbSettings {
            max_nodes: 1,
            ..Default::default()
        },
        ModelGlbSettings {
            max_keyframes: 1,
            ..Default::default()
        },
        ModelGlbSettings {
            max_vertices: 1,
            ..Default::default()
        },
        ModelGlbSettings {
            max_json_bytes: 1,
            ..Default::default()
        },
    ] {
        let error = ModelGlbImporter
            .import(
                &mut ImportContext::new(&bytes, Default::default(), &|| false).unwrap(),
                &settings,
            )
            .unwrap_err();
        assert_eq!(error.kind(), ImportErrorKind::LimitExceeded);
    }
    let budget = crate::import::ImportBudget {
        max_decoded_bytes: 1,
        ..Default::default()
    };
    assert_eq!(
        ModelGlbImporter
            .import(
                &mut ImportContext::new(&bytes, budget, &|| false).unwrap(),
                &Default::default()
            )
            .unwrap_err()
            .kind(),
        ImportErrorKind::LimitExceeded
    );
    doc["extensionsUsed"] = json!(["KHR_materials_specular"]);
    let bytes = pack(&doc, &blob);
    assert_eq!(
        decode(&doc, &blob).unwrap_err().kind(),
        ImportErrorKind::Unsupported
    );
    let m = ModelGlbImporter
        .import(
            &mut ImportContext::new(&bytes, Default::default(), &|| false).unwrap(),
            &ModelGlbSettings {
                allow_material_fallback: true,
                ..Default::default()
            },
        )
        .unwrap();
    assert_eq!(m.data().omitted_extensions, ["KHR_materials_specular"]);
}

#[test]
fn retains_embedded_images_and_core_material_bindings() {
    let (mut doc, mut blob) = fixture();
    let png =
        include_bytes!("../../../../games/minimal-game/assets/presentation/textures/sample.png");
    let offset = blob.len();
    blob.extend(png);
    while !blob.len().is_multiple_of(4) {
        blob.push(0);
    }
    let views = doc["bufferViews"].as_array_mut().unwrap();
    let view = views.len();
    views.push(json!({"buffer":0,"byteOffset":offset,"byteLength":png.len()}));
    doc["buffers"][0]["byteLength"] = json!(blob.len());
    doc["images"] = json!([{"bufferView":view,"mimeType":"image/png"}]);
    doc["textures"] = json!([{"source":0}]);
    doc["materials"] = json!([{"pbrMetallicRoughness":{"baseColorTexture":{"index":0},"baseColorFactor":[1,0.5,0.25,1]}}]);
    doc["meshes"][0]["primitives"][0]["material"] = json!(0);
    let model = decode(&doc, &blob).unwrap();
    assert_eq!(model.data().images[0].bytes, png);
    assert_eq!(model.data().materials[0].base_color_texture, Some(0));
    doc["images"] = json!([{"uri":"external.png"}]);
    assert_eq!(decode(&doc, &blob).unwrap_err().code(), "external_image");
}

#[test]
fn binary_cache_preserves_model_graph_skin_and_animation() {
    let (doc, blob) = fixture();
    let model = decode(&doc, &blob).unwrap();
    let payload = ModelGlbImporter.cache_encode(&model).unwrap();
    let cached = ModelGlbImporter
        .cache_decode(&payload, &Default::default())
        .unwrap();
    assert_eq!(cached.parents(), model.parents());
    assert_eq!(ModelGlbImporter.cache_encode(&cached).unwrap(), payload);
    let mut data = cached.data().clone();
    data.nodes[0].children.push(9999);
    assert!(
        ModelGlbImporter
            .cache_decode(&crate::cache::encode(&data).unwrap(), &Default::default())
            .is_err()
    );
}

#[test]
fn cached_image_bulk_codec_preserves_existing_binary_layout() {
    let image = ModelImage {
        name: "test".into(),
        encoding: ImageEncoding::Png,
        bytes: vec![0, 1, 127, 128, 255],
    };
    let legacy = crate::cache::encode(&(&image.name, image.encoding, &image.bytes)).unwrap();
    assert_eq!(crate::cache::encode(&image).unwrap(), legacy);
    let restored: ModelImage = crate::cache::decode(&legacy).unwrap();
    assert_eq!(restored.bytes, image.bytes);
    let json = serde_json::to_vec(&image).unwrap();
    assert_eq!(
        serde_json::from_slice::<ModelImage>(&json).unwrap().bytes,
        image.bytes
    );
    assert!(crate::cache::decode::<ModelImage>(&legacy[..legacy.len() - 1]).is_err());
}
