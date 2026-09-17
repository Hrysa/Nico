// Shared metallic/roughness BRDF. Linear lighting is written to the sRGB target;
// this initial forward path has no HDR post-process or image-based specular light.
struct VertexOutput {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float3 worldPosition : TEXCOORD1;
    float3 normal : TEXCOORD2;
};
struct DrawUniforms {
    column_major float4x4 transform;
    column_major float4x4 model;
    column_major float4x4 normalTransform;
    float4 color;
};
struct MaterialUniforms { float4 baseColor; float4 emissive; float4 factors; float4 alpha; };
struct FrameUniforms { float4 camera; float4 lightDirection; float4 radiance; float4 ambient; };
[[vk::binding(0, 0)]] Texture2D<float4> baseImage;
[[vk::binding(1, 0)]] SamplerState baseSampler;
[[vk::binding(2, 0)]] Texture2D<float4> mrImage;
[[vk::binding(3, 0)]] SamplerState mrSampler;
[[vk::binding(4, 0)]] Texture2D<float4> normalImage;
[[vk::binding(5, 0)]] SamplerState normalSampler;
[[vk::binding(6, 0)]] Texture2D<float4> aoImage;
[[vk::binding(7, 0)]] SamplerState aoSampler;
[[vk::binding(8, 0)]] Texture2D<float4> emissiveImage;
[[vk::binding(9, 0)]] SamplerState emissiveSampler;
[[vk::binding(10, 0)]] ConstantBuffer<MaterialUniforms> material;
[[vk::binding(0, 1)]] ConstantBuffer<DrawUniforms> draw;
[[vk::binding(0, 2)]] ConstantBuffer<FrameUniforms> frame;

float3 safeNormalize(float3 value, float3 fallback)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-16 ? value * rsqrt(lengthSquared) : fallback;
}
VertexOutput vertexOutput(float4 position, float3 normal, float2 uv)
{
    VertexOutput output;
    output.position = mul(draw.transform, position);
    output.worldPosition = mul(draw.model, position).xyz;
    output.normal = mul(draw.normalTransform, float4(normal, 0.)).xyz;
    output.uv = uv;
    return output;
}

[shader("fragment")]
float4 fragment_main(VertexOutput input, bool front : SV_IsFrontFace) : SV_Target0
{
    float4 base = baseImage.Sample(baseSampler, input.uv) * material.baseColor * draw.color;
    // Derivatives and samples precede alpha discard and divergent branches.
    float2 mr = mrImage.Sample(mrSampler, input.uv).gb;
    float3 mapped = normalImage.Sample(normalSampler, input.uv).xyz * 2. - 1.;
    float ao = aoImage.Sample(aoSampler, input.uv).r;
    float3 emissive = emissiveImage.Sample(emissiveSampler, input.uv).rgb * material.emissive.rgb;
    float3 dx = ddx(input.worldPosition), dy = ddy(input.worldPosition);
    float2 uvx = ddx(input.uv), uvy = ddy(input.uv);
    float3 n = safeNormalize(input.normal, float3(0., 1., 0.));
    float determinant = uvx.x * uvy.y - uvx.y * uvy.x;
    if (material.alpha.z > 0. && abs(determinant) > 1e-10) {
        float3 rawT = (dx * uvy.y - dy * uvx.y) / determinant;
        float3 rawB = (dy * uvx.x - dx * uvy.x) / determinant;
        float3 t = rawT - n * dot(n, rawT);
        if (dot(t, t) > 1e-16) {
            t = normalize(t);
            float3 b = cross(n, t) * (dot(cross(n, t), rawB) < 0. ? -1. : 1.);
            mapped.xy *= material.factors.z;
            n = safeNormalize(t * mapped.x + b * mapped.y + n * mapped.z, n);
        }
    }
    // Reverse the complete mapped normal, including its tangent components.
    n *= front ? 1. : -1.;
    if (material.alpha.x == 1. && base.a < material.alpha.y) discard;
    float alpha = material.alpha.x == 2. ? base.a : 1.;
    float metallic = saturate(material.factors.x * mr.y);
    float roughness = clamp(material.factors.y * mr.x, 0.045, 1.);
    float3 v = safeNormalize(frame.camera.xyz - input.worldPosition, n);
    float3 l = frame.lightDirection.xyz;
    float3 h = safeNormalize(v + l, n);
    float nv = saturate(dot(n, v)), nl = saturate(dot(n, l));
    float nh = saturate(dot(n, h)), vh = saturate(dot(v, h));
    float3 f0 = lerp(float3(0.04), base.rgb, metallic);
    float3 fresnel = f0 + (1. - f0) * pow(1. - vh, 5.);
    float a = roughness * roughness, a2 = a * a;
    float denominator = nh * nh * (a2 - 1.) + 1.;
    float distribution = a2 / (3.14159265359 * denominator * denominator);
    // Height-correlated Smith visibility includes the 1/(4 NoL NoV) factor.
    float visibility = 0.5 / max(nl * sqrt(nv * nv * (1. - a2) + a2)
                              + nv * sqrt(nl * nl * (1. - a2) + a2), 1e-6);
    float3 diffuse = (1. - fresnel) * (1. - metallic) * base.rgb / 3.14159265359;
    float3 direct = (diffuse + fresnel * distribution * visibility) * frame.radiance.rgb * nl;
    float3 ambient = frame.ambient.rgb * base.rgb * (1. - metallic) * lerp(1., ao, material.factors.w);
    return float4(direct + ambient + emissive, alpha);
}
