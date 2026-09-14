//! Backend-neutral rendering hardware interface for Nico.
//!
//! Associated resource types let providers dispatch directly without central
//! handle maps or per-command trait-object calls.

use std::{error::Error, fmt, num::NonZeroU64, ops::Range};

macro_rules! flags {
    ($(#[$meta:meta])* $visibility:vis struct $name:ident($storage:ty); $($constant:ident = $value:expr;)+) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
        $visibility struct $name($storage);

        impl $name {
            $(pub const $constant: Self = Self($value);)+
            pub const EMPTY: Self = Self(0);
            #[must_use] pub const fn bits(self) -> $storage { self.0 }
            #[must_use] pub const fn contains(self, other: Self) -> bool {
                self.0 & other.0 == other.0
            }
        }

        impl std::ops::BitOr for $name {
            type Output = Self;
            fn bitor(self, rhs: Self) -> Self::Output { Self(self.0 | rhs.0) }
        }

        impl std::ops::BitOrAssign for $name {
            fn bitor_assign(&mut self, rhs: Self) { self.0 |= rhs.0; }
        }
    };
}

flags! {
    /// Permitted uses of a buffer.
    pub struct BufferUsages(u32);
    COPY_SOURCE = 1 << 0;
    COPY_DESTINATION = 1 << 1;
    INDEX = 1 << 2;
    VERTEX = 1 << 3;
    UNIFORM = 1 << 4;
    STORAGE = 1 << 5;
    INDIRECT = 1 << 6;
}

flags! {
    /// Permitted uses of a texture.
    pub struct TextureUsages(u32);
    COPY_SOURCE = 1 << 0;
    COPY_DESTINATION = 1 << 1;
    SAMPLED = 1 << 2;
    STORAGE = 1 << 3;
    RENDER_ATTACHMENT = 1 << 4;
}

flags! {
    /// Shader stages visible to a binding.
    pub struct ShaderStages(u32);
    VERTEX = 1 << 0;
    FRAGMENT = 1 << 1;
    COMPUTE = 1 << 2;
}

flags! {
    /// Color channels written by a render target.
    pub struct ColorWrites(u32);
    RED = 1 << 0;
    GREEN = 1 << 1;
    BLUE = 1 << 2;
    ALPHA = 1 << 3;
    ALL = Self::RED.0 | Self::GREEN.0 | Self::BLUE.0 | Self::ALPHA.0;
}

/// Physical dimensions of a resource.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct Extent3d {
    pub width: u32,
    pub height: u32,
    pub depth_or_layers: u32,
}

impl Extent3d {
    #[must_use]
    pub const fn new(width: u32, height: u32, depth_or_layers: u32) -> Self {
        Self {
            width,
            height,
            depth_or_layers,
        }
    }

    #[must_use]
    pub const fn surface(width: u32, height: u32) -> Self {
        Self::new(width, height, 1)
    }

    #[must_use]
    pub const fn is_zero(self) -> bool {
        self.width == 0 || self.height == 0 || self.depth_or_layers == 0
    }
}

/// Linear RGBA color.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Color {
    pub red: f64,
    pub green: f64,
    pub blue: f64,
    pub alpha: f64,
}

impl Color {
    pub const WHITE: Self = Self::new(1.0, 1.0, 1.0, 1.0);
    pub const BLACK: Self = Self::new(0.0, 0.0, 0.0, 1.0);
    #[must_use]
    pub const fn new(red: f64, green: f64, blue: f64, alpha: f64) -> Self {
        Self {
            red,
            green,
            blue,
            alpha,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum GraphicsApi {
    Vulkan,
    Direct3d12,
    Metal,
    BrowserWebGpu,
    Other,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdapterKind {
    IntegratedGpu,
    DiscreteGpu,
    VirtualGpu,
    Cpu,
    Other,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AdapterInfo {
    pub name: String,
    pub api: GraphicsApi,
    pub kind: AdapterKind,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Limits {
    pub max_texture_dimension_2d: u32,
    pub max_bind_groups: u32,
    pub max_uniform_buffer_binding_size: u64,
    pub max_storage_buffer_binding_size: u64,
    pub max_vertex_buffers: u32,
    pub max_vertex_attributes: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Capabilities {
    pub adapter: AdapterInfo,
    pub limits: Limits,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RhiErrorKind {
    Unsupported,
    InvalidDescriptor,
    OutOfMemory,
    DeviceLost,
    Backend,
}

#[derive(Debug)]
pub struct RhiError {
    kind: RhiErrorKind,
    message: String,
}

impl RhiError {
    #[must_use]
    pub fn new(kind: RhiErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
        }
    }
    #[must_use]
    pub const fn kind(&self) -> RhiErrorKind {
        self.kind
    }
}

impl fmt::Display for RhiError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}
impl Error for RhiError {}

#[derive(Clone, Copy, Debug)]
pub struct BufferDescriptor<'a> {
    pub label: Option<&'a str>,
    pub size: u64,
    pub usages: BufferUsages,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextureDimension {
    One,
    Two,
    Three,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextureFormat {
    R8Unorm,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Bgra8Unorm,
    Bgra8UnormSrgb,
    Rgba16Float,
    R32Float,
    Depth24Plus,
    Depth24PlusStencil8,
    Depth32Float,
}

#[derive(Clone, Copy, Debug)]
pub struct TextureDescriptor<'a> {
    pub label: Option<&'a str>,
    pub extent: Extent3d,
    pub mip_levels: u32,
    pub samples: u32,
    pub dimension: TextureDimension,
    pub format: TextureFormat,
    pub usages: TextureUsages,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextureViewDimension {
    One,
    Two,
    TwoArray,
    Cube,
    CubeArray,
    Three,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct TextureViewDescriptor<'a> {
    pub label: Option<&'a str>,
    pub format: Option<TextureFormat>,
    pub dimension: Option<TextureViewDimension>,
    pub base_mip_level: u32,
    pub mip_level_count: Option<u32>,
    pub base_array_layer: u32,
    pub array_layer_count: Option<u32>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AddressMode {
    ClampToEdge,
    Repeat,
    MirrorRepeat,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FilterMode {
    Nearest,
    Linear,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompareFunction {
    Never,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always,
}

#[derive(Clone, Copy, Debug)]
pub struct SamplerDescriptor<'a> {
    pub label: Option<&'a str>,
    pub address_u: AddressMode,
    pub address_v: AddressMode,
    pub address_w: AddressMode,
    pub mag_filter: FilterMode,
    pub min_filter: FilterMode,
    pub mipmap_filter: FilterMode,
    pub lod_min: f32,
    pub lod_max: f32,
    pub compare: Option<CompareFunction>,
    pub max_anisotropy: u16,
}

impl Default for SamplerDescriptor<'_> {
    fn default() -> Self {
        Self {
            label: None,
            address_u: AddressMode::ClampToEdge,
            address_v: AddressMode::ClampToEdge,
            address_w: AddressMode::ClampToEdge,
            mag_filter: FilterMode::Nearest,
            min_filter: FilterMode::Nearest,
            mipmap_filter: FilterMode::Nearest,
            lod_min: 0.0,
            lod_max: 32.0,
            compare: None,
            max_anisotropy: 1,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShaderFormat {
    Wgsl,
    SpirV,
    Dxil,
    MetalLib,
}

#[derive(Clone, Copy, Debug)]
pub struct ShaderModuleDescriptor<'a> {
    pub label: Option<&'a str>,
    pub format: ShaderFormat,
    pub code: &'a [u8],
}

/// A graphics shader artifact and the entry points selected from its module.
#[derive(Clone, Copy, Debug)]
pub struct GraphicsShaderArtifact<'a> {
    pub module: ShaderModuleDescriptor<'a>,
    pub vertex_entry_point: &'a str,
    pub fragment_entry_point: &'a str,
}

/// RHI metadata for engine-owned shaders used to bootstrap presentation.
pub mod builtin_shaders {
    use super::{GraphicsShaderArtifact, ShaderFormat, ShaderModuleDescriptor};

    /// Describes externally loaded WGSL generated from the bootstrap Slang source.
    #[must_use]
    pub const fn bootstrap_wgsl(code: &[u8]) -> GraphicsShaderArtifact<'_> {
        GraphicsShaderArtifact {
            module: ShaderModuleDescriptor {
                label: Some("Nico bootstrap shader"),
                format: ShaderFormat::Wgsl,
                code,
            },
            vertex_entry_point: "vertex_main",
            fragment_entry_point: "fragment_main",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BufferBindingKind {
    Uniform,
    Storage { read_only: bool },
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextureSampleKind {
    Float { filterable: bool },
    Depth,
    Sint,
    Uint,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SamplerBindingKind {
    Filtering,
    NonFiltering,
    Comparison,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StorageTextureAccess {
    WriteOnly,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BindingType {
    Buffer {
        kind: BufferBindingKind,
        dynamic_offset: bool,
        minimum_size: Option<NonZeroU64>,
    },
    Texture {
        sample_kind: TextureSampleKind,
        dimension: TextureViewDimension,
        multisampled: bool,
    },
    StorageTexture {
        access: StorageTextureAccess,
        format: TextureFormat,
        dimension: TextureViewDimension,
    },
    Sampler(SamplerBindingKind),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BindGroupLayoutEntry {
    pub binding: u32,
    pub visibility: ShaderStages,
    pub binding_type: BindingType,
}

#[derive(Clone, Copy, Debug)]
pub struct BindGroupLayoutDescriptor<'a> {
    pub label: Option<&'a str>,
    pub entries: &'a [BindGroupLayoutEntry],
}

pub enum BindingResource<'a, B, V, S> {
    Buffer {
        buffer: &'a B,
        offset: u64,
        size: Option<NonZeroU64>,
    },
    TextureView(&'a V),
    Sampler(&'a S),
}

pub struct BindGroupEntry<'a, B, V, S> {
    pub binding: u32,
    pub resource: BindingResource<'a, B, V, S>,
}
pub struct BindGroupDescriptor<'a, L, B, V, S> {
    pub label: Option<&'a str>,
    pub layout: &'a L,
    pub entries: &'a [BindGroupEntry<'a, B, V, S>],
}
pub struct PipelineLayoutDescriptor<'a, L> {
    pub label: Option<&'a str>,
    pub bind_group_layouts: &'a [&'a L],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VertexStepMode {
    Vertex,
    Instance,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VertexFormat {
    Uint32,
    Sint32,
    Float32,
    Float32x2,
    Float32x3,
    Float32x4,
    Uint32x2,
    Uint32x3,
    Uint32x4,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VertexAttribute {
    pub format: VertexFormat,
    pub offset: u64,
    pub shader_location: u32,
}
#[derive(Clone, Copy, Debug)]
pub struct VertexBufferLayout<'a> {
    pub stride: u64,
    pub step_mode: VertexStepMode,
    pub attributes: &'a [VertexAttribute],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PrimitiveTopology {
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FrontFace {
    CounterClockwise,
    Clockwise,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Face {
    Front,
    Back,
}
#[derive(Clone, Copy, Debug)]
pub struct PrimitiveState {
    pub topology: PrimitiveTopology,
    pub strip_index_format: Option<IndexFormat>,
    pub front_face: FrontFace,
    pub cull_mode: Option<Face>,
}
impl Default for PrimitiveState {
    fn default() -> Self {
        Self {
            topology: PrimitiveTopology::TriangleList,
            strip_index_format: None,
            front_face: FrontFace::CounterClockwise,
            cull_mode: None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BlendFactor {
    Zero,
    One,
    Source,
    OneMinusSource,
    SourceAlpha,
    OneMinusSourceAlpha,
    Destination,
    OneMinusDestination,
    DestinationAlpha,
    OneMinusDestinationAlpha,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BlendOperation {
    Add,
    Subtract,
    ReverseSubtract,
    Min,
    Max,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BlendComponent {
    pub source: BlendFactor,
    pub destination: BlendFactor,
    pub operation: BlendOperation,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BlendState {
    pub color: BlendComponent,
    pub alpha: BlendComponent,
}
#[derive(Clone, Copy, Debug)]
pub struct ColorTargetState {
    pub format: TextureFormat,
    pub blend: Option<BlendState>,
    pub write_mask: ColorWrites,
}
#[derive(Clone, Copy, Debug)]
pub struct DepthStencilState {
    pub format: TextureFormat,
    pub depth_write_enabled: bool,
    pub depth_compare: CompareFunction,
}
#[derive(Clone, Copy, Debug)]
pub struct MultisampleState {
    pub count: u32,
    pub mask: u64,
    pub alpha_to_coverage_enabled: bool,
}
impl Default for MultisampleState {
    fn default() -> Self {
        Self {
            count: 1,
            mask: !0,
            alpha_to_coverage_enabled: false,
        }
    }
}

pub struct VertexState<'a, S> {
    pub shader: &'a S,
    pub entry_point: &'a str,
    pub buffers: &'a [VertexBufferLayout<'a>],
}
pub struct FragmentState<'a, S> {
    pub shader: &'a S,
    pub entry_point: &'a str,
    pub targets: &'a [Option<ColorTargetState>],
}
pub struct RenderPipelineDescriptor<'a, L, S> {
    pub label: Option<&'a str>,
    pub layout: &'a L,
    pub vertex: VertexState<'a, S>,
    pub fragment: Option<FragmentState<'a, S>>,
    pub primitive: PrimitiveState,
    pub depth_stencil: Option<DepthStencilState>,
    pub multisample: MultisampleState,
}
pub struct ComputePipelineDescriptor<'a, L, S> {
    pub label: Option<&'a str>,
    pub layout: &'a L,
    pub shader: &'a S,
    pub entry_point: &'a str,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexFormat {
    Uint16,
    Uint32,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StoreOp {
    Store,
    Discard,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum LoadOp<T> {
    Load,
    Clear(T),
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Operations<T> {
    pub load: LoadOp<T>,
    pub store: StoreOp,
}

pub struct RenderPassColorAttachment<'a, V> {
    pub view: &'a V,
    pub resolve_target: Option<&'a V>,
    pub operations: Operations<Color>,
}
pub struct RenderPassDepthStencilAttachment<'a, V> {
    pub view: &'a V,
    pub depth_operations: Option<Operations<f32>>,
    pub stencil_operations: Option<Operations<u32>>,
}
pub struct RenderPassDescriptor<'a, V> {
    pub label: Option<&'a str>,
    pub color_attachments: &'a [Option<RenderPassColorAttachment<'a, V>>],
    pub depth_stencil_attachment: Option<RenderPassDepthStencilAttachment<'a, V>>,
}
#[derive(Clone, Copy, Debug, Default)]
pub struct ComputePassDescriptor<'a> {
    pub label: Option<&'a str>,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct Origin3d {
    pub x: u32,
    pub y: u32,
    pub z: u32,
}
pub struct TextureCopy<'a, T> {
    pub texture: &'a T,
    pub mip_level: u32,
    pub origin: Origin3d,
}
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TextureDataLayout {
    pub offset: u64,
    pub bytes_per_row: Option<u32>,
    pub rows_per_image: Option<u32>,
}
pub struct BufferTextureCopy<'a, B> {
    pub buffer: &'a B,
    pub layout: TextureDataLayout,
}

pub enum SurfaceAcquire<F, V> {
    Acquired { frame: F, view: V },
    ZeroSized,
    Timeout,
    Occluded,
}

/// GPU device and resource factory.
/// Providers must retain resources referenced by recorded commands and submitted
/// work. Dropping a resource wrapper must not invalidate those uses; callers need
/// not wait for GPU completion before releasing their own resource references.
pub trait RhiDevice {
    type Buffer;
    type Texture;
    type TextureView;
    type Sampler;
    type ShaderModule;
    type BindGroupLayout;
    type BindGroup;
    type PipelineLayout;
    type RenderPipeline;
    type ComputePipeline;
    type CommandBuffer;
    type CommandEncoder: RhiCommandEncoder<
            Buffer = Self::Buffer,
            Texture = Self::Texture,
            TextureView = Self::TextureView,
            BindGroup = Self::BindGroup,
            RenderPipeline = Self::RenderPipeline,
            ComputePipeline = Self::ComputePipeline,
            CommandBuffer = Self::CommandBuffer,
        >;

    fn capabilities(&self) -> &Capabilities;
    fn create_buffer(&self, descriptor: BufferDescriptor<'_>) -> Result<Self::Buffer, RhiError>;
    fn create_texture(&self, descriptor: TextureDescriptor<'_>) -> Result<Self::Texture, RhiError>;
    fn create_texture_view(
        &self,
        texture: &Self::Texture,
        descriptor: TextureViewDescriptor<'_>,
    ) -> Result<Self::TextureView, RhiError>;
    fn create_sampler(&self, descriptor: SamplerDescriptor<'_>) -> Result<Self::Sampler, RhiError>;
    fn create_shader_module(
        &self,
        descriptor: ShaderModuleDescriptor<'_>,
    ) -> Result<Self::ShaderModule, RhiError>;
    fn create_bind_group_layout(
        &self,
        descriptor: BindGroupLayoutDescriptor<'_>,
    ) -> Result<Self::BindGroupLayout, RhiError>;
    fn create_bind_group(
        &self,
        descriptor: BindGroupDescriptor<
            '_,
            Self::BindGroupLayout,
            Self::Buffer,
            Self::TextureView,
            Self::Sampler,
        >,
    ) -> Result<Self::BindGroup, RhiError>;
    fn create_pipeline_layout(
        &self,
        descriptor: PipelineLayoutDescriptor<'_, Self::BindGroupLayout>,
    ) -> Result<Self::PipelineLayout, RhiError>;
    fn create_render_pipeline(
        &self,
        descriptor: RenderPipelineDescriptor<'_, Self::PipelineLayout, Self::ShaderModule>,
    ) -> Result<Self::RenderPipeline, RhiError>;
    fn create_compute_pipeline(
        &self,
        descriptor: ComputePipelineDescriptor<'_, Self::PipelineLayout, Self::ShaderModule>,
    ) -> Result<Self::ComputePipeline, RhiError>;
    fn create_command_encoder(&self, label: Option<&str>) -> Self::CommandEncoder;
}

/// Queue upload and submission interface.
pub trait RhiQueue<D: RhiDevice> {
    fn write_buffer(&self, buffer: &D::Buffer, offset: u64, data: &[u8]);
    fn write_texture(
        &self,
        destination: TextureCopy<'_, D::Texture>,
        data: &[u8],
        layout: TextureDataLayout,
        extent: Extent3d,
    );
    fn submit(&self, command_buffers: Vec<D::CommandBuffer>);
}

/// Presentation surface independent from window-provider types.
pub trait RhiSurface<D: RhiDevice, Q: RhiQueue<D>> {
    type Frame;
    fn format(&self) -> TextureFormat;
    fn resize(&mut self, device: &D, extent: Extent3d);
    fn acquire(
        &mut self,
        device: &D,
    ) -> Result<SurfaceAcquire<Self::Frame, D::TextureView>, RhiError>;
    fn present(&mut self, device: &D, queue: &Q, frame: Self::Frame);
}

/// Transfer, render, and compute command encoder.
pub trait RhiCommandEncoder: Sized {
    type Buffer;
    type Texture;
    type TextureView;
    type BindGroup;
    type RenderPipeline;
    type ComputePipeline;
    type CommandBuffer;
    type RenderPass<'pass>: RhiRenderPass<
            'pass,
            Buffer = Self::Buffer,
            BindGroup = Self::BindGroup,
            Pipeline = Self::RenderPipeline,
        >
    where
        Self: 'pass;
    type ComputePass<'pass>: RhiComputePass<'pass, BindGroup = Self::BindGroup, Pipeline = Self::ComputePipeline>
    where
        Self: 'pass;
    fn copy_buffer_to_buffer(
        &mut self,
        source: &Self::Buffer,
        source_offset: u64,
        destination: &Self::Buffer,
        destination_offset: u64,
        size: u64,
    );
    fn copy_buffer_to_texture(
        &mut self,
        source: BufferTextureCopy<'_, Self::Buffer>,
        destination: TextureCopy<'_, Self::Texture>,
        extent: Extent3d,
    );
    fn copy_texture_to_buffer(
        &mut self,
        source: TextureCopy<'_, Self::Texture>,
        destination: BufferTextureCopy<'_, Self::Buffer>,
        extent: Extent3d,
    );
    fn begin_render_pass<'pass>(
        &'pass mut self,
        descriptor: RenderPassDescriptor<'pass, Self::TextureView>,
    ) -> Self::RenderPass<'pass>;
    fn begin_compute_pass<'pass>(
        &'pass mut self,
        descriptor: ComputePassDescriptor<'pass>,
    ) -> Self::ComputePass<'pass>;
    fn finish(self) -> Self::CommandBuffer;
}

pub trait RhiRenderPass<'pass> {
    type Buffer: 'pass;
    type BindGroup: 'pass;
    type Pipeline: 'pass;
    fn set_pipeline(&mut self, pipeline: &'pass Self::Pipeline);
    fn set_bind_group(&mut self, index: u32, bind_group: &'pass Self::BindGroup, offsets: &[u32]);
    fn set_vertex_buffer(&mut self, slot: u32, buffer: &'pass Self::Buffer, range: Range<u64>);
    fn set_index_buffer(
        &mut self,
        buffer: &'pass Self::Buffer,
        format: IndexFormat,
        range: Range<u64>,
    );
    fn set_viewport(
        &mut self,
        x: f32,
        y: f32,
        width: f32,
        height: f32,
        min_depth: f32,
        max_depth: f32,
    );
    fn set_scissor_rect(&mut self, x: u32, y: u32, width: u32, height: u32);
    fn draw(&mut self, vertices: Range<u32>, instances: Range<u32>);
    fn draw_indexed(&mut self, indices: Range<u32>, base_vertex: i32, instances: Range<u32>);
}

pub trait RhiComputePass<'pass> {
    type BindGroup: 'pass;
    type Pipeline: 'pass;
    fn set_pipeline(&mut self, pipeline: &'pass Self::Pipeline);
    fn set_bind_group(&mut self, index: u32, bind_group: &'pass Self::BindGroup, offsets: &[u32]);
    fn dispatch(&mut self, x: u32, y: u32, z: u32);
}

#[cfg(test)]
mod tests {
    use super::{
        BufferUsages, Color, Extent3d, RhiError, RhiErrorKind, ShaderFormat, ShaderStages,
        builtin_shaders,
    };

    #[test]
    fn extent_is_unpresentable_when_any_dimension_is_zero() {
        assert!(!Extent3d::surface(1280, 720).is_zero());
        assert!(Extent3d::surface(0, 720).is_zero());
        assert!(Extent3d::new(4, 4, 0).is_zero());
    }

    #[test]
    fn usage_flags_compose_without_backend_types() {
        let usages = BufferUsages::VERTEX | BufferUsages::COPY_DESTINATION;
        assert!(usages.contains(BufferUsages::VERTEX));
        assert!(usages.contains(BufferUsages::COPY_DESTINATION));
        assert!(!usages.contains(BufferUsages::INDEX));
        assert_eq!((ShaderStages::VERTEX | ShaderStages::FRAGMENT).bits(), 3);
    }

    #[test]
    fn public_values_preserve_backend_neutral_data() {
        assert_eq!(Color::WHITE, Color::new(1.0, 1.0, 1.0, 1.0));
        let error = RhiError::new(RhiErrorKind::Unsupported, "no adapter");
        assert_eq!(error.kind(), RhiErrorKind::Unsupported);
        assert_eq!(error.to_string(), "no adapter");
    }

    #[test]
    fn rhi_describes_an_externally_loaded_bootstrap_artifact() {
        let artifact = builtin_shaders::bootstrap_wgsl(b"generated shader");

        assert_eq!(artifact.module.format, ShaderFormat::Wgsl);
        assert_eq!(artifact.module.code, b"generated shader");
        assert_eq!(artifact.vertex_entry_point, "vertex_main");
        assert_eq!(artifact.fragment_entry_point, "fragment_main");
    }
}
