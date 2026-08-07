struct PushConstants
{
    row_major float4x4 model;
    row_major float4x4 view;
    row_major float4x4 projection;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> pc;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float4 color : COLOR0;
    [[vk::location(2)]] float2 localPosition : TEXCOORD0;
    [[vk::location(3)]] float2 halfSize : TEXCOORD1;
    [[vk::location(4)]] float2 shape : TEXCOORD2;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 color : COLOR0;
    [[vk::location(1)]] float2 localPosition : TEXCOORD0;
    [[vk::location(2)]] float2 halfSize : TEXCOORD1;
    [[vk::location(3)]] nointerpolation float2 shape : TEXCOORD2;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    float4 worldPosition = mul(float4(input.position, 1.0), pc.model);
    float4 viewPosition = mul(worldPosition, pc.view);
    output.position = mul(viewPosition, pc.projection);
    output.color = input.color;
    output.localPosition = input.localPosition;
    output.halfSize = input.halfSize;
    output.shape = input.shape;
    return output;
}

float roundedBoxDistance(float2 point, float2 halfSize, float radius)
{
    float2 innerHalfSize = max(halfSize - radius, 0.0);
    float2 delta = abs(point) - innerHalfSize;
    return length(max(delta, 0.0)) + min(max(delta.x, delta.y), 0.0) - radius;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float coverage;
    if (input.shape.x < 1.5)
    {
        float distance = roundedBoxDistance(
            input.localPosition, input.halfSize, input.shape.y);
        float filterWidth = max(fwidth(distance), 0.0001);
        coverage = saturate(0.5 - distance / filterWidth);
    }
    else
    {
        float2 safeHalfSize = max(input.halfSize, 0.0001);
        float2 normalized = input.localPosition / safeHalfSize;
        float implicitDistance = dot(normalized, normalized) - 1.0;
        float2 gradient = 2.0 * input.localPosition / (safeHalfSize * safeHalfSize);
        float signedDistance = implicitDistance / max(length(gradient), 0.0001);
        float distance = input.shape.x < 2.5
            ? signedDistance
            : abs(signedDistance) - input.shape.y * 0.5;
        float filterWidth = max(fwidth(distance), 0.0001);
        coverage = saturate(0.5 - distance / filterWidth);
    }

    return float4(input.color.rgb, input.color.a * coverage);
}
