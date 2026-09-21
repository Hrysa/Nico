// Version-one 800-byte uniform ABI; CPU encoder lives in nico-render/foliage.rs.
struct FoliageField {
    float4 positionRadius;
    float4 directionStrength;
    uint4 kindPadding;
};
struct FoliageUniform {
    float4 profile; // root Y, reciprocal height, maximum world bend, visual cycle
    uint4 counts;
    FoliageField fields[16];
};
[[vk::binding(3, 3)]] ConstantBuffer<FoliageUniform> foliage;

float3 foliageClamp(float3 value, float maximum) {
    float magnitude = length(value);
    return magnitude > maximum && magnitude > 0. ? value * (maximum / magnitude) : value;
}

void deformFoliage(float3 localPosition, InstanceData data, inout float3 world, inout float3 normal) {
    if (foliage.counts.x == 0u) {
        normal = normalize(normal);
        return;
    }
    float4 localRoot = float4(0., foliage.profile.x, 0., 1.);
    float3 root = float3(dot(data.model0, localRoot), dot(data.model1, localRoot), dot(data.model2, localRoot));
    float3 combined = float3(0.);
    float windWeight = 1. - data.normal2.w * 0.5 * (1. - cos((foliage.profile.w + data.normal0.w) * 6.283185307179586));
    for (uint i = 0; i < min(foliage.counts.x, 16u); ++i) {
        FoliageField field = foliage.fields[i];
        float3 delta = root - field.positionRadius.xyz;
        float distance = length(delta);
        if (distance >= field.positionRadius.w) continue;
        float falloff = 1. - distance / field.positionRadius.w;
        float3 direction = field.directionStrength.xyz;
        if (field.kindPadding.x == 1u && distance > 0.) direction = delta / distance;
        float response = field.kindPadding.x == 0u ? windWeight : 1.;
        combined += direction * (field.directionStrength.w * falloff * falloff * response);
    }
    float3 gradient = float3(data.normal0.y, data.normal1.y, data.normal2.y) * foliage.profile.y;
    float3 axis = normalize(gradient);
    float3 bend = foliageClamp(combined, 1.) * (foliage.profile.z * data.normal1.w);
    bend = foliageClamp(bend - axis * dot(bend, axis), foliage.profile.z);
    float height = (localPosition.y - foliage.profile.x) * foliage.profile.y;
    float weight = clamp(height, 0., 1.);
    float derivative = height > 0. && height <= 1. ? 2. * weight : 0.;
    world += bend * weight * weight;
    normal = normalize(normal - gradient * (derivative * dot(bend, normal)));
}
