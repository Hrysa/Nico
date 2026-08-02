struct GridPushConstants
{
    row_major float4x4 viewProjection;
    row_major float4x4 inverseViewProjection;
};

[[vk::push_constant]]
ConstantBuffer<GridPushConstants> pc;

struct FragmentInput
{
    [[vk::location(0)]] float3 nearWorld : TEXCOORD0;
    [[vk::location(1)]] float3 farWorld : TEXCOORD1;
};

struct FragmentOutput
{
    float4 color : SV_Target0;
    float depth : SV_Depth;
};

float gridMask(float2 worldPosition, float spacing)
{
    float2 coordinate = worldPosition / spacing;
    float2 width = max(fwidth(coordinate), float2(0.0001, 0.0001));
    float2 distanceToLine = abs(frac(coordinate - 0.5) - 0.5) / width;
    return 1.0 - min(min(distanceToLine.x, distanceToLine.y), 1.0);
}

[shader("fragment")]
FragmentOutput main(FragmentInput input)
{
    float3 ray = input.farWorld - input.nearWorld;
    if (abs(ray.y) < 0.000001)
        discard;

    float rayDistance = -input.nearWorld.y / ray.y;
    if (rayDistance <= 0.0)
        discard;

    float3 worldPosition = input.nearWorld + ray * rayDistance;
    float minor = gridMask(worldPosition.xz, 1.0);
    float major = gridMask(worldPosition.xz, 5.0);
    float line = max(minor * 0.48, major);
    if (line <= 0.001)
        discard;

    float distanceFromCamera = length(worldPosition - input.nearWorld);
    float fade = 1.0 - smoothstep(20.0, 120.0, distanceFromCamera);
    float horizonFade = smoothstep(0.0, 0.025, abs(ray.y));
    float alpha = line * fade * horizonFade;
    if (alpha <= 0.001)
        discard;

    float3 minorColor = float3(0.22, 0.22, 0.24);
    float3 majorColor = float3(0.38, 0.38, 0.41);
    float3 color = lerp(minorColor, majorColor, major);
    float4 clip = mul(float4(worldPosition, 1.0), pc.viewProjection);

    FragmentOutput output;
    output.depth = clip.z / clip.w;
    output.color = float4(color, alpha);
    return output;
}
