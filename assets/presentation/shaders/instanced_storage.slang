#include "pbr.slang"
#include "instance_transform.slang"
struct GroupSelection { uint group; uint offsetsWord; uint idsWord; uint sourceBase; };
// Entry points use exactly one of these aliases; neither accesses both.
[[vk::binding(0,3)]] StructuredBuffer<InstanceData> instances;
[[vk::binding(0,3)]] StructuredBuffer<PackedInstanceData> compactInstances;
[[vk::binding(1,3)]] StructuredBuffer<uint> visibility;
[[vk::binding(2,3)]] ConstantBuffer<GroupSelection> selection;
struct StorageVertexInput {
    float3 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 normal : NORMAL;
    uint instance : SV_InstanceID;
};
[shader("vertex")]
VertexOutput vertex_main(StorageVertexInput input) {
    uint offset=visibility[selection.offsetsWord+selection.group];
    uint id=visibility[selection.idsWord+offset+input.instance];
    return instanceVertex(input.position,input.uv,input.normal,instances[id-selection.sourceBase]);
}

[shader("vertex")]
VertexOutput vertex_compact_main(StorageVertexInput input) {
    uint offset=visibility[selection.offsetsWord+selection.group];
    uint id=visibility[selection.idsWord+offset+input.instance];
    return instanceVertex(input.position,input.uv,input.normal,decodeInstance(compactInstances[id-selection.sourceBase]));
}
