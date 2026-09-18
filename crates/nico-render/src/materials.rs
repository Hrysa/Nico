//! Persistent material bindings and texture residency, separate from frame uniforms.
use nico_assets::{
    MaterialTexture, PbrMaterial, Texture,
    model::{AlphaMode, Filter, WrapMode},
};
use nico_presentation::MeshInstance;
use nico_rhi::*;
use std::{
    num::NonZeroU64,
    sync::{Arc, Weak},
};

struct Image<D: RhiDevice> {
    source: Weak<Texture>,
    srgb: bool,
    _texture: D::Texture,
    view: D::TextureView,
}
struct Material<D: RhiDevice> {
    source: Option<Weak<PbrMaterial>>,
    legacy: Option<Weak<Texture>>,
    binding: D::BindGroup,
    _buffer: D::Buffer,
    _samplers: Vec<D::Sampler>,
}
pub(super) struct Materials<D: RhiDevice> {
    pub layout: D::BindGroupLayout,
    materials: Vec<Material<D>>,
    images: Vec<Image<D>>,
    white: Arc<Texture>,
    normal: Arc<Texture>,
}
fn matches<T>(weak: &Option<Weak<T>>, strong: Option<&Arc<T>>) -> bool {
    match (weak, strong) {
        (Some(a), Some(b)) => a.ptr_eq(&Arc::downgrade(b)),
        (None, None) => true,
        _ => false,
    }
}
impl<D: RhiDevice> Materials<D> {
    pub fn new(device: &D) -> Result<Self, RhiError> {
        let mut entries = Vec::new();
        for slot in 0..5 {
            entries.push(BindGroupLayoutEntry {
                binding: slot * 2,
                visibility: ShaderStages::FRAGMENT,
                binding_type: BindingType::Texture {
                    sample_kind: TextureSampleKind::Float { filterable: true },
                    dimension: TextureViewDimension::Two,
                    multisampled: false,
                },
            });
            entries.push(BindGroupLayoutEntry {
                binding: slot * 2 + 1,
                visibility: ShaderStages::FRAGMENT,
                binding_type: BindingType::Sampler(SamplerBindingKind::Filtering),
            });
        }
        entries.push(BindGroupLayoutEntry {
            binding: 10,
            visibility: ShaderStages::FRAGMENT,
            binding_type: BindingType::Buffer {
                kind: BufferBindingKind::Uniform,
                dynamic_offset: false,
                minimum_size: NonZeroU64::new(64),
            },
        });
        Ok(Self {
            layout: device.create_bind_group_layout(BindGroupLayoutDescriptor {
                label: Some("PBR material layout"),
                entries: &entries,
            })?,
            materials: Vec::new(),
            images: Vec::new(),
            white: Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap()),
            normal: Arc::new(Texture::rgba8(1, 1, vec![128, 128, 255, 255]).unwrap()),
        })
    }
    pub fn retain_live_sources(&mut self) {
        self.materials.retain(|m| {
            m.source.as_ref().is_none_or(|s| s.strong_count() > 0)
                && m.legacy.as_ref().is_none_or(|s| s.strong_count() > 0)
        });
        self.images.retain(|image| image.source.strong_count() > 0);
    }
    pub fn binding(&self, slot: usize) -> &D::BindGroup {
        &self.materials[slot].binding
    }
    pub fn slot<Q: RhiQueue<D>>(
        &mut self,
        device: &D,
        queue: &Q,
        draw: &MeshInstance,
    ) -> Result<usize, RhiError> {
        let legacy = if draw.material.is_none() {
            draw.texture.as_ref()
        } else {
            None
        };
        if let Some(slot) = self
            .materials
            .iter()
            .position(|m| matches(&m.source, draw.material.as_ref()) && matches(&m.legacy, legacy))
        {
            return Ok(slot);
        }
        let fallback = PbrMaterial {
            base_color_texture: legacy.cloned().map(MaterialTexture::new),
            alpha: AlphaMode::Mask,
            double_sided: true,
            ..Default::default()
        };
        let material = draw.material.as_deref().unwrap_or(&fallback);
        let mut image_slots = Vec::new();
        let mut samplers = Vec::new();
        for (slot, texture) in material.textures().into_iter().enumerate() {
            let fallback = MaterialTexture::new(if slot == 2 {
                self.normal.clone()
            } else {
                self.white.clone()
            });
            let texture = texture.unwrap_or(&fallback);
            let srgb = slot == 0 || slot == 4;
            let image_slot =
                if let Some(i) = self.images.iter().position(|i| {
                    i.srgb == srgb && i.source.ptr_eq(&Arc::downgrade(&texture.image))
                }) {
                    i
                } else {
                    self.images
                        .push(upload(device, queue, &texture.image, srgb)?);
                    self.images.len() - 1
                };
            image_slots.push(image_slot);
            samplers.push(device.create_sampler(SamplerDescriptor {
                label: Some("material sampler"),
                address_u: wrap(texture.wrap_s),
                address_v: wrap(texture.wrap_t),
                min_filter: filter(texture.min_filter),
                mag_filter: filter(texture.mag_filter),
                lod_max: 0.,
                ..Default::default()
            })?);
        }
        let bytes: Vec<_> = parameters(material)
            .into_iter()
            .flat_map(f32::to_le_bytes)
            .collect();
        let buffer = device.create_buffer(BufferDescriptor {
            label: Some("material factors"),
            size: 64,
            usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
        })?;
        queue.write_buffer(&buffer, 0, &bytes);
        let mut entries = Vec::new();
        for (slot, (&image, sampler)) in image_slots.iter().zip(&samplers).enumerate() {
            entries.push(BindGroupEntry {
                binding: slot as u32 * 2,
                resource: BindingResource::TextureView(&self.images[image].view),
            });
            entries.push(BindGroupEntry {
                binding: slot as u32 * 2 + 1,
                resource: BindingResource::Sampler(sampler),
            });
        }
        entries.push(BindGroupEntry {
            binding: 10,
            resource: BindingResource::Buffer {
                buffer: &buffer,
                offset: 0,
                size: NonZeroU64::new(64),
            },
        });
        let binding = device.create_bind_group(BindGroupDescriptor {
            label: Some("PBR material"),
            layout: &self.layout,
            entries: &entries,
        })?;
        self.materials.push(Material {
            source: draw.material.as_ref().map(Arc::downgrade),
            legacy: legacy.map(Arc::downgrade),
            binding,
            _buffer: buffer,
            _samplers: samplers,
        });
        Ok(self.materials.len() - 1)
    }
}
fn parameters(m: &PbrMaterial) -> [f32; 16] {
    [
        m.base_color[0],
        m.base_color[1],
        m.base_color[2],
        m.base_color[3],
        m.emissive[0],
        m.emissive[1],
        m.emissive[2],
        0.,
        m.metallic,
        m.roughness,
        m.normal_scale,
        m.occlusion_strength,
        match m.alpha {
            AlphaMode::Opaque => 0.,
            AlphaMode::Mask => 1.,
            AlphaMode::Blend => 2.,
        },
        m.alpha_cutoff,
        if m.normal_texture.is_some() { 1. } else { 0. },
        0.,
    ]
}
fn wrap(mode: WrapMode) -> AddressMode {
    match mode {
        WrapMode::Clamp => AddressMode::ClampToEdge,
        WrapMode::Repeat => AddressMode::Repeat,
        WrapMode::Mirror => AddressMode::MirrorRepeat,
    }
}
fn filter(mode: Filter) -> FilterMode {
    match mode {
        Filter::Nearest | Filter::NearestMipmapNearest | Filter::NearestMipmapLinear => {
            FilterMode::Nearest
        }
        _ => FilterMode::Linear,
    }
}
fn upload<D: RhiDevice, Q: RhiQueue<D>>(
    device: &D,
    queue: &Q,
    source: &Arc<Texture>,
    srgb: bool,
) -> Result<Image<D>, RhiError> {
    crate::validate_texture(device, source)?;
    let extent = Extent3d::surface(source.width(), source.height());
    let texture = device.create_texture(TextureDescriptor {
        label: Some("material image"),
        extent,
        mip_levels: 1,
        samples: 1,
        dimension: TextureDimension::Two,
        format: if srgb {
            TextureFormat::Rgba8UnormSrgb
        } else {
            TextureFormat::Rgba8Unorm
        },
        usages: TextureUsages::SAMPLED | TextureUsages::COPY_DESTINATION,
    })?;
    queue.write_texture(
        TextureCopy {
            texture: &texture,
            mip_level: 0,
            origin: Origin3d::default(),
        },
        source.pixels(),
        TextureDataLayout {
            offset: 0,
            bytes_per_row: Some(source.width() * 4),
            rows_per_image: Some(source.height()),
        },
        extent,
    );
    let view = device.create_texture_view(&texture, TextureViewDescriptor::default())?;
    Ok(Image {
        source: Arc::downgrade(source),
        srgb,
        _texture: texture,
        view,
    })
}
