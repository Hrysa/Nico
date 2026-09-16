struct VertexInput { float3 position : POSITION; float2 uv : TEXCOORD0; uint4 joints : BLENDINDICES; float4 weights : BLENDWEIGHT; };
struct VertexOutput { float4 position : SV_Position; float2 uv : TEXCOORD0; };
struct DrawUniforms { column_major float4x4 transform; float4 color; };
[[vk::binding(0, 0)]] Texture2D<float4> image;
[[vk::binding(1, 0)]] SamplerState imageSampler;
[[vk::binding(0, 1)]] ConstantBuffer<DrawUniforms> draw;

struct SkinPalette { column_major float4x4 joints[256]; };
[[vk::binding(0, 2)]] ConstantBuffer<SkinPalette> skin;

[shader("vertex")]
VertexOutput vertex_main(VertexInput input)
{
    VertexOutput output;
    float4 position = float4(0.0);
    for (uint i = 0; i < 4; ++i)
        position += mul(skin.joints[input.joints[i]], float4(input.position, 1.0)) * input.weights[i];
    output.position = mul(draw.transform, position);
    output.uv = input.uv;
    return output;
}

[shader("fragment")]
float4 fragment_main(VertexOutput input) : SV_Target0
{
    float4 color = image.Sample(imageSampler, input.uv) * draw.color;
    if (color.a < 0.5) discard;
    return float4(color.rgb, 1.0);
}
