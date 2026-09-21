#include "pbr.slang"
#include "instance_transform.slang"
struct InstanceVertexInput {
    float3 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 normal : NORMAL;
    float4 model0 : TEXCOORD1;
    float4 model1 : TEXCOORD2;
    float4 model2 : TEXCOORD3;
    float4 normal0 : TEXCOORD4;
    float4 normal1 : TEXCOORD5;
    float4 normal2 : TEXCOORD6;
    float4 tint : COLOR0;
};
[shader("vertex")]
VertexOutput vertex_main(InstanceVertexInput input)
{
    InstanceData data = {input.model0,input.model1,input.model2,input.normal0,input.normal1,input.normal2,input.tint};
    return instanceVertex(input.position,input.uv,input.normal,data);
}

struct CompactVertexInput {
    float3 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 normal : NORMAL;
    float4 model0 : TEXCOORD1;
    float4 model1 : TEXCOORD2;
    float4 model2 : TEXCOORD3;
    float4 responseInverseDeterminant : TEXCOORD4;
    float4 tint : COLOR0;
};
[shader("vertex")]
VertexOutput vertex_compact_main(CompactVertexInput input)
{
    PackedInstanceData packed = {input.model0,input.model1,input.model2,input.responseInverseDeterminant,input.tint};
    InstanceData data = decodeInstance(packed);
    return instanceVertex(input.position,input.uv,input.normal,data);
}
