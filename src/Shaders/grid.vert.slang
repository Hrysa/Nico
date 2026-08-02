struct GridPushConstants
{
    row_major float4x4 viewProjection;
    row_major float4x4 inverseViewProjection;
};

[[vk::push_constant]]
ConstantBuffer<GridPushConstants> pc;

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
VertexOutput main(uint vertexIndex : SV_VertexID)
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
    output.position = float4(position, 0.0, 1.0);
    return output;
}
