struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct _Array_std140_matrixx3Cfloatx2C4x2C4x3E256_0
{
    @align(16) data_1 : array<_MatrixStorage_float4x4_ColMajorstd140_0, i32(256)>,
};

struct SkinPalette_std140_0
{
    @align(16) joints_0 : _Array_std140_matrixx3Cfloatx2C4x2C4x3E256_0,
};

@binding(0) @group(2) var<uniform> skin_0 : SkinPalette_std140_0;
struct DrawUniforms_std140_0
{
    @align(16) transform_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) color_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> draw_0 : DrawUniforms_std140_0;
@binding(0) @group(0) var image_0 : texture_2d<f32>;

@binding(1) @group(0) var imageSampler_0 : sampler;

struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
    @location(1) uv_1 : vec2<f32>,
    @location(2) joints_1 : vec4<u32>,
    @location(3) weights_0 : vec4<f32>,
};

@vertex
fn vertex_main( _S1 : vertexInput_0) -> VertexOutput_0
{
    var output_0 : VertexOutput_0;
    var _S2 : vec4<f32> = vec4<f32>(0.0f);
    var i_0 : u32 = u32(0);
    var position_2 : vec4<f32> = _S2;
    for(;;)
    {
        if(i_0 < u32(4))
        {
        }
        else
        {
            break;
        }
        var position_3 : vec4<f32> = position_2 + (((vec4<f32>(_S1.position_1, 1.0f)) * (mat4x4<f32>(skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(3)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(3)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(3)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(3)])))) * vec4<f32>(_S1.weights_0[i_0]);
        i_0 = i_0 + u32(1);
        position_2 = position_3;
    }
    output_0.position_0 = (((position_2) * (mat4x4<f32>(draw_0.transform_0.data_0[i32(0)][i32(0)], draw_0.transform_0.data_0[i32(1)][i32(0)], draw_0.transform_0.data_0[i32(2)][i32(0)], draw_0.transform_0.data_0[i32(3)][i32(0)], draw_0.transform_0.data_0[i32(0)][i32(1)], draw_0.transform_0.data_0[i32(1)][i32(1)], draw_0.transform_0.data_0[i32(2)][i32(1)], draw_0.transform_0.data_0[i32(3)][i32(1)], draw_0.transform_0.data_0[i32(0)][i32(2)], draw_0.transform_0.data_0[i32(1)][i32(2)], draw_0.transform_0.data_0[i32(2)][i32(2)], draw_0.transform_0.data_0[i32(3)][i32(2)], draw_0.transform_0.data_0[i32(0)][i32(3)], draw_0.transform_0.data_0[i32(1)][i32(3)], draw_0.transform_0.data_0[i32(2)][i32(3)], draw_0.transform_0.data_0[i32(3)][i32(3)]))));
    output_0.uv_0 = _S1.uv_1;
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_2 : vec2<f32>,
};

@fragment
fn fragment_main( _S3 : pixelInput_0, @builtin(position) position_4 : vec4<f32>) -> pixelOutput_0
{
    var color_1 : vec4<f32> = (textureSample((image_0), (imageSampler_0), (_S3.uv_2))) * draw_0.color_0;
    if((color_1.w) < 0.5f)
    {
        discard;
    }
    var _S4 : pixelOutput_0 = pixelOutput_0( vec4<f32>(color_1.xyz, 1.0f) );
    return _S4;
}
