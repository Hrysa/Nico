// Persistent input bounds and group draw metadata; output is private to a view.
struct CullRecord { float3 minimum; uint group; float3 maximum; uint reserved; };
struct CullGroup { uint indexCount; uint firstIndex; int baseVertex; float distance; };
struct CullView {
    column_major float4x4 clip;
    float3 eye; uint records;
    uint groups; uint activeRecords; uint partitionedOutput; uint activeGroups;
    uint4 enabledGroups[4];
};
[[vk::binding(0,0)]] StructuredBuffer<CullRecord> records;
[[vk::binding(1,0)]] StructuredBuffer<CullGroup> groups;
[[vk::binding(2,0)]] RWStructuredBuffer<Atomic<uint>> output;
[[vk::binding(3,0)]] ConstantBuffer<CullView> view;
// flags[N], counts[G], offsets[G], cursors[G], ids[N], args[5G].
// Single-group pages alias their count to args.instanceCount; their counts/cursor
// scratch slots are unused. records/groups are allocation capacities; active
// prefixes may grow after prior consumers have been submitted.
uint countsBase() { return view.groups == 1 ? argsBase()+1 : view.records; }
uint offsetsBase() { return view.records + view.groups; }
uint cursorsBase() { return view.records + 2*view.groups; }
uint idsBase() { return view.records + 3*view.groups; }
uint argsBase() { return 2*view.records + 3*view.groups; }
bool partitioned() { return view.partitionedOutput != 0; }
bool selected(uint group) {
    return (view.enabledGroups[group >> 7][(group >> 5) & 3] & (1u << (group & 31))) != 0;
}

[shader("compute")]
[numthreads(64,1,1)]
void reset_main(uint3 tid : SV_DispatchThreadID) {
    uint g = tid.x;
    if (g >= view.activeGroups) return;
    output[countsBase()+g].store(0);
    if (!partitioned()) output[offsetsBase()+g].store(0);
    // Reusable range pages retain their exclusive input-range end here.
    if (view.partitionedOutput != 2) output[cursorsBase()+g].store(0);
    for (uint i=0; i<5; ++i) output[argsBase()+g*5+i].store(0);
    // Single-group and fixed-range pages compact IDs directly in count_main.
    if (view.groups == 1 || partitioned()) {
        output[argsBase()+g*5].store(groups[g].indexCount);
        output[argsBase()+g*5+2].store(groups[g].firstIndex);
        output[argsBase()+g*5+3].store(asuint(groups[g].baseVertex));
    }
}

bool visible(CullRecord record) {
    float3 nearest = clamp(view.eye,record.minimum,record.maximum);
    if (length(view.eye-nearest) > groups[record.group].distance) return false;
    float4 w = view.clip[3];
    float4 planes[6] = { w+view.clip[0], w-view.clip[0], w+view.clip[1],
        w-view.clip[1], view.clip[2], w-view.clip[2] };
    float3 magnitude = max(abs(record.minimum), abs(record.maximum));
    for (uint i=0; i<6; ++i) {
        float4 plane = planes[i];
        float3 support = select(plane.xyz >= 0., record.maximum, record.minimum);
        float margin = max(dot(abs(plane.xyz), magnitude)+abs(plane.w), 1.) * 1e-6;
        if (dot(plane.xyz, support)+plane.w < -margin) return false;
    }
    return true;
}
[shader("compute")]
[numthreads(64,1,1)]
void count_main(uint3 tid : SV_DispatchThreadID) {
    uint id=tid.x;
    if (id >= view.activeRecords) return;
    CullRecord record=records[id];
    if (record.group >= view.activeGroups || !selected(record.group)) {
        if (!partitioned()) output[id].store(0);
        return;
    }
    if (view.partitionedOutput == 2 && record.group < view.activeGroups) {
        if (id < output[offsetsBase()+record.group].load()
            || id >= output[cursorsBase()+record.group].load()) return;
    }
    bool keep=record.group < view.activeGroups && visible(record);
    if (!partitioned()) output[id].store(keep ? 1 : 0);
    if (keep) {
        uint slot = output[countsBase()+record.group].add(1);
        if (view.groups == 1 || partitioned()) {
            uint destination = output[offsetsBase()+record.group].load()+slot;
            if (destination < view.records) {
                output[idsBase()+destination].store(id);
                if (view.groups != 1) output[argsBase()+record.group*5+1].add(1);
            }
        }
    }
}
// At most 512 groups per page. A single invocation makes scan ordering explicit.
[shader("compute")]
[numthreads(1,1,1)]
void scan_main(uint3 tid : SV_DispatchThreadID) {
    uint offset=0;
    for (uint g=0; g<view.activeGroups; ++g) {
        uint count=output[countsBase()+g].load();
        output[offsetsBase()+g].store(offset);
        // Arguments depend only on counts and offsets, not scattered IDs.
        // The subsequent scatter dispatch completes before the render pass.
        uint args=argsBase()+g*5;
        output[args].store(groups[g].indexCount);
        output[args+1].store(min(count,view.records-min(offset,view.records)));
        output[args+2].store(groups[g].firstIndex);
        output[args+3].store(asuint(groups[g].baseVertex));
        output[args+4].store(0);
        offset+=count;
    }
}
[shader("compute")]
[numthreads(64,1,1)]
void scatter_main(uint3 tid : SV_DispatchThreadID) {
    uint id=tid.x;
    if (id >= view.activeRecords || output[id].load()==0) return;
    uint g=records[id].group;
    uint slot=output[cursorsBase()+g].add(1);
    uint destination=output[offsetsBase()+g].load()+slot;
    if (destination < view.records) output[idsBase()+destination].store(id);
}
