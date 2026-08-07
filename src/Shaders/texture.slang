struct PushConstants
{
    row_major float4x4 model;
    row_major float4x4 view;
    row_major float4x4 projection;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> pc;

[[vk::binding(0, 0)]]
Sampler2D textureSampler;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float2 textureCoordinate : TEXCOORD0;
    [[vk::location(2)]] float opacity : TEXCOORD1;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 textureCoordinate : TEXCOORD0;
    [[vk::location(1)]] float opacity : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    float4 worldPosition = mul(float4(input.position, 1.0), pc.model);
    float4 viewPosition = mul(worldPosition, pc.view);
    output.position = mul(viewPosition, pc.projection);
    output.textureCoordinate = input.textureCoordinate;
    output.opacity = input.opacity;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float4 color = textureSampler.Sample(input.textureCoordinate);
    color.a *= input.opacity;
    return color;
}
