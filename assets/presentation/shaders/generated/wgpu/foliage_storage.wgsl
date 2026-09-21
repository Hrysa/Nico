struct GroupSelection_std140_0
{
    @align(16) group_0 : u32,
    @align(4) offsetsWord_0 : u32,
    @align(8) idsWord_0 : u32,
    @align(4) sourceBase_0 : u32,
};

@binding(2) @group(3) var<uniform> selection_0 : GroupSelection_std140_0;
@binding(1) @group(3) var<storage, read> visibility_0 : array<u32>;

struct InstanceData_std430_0
{
    @align(16) model0_0 : vec4<f32>,
    @align(16) model1_0 : vec4<f32>,
    @align(16) model2_0 : vec4<f32>,
    @align(16) normal0_0 : vec4<f32>,
    @align(16) normal1_0 : vec4<f32>,
    @align(16) normal2_0 : vec4<f32>,
    @align(16) tint_0 : vec4<f32>,
};

@binding(0) @group(3) var<storage, read> instances_0 : array<InstanceData_std430_0>;

struct FoliageField_std140_0
{
    @align(16) positionRadius_0 : vec4<f32>,
    @align(16) directionStrength_0 : vec4<f32>,
    @align(16) kindPadding_0 : vec4<u32>,
};

struct _Array_std140_FoliageField16_0
{
    @align(16) data_0 : array<FoliageField_std140_0, i32(16)>,
};

struct FoliageUniform_std140_0
{
    @align(16) profile_0 : vec4<f32>,
    @align(16) counts_0 : vec4<u32>,
    @align(16) fields_0 : _Array_std140_FoliageField16_0,
};

@binding(3) @group(3) var<uniform> foliage_0 : FoliageUniform_std140_0;
struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_1 : array<vec4<f32>, i32(4)>,
};

struct DrawUniforms_std140_0
{
    @align(16) transform_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) model_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) normalTransform_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) color_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> draw_0 : DrawUniforms_std140_0;
@binding(0) @group(0) var baseImage_0 : texture_2d<f32>;

@binding(1) @group(0) var baseSampler_0 : sampler;

struct MaterialUniforms_std140_0
{
    @align(16) baseColor_0 : vec4<f32>,
    @align(16) emissive_0 : vec4<f32>,
    @align(16) factors_0 : vec4<f32>,
    @align(16) alpha_0 : vec4<f32>,
};

@binding(10) @group(0) var<uniform> material_0 : MaterialUniforms_std140_0;
@binding(2) @group(0) var mrImage_0 : texture_2d<f32>;

@binding(3) @group(0) var mrSampler_0 : sampler;

@binding(4) @group(0) var normalImage_0 : texture_2d<f32>;

@binding(5) @group(0) var normalSampler_0 : sampler;

@binding(6) @group(0) var aoImage_0 : texture_2d<f32>;

@binding(7) @group(0) var aoSampler_0 : sampler;

@binding(8) @group(0) var emissiveImage_0 : texture_2d<f32>;

@binding(9) @group(0) var emissiveSampler_0 : sampler;

struct FrameUniforms_std140_0
{
    @align(16) camera_0 : vec4<f32>,
    @align(16) lightDirection_0 : vec4<f32>,
    @align(16) radiance_0 : vec4<f32>,
    @align(16) ambient_0 : vec4<f32>,
};

@binding(0) @group(2) var<uniform> frame_0 : FrameUniforms_std140_0;
struct PackedInstanceData_std430_0
{
    @align(16) model0_1 : vec4<f32>,
    @align(16) model1_1 : vec4<f32>,
    @align(16) model2_1 : vec4<f32>,
    @align(16) responseInverseDeterminant_0 : vec4<f32>,
    @align(16) tint_1 : vec4<f32>,
};

@binding(0) @group(3) var<storage, read> compactInstances_0 : array<PackedInstanceData_std430_0>;

fn foliageClamp_0( value_0 : vec3<f32>,  maximum_0 : f32) -> vec3<f32>
{
    var magnitude_0 : f32 = length(value_0);
    var _S1 : bool;
    if(magnitude_0 > maximum_0)
    {
        _S1 = magnitude_0 > 0.0f;
    }
    else
    {
        _S1 = false;
    }
    var _S2 : vec3<f32>;
    if(_S1)
    {
        _S2 = value_0 * vec3<f32>((maximum_0 / magnitude_0));
    }
    else
    {
        _S2 = value_0;
    }
    return _S2;
}

struct InstanceData_0
{
     model0_0 : vec4<f32>,
     model1_0 : vec4<f32>,
     model2_0 : vec4<f32>,
     normal0_0 : vec4<f32>,
     normal1_0 : vec4<f32>,
     normal2_0 : vec4<f32>,
     tint_0 : vec4<f32>,
};

fn deformFoliage_0( localPosition_0 : vec3<f32>,  data_2 : InstanceData_0,  world_0 : ptr<function, vec3<f32>>,  normal_0 : ptr<function, vec3<f32>>)
{
    if((foliage_0.counts_0.x) == u32(0))
    {
        (*normal_0) = normalize((*normal_0));
        return;
    }
    var response_0 : f32;
    var _S3 : bool;
    var localRoot_0 : vec4<f32> = vec4<f32>(0.0f, foliage_0.profile_0.x, 0.0f, 1.0f);
    var _S4 : vec3<f32> = vec3<f32>(dot(data_2.model0_0, localRoot_0), dot(data_2.model1_0, localRoot_0), dot(data_2.model2_0, localRoot_0));
    var _S5 : vec3<f32> = vec3<f32>(0.0f);
    var _S6 : f32 = 1.0f - data_2.normal2_0.w * 0.5f * (1.0f - cos((foliage_0.profile_0.w + data_2.normal0_0.w) * 6.28318548202514648f));
    var i_0 : u32 = u32(0);
    var combined_0 : vec3<f32> = _S5;
    for(;;)
    {
        if(i_0 < (min(foliage_0.counts_0.x, u32(16))))
        {
        }
        else
        {
            break;
        }
        var _S7 : vec4<f32> = foliage_0.fields_0.data_0[i_0].directionStrength_0;
        var _S8 : vec4<u32> = foliage_0.fields_0.data_0[i_0].kindPadding_0;
        var delta_0 : vec3<f32> = _S4 - foliage_0.fields_0.data_0[i_0].positionRadius_0.xyz;
        var distance_0 : f32 = length(delta_0);
        var _S9 : f32 = foliage_0.fields_0.data_0[i_0].positionRadius_0.w;
        if(distance_0 >= _S9)
        {
            i_0 = i_0 + u32(1);
            continue;
        }
        var falloff_0 : f32 = 1.0f - distance_0 / _S9;
        var _S10 : vec3<f32> = _S7.xyz;
        var _S11 : u32 = _S8.x;
        if(_S11 == u32(1))
        {
            _S3 = distance_0 > 0.0f;
        }
        else
        {
            _S3 = false;
        }
        var direction_0 : vec3<f32>;
        if(_S3)
        {
            direction_0 = delta_0 / vec3<f32>(distance_0);
        }
        else
        {
            direction_0 = _S10;
        }
        if(_S11 == u32(0))
        {
            response_0 = _S6;
        }
        else
        {
            response_0 = 1.0f;
        }
        combined_0 = combined_0 + direction_0 * vec3<f32>((_S7.w * falloff_0 * falloff_0 * response_0));
        i_0 = i_0 + u32(1);
    }
    var gradient_0 : vec3<f32> = vec3<f32>(data_2.normal0_0.y, data_2.normal1_0.y, data_2.normal2_0.y) * vec3<f32>(foliage_0.profile_0.y);
    var axis_0 : vec3<f32> = normalize(gradient_0);
    var bend_0 : vec3<f32> = foliageClamp_0(combined_0, 1.0f) * vec3<f32>((foliage_0.profile_0.z * data_2.normal1_0.w));
    var bend_1 : vec3<f32> = foliageClamp_0(bend_0 - axis_0 * vec3<f32>(dot(bend_0, axis_0)), foliage_0.profile_0.z);
    var height_0 : f32 = (localPosition_0.y - foliage_0.profile_0.x) * foliage_0.profile_0.y;
    var weight_0 : f32 = clamp(height_0, 0.0f, 1.0f);
    if(height_0 > 0.0f)
    {
        _S3 = height_0 <= 1.0f;
    }
    else
    {
        _S3 = false;
    }
    if(_S3)
    {
        response_0 = 2.0f * weight_0;
    }
    else
    {
        response_0 = 0.0f;
    }
    var _S12 : vec3<f32> = vec3<f32>(weight_0);
    (*world_0) = (*world_0) + bend_1 * _S12 * _S12;
    (*normal_0) = normalize((*normal_0) - gradient_0 * vec3<f32>((response_0 * dot(bend_1, (*normal_0)))));
    return;
}

struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
    @location(1) worldPosition_0 : vec3<f32>,
    @location(2) normal_1 : vec3<f32>,
    @location(3) tint_2 : vec4<f32>,
};

fn instanceVertex_0( position_1 : vec3<f32>,  uv_1 : vec2<f32>,  normal_2 : vec3<f32>,  data_3 : InstanceData_0) -> VertexOutput_0
{
    var local_0 : vec4<f32> = vec4<f32>(position_1, 1.0f);
    var world_1 : vec3<f32> = vec3<f32>(dot(data_3.model0_0, local_0), dot(data_3.model1_0, local_0), dot(data_3.model2_0, local_0));
    var worldNormal_0 : vec3<f32> = vec3<f32>(dot(data_3.normal0_0.xyz, normal_2), dot(data_3.normal1_0.xyz, normal_2), dot(data_3.normal2_0.xyz, normal_2));
    deformFoliage_0(position_1, data_3, &(world_1), &(worldNormal_0));
    var output_0 : VertexOutput_0;
    output_0.position_0 = (((vec4<f32>(world_1, 1.0f)) * (mat4x4<f32>(draw_0.transform_0.data_1[i32(0)][i32(0)], draw_0.transform_0.data_1[i32(1)][i32(0)], draw_0.transform_0.data_1[i32(2)][i32(0)], draw_0.transform_0.data_1[i32(3)][i32(0)], draw_0.transform_0.data_1[i32(0)][i32(1)], draw_0.transform_0.data_1[i32(1)][i32(1)], draw_0.transform_0.data_1[i32(2)][i32(1)], draw_0.transform_0.data_1[i32(3)][i32(1)], draw_0.transform_0.data_1[i32(0)][i32(2)], draw_0.transform_0.data_1[i32(1)][i32(2)], draw_0.transform_0.data_1[i32(2)][i32(2)], draw_0.transform_0.data_1[i32(3)][i32(2)], draw_0.transform_0.data_1[i32(0)][i32(3)], draw_0.transform_0.data_1[i32(1)][i32(3)], draw_0.transform_0.data_1[i32(2)][i32(3)], draw_0.transform_0.data_1[i32(3)][i32(3)]))));
    output_0.worldPosition_0 = world_1;
    output_0.normal_1 = worldNormal_0;
    output_0.uv_0 = uv_1;
    output_0.tint_2 = data_3.tint_0;
    return output_0;
}

struct vertexInput_0
{
    @location(0) position_2 : vec3<f32>,
    @location(1) uv_2 : vec2<f32>,
    @location(2) normal_3 : vec3<f32>,
};

@vertex
fn vertex_main( _S13 : vertexInput_0, @builtin(instance_index) instance_0 : u32) -> VertexOutput_0
{
    var _S14 : InstanceData_0 = InstanceData_0( instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].model0_0, instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].model1_0, instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].model2_0, instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].normal0_0, instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].normal1_0, instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].normal2_0, instances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_0] - selection_0.sourceBase_0].tint_0 );
    return instanceVertex_0(_S13.position_2, _S13.uv_2, _S13.normal_3, _S14);
}

fn rsqrt_0( x_0 : f32) -> f32
{
    return 1.0f / sqrt(x_0);
}

fn safeNormalize_0( value_1 : vec3<f32>,  fallback_0 : vec3<f32>) -> vec3<f32>
{
    var lengthSquared_0 : f32 = dot(value_1, value_1);
    var _S15 : vec3<f32>;
    if(lengthSquared_0 > 1.00000001686238353e-16f)
    {
        _S15 = value_1 * vec3<f32>(rsqrt_0(lengthSquared_0));
    }
    else
    {
        _S15 = fallback_0;
    }
    return _S15;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_3 : vec2<f32>,
    @location(1) worldPosition_1 : vec3<f32>,
    @location(2) normal_4 : vec3<f32>,
    @location(3) tint_3 : vec4<f32>,
};

@fragment
fn fragment_main( _S16 : pixelInput_0, @builtin(front_facing) front_0 : bool, @builtin(position) position_3 : vec4<f32>) -> pixelOutput_0
{
    var base_0 : vec4<f32> = (textureSample((baseImage_0), (baseSampler_0), (_S16.uv_3))) * material_0.baseColor_0 * _S16.tint_3;
    var mr_0 : vec2<f32> = (textureSample((mrImage_0), (mrSampler_0), (_S16.uv_3))).yz;
    var _S17 : vec3<f32> = vec3<f32>(1.0f);
    var mapped_0 : vec3<f32> = (textureSample((normalImage_0), (normalSampler_0), (_S16.uv_3))).xyz * vec3<f32>(2.0f) - _S17;
    var ao_0 : f32 = (textureSample((aoImage_0), (aoSampler_0), (_S16.uv_3))).x;
    var emissive_1 : vec3<f32> = (textureSample((emissiveImage_0), (emissiveSampler_0), (_S16.uv_3))).xyz * material_0.emissive_0.xyz;
    var dx_0 : vec3<f32> = dpdx(_S16.worldPosition_1);
    var dy_0 : vec3<f32> = dpdy(_S16.worldPosition_1);
    var uvx_0 : vec2<f32> = dpdx(_S16.uv_3);
    var uvy_0 : vec2<f32> = dpdy(_S16.uv_3);
    var n_0 : vec3<f32> = safeNormalize_0(_S16.normal_4, vec3<f32>(0.0f, 1.0f, 0.0f));
    var _S18 : f32 = uvx_0.x;
    var _S19 : f32 = uvy_0.y;
    var _S20 : f32 = uvx_0.y;
    var _S21 : f32 = uvy_0.x;
    var determinant_0 : f32 = _S18 * _S19 - _S20 * _S21;
    var _S22 : bool;
    if((material_0.alpha_0.z) > 0.0f)
    {
        _S22 = (abs(determinant_0)) > 1.00000001335143196e-10f;
    }
    else
    {
        _S22 = false;
    }
    var alpha_1 : f32;
    var n_1 : vec3<f32>;
    if(_S22)
    {
        var _S23 : vec3<f32> = vec3<f32>(determinant_0);
        var rawT_0 : vec3<f32> = (dx_0 * vec3<f32>(_S19) - dy_0 * vec3<f32>(_S20)) / _S23;
        var rawB_0 : vec3<f32> = (dy_0 * vec3<f32>(_S18) - dx_0 * vec3<f32>(_S21)) / _S23;
        var t_0 : vec3<f32> = rawT_0 - n_0 * vec3<f32>(dot(n_0, rawT_0));
        if((dot(t_0, t_0)) > 1.00000001686238353e-16f)
        {
            var t_1 : vec3<f32> = normalize(t_0);
            var _S24 : vec3<f32> = cross(n_0, t_1);
            if((dot(_S24, rawB_0)) < 0.0f)
            {
                alpha_1 = -1.0f;
            }
            else
            {
                alpha_1 = 1.0f;
            }
            var b_0 : vec3<f32> = _S24 * vec3<f32>(alpha_1);
            var _S25 : vec2<f32> = mapped_0.xy * vec2<f32>(material_0.factors_0.z);
            mapped_0.x = _S25.x;
            mapped_0.y = _S25.y;
            n_1 = safeNormalize_0(t_1 * vec3<f32>(mapped_0.x) + b_0 * vec3<f32>(mapped_0.y) + n_0 * vec3<f32>(mapped_0.z), n_0);
        }
        else
        {
            n_1 = n_0;
        }
    }
    else
    {
        n_1 = n_0;
    }
    if(front_0)
    {
        alpha_1 = 1.0f;
    }
    else
    {
        alpha_1 = -1.0f;
    }
    var n_2 : vec3<f32> = n_1 * vec3<f32>(alpha_1);
    if((material_0.alpha_0.x) == 1.0f)
    {
        _S22 = (base_0.w) < (material_0.alpha_0.y);
    }
    else
    {
        _S22 = false;
    }
    if(_S22)
    {
        discard;
    }
    if((material_0.alpha_0.x) == 2.0f)
    {
        alpha_1 = base_0.w;
    }
    else
    {
        alpha_1 = 1.0f;
    }
    var metallic_0 : f32 = saturate(material_0.factors_0.x * mr_0.y);
    var roughness_0 : f32 = clamp(material_0.factors_0.y * mr_0.x, 0.04500000178813934f, 1.0f);
    var v_0 : vec3<f32> = safeNormalize_0(frame_0.camera_0.xyz - _S16.worldPosition_1, n_2);
    var l_0 : vec3<f32> = frame_0.lightDirection_0.xyz;
    var h_0 : vec3<f32> = safeNormalize_0(v_0 + l_0, n_2);
    var nv_0 : f32 = saturate(dot(n_2, v_0));
    var nl_0 : f32 = saturate(dot(n_2, l_0));
    var nh_0 : f32 = saturate(dot(n_2, h_0));
    var _S26 : vec3<f32> = base_0.xyz;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), _S26, vec3<f32>(metallic_0));
    var fresnel_0 : vec3<f32> = f0_0 + (_S17 - f0_0) * vec3<f32>(pow(1.0f - saturate(dot(v_0, h_0)), 5.0f));
    var a_0 : f32 = roughness_0 * roughness_0;
    var a2_0 : f32 = a_0 * a_0;
    var denominator_0 : f32 = nh_0 * nh_0 * (a2_0 - 1.0f) + 1.0f;
    var _S27 : f32 = 1.0f - a2_0;
    var _S28 : vec3<f32> = vec3<f32>((1.0f - metallic_0));
    var _S29 : pixelOutput_0 = pixelOutput_0( vec4<f32>(((_S17 - fresnel_0) * _S28 * _S26 / vec3<f32>(3.14159274101257324f) + fresnel_0 * vec3<f32>((a2_0 / (3.14159274101257324f * denominator_0 * denominator_0))) * vec3<f32>((0.5f / max(nl_0 * sqrt(nv_0 * nv_0 * _S27 + a2_0) + nv_0 * sqrt(nl_0 * nl_0 * _S27 + a2_0), 9.99999997475242708e-07f)))) * frame_0.radiance_0.xyz * vec3<f32>(nl_0) + frame_0.ambient_0.xyz * _S26 * _S28 * vec3<f32>(mix(1.0f, ao_0, material_0.factors_0.w)) + emissive_1, alpha_1) );
    return _S29;
}

struct PackedInstanceData_0
{
     model0_1 : vec4<f32>,
     model1_1 : vec4<f32>,
     model2_1 : vec4<f32>,
     responseInverseDeterminant_0 : vec4<f32>,
     tint_1 : vec4<f32>,
};

fn decodeInstance_0( packed_0 : PackedInstanceData_0) -> InstanceData_0
{
    var data_4 : InstanceData_0;
    data_4.model0_0 = packed_0.model0_1;
    data_4.model1_0 = packed_0.model1_1;
    data_4.model2_0 = packed_0.model2_1;
    var _S30 : vec3<f32> = packed_0.model1_1.xyz;
    var _S31 : vec3<f32> = packed_0.model2_1.xyz;
    var _S32 : vec3<f32> = vec3<f32>(packed_0.responseInverseDeterminant_0.w);
    data_4.normal0_0 = vec4<f32>(cross(_S30, _S31) * _S32, packed_0.responseInverseDeterminant_0.x);
    var _S33 : vec3<f32> = packed_0.model0_1.xyz;
    data_4.normal1_0 = vec4<f32>(cross(_S31, _S33) * _S32, packed_0.responseInverseDeterminant_0.y);
    data_4.normal2_0 = vec4<f32>(cross(_S33, _S30) * _S32, packed_0.responseInverseDeterminant_0.z);
    data_4.tint_0 = packed_0.tint_1;
    return data_4;
}

struct vertexInput_1
{
    @location(0) position_4 : vec3<f32>,
    @location(1) uv_4 : vec2<f32>,
    @location(2) normal_5 : vec3<f32>,
};

@vertex
fn vertex_compact_main( _S34 : vertexInput_1, @builtin(instance_index) instance_1 : u32) -> VertexOutput_0
{
    var _S35 : PackedInstanceData_0 = PackedInstanceData_0( compactInstances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_1] - selection_0.sourceBase_0].model0_1, compactInstances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_1] - selection_0.sourceBase_0].model1_1, compactInstances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_1] - selection_0.sourceBase_0].model2_1, compactInstances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_1] - selection_0.sourceBase_0].responseInverseDeterminant_0, compactInstances_0[visibility_0[selection_0.idsWord_0 + visibility_0[selection_0.offsetsWord_0 + selection_0.group_0] + instance_1] - selection_0.sourceBase_0].tint_1 );
    return instanceVertex_0(_S34.position_4, _S34.uv_4, _S34.normal_5, decodeInstance_0(_S35));
}
