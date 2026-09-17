#include "pbr.slang"
struct VertexInput {
    float3 position : POSITION; float2 uv : TEXCOORD0; float3 normal : NORMAL;
    uint4 joints : BLENDINDICES; float4 weights : BLENDWEIGHT;
};
struct SkinPalette { column_major float4x4 joints[256]; };
[[vk::binding(0, 3)]] ConstantBuffer<SkinPalette> skin;
[shader("vertex")]
VertexOutput vertex_main(VertexInput input)
{
    float4x4 matrix = float4x4(0.);
    for (uint i = 0; i < 4; ++i)
        matrix += skin.joints[input.joints[i]] * input.weights[i];
    float3 a = mul(matrix, float4(1., 0., 0., 0.)).xyz;
    float3 b = mul(matrix, float4(0., 1., 0., 0.)).xyz;
    float3 c = mul(matrix, float4(0., 0., 1., 0.)).xyz;
    // Remove the common scale before testing singularity so asset units do not
    // decide whether valid nonuniform transforms use the inverse transpose.
    float3 largest = max(abs(a), max(abs(b), abs(c)));
    float scale = max(largest.x, max(largest.y, largest.z));
    // Singular blends have no defined normal; use the transformed input as fallback.
    float3 normal = mul(matrix, float4(input.normal, 0.)).xyz;
    if (scale > 0.) {
        a /= scale;
        b /= scale;
        c /= scale;
        float determinant = dot(a, cross(b, c));
        if (abs(determinant) > 1e-10) {
            // Restore inverse scale as well as direction before interpolation.
            normal = ((cross(b, c) * input.normal.x + cross(c, a) * input.normal.y + cross(a, b) * input.normal.z) / determinant) / scale;
        }
    }
    return vertexOutput(mul(matrix, float4(input.position, 1.)), normal, input.uv);
}
