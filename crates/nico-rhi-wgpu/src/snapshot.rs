use super::{WgpuDevice, WgpuQueue};

pub struct CapturedPixels {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

pub(super) fn readback(
    device: &WgpuDevice,
    queue: &WgpuQueue,
    texture: &wgpu::Texture,
) -> Result<CapturedPixels, String> {
    let width = texture.width();
    let height = texture.height();
    if width == 0 || height == 0 || width > 4096 || height > 4096 {
        return Err("snapshot dimensions must be 1..4096".into());
    }
    if !texture.usage().contains(wgpu::TextureUsages::COPY_SRC) {
        return Err("surface does not support snapshot copy".into());
    }
    let bgra = match texture.format() {
        wgpu::TextureFormat::Bgra8UnormSrgb | wgpu::TextureFormat::Bgra8Unorm => true,
        wgpu::TextureFormat::Rgba8UnormSrgb | wgpu::TextureFormat::Rgba8Unorm => false,
        _ => return Err("snapshot requires an RGBA8 or BGRA8 surface".into()),
    };
    let stride = (width * 4).div_ceil(256) * 256;
    let buffer = device.inner.create_buffer(&wgpu::BufferDescriptor {
        label: Some("window snapshot readback"),
        size: u64::from(stride) * u64::from(height),
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });
    let mut encoder = device
        .inner
        .create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
    encoder.copy_texture_to_buffer(
        wgpu::TexelCopyTextureInfo {
            texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::TexelCopyBufferInfo {
            buffer: &buffer,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(stride),
                rows_per_image: Some(height),
            },
        },
        wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
    );
    queue.inner.submit([encoder.finish()]);
    let (tx, rx) = std::sync::mpsc::sync_channel(1);
    buffer
        .slice(..)
        .map_async(wgpu::MapMode::Read, move |result| {
            let _ = tx.send(result);
        });
    device
        .inner
        .poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(std::time::Duration::from_secs(2)),
        })
        .map_err(|e| e.to_string())?;
    rx.try_recv()
        .map_err(|e| e.to_string())?
        .map_err(|e| e.to_string())?;
    let mapped = buffer
        .slice(..)
        .get_mapped_range()
        .map_err(|e| e.to_string())?;
    let mut rgba = Vec::with_capacity((width * height * 4) as usize);
    for row in mapped.chunks_exact(stride as usize) {
        for pixel in row[..(width * 4) as usize].as_chunks::<4>().0 {
            if bgra {
                rgba.extend_from_slice(&[pixel[2], pixel[1], pixel[0], 255]);
            } else {
                rgba.extend_from_slice(&[pixel[0], pixel[1], pixel[2], 255]);
            }
        }
    }
    drop(mapped);
    buffer.unmap();
    Ok(CapturedPixels {
        width,
        height,
        rgba,
    })
}
