struct GpuSceneLight
{
    float4 positionRange;
    float4 directionType;
    float4 colorIntensity;
    float4 spotParameters;
};

struct SceneLightingData
{
    float4 ambientIntensity;
    float4 cameraPositionLightCount;
    float4 mainLightIndex;
    GpuSceneLight lights[64];
};

[[vk::binding(0, 4)]]
ConstantBuffer<SceneLightingData> sceneLighting;

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

float3 EvaluateForwardLighting(
    float3 worldPosition,
    float3 normal,
    float3 baseColor,
    float metallic,
    float roughness)
{
    float3 viewDirection = normalize(
        sceneLighting.cameraPositionLightCount.xyz - worldPosition);
    float normalDotView = max(dot(normal, viewDirection), 0.0001);
    float3 reflectance = lerp(float3(0.04, 0.04, 0.04), baseColor, metallic);
    float3 ambient = (baseColor * (1.0 - metallic) + reflectance * 0.25) *
        sceneLighting.ambientIntensity.rgb * sceneLighting.ambientIntensity.w;
    float3 direct = float3(0.0, 0.0, 0.0);
    int lightCount = min((int)round(sceneLighting.cameraPositionLightCount.w), 64);
    int mainLightIndex = (int)round(sceneLighting.mainLightIndex.x);

    for (int lightIndex = 0; lightIndex < lightCount; lightIndex++)
    {
        GpuSceneLight light = sceneLighting.lights[lightIndex];
        int lightType = (int)round(light.directionType.w);
        float3 lightDirection;
        float attenuation = 1.0;
        if (lightType == 0)
        {
            lightDirection = normalize(light.directionType.xyz);
        }
        else
        {
            float3 toLight = light.positionRange.xyz - worldPosition;
            float distanceSquared = max(dot(toLight, toLight), 0.0001);
            float distanceToLight = sqrt(distanceSquared);
            float range = max(light.positionRange.w, 0.0001);
            if (distanceToLight >= range)
                continue;
            lightDirection = toLight / distanceToLight;
            float normalizedDistance = distanceToLight / range;
            float rangeWindow = saturate(1.0 - pow(normalizedDistance, 4.0));
            attenuation = rangeWindow * rangeWindow / distanceSquared;
            if (lightType == 2)
            {
                float coneCosine = dot(normalize(light.directionType.xyz), -lightDirection);
                attenuation *= smoothstep(
                    light.spotParameters.y, light.spotParameters.x, coneCosine);
            }
        }

        float normalDotLight = max(dot(normal, lightDirection), 0.0);
        if (normalDotLight <= 0.0 || attenuation <= 0.0)
            continue;
        float3 halfwayDirection = normalize(viewDirection + lightDirection);
        float normalDotHalf = max(dot(normal, halfwayDirection), 0.0);
        float halfwayDotView = max(dot(halfwayDirection, viewDirection), 0.0);
        float3 fresnel = FresnelSchlick(halfwayDotView, reflectance);
        float distribution = DistributionGgx(normalDotHalf, roughness);
        float geometry = GeometrySchlickGgx(normalDotView, roughness) *
            GeometrySchlickGgx(normalDotLight, roughness);
        float3 specular = distribution * geometry * fresnel /
            max(4.0 * normalDotView * normalDotLight, 0.0001);
        float3 diffuseWeight = (float3(1.0, 1.0, 1.0) - fresnel) * (1.0 - metallic);
        float visibility = 1.0;
        if (lightIndex == mainLightIndex && lightType == 0)
        {
            visibility = ShadowVisibility(worldPosition, normal,
                sceneLighting.cameraPositionLightCount.xyz);
        }
        else if (lightType != 0)
        {
            visibility = LocalShadowVisibility(
                (int)round(light.spotParameters.z), lightType,
                light.positionRange.xyz, light.positionRange.w,
                worldPosition, normal);
        }
        float3 radiance = light.colorIntensity.rgb * light.colorIntensity.w * attenuation;
        direct += (diffuseWeight * baseColor / 3.14159265 + specular) *
            radiance * normalDotLight * visibility;
    }
    return ambient + direct;
}
