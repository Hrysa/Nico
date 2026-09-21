// Shared 112-byte source record for direct vertex streams and storage fetch.
// Include after pbr.slang, which owns the per-view draw transform and PBR varyings.
struct InstanceData {
    float4 model0; float4 model1; float4 model2;
    float4 normal0; float4 normal1; float4 normal2;
    float4 tint;
};
struct PackedInstanceData {
    float4 model0; float4 model1; float4 model2;
    float4 responseInverseDeterminant; float4 tint;
};
InstanceData decodeInstance(PackedInstanceData packed) {
    InstanceData data;
    data.model0=packed.model0; data.model1=packed.model1; data.model2=packed.model2;
    float inverseDeterminant=packed.responseInverseDeterminant.w;
    data.normal0=float4(cross(packed.model1.xyz,packed.model2.xyz)*inverseDeterminant,packed.responseInverseDeterminant.x);
    data.normal1=float4(cross(packed.model2.xyz,packed.model0.xyz)*inverseDeterminant,packed.responseInverseDeterminant.y);
    data.normal2=float4(cross(packed.model0.xyz,packed.model1.xyz)*inverseDeterminant,packed.responseInverseDeterminant.z);
    data.tint=packed.tint;
    return data;
}
#ifdef NICO_FOLIAGE
#include "foliage_deform.slang"
#endif
VertexOutput instanceVertex(float3 position, float2 uv, float3 normal, InstanceData data) {
    float4 local=float4(position,1.);
    float3 world=float3(dot(data.model0,local),dot(data.model1,local),dot(data.model2,local));
    float3 worldNormal=float3(dot(data.normal0.xyz,normal),dot(data.normal1.xyz,normal),dot(data.normal2.xyz,normal));
#ifdef NICO_FOLIAGE
    deformFoliage(position,data,world,worldNormal);
#endif
    VertexOutput output;
    output.position=mul(draw.transform,float4(world,1.));
    output.worldPosition=world;
    output.normal=worldNormal;
    output.uv=uv;
    output.tint=data.tint;
    return output;
}
