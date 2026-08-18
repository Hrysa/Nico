struct PushConstants
{
    row_major float4x4 model;
    row_major float4x4 viewProjection;
    float4 materialFactors;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> pc;

[[vk::binding(0, 0)]]
Sampler2D baseColorTexture;

[[vk::binding(0, 1)]]
Sampler2D normalTexture;

[[vk::binding(0, 2)]]
Sampler2D metallicRoughnessTexture;

#include "shadow_sampling.slang"
#include "forward_lighting.slang"
#include "surface_normal.slang"

[[vk::binding(0, 5)]]
StructuredBuffer<row_major float4x4> jointMatrices;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 texCoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent : TANGENT;
    [[vk::location(4)]] float4 baseColor : COLOR0;
    [[vk::location(5)]] uint4 joints : BLENDINDICES0;
    [[vk::location(6)]] float4 weights : BLENDWEIGHT0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldPosition : POSITION0;
    [[vk::location(1)]] float3 worldNormal : NORMAL;
    [[vk::location(2)]] float4 worldTangent : TANGENT;
    [[vk::location(3)]] float2 texCoord : TEXCOORD0;
    [[vk::location(4)]] float4 baseColor : COLOR0;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    float4 bindPosition = float4(input.position, 1.0);
    float4 bindNormal = float4(input.normal, 0.0);
    float4 bindTangent = float4(input.tangent.xyz, 0.0);
    float4 skinnedPosition =
        mul(bindPosition, jointMatrices[input.joints.x]) * input.weights.x +
        mul(bindPosition, jointMatrices[input.joints.y]) * input.weights.y +
        mul(bindPosition, jointMatrices[input.joints.z]) * input.weights.z +
        mul(bindPosition, jointMatrices[input.joints.w]) * input.weights.w;
    float3 skinnedNormal =
        mul(bindNormal, jointMatrices[input.joints.x]).xyz * input.weights.x +
        mul(bindNormal, jointMatrices[input.joints.y]).xyz * input.weights.y +
        mul(bindNormal, jointMatrices[input.joints.z]).xyz * input.weights.z +
        mul(bindNormal, jointMatrices[input.joints.w]).xyz * input.weights.w;
    float3 skinnedTangent =
        mul(bindTangent, jointMatrices[input.joints.x]).xyz * input.weights.x +
        mul(bindTangent, jointMatrices[input.joints.y]).xyz * input.weights.y +
        mul(bindTangent, jointMatrices[input.joints.z]).xyz * input.weights.z +
        mul(bindTangent, jointMatrices[input.joints.w]).xyz * input.weights.w;
    float4 worldPosition = mul(skinnedPosition, pc.model);
    output.position = mul(worldPosition, pc.viewProjection);
    output.worldPosition = worldPosition.xyz;
    output.worldNormal = normalize(mul(float4(skinnedNormal, 0.0), pc.model).xyz);
    output.worldTangent = float4(
        normalize(mul(float4(skinnedTangent, 0.0), pc.model).xyz), input.tangent.w);
    output.texCoord = input.texCoord;
    output.baseColor = input.baseColor;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    float3 sampledNormal = normalTexture.Sample(input.texCoord).xyz * 2.0 - 1.0;
    float3 normal = ResolveSurfaceNormal(
        input.worldNormal, input.worldTangent, sampledNormal, isFrontFace);

    float4 surfaceColor = input.baseColor * baseColorTexture.Sample(input.texCoord);
    float4 materialSample = metallicRoughnessTexture.Sample(input.texCoord);
    float metallic = saturate(pc.materialFactors.y * materialSample.b);
    float roughness = clamp(pc.materialFactors.x * materialSample.g, 0.045, 1.0);
    return float4(EvaluateForwardLighting(
        input.worldPosition, normal, surfaceColor.rgb, metallic, roughness),
        surfaceColor.a);
}
