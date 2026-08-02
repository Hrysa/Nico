[[vk::binding(0, 0)]]
Sampler2D textureSampler;

struct FragmentInput
{
    [[vk::location(0)]] float2 textureCoordinate : TEXCOORD0;
};

[shader("fragment")]
float4 main(FragmentInput input) : SV_Target0
{
    return textureSampler.Sample(input.textureCoordinate);
}
