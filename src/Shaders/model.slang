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

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 texCoord : TEXCOORD0;
    [[vk::location(3)]] float4 baseColor : COLOR0;
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
    float4 worldPosition = mul(float4(input.position, 1.0), pc.model);
    output.position = mul(mul(worldPosition, pc.view), pc.projection);
    output.worldNormal = normalize(mul(float4(input.normal, 0.0), pc.model).xyz);
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
