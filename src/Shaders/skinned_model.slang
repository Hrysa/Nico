struct PushConstants
{
    row_major float4x4 model;
    row_major float4x4 view;
    row_major float4x4 projection;
    float4 lightDirectionIntensity;
    float4 lightColorAmbient;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> pc;

[[vk::binding(0, 0)]]
Sampler2D baseColorTexture;

[[vk::binding(0, 1)]]
StructuredBuffer<row_major float4x4> jointMatrices;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 texCoord : TEXCOORD0;
    [[vk::location(3)]] float4 baseColor : COLOR0;
    [[vk::location(4)]] uint4 joints : BLENDINDICES0;
    [[vk::location(5)]] float4 weights : BLENDWEIGHT0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldNormal : NORMAL;
    [[vk::location(1)]] float2 texCoord : TEXCOORD0;
    [[vk::location(2)]] float4 baseColor : COLOR0;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    float4 bindPosition = float4(input.position, 1.0);
    float4 bindNormal = float4(input.normal, 0.0);
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
    float4 worldPosition = mul(skinnedPosition, pc.model);
    output.position = mul(mul(worldPosition, pc.view), pc.projection);
    output.worldNormal = normalize(mul(float4(skinnedNormal, 0.0), pc.model).xyz);
    output.texCoord = input.texCoord;
    output.baseColor = input.baseColor;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float3 normal = normalize(input.worldNormal);
    float3 lightDirection = normalize(pc.lightDirectionIntensity.xyz);
    float diffuse = max(dot(normal, lightDirection), 0.0);
    float3 lighting = pc.lightColorAmbient.rgb *
        (pc.lightColorAmbient.w + diffuse * pc.lightDirectionIntensity.w);
    float4 sampledColor = baseColorTexture.Sample(input.texCoord);
    float4 surfaceColor = input.baseColor * sampledColor;
    return float4(surfaceColor.rgb * lighting, surfaceColor.a);
}
