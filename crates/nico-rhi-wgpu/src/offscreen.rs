//! Provider-owned render target for embedding Nico in native GPU applications.
use super::*;

pub struct WgpuOffscreen {
    texture: wgpu::Texture,
    view: WgpuTextureView,
    requested: Extent3d,
}
impl WgpuOffscreen {
    pub fn new(device: &WgpuDevice, width: u32, height: u32) -> Result<Self, RhiError> {
        if width == 0
            || height == 0
            || width > device.inner.limits().max_texture_dimension_2d
            || height > device.inner.limits().max_texture_dimension_2d
        {
            return Err(RhiError::new(
                RhiErrorKind::InvalidDescriptor,
                "invalid offscreen extent",
            ));
        }
        let texture = device.inner.create_texture(&wgpu::TextureDescriptor {
            label: Some("Nico embedded viewport"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8UnormSrgb,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = WgpuTextureView(texture.create_view(&wgpu::TextureViewDescriptor::default()));
        Ok(Self {
            texture,
            view,
            requested: Extent3d::surface(width, height),
        })
    }
    pub fn view(&self) -> &wgpu::TextureView {
        self.view.raw()
    }
    pub fn extent(&self) -> Extent3d {
        self.requested
    }
}
impl RhiSurface<WgpuDevice, WgpuQueue> for WgpuOffscreen {
    type Frame = ();
    fn format(&self) -> TextureFormat {
        TextureFormat::Rgba8UnormSrgb
    }
    fn resize(&mut self, device: &WgpuDevice, extent: Extent3d) {
        // External texture bindings must be refreshed after a successful resize.
        if extent.depth_or_layers == 1
            && let Ok(next) = Self::new(device, extent.width, extent.height)
        {
            *self = next;
        }
        self.requested = extent;
    }
    fn acquire(
        &mut self,
        _device: &WgpuDevice,
    ) -> Result<SurfaceAcquire<(), WgpuTextureView>, RhiError> {
        if self.requested.is_zero() {
            return Ok(SurfaceAcquire::ZeroSized);
        }
        if self.requested != Extent3d::surface(self.texture.width(), self.texture.height()) {
            return Err(RhiError::new(
                RhiErrorKind::InvalidDescriptor,
                "invalid offscreen resize",
            ));
        }
        Ok(SurfaceAcquire::Acquired {
            frame: (),
            view: self.view.clone(),
        })
    }
    fn present(&mut self, _device: &WgpuDevice, _queue: &WgpuQueue, _frame: ()) {}
}
