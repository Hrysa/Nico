// Measurement/reproduction candidate only; production uses the scalar kernel.
// Known count mismatch on GTX 1660/DX12; not eligible for production selection.
#define count_main count_scalar_main
#include "../../../../assets/presentation/shaders/instance_visibility.slang"
#undef count_main

[shader("compute")]
[numthreads(64,1,1)]
void count_main(uint3 tid : SV_DispatchThreadID) {
    if (!partitioned()) {
        uint id=tid.x;
        if (id >= view.activeRecords) return;
        CullRecord record=records[id];
        bool keep=record.group < view.activeGroups && selected(record.group) && visible(record);
        output[id].store(keep ? 1u : 0u);
        if (keep) {
            uint slot=output[countsBase()+record.group].add(1);
            if (view.groups == 1) output[idsBase()+slot].store(id);
        }
        return;
    }
    uint id=tid.x;
    uint group=0xffffffffu;
    bool keep=false;
    if (id < view.activeRecords) {
        CullRecord record=records[id];
        group=record.group;
        if (group < view.activeGroups && selected(group)) {
            bool inRange=view.partitionedOutput != 2
                || (id >= output[offsetsBase()+group].load()
                    && id < output[cursorsBase()+group].load());
            keep=inRange && visible(record);
        }
    }
    // A whole subgroup can leave without removing its leader from surviving work.
    if (WaveActiveAllTrue(!keep)) return;
    uint firstGroup=WaveReadLaneFirst(group);
    bool same=WaveActiveAllTrue(group == firstGroup);
    uint count=WaveActiveSum(keep ? 1u : 0u);
    uint prefix=WavePrefixSum(keep ? 1u : 0u);
    // No individual lane exits before the subgroup operations.
    bool first=WaveGetLaneIndex() == 0;
    uint base=0;
    if (same && count != 0 && first) {
        base=output[countsBase()+group].add(count);
        if (view.groups != 1) output[argsBase()+group*5+1].add(count);
    }
    base=WaveReadLaneFirst(base);
    if (keep) {
        uint slot;
        if (same) slot=base+prefix;
        else {
            slot=output[countsBase()+group].add(1);
            if (view.groups != 1) output[argsBase()+group*5+1].add(1);
        }
        uint destination=output[offsetsBase()+group].load()+slot;
        if (destination < view.records) output[idsBase()+destination].store(id);
    }
}
