#include "pbr.slang"
struct VertexInput { float3 position : POSITION; float2 uv : TEXCOORD0; float3 normal : NORMAL; };
[shader("vertex")]
VertexOutput vertex_main(VertexInput input)
{
    return vertexOutput(float4(input.position, 1.), input.normal, input.uv);
}
