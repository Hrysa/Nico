struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct CullView_std140_0
{
    @align(16) clip_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) eye_0 : vec3<f32>,
    @align(4) records_0 : u32,
    @align(16) groups_0 : u32,
    @align(4) activeRecords_0 : u32,
    @align(8) partitionedOutput_0 : u32,
    @align(4) activeGroups_0 : u32,
    @align(16) enabledGroups_0 : array<vec4<u32>, i32(4)>,
};

@binding(3) @group(0) var<uniform> view_0 : CullView_std140_0;
@binding(2) @group(0) var<storage, read_write> output_0 : array<atomic<u32>>;

struct CullGroup_std430_0
{
    @align(4) indexCount_0 : u32,
    @align(4) firstIndex_0 : u32,
    @align(4) baseVertex_0 : i32,
    @align(4) distance_0 : f32,
};

@binding(1) @group(0) var<storage, read> groups_1 : array<CullGroup_std430_0>;

struct CullRecord_std430_0
{
    @align(16) minimum_0 : vec3<f32>,
    @align(4) group_0 : u32,
    @align(16) maximum_0 : vec3<f32>,
    @align(4) reserved_0 : u32,
};

@binding(0) @group(0) var<storage, read> records_1 : array<CullRecord_std430_0>;

fn argsBase_0() -> u32
{
    return u32(2) * view_0.records_0 + u32(3) * view_0.groups_0;
}

fn countsBase_0() -> u32
{
    var _S1 : u32;
    if((view_0.groups_0) == u32(1))
    {
        _S1 = argsBase_0() + u32(1);
    }
    else
    {
        _S1 = view_0.records_0;
    }
    return _S1;
}

fn partitioned_0() -> bool
{
    return (view_0.partitionedOutput_0) != u32(0);
}

fn offsetsBase_0() -> u32
{
    return view_0.records_0 + view_0.groups_0;
}

fn cursorsBase_0() -> u32
{
    return view_0.records_0 + u32(2) * view_0.groups_0;
}

@compute
@workgroup_size(64, 1, 1)
fn reset_main(@builtin(global_invocation_id) tid_0 : vec3<u32>)
{
    var g_0 : u32 = tid_0.x;
    if(g_0 >= (view_0.activeGroups_0))
    {
        return;
    }
    atomicStore(&(output_0[countsBase_0() + g_0]), u32(0));
    var _S2 : bool = partitioned_0();
    if(!_S2)
    {
        atomicStore(&(output_0[offsetsBase_0() + g_0]), u32(0));
    }
    if((view_0.partitionedOutput_0) != u32(2))
    {
        atomicStore(&(output_0[cursorsBase_0() + g_0]), u32(0));
    }
    var i_0 : u32 = u32(0);
    for(;;)
    {
        if(i_0 < u32(5))
        {
        }
        else
        {
            break;
        }
        atomicStore(&(output_0[argsBase_0() + g_0 * u32(5) + i_0]), u32(0));
        i_0 = i_0 + u32(1);
    }
    var _S3 : bool;
    if((view_0.groups_0) == u32(1))
    {
        _S3 = true;
    }
    else
    {
        _S3 = _S2;
    }
    if(_S3)
    {
        var _S4 : u32 = argsBase_0() + g_0 * u32(5);
        atomicStore(&(output_0[_S4]), groups_1[g_0].indexCount_0);
        atomicStore(&(output_0[_S4 + u32(2)]), groups_1[g_0].firstIndex_0);
        atomicStore(&(output_0[_S4 + u32(3)]), (bitcast<u32>((groups_1[g_0].baseVertex_0))));
    }
    return;
}

fn selected_0( group_1 : u32) -> bool
{
    return (((view_0.enabledGroups_0[(group_1 >> (u32(7)))][(((group_1 >> (u32(5)))) & (u32(3)))]) & (((u32(1) << (((group_1 & (u32(31)))))))))) != u32(0);
}

struct CullRecord_0
{
     minimum_0 : vec3<f32>,
     group_0 : u32,
     maximum_0 : vec3<f32>,
     reserved_0 : u32,
};

fn visible_0( record_0 : CullRecord_0) -> bool
{
    if((length(view_0.eye_0 - clamp(view_0.eye_0, record_0.minimum_0, record_0.maximum_0))) > (groups_1[record_0.group_0].distance_0))
    {
        return false;
    }
    var _S5 : vec4<f32> = vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(3)], view_0.clip_0.data_0[i32(1)][i32(3)], view_0.clip_0.data_0[i32(2)][i32(3)], view_0.clip_0.data_0[i32(3)][i32(3)]);
    var _S6 : array<vec4<f32>, i32(6)> = array<vec4<f32>, i32(6)>( _S5 + vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(0)], view_0.clip_0.data_0[i32(1)][i32(0)], view_0.clip_0.data_0[i32(2)][i32(0)], view_0.clip_0.data_0[i32(3)][i32(0)]), _S5 - vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(0)], view_0.clip_0.data_0[i32(1)][i32(0)], view_0.clip_0.data_0[i32(2)][i32(0)], view_0.clip_0.data_0[i32(3)][i32(0)]), _S5 + vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(1)], view_0.clip_0.data_0[i32(1)][i32(1)], view_0.clip_0.data_0[i32(2)][i32(1)], view_0.clip_0.data_0[i32(3)][i32(1)]), _S5 - vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(1)], view_0.clip_0.data_0[i32(1)][i32(1)], view_0.clip_0.data_0[i32(2)][i32(1)], view_0.clip_0.data_0[i32(3)][i32(1)]), vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(2)], view_0.clip_0.data_0[i32(1)][i32(2)], view_0.clip_0.data_0[i32(2)][i32(2)], view_0.clip_0.data_0[i32(3)][i32(2)]), _S5 - vec4<f32>(view_0.clip_0.data_0[i32(0)][i32(2)], view_0.clip_0.data_0[i32(1)][i32(2)], view_0.clip_0.data_0[i32(2)][i32(2)], view_0.clip_0.data_0[i32(3)][i32(2)]) );
    var _S7 : vec3<f32> = max(abs(record_0.minimum_0), abs(record_0.maximum_0));
    var i_1 : u32 = u32(0);
    for(;;)
    {
        if(i_1 < u32(6))
        {
        }
        else
        {
            break;
        }
        var _S8 : vec3<f32> = _S6[i_1].xyz;
        var _S9 : f32 = _S6[i_1].w;
        if((dot(_S8, select(record_0.minimum_0, record_0.maximum_0, _S8 >= vec3<f32>(0.0f))) + _S9) < (- (max(dot(abs(_S8), _S7) + abs(_S9), 1.0f) * 9.99999997475242708e-07f)))
        {
            return false;
        }
        i_1 = i_1 + u32(1);
    }
    return true;
}

fn idsBase_0() -> u32
{
    return view_0.records_0 + u32(3) * view_0.groups_0;
}

@compute
@workgroup_size(64, 1, 1)
fn count_main(@builtin(global_invocation_id) tid_1 : vec3<u32>)
{
    var id_0 : u32 = tid_1.x;
    if(id_0 >= (view_0.activeRecords_0))
    {
        return;
    }
    var _S10 : CullRecord_0 = CullRecord_0( records_1[id_0].minimum_0, records_1[id_0].group_0, records_1[id_0].maximum_0, records_1[id_0].reserved_0 );
    var _S11 : u32 = records_1[id_0].group_0;
    var keep_0 : bool;
    if((records_1[id_0].group_0) >= (view_0.activeGroups_0))
    {
        keep_0 = true;
    }
    else
    {
        keep_0 = !selected_0(_S11);
    }
    if(keep_0)
    {
        if(!partitioned_0())
        {
            atomicStore(&(output_0[id_0]), u32(0));
        }
        return;
    }
    if((view_0.partitionedOutput_0) == u32(2))
    {
        keep_0 = _S11 < (view_0.activeGroups_0);
    }
    else
    {
        keep_0 = false;
    }
    if(keep_0)
    {
        var _S12 : u32 = atomicLoad(&(output_0[offsetsBase_0() + _S11]));
        if(id_0 < _S12)
        {
            keep_0 = true;
        }
        else
        {
            var _S13 : u32 = atomicLoad(&(output_0[cursorsBase_0() + _S11]));
            keep_0 = id_0 >= _S13;
        }
        if(keep_0)
        {
            return;
        }
    }
    if(_S11 < (view_0.activeGroups_0))
    {
        keep_0 = visible_0(_S10);
    }
    else
    {
        keep_0 = false;
    }
    var _S14 : bool = partitioned_0();
    if(!_S14)
    {
        var _S15 : i32;
        if(keep_0)
        {
            _S15 = i32(1);
        }
        else
        {
            _S15 = i32(0);
        }
        atomicStore(&(output_0[id_0]), u32(_S15));
    }
    if(keep_0)
    {
        var slot_0 : u32 = atomicAdd(&(output_0[countsBase_0() + _S11]), u32(1));
        if((view_0.groups_0) == u32(1))
        {
            keep_0 = true;
        }
        else
        {
            keep_0 = _S14;
        }
        if(keep_0)
        {
            var _S16 : u32 = atomicLoad(&(output_0[offsetsBase_0() + _S11]));
            var destination_0 : u32 = _S16 + slot_0;
            if(destination_0 < (view_0.records_0))
            {
                atomicStore(&(output_0[idsBase_0() + destination_0]), id_0);
                if((view_0.groups_0) != u32(1))
                {
                    var _S17 : u32 = atomicAdd(&(output_0[argsBase_0() + _S11 * u32(5) + u32(1)]), u32(1));
                }
            }
        }
    }
    return;
}

@compute
@workgroup_size(1, 1, 1)
fn scan_main(@builtin(global_invocation_id) tid_2 : vec3<u32>)
{
    var g_1 : u32 = u32(0);
    var offset_0 : u32 = u32(0);
    for(;;)
    {
        if(g_1 < (view_0.activeGroups_0))
        {
        }
        else
        {
            break;
        }
        var count_0 : u32 = atomicLoad(&(output_0[countsBase_0() + g_1]));
        atomicStore(&(output_0[offsetsBase_0() + g_1]), offset_0);
        var args_0 : u32 = argsBase_0() + g_1 * u32(5);
        atomicStore(&(output_0[args_0]), groups_1[g_1].indexCount_0);
        atomicStore(&(output_0[args_0 + u32(1)]), min(count_0, view_0.records_0 - min(offset_0, view_0.records_0)));
        atomicStore(&(output_0[args_0 + u32(2)]), groups_1[g_1].firstIndex_0);
        atomicStore(&(output_0[args_0 + u32(3)]), (bitcast<u32>((groups_1[g_1].baseVertex_0))));
        atomicStore(&(output_0[args_0 + u32(4)]), u32(0));
        var offset_1 : u32 = offset_0 + count_0;
        g_1 = g_1 + u32(1);
        offset_0 = offset_1;
    }
    return;
}

@compute
@workgroup_size(64, 1, 1)
fn scatter_main(@builtin(global_invocation_id) tid_3 : vec3<u32>)
{
    var id_1 : u32 = tid_3.x;
    var _S18 : bool;
    if(id_1 >= (view_0.activeRecords_0))
    {
        _S18 = true;
    }
    else
    {
        var _S19 : u32 = atomicLoad(&(output_0[id_1]));
        _S18 = _S19 == u32(0);
    }
    if(_S18)
    {
        return;
    }
    var _S20 : u32 = records_1[id_1].group_0;
    var slot_1 : u32 = atomicAdd(&(output_0[cursorsBase_0() + records_1[id_1].group_0]), u32(1));
    var _S21 : u32 = atomicLoad(&(output_0[offsetsBase_0() + _S20]));
    var destination_1 : u32 = _S21 + slot_1;
    if(destination_1 < (view_0.records_0))
    {
        atomicStore(&(output_0[idsBase_0() + destination_1]), id_1);
    }
    return;
}
