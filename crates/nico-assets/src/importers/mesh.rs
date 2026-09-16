use crate::{
    Mesh, MeshVertex,
    asset_error::{AssetError, AssetLimits},
};

pub(super) fn decode_bytes(bytes: &[u8], limits: AssetLimits) -> Result<Mesh, AssetError> {
    if !bytes.starts_with(b"glTF") {
        return Err(AssetError::InvalidMesh("expected binary GLB".into()));
    }
    let gltf = gltf::Gltf::from_slice(bytes).map_err(|e| AssetError::InvalidMesh(e.to_string()))?;
    let blob = gltf.blob.as_deref().ok_or(AssetError::UnsupportedMesh)?;
    let document = &gltf.document;
    if document.extensions_required().next().is_some() {
        return Err(AssetError::UnsupportedMesh);
    }
    if document.meshes().len() != 1
        || document.buffers().len() != 1
        || document.animations().len() != 0
        || document.skins().len() != 0
        || document.images().len() != 0
        || document.materials().len() != 0
        || document.nodes().len() > 1
    {
        return Err(AssetError::UnsupportedMesh);
    }
    let buffer = document.buffers().next().unwrap();
    if !matches!(buffer.source(), gltf::buffer::Source::Bin) || buffer.length() > blob.len() {
        return Err(AssetError::UnsupportedMesh);
    }
    for node in document.nodes() {
        if node.transform().matrix()
            != [
                [1.0, 0.0, 0.0, 0.0],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ]
            || node.children().len() != 0
            || node.mesh().is_none()
        {
            return Err(AssetError::UnsupportedMesh);
        }
    }
    let mesh = document.meshes().next().unwrap();
    if mesh.primitives().len() != 1 || mesh.weights().is_some() {
        return Err(AssetError::UnsupportedMesh);
    }
    let primitive = mesh.primitives().next().unwrap();
    if primitive.mode() != gltf::mesh::Mode::Triangles || primitive.morph_targets().len() != 0 {
        return Err(AssetError::UnsupportedMesh);
    }
    let position = primitive
        .get(&gltf::Semantic::Positions)
        .ok_or_else(|| AssetError::InvalidMesh("missing POSITION".into()))?;
    let uv = primitive
        .get(&gltf::Semantic::TexCoords(0))
        .ok_or_else(|| AssetError::InvalidMesh("missing TEXCOORD_0".into()))?;
    let indices = primitive.indices().ok_or(AssetError::UnsupportedMesh)?;
    if position.data_type() != gltf::accessor::DataType::F32
        || position.dimensions() != gltf::accessor::Dimensions::Vec3
        || uv.data_type() != gltf::accessor::DataType::F32
        || uv.dimensions() != gltf::accessor::Dimensions::Vec2
        || indices.dimensions() != gltf::accessor::Dimensions::Scalar
        || !matches!(
            indices.data_type(),
            gltf::accessor::DataType::U8
                | gltf::accessor::DataType::U16
                | gltf::accessor::DataType::U32
        )
        || position.normalized()
        || uv.normalized()
        || indices.normalized()
    {
        return Err(AssetError::UnsupportedMesh);
    }
    if position.count() > limits.max_vertices
        || indices.count() > limits.max_indices
        || position
            .count()
            .checked_mul(20)
            .and_then(|n| {
                indices
                    .count()
                    .checked_mul(4)
                    .and_then(|m| n.checked_add(m))
            })
            .is_none_or(|n| n > limits.max_decoded_bytes)
    {
        return Err(AssetError::LimitExceeded);
    }
    if uv.count() != position.count()
        || position.sparse().is_some()
        || uv.sparse().is_some()
        || indices.sparse().is_some()
    {
        return Err(AssetError::UnsupportedMesh);
    }
    // Validate all arithmetic before entering gltf's iterators, which assume valid
    // nonzero counts, strides, and offset arithmetic.
    for accessor in [&position, &uv, &indices] {
        let view = accessor.view().ok_or(AssetError::UnsupportedMesh)?;
        let stride = view.stride().unwrap_or(accessor.size());
        let view_end = view.offset().checked_add(view.length());
        let data_end = accessor
            .count()
            .checked_sub(1)
            .and_then(|n| n.checked_mul(stride))
            .and_then(|n| n.checked_add(accessor.offset()))
            .and_then(|n| n.checked_add(accessor.size()));
        if stride < accessor.size()
            || view.buffer().index() != 0
            || view_end.is_none_or(|end| end > buffer.length())
            || data_end.is_none_or(|end| end > view.length())
        {
            return Err(AssetError::InvalidMesh(
                "accessor exceeds its buffer view".into(),
            ));
        }
    }
    let reader = primitive.reader(|buffer| (buffer.index() == 0).then_some(blob));
    let positions = reader
        .read_positions()
        .ok_or_else(|| AssetError::InvalidMesh("invalid position buffer".into()))?;
    let uvs = reader
        .read_tex_coords(0)
        .ok_or_else(|| AssetError::InvalidMesh("invalid UV buffer".into()))?
        .into_f32();
    let vertices: Vec<_> = positions
        .zip(uvs)
        .map(|(position, uv)| MeshVertex { position, uv })
        .collect();
    let values: Vec<_> = reader
        .read_indices()
        .ok_or_else(|| AssetError::InvalidMesh("invalid index buffer".into()))?
        .into_u32()
        .collect();
    if vertices.len() != position.count() || values.len() != indices.count() {
        return Err(AssetError::InvalidMesh("truncated attributes".into()));
    }
    Mesh::triangles(vertices, values)
        .ok_or_else(|| AssetError::InvalidMesh("invalid triangle geometry".into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn real_mesh_and_bounds_are_checked() {
        let bytes =
            include_bytes!("../../../../games/minimal-game/assets/presentation/meshes/cube.glb");
        let mesh = decode_bytes(bytes, AssetLimits::default()).unwrap();
        assert_eq!(mesh.vertices().len(), 24);
        assert_eq!(mesh.indices().len(), 36);
        assert!(matches!(
            decode_bytes(
                bytes,
                AssetLimits {
                    max_vertices: 3,
                    ..AssetLimits::default()
                }
            ),
            Err(AssetError::LimitExceeded)
        ));
        assert!(decode_bytes(&bytes[..bytes.len() / 2], AssetLimits::default()).is_err());
        assert!(decode_bytes(b"invalid", AssetLimits::default()).is_err());
    }

    fn changed(change: impl FnOnce(&mut serde_json::Value, &mut Vec<u8>)) -> Vec<u8> {
        let original =
            include_bytes!("../../../../games/minimal-game/assets/presentation/meshes/cube.glb");
        let length = u32::from_le_bytes(original[12..16].try_into().unwrap()) as usize;
        let mut doc = serde_json::from_slice(&original[20..20 + length]).unwrap();
        let mut binary = original[28 + length..].to_vec();
        change(&mut doc, &mut binary);
        let mut json = serde_json::to_vec(&doc).unwrap();
        while !json.len().is_multiple_of(4) {
            json.push(b' ');
        }
        let mut bytes = b"glTF".to_vec();
        bytes.extend_from_slice(&2_u32.to_le_bytes());
        bytes.extend_from_slice(&((28 + json.len() + binary.len()) as u32).to_le_bytes());
        bytes.extend_from_slice(&(json.len() as u32).to_le_bytes());
        bytes.extend_from_slice(b"JSON");
        bytes.extend(json);
        bytes.extend_from_slice(&(binary.len() as u32).to_le_bytes());
        bytes.extend_from_slice(b"BIN\0");
        bytes.extend(binary);
        bytes
    }
    #[test]
    fn malformed_geometry_and_unsupported_scene_features_fail_without_panicking() {
        let mutations: Vec<Vec<u8>> = vec![
            changed(|doc, _| doc["accessors"][0]["count"] = 0.into()),
            changed(|doc, _| doc["accessors"][0]["byteOffset"] = u64::MAX.into()),
            changed(|doc, _| doc["bufferViews"][0]["byteStride"] = 4.into()),
            changed(|doc, _| doc["buffers"][0]["uri"] = "external.bin".into()),
            changed(|doc, _| doc["nodes"][0]["translation"] = serde_json::json!([1, 0, 0])),
            changed(|_, bytes| bytes[..4].copy_from_slice(&f32::NAN.to_le_bytes())),
            changed(|_, bytes| bytes[480..482].copy_from_slice(&65535_u16.to_le_bytes())),
        ];
        for bytes in mutations {
            assert!(decode_bytes(&bytes, AssetLimits::default()).is_err());
        }
    }

    #[cfg(all(feature = "runtime-loading", feature = "png-import"))]
    #[test]
    fn mesh_and_texture_completions_have_independent_identity_and_lifetimes() {
        use crate::loading::{MeshState, MeshStore, TextureState, TextureStore};
        use crate::{AssetId, Handle, Texture};
        use nico_runtime::AppBuilder;
        use std::path::Path;
        use std::time::{Duration, Instant};
        let mut builder = AppBuilder::new().with_event_capacity(1);
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../games/minimal-game/assets/presentation");
        let mesh = Handle::<Mesh>::new(AssetId::from_u128(1));
        let texture = Handle::<Texture>::new(AssetId::from_u128(1));
        MeshStore::install(
            &mut builder,
            &root,
            [(mesh.id(), "meshes/cube.glb".into())],
            AssetLimits::default(),
        )
        .unwrap();
        TextureStore::install(
            &mut builder,
            &root,
            [(texture.id(), "textures/sample.png".into())],
            AssetLimits::default(),
        )
        .unwrap();
        let mut app = builder.build().unwrap();
        app.start().unwrap();
        let lease = app
            .world_mut()
            .resource_mut::<MeshStore>()
            .unwrap()
            .request(mesh)
            .unwrap();
        let _texture_lease = app
            .world_mut()
            .resource_mut::<TextureStore>()
            .unwrap()
            .request(texture)
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            app.tick(Duration::ZERO).unwrap();
            if matches!(
                app.world().resource::<MeshStore>().unwrap().state(mesh),
                Some(MeshState::Ready(_))
            ) && matches!(
                app.world()
                    .resource::<TextureStore>()
                    .unwrap()
                    .state(texture),
                Some(TextureState::Ready(_))
            ) {
                break;
            }
            assert!(Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(2));
        }
        drop(lease);
        app.tick(Duration::ZERO).unwrap();
        assert!(
            app.world()
                .resource::<MeshStore>()
                .unwrap()
                .state(mesh)
                .is_none()
        );
        assert!(matches!(
            app.world()
                .resource::<TextureStore>()
                .unwrap()
                .state(texture),
            Some(TextureState::Ready(_))
        ));
        app.shutdown().unwrap();
    }
}
