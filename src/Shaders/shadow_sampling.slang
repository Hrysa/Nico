struct DirectionalShadowData
{
    row_major float4x4 cascadeMatrices[4];
    float4 cascadeSplits;
    float4 cameraForwardCascadeCount;
    float4 shadowParameters;
    float4 worldTexelSizes;
};

[[vk::binding(0, 3)]]
Texture2D<float> shadowAtlas;

[[vk::binding(1, 3)]]
SamplerComparisonState shadowSampler;

[[vk::binding(2, 3)]]
ConstantBuffer<DirectionalShadowData> shadowData;

struct LocalShadowData
{
    row_major float4x4 shadowMatrices[24];
    float4 parameters;
};

[[vk::binding(3, 3)]]
Texture2D<float> localShadowAtlas;

[[vk::binding(4, 3)]]
SamplerComparisonState localShadowSampler;

[[vk::binding(5, 3)]]
ConstantBuffer<LocalShadowData> localShadowData;

float SampleShadowCascade(int cascadeIndex, float3 worldPosition, float3 worldNormal)
{
    float normalOffset = shadowData.shadowParameters.z *
        shadowData.worldTexelSizes[cascadeIndex];
    float3 receiverPosition = worldPosition + worldNormal * normalOffset;
    float4 lightClip = mul(float4(receiverPosition, 1.0),
        shadowData.cascadeMatrices[cascadeIndex]);
    float3 projected = lightClip.xyz / lightClip.w;
    float2 localUv = projected.xy * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z >= 1.0 ||
        any(localUv < float2(0.0, 0.0)) || any(localUv > float2(1.0, 1.0)))
        return 1.0;

    float inverseResolution = shadowData.shadowParameters.w;
    // Raster bias alone is quantized to depth-buffer units and can leave animated,
    // low-poly receivers comparing against their own stored depth. Pull the receiver
    // half a shadow texel toward the light before the comparison to suppress acne
    // without moving the caster geometry or widening silhouettes.
    float comparisonDepth = projected.z - inverseResolution * 0.5;
    float border = inverseResolution * 1.5;
    localUv = clamp(localUv, float2(border, border),
        float2(1.0 - border, 1.0 - border));
    float2 tileOffset = float2(cascadeIndex & 1, cascadeIndex >> 1) * 0.5;
    float visibility = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 filteredLocalUv = localUv + float2(x, y) * inverseResolution;
            float2 atlasUv = tileOffset + filteredLocalUv * 0.5;
            visibility += shadowAtlas.SampleCmpLevelZero(
                shadowSampler, atlasUv, comparisonDepth);
        }
    }
    return visibility / 9.0;
}

float ShadowVisibility(
    float3 worldPosition,
    float3 worldNormal,
    float3 cameraPosition)
{
    float strength = shadowData.shadowParameters.x;
    if (strength <= 0.0)
        return 1.0;
    int cascadeCount = (int)shadowData.cameraForwardCascadeCount.w;
    float viewDepth = dot(worldPosition - cameraPosition,
        shadowData.cameraForwardCascadeCount.xyz);
    int cascadeIndex = 0;
    while (cascadeIndex < cascadeCount &&
        viewDepth > shadowData.cascadeSplits[cascadeIndex])
    {
        cascadeIndex++;
    }
    if (cascadeIndex >= cascadeCount)
        return 1.0;

    float visibility = SampleShadowCascade(cascadeIndex, worldPosition, worldNormal);
    if (cascadeIndex + 1 < cascadeCount)
    {
        float cascadeNear = cascadeIndex == 0
            ? 0.0 : shadowData.cascadeSplits[cascadeIndex - 1];
        float cascadeFar = shadowData.cascadeSplits[cascadeIndex];
        float blendWidth = (cascadeFar - cascadeNear) * shadowData.shadowParameters.y;
        if (blendWidth > 0.0 && viewDepth > cascadeFar - blendWidth)
        {
            float nextVisibility = SampleShadowCascade(
                cascadeIndex + 1, worldPosition, worldNormal);
            float blend = saturate((viewDepth - (cascadeFar - blendWidth)) / blendWidth);
            visibility = lerp(visibility, nextVisibility, blend);
        }
    }
    return lerp(1.0, visibility, strength);
}

int SelectPointShadowFace(float3 direction)
{
    float3 absoluteDirection = abs(direction);
    if (absoluteDirection.x >= absoluteDirection.y &&
        absoluteDirection.x >= absoluteDirection.z)
        return direction.x >= 0.0 ? 0 : 1;
    if (absoluteDirection.y >= absoluteDirection.z)
        return direction.y >= 0.0 ? 2 : 3;
    return direction.z >= 0.0 ? 4 : 5;
}

float LocalShadowVisibility(
    int shadowIndex,
    int lightType,
    float3 lightPosition,
    float lightRange,
    float3 worldPosition,
    float3 worldNormal)
{
    float strength = localShadowData.parameters.x;
    if (shadowIndex < 0 || shadowIndex >= 4 || strength <= 0.0)
        return 1.0;
    float3 fromLight = worldPosition - lightPosition;
    int faceIndex = lightType == 1 ? SelectPointShadowFace(fromLight) : 0;
    float normalOffset = localShadowData.parameters.y * lightRange;
    float3 receiverPosition = worldPosition + worldNormal * normalOffset;
    float4 lightClip = mul(float4(receiverPosition, 1.0),
        localShadowData.shadowMatrices[shadowIndex * 6 + faceIndex]);
    float3 projected = lightClip.xyz / lightClip.w;
    float2 localUv = projected.xy * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z >= 1.0 ||
        any(localUv < float2(0.0, 0.0)) || any(localUv > float2(1.0, 1.0)))
        return 1.0;

    float inverseResolution = localShadowData.parameters.z;
    float border = inverseResolution * 1.5;
    localUv = clamp(localUv, float2(border, border),
        float2(1.0 - border, 1.0 - border));
    float2 atlasScale = float2(1.0 / 6.0, 1.0 / 4.0);
    float2 tileOffset = float2(faceIndex, shadowIndex) * atlasScale;
    float visibility = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 filteredLocalUv = localUv + float2(x, y) * inverseResolution;
            float2 atlasUv = tileOffset + filteredLocalUv * atlasScale;
            visibility += localShadowAtlas.SampleCmpLevelZero(
                localShadowSampler, atlasUv, projected.z);
        }
    }
    visibility /= 9.0;
    return lerp(1.0, visibility, strength);
}
