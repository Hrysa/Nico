struct PushConstants
{
    row_major float4x4 model;
    row_major float4x4 view;
    row_major float4x4 projection;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> pc;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 color : COLOR0;
    [[vk::location(2)]] float alpha : COLOR1;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 color : COLOR0;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    float4 worldPosition = mul(float4(input.position, 1.0), pc.model);
    float4 viewPosition = mul(worldPosition, pc.view);
    output.position = mul(viewPosition, pc.projection);
    output.color = float4(input.color, input.alpha);
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    return input.color;
}
