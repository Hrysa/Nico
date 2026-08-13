struct ShadowPushConstants
{
    row_major float4x4 model;
    row_major float4x4 lightViewProjection;
};

[[vk::push_constant]]
ConstantBuffer<ShadowPushConstants> pc;

[[vk::binding(0, 4)]]
StructuredBuffer<row_major float4x4> jointMatrices;

struct StaticVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

struct SkinnedVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(5)]] uint4 joints : BLENDINDICES0;
    [[vk::location(6)]] float4 weights : BLENDWEIGHT0;
};

[shader("vertex")]
float4 staticVertexMain(StaticVertexInput input) : SV_Position
{
    return mul(mul(float4(input.position, 1.0), pc.model), pc.lightViewProjection);
}

[shader("vertex")]
float4 skinnedVertexMain(SkinnedVertexInput input) : SV_Position
{
    float4 bindPosition = float4(input.position, 1.0);
    float4 position =
        mul(bindPosition, jointMatrices[input.joints.x]) * input.weights.x +
        mul(bindPosition, jointMatrices[input.joints.y]) * input.weights.y +
        mul(bindPosition, jointMatrices[input.joints.z]) * input.weights.z +
        mul(bindPosition, jointMatrices[input.joints.w]) * input.weights.w;
    return mul(mul(position, pc.model), pc.lightViewProjection);
}
