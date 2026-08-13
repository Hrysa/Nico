struct PushConstants
{
    row_major float4x4 model;
    row_major float4x4 viewProjection;
    row_major float4x4 lightViewProjection;
    float4 lightDirectionIntensity;
    float4 lightColorAmbient;
    float4 cameraPositionMetallic;
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

[[vk::binding(0, 3)]]
Sampler2D shadowMap;

[[vk::binding(0, 4)]]
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

float DistributionGgx(float normalDotHalf, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSquared = alpha * alpha;
    float denominator = normalDotHalf * normalDotHalf * (alphaSquared - 1.0) + 1.0;
    return alphaSquared / max(3.14159265 * denominator * denominator, 0.000001);
}

float GeometrySchlickGgx(float normalDotDirection, float roughness)
{
    float radius = roughness + 1.0;
    float k = radius * radius / 8.0;
    return normalDotDirection /
        max(normalDotDirection * (1.0 - k) + k, 0.000001);
}

float3 FresnelSchlick(float cosine, float3 reflectance)
{
    return reflectance + (float3(1.0, 1.0, 1.0) - reflectance) *
        pow(saturate(1.0 - cosine), 5.0);
}

float ShadowVisibility(float3 worldPosition, float normalDotLight)
{
    if (pc.materialFactors.y <= 0.0)
        return 1.0;
    float4 lightClip = mul(float4(worldPosition, 1.0), pc.lightViewProjection);
    float3 projected = lightClip.xyz / lightClip.w;
    float2 uv = projected.xy * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z >= 1.0 ||
        any(uv < float2(0.0, 0.0)) || any(uv > float2(1.0, 1.0)))
        return 1.0;
    float bias = max(0.0015 * (1.0 - normalDotLight), 0.00035);
    float occluded = 0.0;
    const float texel = 1.0 / 2048.0;
    for (int y = 0; y <= 1; y++)
    {
        for (int x = 0; x <= 1; x++)
        {
            float2 offset = (float2(x, y) - 0.5) * texel;
            float depth = shadowMap.Sample(uv + offset).r;
            occluded += projected.z - bias > depth ? 1.0 : 0.0;
        }
    }
    return 1.0 - pc.materialFactors.y * occluded / 4.0;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    float3 geometricNormal = normalize(input.worldNormal) * (isFrontFace ? 1.0 : -1.0);
    float3 tangent = normalize(input.worldTangent.xyz - geometricNormal *
        dot(input.worldTangent.xyz, geometricNormal));
    float3 bitangent = normalize(cross(geometricNormal, tangent)) * input.worldTangent.w;
    float3 sampledNormal = normalTexture.Sample(input.texCoord).xyz * 2.0 - 1.0;
    float3 normal = normalize(tangent * sampledNormal.x + bitangent * sampledNormal.y +
        geometricNormal * sampledNormal.z);

    float4 surfaceColor = input.baseColor * baseColorTexture.Sample(input.texCoord);
    float4 materialSample = metallicRoughnessTexture.Sample(input.texCoord);
    float metallic = saturate(pc.cameraPositionMetallic.w * materialSample.b);
    float roughness = clamp(pc.materialFactors.x * materialSample.g, 0.045, 1.0);

    float3 viewDirection = normalize(pc.cameraPositionMetallic.xyz - input.worldPosition);
    float3 lightDirection = normalize(pc.lightDirectionIntensity.xyz);
    float3 halfwayDirection = normalize(viewDirection + lightDirection);
    float normalDotView = max(dot(normal, viewDirection), 0.0001);
    float normalDotLight = max(dot(normal, lightDirection), 0.0);
    float normalDotHalf = max(dot(normal, halfwayDirection), 0.0);
    float halfwayDotView = max(dot(halfwayDirection, viewDirection), 0.0);

    float3 reflectance = lerp(float3(0.04, 0.04, 0.04), surfaceColor.rgb, metallic);
    float3 fresnel = FresnelSchlick(halfwayDotView, reflectance);
    float distribution = DistributionGgx(normalDotHalf, roughness);
    float geometry = GeometrySchlickGgx(normalDotView, roughness) *
        GeometrySchlickGgx(normalDotLight, roughness);
    float3 specular = distribution * geometry * fresnel /
        max(4.0 * normalDotView * normalDotLight, 0.0001);
    float3 diffuseWeight = (float3(1.0, 1.0, 1.0) - fresnel) * (1.0 - metallic);
    float3 radiance = pc.lightColorAmbient.rgb * pc.lightDirectionIntensity.w;
    float visibility = ShadowVisibility(input.worldPosition, normalDotLight);
    float3 direct = (diffuseWeight * surfaceColor.rgb / 3.14159265 + specular) *
        radiance * normalDotLight * visibility;
    float3 ambient = (surfaceColor.rgb * (1.0 - metallic) + reflectance * 0.25) *
        pc.lightColorAmbient.rgb * pc.lightColorAmbient.w;
    float ambientVisibility = lerp(1.0, visibility, 0.5);
    return float4(ambient * ambientVisibility + direct, surfaceColor.a);
}
