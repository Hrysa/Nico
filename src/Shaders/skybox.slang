struct SkyboxPushConstants
{
    row_major float4x4 inverseViewProjection;
    float4 tintIntensity;
    float4 rotation;
};

[[vk::push_constant]]
ConstantBuffer<SkyboxPushConstants> pc;

[[vk::binding(0, 0)]]
Sampler2D skyboxTexture;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 nearWorld : TEXCOORD0;
    [[vk::location(1)]] float3 farWorld : TEXCOORD1;
};

float3 unproject(float2 position, float depth)
{
    float4 world = mul(float4(position, depth, 1.0), pc.inverseViewProjection);
    return world.xyz / world.w;
}

[shader("vertex")]
VertexOutput vertexMain(uint vertexIndex : SV_VertexID)
{
    static const float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(3.0, -1.0),
        float2(-1.0, 3.0)
    };

    VertexOutput output;
    float2 position = positions[vertexIndex];
    output.nearWorld = unproject(position, 0.0);
    output.farWorld = unproject(position, 1.0);
    output.position = float4(position, 1.0, 1.0);
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float3 direction = normalize(input.farWorld - input.nearWorld);
    float sine = sin(pc.rotation.x);
    float cosine = cos(pc.rotation.x);
    direction.xz = float2(
        cosine * direction.x - sine * direction.z,
        sine * direction.x + cosine * direction.z);

    const float inverseTau = 0.15915494309189535;
    const float inversePi = 0.3183098861837907;
    float2 uv = float2(
        0.5 + atan2(direction.z, direction.x) * inverseTau,
        acos(clamp(direction.y, -1.0, 1.0)) * inversePi);
    float3 color = skyboxTexture.Sample(uv).rgb *
        pc.tintIntensity.rgb * pc.tintIntensity.a;
    return float4(color, 1.0);
}
