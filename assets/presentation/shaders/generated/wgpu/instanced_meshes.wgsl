struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
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

fn InstanceData_x24init_0( model0_1 : vec4<f32>,  model1_1 : vec4<f32>,  model2_1 : vec4<f32>,  normal0_1 : vec4<f32>,  normal1_1 : vec4<f32>,  normal2_1 : vec4<f32>,  tint_1 : vec4<f32>) -> InstanceData_0
{
    var _S1 : InstanceData_0;
    _S1.model0_0 = model0_1;
    _S1.model1_0 = model1_1;
    _S1.model2_0 = model2_1;
    _S1.normal0_0 = normal0_1;
    _S1.normal1_0 = normal1_1;
    _S1.normal2_0 = normal2_1;
    _S1.tint_0 = tint_1;
    return _S1;
}

struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
    @location(1) worldPosition_0 : vec3<f32>,
    @location(2) normal_0 : vec3<f32>,
    @location(3) tint_2 : vec4<f32>,
};

fn instanceVertex_0( position_1 : vec3<f32>,  uv_1 : vec2<f32>,  normal_1 : vec3<f32>,  data_1 : InstanceData_0) -> VertexOutput_0
{
    var local_0 : vec4<f32> = vec4<f32>(position_1, 1.0f);
    var world_0 : vec3<f32> = vec3<f32>(dot(data_1.model0_0, local_0), dot(data_1.model1_0, local_0), dot(data_1.model2_0, local_0));
    var worldNormal_0 : vec3<f32> = vec3<f32>(dot(data_1.normal0_0.xyz, normal_1), dot(data_1.normal1_0.xyz, normal_1), dot(data_1.normal2_0.xyz, normal_1));
    var output_0 : VertexOutput_0;
    output_0.position_0 = (((vec4<f32>(world_0, 1.0f)) * (mat4x4<f32>(draw_0.transform_0.data_0[i32(0)][i32(0)], draw_0.transform_0.data_0[i32(1)][i32(0)], draw_0.transform_0.data_0[i32(2)][i32(0)], draw_0.transform_0.data_0[i32(3)][i32(0)], draw_0.transform_0.data_0[i32(0)][i32(1)], draw_0.transform_0.data_0[i32(1)][i32(1)], draw_0.transform_0.data_0[i32(2)][i32(1)], draw_0.transform_0.data_0[i32(3)][i32(1)], draw_0.transform_0.data_0[i32(0)][i32(2)], draw_0.transform_0.data_0[i32(1)][i32(2)], draw_0.transform_0.data_0[i32(2)][i32(2)], draw_0.transform_0.data_0[i32(3)][i32(2)], draw_0.transform_0.data_0[i32(0)][i32(3)], draw_0.transform_0.data_0[i32(1)][i32(3)], draw_0.transform_0.data_0[i32(2)][i32(3)], draw_0.transform_0.data_0[i32(3)][i32(3)]))));
    output_0.worldPosition_0 = world_0;
    output_0.normal_0 = worldNormal_0;
    output_0.uv_0 = uv_1;
    output_0.tint_2 = data_1.tint_0;
    return output_0;
}

struct vertexInput_0
{
    @location(0) position_2 : vec3<f32>,
    @location(7) uv_2 : vec2<f32>,
    @location(8) normal_2 : vec3<f32>,
    @location(1) model0_2 : vec4<f32>,
    @location(2) model1_2 : vec4<f32>,
    @location(3) model2_2 : vec4<f32>,
    @location(4) normal0_2 : vec4<f32>,
    @location(5) normal1_2 : vec4<f32>,
    @location(6) normal2_2 : vec4<f32>,
    @location(9) tint_3 : vec4<f32>,
};

@vertex
fn vertex_main( _S2 : vertexInput_0) -> VertexOutput_0
{
    return instanceVertex_0(_S2.position_2, _S2.uv_2, _S2.normal_2, InstanceData_x24init_0(_S2.model0_2, _S2.model1_2, _S2.model2_2, _S2.normal0_2, _S2.normal1_2, _S2.normal2_2, _S2.tint_3));
}

fn rsqrt_0( x_0 : f32) -> f32
{
    return 1.0f / sqrt(x_0);
}

fn safeNormalize_0( value_0 : vec3<f32>,  fallback_0 : vec3<f32>) -> vec3<f32>
{
    var lengthSquared_0 : f32 = dot(value_0, value_0);
    var _S3 : vec3<f32>;
    if(lengthSquared_0 > 1.00000001686238353e-16f)
    {
        _S3 = value_0 * vec3<f32>(rsqrt_0(lengthSquared_0));
    }
    else
    {
        _S3 = fallback_0;
    }
    return _S3;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_3 : vec2<f32>,
    @location(1) worldPosition_1 : vec3<f32>,
    @location(2) normal_3 : vec3<f32>,
    @location(3) tint_4 : vec4<f32>,
};

@fragment
fn fragment_main( _S4 : pixelInput_0, @builtin(front_facing) front_0 : bool, @builtin(position) position_3 : vec4<f32>) -> pixelOutput_0
{
    var base_0 : vec4<f32> = (textureSample((baseImage_0), (baseSampler_0), (_S4.uv_3))) * material_0.baseColor_0 * _S4.tint_4;
    var mr_0 : vec2<f32> = (textureSample((mrImage_0), (mrSampler_0), (_S4.uv_3))).yz;
    var _S5 : vec3<f32> = vec3<f32>(1.0f);
    var mapped_0 : vec3<f32> = (textureSample((normalImage_0), (normalSampler_0), (_S4.uv_3))).xyz * vec3<f32>(2.0f) - _S5;
    var ao_0 : f32 = (textureSample((aoImage_0), (aoSampler_0), (_S4.uv_3))).x;
    var emissive_1 : vec3<f32> = (textureSample((emissiveImage_0), (emissiveSampler_0), (_S4.uv_3))).xyz * material_0.emissive_0.xyz;
    var dx_0 : vec3<f32> = dpdx(_S4.worldPosition_1);
    var dy_0 : vec3<f32> = dpdy(_S4.worldPosition_1);
    var uvx_0 : vec2<f32> = dpdx(_S4.uv_3);
    var uvy_0 : vec2<f32> = dpdy(_S4.uv_3);
    var n_0 : vec3<f32> = safeNormalize_0(_S4.normal_3, vec3<f32>(0.0f, 1.0f, 0.0f));
    var _S6 : f32 = uvx_0.x;
    var _S7 : f32 = uvy_0.y;
    var _S8 : f32 = uvx_0.y;
    var _S9 : f32 = uvy_0.x;
    var determinant_0 : f32 = _S6 * _S7 - _S8 * _S9;
    var _S10 : bool;
    if((material_0.alpha_0.z) > 0.0f)
    {
        _S10 = (abs(determinant_0)) > 1.00000001335143196e-10f;
    }
    else
    {
        _S10 = false;
    }
    var alpha_1 : f32;
    var n_1 : vec3<f32>;
    if(_S10)
    {
        var _S11 : vec3<f32> = vec3<f32>(determinant_0);
        var rawT_0 : vec3<f32> = (dx_0 * vec3<f32>(_S7) - dy_0 * vec3<f32>(_S8)) / _S11;
        var rawB_0 : vec3<f32> = (dy_0 * vec3<f32>(_S6) - dx_0 * vec3<f32>(_S9)) / _S11;
        var t_0 : vec3<f32> = rawT_0 - n_0 * vec3<f32>(dot(n_0, rawT_0));
        if((dot(t_0, t_0)) > 1.00000001686238353e-16f)
        {
            var t_1 : vec3<f32> = normalize(t_0);
            var _S12 : vec3<f32> = cross(n_0, t_1);
            if((dot(_S12, rawB_0)) < 0.0f)
            {
                alpha_1 = -1.0f;
            }
            else
            {
                alpha_1 = 1.0f;
            }
            var b_0 : vec3<f32> = _S12 * vec3<f32>(alpha_1);
            var _S13 : vec2<f32> = mapped_0.xy * vec2<f32>(material_0.factors_0.z);
            mapped_0.x = _S13.x;
            mapped_0.y = _S13.y;
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
        _S10 = (base_0.w) < (material_0.alpha_0.y);
    }
    else
    {
        _S10 = false;
    }
    if(_S10)
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
    var v_0 : vec3<f32> = safeNormalize_0(frame_0.camera_0.xyz - _S4.worldPosition_1, n_2);
    var l_0 : vec3<f32> = frame_0.lightDirection_0.xyz;
    var h_0 : vec3<f32> = safeNormalize_0(v_0 + l_0, n_2);
    var nv_0 : f32 = saturate(dot(n_2, v_0));
    var nl_0 : f32 = saturate(dot(n_2, l_0));
    var nh_0 : f32 = saturate(dot(n_2, h_0));
    var _S14 : vec3<f32> = base_0.xyz;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), _S14, vec3<f32>(metallic_0));
    var fresnel_0 : vec3<f32> = f0_0 + (_S5 - f0_0) * vec3<f32>(pow(1.0f - saturate(dot(v_0, h_0)), 5.0f));
    var a_0 : f32 = roughness_0 * roughness_0;
    var a2_0 : f32 = a_0 * a_0;
    var denominator_0 : f32 = nh_0 * nh_0 * (a2_0 - 1.0f) + 1.0f;
    var _S15 : f32 = 1.0f - a2_0;
    var _S16 : vec3<f32> = vec3<f32>((1.0f - metallic_0));
    var _S17 : pixelOutput_0 = pixelOutput_0( vec4<f32>(((_S5 - fresnel_0) * _S16 * _S14 / vec3<f32>(3.14159274101257324f) + fresnel_0 * vec3<f32>((a2_0 / (3.14159274101257324f * denominator_0 * denominator_0))) * vec3<f32>((0.5f / max(nl_0 * sqrt(nv_0 * nv_0 * _S15 + a2_0) + nv_0 * sqrt(nl_0 * nl_0 * _S15 + a2_0), 9.99999997475242708e-07f)))) * frame_0.radiance_0.xyz * vec3<f32>(nl_0) + frame_0.ambient_0.xyz * _S14 * _S16 * vec3<f32>(mix(1.0f, ao_0, material_0.factors_0.w)) + emissive_1, alpha_1) );
    return _S17;
}

struct PackedInstanceData_0
{
     model0_3 : vec4<f32>,
     model1_3 : vec4<f32>,
     model2_3 : vec4<f32>,
     responseInverseDeterminant_0 : vec4<f32>,
     tint_5 : vec4<f32>,
};

fn PackedInstanceData_x24init_0( model0_4 : vec4<f32>,  model1_4 : vec4<f32>,  model2_4 : vec4<f32>,  responseInverseDeterminant_1 : vec4<f32>,  tint_6 : vec4<f32>) -> PackedInstanceData_0
{
    var _S18 : PackedInstanceData_0;
    _S18.model0_3 = model0_4;
    _S18.model1_3 = model1_4;
    _S18.model2_3 = model2_4;
    _S18.responseInverseDeterminant_0 = responseInverseDeterminant_1;
    _S18.tint_5 = tint_6;
    return _S18;
}

fn decodeInstance_0( packed_0 : PackedInstanceData_0) -> InstanceData_0
{
    var data_2 : InstanceData_0;
    data_2.model0_0 = packed_0.model0_3;
    data_2.model1_0 = packed_0.model1_3;
    data_2.model2_0 = packed_0.model2_3;
    var _S19 : vec3<f32> = packed_0.model1_3.xyz;
    var _S20 : vec3<f32> = packed_0.model2_3.xyz;
    var _S21 : vec3<f32> = vec3<f32>(packed_0.responseInverseDeterminant_0.w);
    data_2.normal0_0 = vec4<f32>(cross(_S19, _S20) * _S21, packed_0.responseInverseDeterminant_0.x);
    var _S22 : vec3<f32> = packed_0.model0_3.xyz;
    data_2.normal1_0 = vec4<f32>(cross(_S20, _S22) * _S21, packed_0.responseInverseDeterminant_0.y);
    data_2.normal2_0 = vec4<f32>(cross(_S22, _S19) * _S21, packed_0.responseInverseDeterminant_0.z);
    data_2.tint_0 = packed_0.tint_5;
    return data_2;
}

struct vertexInput_1
{
    @location(0) position_4 : vec3<f32>,
    @location(5) uv_4 : vec2<f32>,
    @location(6) normal_4 : vec3<f32>,
    @location(1) model0_5 : vec4<f32>,
    @location(2) model1_5 : vec4<f32>,
    @location(3) model2_5 : vec4<f32>,
    @location(4) responseInverseDeterminant_2 : vec4<f32>,
    @location(7) tint_7 : vec4<f32>,
};

@vertex
fn vertex_compact_main( _S23 : vertexInput_1) -> VertexOutput_0
{
    return instanceVertex_0(_S23.position_4, _S23.uv_4, _S23.normal_4, decodeInstance_0(PackedInstanceData_x24init_0(_S23.model0_5, _S23.model1_5, _S23.model2_5, _S23.responseInverseDeterminant_2, _S23.tint_7)));
}
