//! Shared material/canvas upload policy. Unsupported devices decode BC3 to RGBA8.
use nico_assets::{Texture, TextureEncoding};
use nico_rhi::*;

fn upload_data(source: &Texture, bc: bool, srgb: bool) -> (TextureFormat, &[u8], u32, u32) {
    if bc && source.encoding() == TextureEncoding::Bc3 {
        (
            if srgb {
                TextureFormat::Bc3RgbaUnormSrgb
            } else {
                TextureFormat::Bc3RgbaUnorm
            },
            source.encoded_bytes(),
            source.width() / 4 * 16,
            source.height() / 4,
        )
    } else {
        (
            if srgb {
                TextureFormat::Rgba8UnormSrgb
            } else {
                TextureFormat::Rgba8Unorm
            },
            source.pixels(),
            source.width() * 4,
            source.height(),
        )
    }
}

pub(super) fn upload<D: RhiDevice, Q: RhiQueue<D>>(
    device: &D,
    queue: &Q,
    source: &Texture,
    srgb: bool,
) -> Result<D::Texture, RhiError> {
    crate::validate_texture(device, source)?;
    let (format, data, bytes_per_row, rows_per_image) =
        upload_data(source, device.capabilities().texture_compression_bc, srgb);
    let extent = Extent3d::surface(source.width(), source.height());
    let texture = device.create_texture(TextureDescriptor {
        label: Some("asset texture"),
        extent,
        mip_levels: 1,
        samples: 1,
        dimension: TextureDimension::Two,
        format,
        usages: TextureUsages::SAMPLED | TextureUsages::COPY_DESTINATION,
    })?;
    queue.write_texture(
        TextureCopy {
            texture: &texture,
            mip_level: 0,
            origin: Origin3d::default(),
        },
        data,
        TextureDataLayout {
            offset: 0,
            bytes_per_row: Some(bytes_per_row),
            rows_per_image: Some(rows_per_image),
        },
        extent,
    );
    Ok(texture)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn uploads_preserve_slot_color_space_with_and_without_bc_support() {
        let texture = Texture::rgba8(8, 4, [255, 0, 0, 128].repeat(32))
            .unwrap()
            .compress_bc3(|| Ok::<_, ()>(()))
            .unwrap();
        for (bc, srgb, format) in [
            (true, false, TextureFormat::Bc3RgbaUnorm),
            (true, true, TextureFormat::Bc3RgbaUnormSrgb),
            (false, false, TextureFormat::Rgba8Unorm),
            (false, true, TextureFormat::Rgba8UnormSrgb),
        ] {
            let (actual, data, stride, rows) = upload_data(&texture, bc, srgb);
            assert_eq!(actual, format);
            if bc {
                assert_eq!(data, texture.encoded_bytes());
                assert_eq!((stride, rows), (32, 1));
            } else {
                assert_eq!(data, [255, 0, 0, 128].repeat(32));
                assert_eq!((stride, rows), (32, 4));
            }
        }
        let raw = Texture::rgba8(1, 1, vec![255; 4]).unwrap();
        assert_eq!(
            upload_data(&raw, true, true).0,
            TextureFormat::Rgba8UnormSrgb
        );
    }
}
