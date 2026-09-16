use crate::{
    Texture,
    asset_error::{AssetError as TextureError, AssetLimits as TextureLimits},
};
use std::io::Cursor;
type LoadResult = Result<Texture, TextureError>;

#[cfg(test)]
pub(crate) fn decode_png(
    path: &std::path::Path,
    limits: TextureLimits,
) -> Result<Texture, TextureError> {
    let bytes = crate::import::read_source(
        path,
        crate::import::ImportBudget {
            max_input_bytes: limits.max_file_bytes,
            max_decoded_bytes: limits.max_decoded_bytes,
        },
        &|| false,
    )
    .map_err(|e| match e.kind() {
        crate::import::ImportErrorKind::LimitExceeded => TextureError::LimitExceeded,
        _ => TextureError::Io(e.to_string()),
    })?;
    decode_bytes(&bytes, limits)
}

pub(crate) fn decode_bytes(bytes: &[u8], limits: TextureLimits) -> LoadResult {
    let mut decoder = png::Decoder::new(Cursor::new(bytes));
    decoder.set_limits(png::Limits {
        bytes: limits.max_decoded_bytes,
    });
    decoder.set_transformations(png::Transformations::EXPAND | png::Transformations::STRIP_16);
    let mut reader = decoder
        .read_info()
        .map_err(|e| TextureError::InvalidPng(e.to_string()))?;
    let info = reader.info();
    if info.animation_control.is_some() {
        return Err(TextureError::UnsupportedPng);
    }
    let (width, height) = (info.width, info.height);
    let rgba_len = (width as usize)
        .checked_mul(height as usize)
        .and_then(|n| n.checked_mul(4))
        .ok_or(TextureError::LimitExceeded)?;
    if width > limits.max_dimension
        || height > limits.max_dimension
        || rgba_len > limits.max_decoded_bytes
    {
        return Err(TextureError::LimitExceeded);
    }
    let size = reader
        .output_buffer_size()
        .ok_or(TextureError::LimitExceeded)?;
    if size > limits.max_decoded_bytes {
        return Err(TextureError::LimitExceeded);
    }
    let mut decoded = vec![0; size];
    let output = reader
        .next_frame(&mut decoded)
        .map_err(|e| TextureError::InvalidPng(e.to_string()))?;
    reader
        .finish()
        .map_err(|e| TextureError::InvalidPng(e.to_string()))?;
    let mut pixels = Vec::with_capacity(rgba_len);
    let channels = output.color_type.samples();
    for pixel in decoded[..output.buffer_size()].chunks_exact(channels) {
        match output.color_type {
            png::ColorType::Grayscale => {
                pixels.extend_from_slice(&[pixel[0], pixel[0], pixel[0], 255])
            }
            png::ColorType::GrayscaleAlpha => {
                pixels.extend_from_slice(&[pixel[0], pixel[0], pixel[0], pixel[1]])
            }
            png::ColorType::Rgb => pixels.extend_from_slice(&[pixel[0], pixel[1], pixel[2], 255]),
            png::ColorType::Rgba => pixels.extend_from_slice(pixel),
            png::ColorType::Indexed => return Err(TextureError::UnsupportedPng),
        }
    }
    Ok(Texture {
        width,
        height,
        pixels,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    fn fixture() -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../games/minimal-game/assets/presentation/textures/sample.png")
    }
    #[test]
    fn real_png_is_decoded_and_limits_are_enforced() {
        let texture = decode_png(&fixture(), TextureLimits::default()).unwrap();
        assert_eq!((texture.width(), texture.height()), (2, 2));
        assert_eq!(
            texture.pixels(),
            &[
                255, 255, 255, 255, 255, 80, 40, 255, 40, 160, 255, 255, 0, 0, 0, 0
            ]
        );
        for limits in [
            TextureLimits {
                max_file_bytes: 1,
                ..TextureLimits::default()
            },
            TextureLimits {
                max_dimension: 1,
                ..TextureLimits::default()
            },
        ] {
            assert!(matches!(
                decode_png(&fixture(), limits),
                Err(TextureError::LimitExceeded)
            ));
        }
        assert!(
            decode_png(
                &fixture(),
                TextureLimits {
                    max_decoded_bytes: 1,
                    ..TextureLimits::default()
                }
            )
            .is_err()
        );
        assert!(matches!(
            decode_png(
                &fixture().with_extension("missing"),
                TextureLimits::default()
            ),
            Err(TextureError::Io(_))
        ));
        let invalid = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml");
        assert!(matches!(
            decode_png(&invalid, TextureLimits::default()),
            Err(TextureError::InvalidPng(_))
        ));
    }

    fn encoded(color: png::ColorType, depth: png::BitDepth, pixels: &[u8]) -> Vec<u8> {
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, 1, 1);
            encoder.set_color(color);
            encoder.set_depth(depth);
            if color == png::ColorType::Indexed {
                encoder.set_palette(vec![10, 20, 30]);
                encoder.set_trns(vec![40]);
            }
            encoder
                .write_header()
                .unwrap()
                .write_image_data(pixels)
                .unwrap();
        }
        bytes
    }

    #[test]
    fn png_variants_normalize_to_rgba8_and_truncation_fails() {
        for (color, depth, input, expected) in [
            (
                png::ColorType::Grayscale,
                png::BitDepth::Eight,
                vec![10],
                [10, 10, 10, 255],
            ),
            (
                png::ColorType::GrayscaleAlpha,
                png::BitDepth::Eight,
                vec![10, 20],
                [10, 10, 10, 20],
            ),
            (
                png::ColorType::Rgb,
                png::BitDepth::Eight,
                vec![10, 20, 30],
                [10, 20, 30, 255],
            ),
            (
                png::ColorType::Indexed,
                png::BitDepth::Eight,
                vec![0],
                [10, 20, 30, 40],
            ),
            (
                png::ColorType::Grayscale,
                png::BitDepth::Sixteen,
                vec![10, 255],
                [10, 10, 10, 255],
            ),
        ] {
            let mut bytes = encoded(color, depth, &input);
            let texture = decode_bytes(&bytes, TextureLimits::default()).unwrap();
            assert_eq!(texture.pixels(), expected);
            bytes.truncate(bytes.len() / 2);
            assert!(matches!(
                decode_bytes(&bytes, TextureLimits::default()),
                Err(TextureError::InvalidPng(_))
            ));
        }
    }
}
