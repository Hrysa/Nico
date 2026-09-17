struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct _Array_std140_matrixx3Cfloatx2C4x2C4x3E256_0
{
    @align(16) data_1 : array<_MatrixStorage_float4x4_ColMajorstd140_0, i32(256)>,
};

struct SkinPalette_std140_0
{
    @align(16) joints_0 : _Array_std140_matrixx3Cfloatx2C4x2C4x3E256_0,
};

@binding(0) @group(3) var<uniform> skin_0 : SkinPalette_std140_0;
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
fn rsqrt_0( x_0 : f32) -> f32
{
    return 1.0f / sqrt(x_0);
}

struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
    @location(1) worldPosition_0 : vec3<f32>,
    @location(2) normal_0 : vec3<f32>,
};

fn vertexOutput_0( position_1 : vec4<f32>,  normal_1 : vec3<f32>,  uv_1 : vec2<f32>) -> VertexOutput_0
{
    var output_0 : VertexOutput_0;
    output_0.position_0 = (((position_1) * (mat4x4<f32>(draw_0.transform_0.data_0[i32(0)][i32(0)], draw_0.transform_0.data_0[i32(1)][i32(0)], draw_0.transform_0.data_0[i32(2)][i32(0)], draw_0.transform_0.data_0[i32(3)][i32(0)], draw_0.transform_0.data_0[i32(0)][i32(1)], draw_0.transform_0.data_0[i32(1)][i32(1)], draw_0.transform_0.data_0[i32(2)][i32(1)], draw_0.transform_0.data_0[i32(3)][i32(1)], draw_0.transform_0.data_0[i32(0)][i32(2)], draw_0.transform_0.data_0[i32(1)][i32(2)], draw_0.transform_0.data_0[i32(2)][i32(2)], draw_0.transform_0.data_0[i32(3)][i32(2)], draw_0.transform_0.data_0[i32(0)][i32(3)], draw_0.transform_0.data_0[i32(1)][i32(3)], draw_0.transform_0.data_0[i32(2)][i32(3)], draw_0.transform_0.data_0[i32(3)][i32(3)]))));
    output_0.worldPosition_0 = (((position_1) * (mat4x4<f32>(draw_0.model_0.data_0[i32(0)][i32(0)], draw_0.model_0.data_0[i32(1)][i32(0)], draw_0.model_0.data_0[i32(2)][i32(0)], draw_0.model_0.data_0[i32(3)][i32(0)], draw_0.model_0.data_0[i32(0)][i32(1)], draw_0.model_0.data_0[i32(1)][i32(1)], draw_0.model_0.data_0[i32(2)][i32(1)], draw_0.model_0.data_0[i32(3)][i32(1)], draw_0.model_0.data_0[i32(0)][i32(2)], draw_0.model_0.data_0[i32(1)][i32(2)], draw_0.model_0.data_0[i32(2)][i32(2)], draw_0.model_0.data_0[i32(3)][i32(2)], draw_0.model_0.data_0[i32(0)][i32(3)], draw_0.model_0.data_0[i32(1)][i32(3)], draw_0.model_0.data_0[i32(2)][i32(3)], draw_0.model_0.data_0[i32(3)][i32(3)])))).xyz;
    output_0.normal_0 = (((vec4<f32>(normal_1, 0.0f)) * (mat4x4<f32>(draw_0.normalTransform_0.data_0[i32(0)][i32(0)], draw_0.normalTransform_0.data_0[i32(1)][i32(0)], draw_0.normalTransform_0.data_0[i32(2)][i32(0)], draw_0.normalTransform_0.data_0[i32(3)][i32(0)], draw_0.normalTransform_0.data_0[i32(0)][i32(1)], draw_0.normalTransform_0.data_0[i32(1)][i32(1)], draw_0.normalTransform_0.data_0[i32(2)][i32(1)], draw_0.normalTransform_0.data_0[i32(3)][i32(1)], draw_0.normalTransform_0.data_0[i32(0)][i32(2)], draw_0.normalTransform_0.data_0[i32(1)][i32(2)], draw_0.normalTransform_0.data_0[i32(2)][i32(2)], draw_0.normalTransform_0.data_0[i32(3)][i32(2)], draw_0.normalTransform_0.data_0[i32(0)][i32(3)], draw_0.normalTransform_0.data_0[i32(1)][i32(3)], draw_0.normalTransform_0.data_0[i32(2)][i32(3)], draw_0.normalTransform_0.data_0[i32(3)][i32(3)])))).xyz;
    output_0.uv_0 = uv_1;
    return output_0;
}

struct vertexInput_0
{
    @location(0) position_2 : vec3<f32>,
    @location(1) uv_2 : vec2<f32>,
    @location(2) normal_2 : vec3<f32>,
    @location(3) joints_1 : vec4<u32>,
    @location(4) weights_0 : vec4<f32>,
};

@vertex
fn vertex_main( _S1 : vertexInput_0) -> VertexOutput_0
{
    var _S2 : mat4x4<f32> = mat4x4<f32>(0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
    var i_0 : u32 = u32(0);
    var matrix_0 : mat4x4<f32> = _S2;
    for(;;)
    {
        if(i_0 < u32(4))
        {
        }
        else
        {
            break;
        }
        var _S3 : mat4x4<f32> = mat4x4<f32>(skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(0)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(1)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(2)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(0)][i32(3)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(1)][i32(3)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(2)][i32(3)], skin_0.joints_0.data_1[_S1.joints_1[i_0]].data_0[i32(3)][i32(3)]);
        var _S4 : mat4x4<f32> = mat4x4<f32>(_S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0], _S1.weights_0[i_0]);
        var _S5 : mat4x4<f32> = matrix_0 + mat4x4<f32>(_S3[0] * _S4[0], _S3[1] * _S4[1], _S3[2] * _S4[2], _S3[3] * _S4[3]);
        i_0 = i_0 + u32(1);
        matrix_0 = _S5;
    }
    var a_0 : vec3<f32> = (((vec4<f32>(1.0f, 0.0f, 0.0f, 0.0f)) * (matrix_0))).xyz;
    var b_0 : vec3<f32> = (((vec4<f32>(0.0f, 1.0f, 0.0f, 0.0f)) * (matrix_0))).xyz;
    var c_0 : vec3<f32> = (((vec4<f32>(0.0f, 0.0f, 1.0f, 0.0f)) * (matrix_0))).xyz;
    var largest_0 : vec3<f32> = max(abs(a_0), max(abs(b_0), abs(c_0)));
    var _S6 : f32 = max(largest_0.x, max(largest_0.y, largest_0.z));
    var _S7 : vec3<f32> = (((vec4<f32>(_S1.normal_2, 0.0f)) * (matrix_0))).xyz;
    var normal_3 : vec3<f32>;
    if(_S6 > 0.0f)
    {
        var _S8 : vec3<f32> = vec3<f32>(_S6);
        var a_1 : vec3<f32> = a_0 / _S8;
        var b_1 : vec3<f32> = b_0 / _S8;
        var c_1 : vec3<f32> = c_0 / _S8;
        var _S9 : vec3<f32> = cross(b_1, c_1);
        var determinant_0 : f32 = dot(a_1, _S9);
        if((abs(determinant_0)) > 1.00000001335143196e-10f)
        {
            normal_3 = (_S9 * vec3<f32>(_S1.normal_2.x) + cross(c_1, a_1) * vec3<f32>(_S1.normal_2.y) + cross(a_1, b_1) * vec3<f32>(_S1.normal_2.z)) / vec3<f32>(determinant_0) / _S8;
        }
        else
        {
            normal_3 = _S7;
        }
    }
    else
    {
        normal_3 = _S7;
    }
    return vertexOutput_0((((vec4<f32>(_S1.position_2, 1.0f)) * (matrix_0))), normal_3, _S1.uv_2);
}

fn safeNormalize_0( value_0 : vec3<f32>,  fallback_0 : vec3<f32>) -> vec3<f32>
{
    var lengthSquared_0 : f32 = dot(value_0, value_0);
    var _S10 : vec3<f32>;
    if(lengthSquared_0 > 1.00000001686238353e-16f)
    {
        _S10 = value_0 * vec3<f32>(rsqrt_0(lengthSquared_0));
    }
    else
    {
        _S10 = fallback_0;
    }
    return _S10;
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
};

@fragment
fn fragment_main( _S11 : pixelInput_0, @builtin(front_facing) front_0 : bool, @builtin(position) position_3 : vec4<f32>) -> pixelOutput_0
{
    var base_0 : vec4<f32> = (textureSample((baseImage_0), (baseSampler_0), (_S11.uv_3))) * material_0.baseColor_0 * draw_0.color_0;
    var mr_0 : vec2<f32> = (textureSample((mrImage_0), (mrSampler_0), (_S11.uv_3))).yz;
    var _S12 : vec3<f32> = vec3<f32>(1.0f);
    var mapped_0 : vec3<f32> = (textureSample((normalImage_0), (normalSampler_0), (_S11.uv_3))).xyz * vec3<f32>(2.0f) - _S12;
    var ao_0 : f32 = (textureSample((aoImage_0), (aoSampler_0), (_S11.uv_3))).x;
    var emissive_1 : vec3<f32> = (textureSample((emissiveImage_0), (emissiveSampler_0), (_S11.uv_3))).xyz * material_0.emissive_0.xyz;
    var dx_0 : vec3<f32> = dpdx(_S11.worldPosition_1);
    var dy_0 : vec3<f32> = dpdy(_S11.worldPosition_1);
    var uvx_0 : vec2<f32> = dpdx(_S11.uv_3);
    var uvy_0 : vec2<f32> = dpdy(_S11.uv_3);
    var n_0 : vec3<f32> = safeNormalize_0(_S11.normal_4, vec3<f32>(0.0f, 1.0f, 0.0f));
    var _S13 : f32 = uvx_0.x;
    var _S14 : f32 = uvy_0.y;
    var _S15 : f32 = uvx_0.y;
    var _S16 : f32 = uvy_0.x;
    var determinant_1 : f32 = _S13 * _S14 - _S15 * _S16;
    var _S17 : bool;
    if((material_0.alpha_0.z) > 0.0f)
    {
        _S17 = (abs(determinant_1)) > 1.00000001335143196e-10f;
    }
    else
    {
        _S17 = false;
    }
    var alpha_1 : f32;
    var n_1 : vec3<f32>;
    if(_S17)
    {
        var _S18 : vec3<f32> = vec3<f32>(determinant_1);
        var rawT_0 : vec3<f32> = (dx_0 * vec3<f32>(_S14) - dy_0 * vec3<f32>(_S15)) / _S18;
        var rawB_0 : vec3<f32> = (dy_0 * vec3<f32>(_S13) - dx_0 * vec3<f32>(_S16)) / _S18;
        var t_0 : vec3<f32> = rawT_0 - n_0 * vec3<f32>(dot(n_0, rawT_0));
        if((dot(t_0, t_0)) > 1.00000001686238353e-16f)
        {
            var t_1 : vec3<f32> = normalize(t_0);
            var _S19 : vec3<f32> = cross(n_0, t_1);
            if((dot(_S19, rawB_0)) < 0.0f)
            {
                alpha_1 = -1.0f;
            }
            else
            {
                alpha_1 = 1.0f;
            }
            var b_2 : vec3<f32> = _S19 * vec3<f32>(alpha_1);
            var _S20 : vec2<f32> = mapped_0.xy * vec2<f32>(material_0.factors_0.z);
            mapped_0.x = _S20.x;
            mapped_0.y = _S20.y;
            n_1 = safeNormalize_0(t_1 * vec3<f32>(mapped_0.x) + b_2 * vec3<f32>(mapped_0.y) + n_0 * vec3<f32>(mapped_0.z), n_0);
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
        _S17 = (base_0.w) < (material_0.alpha_0.y);
    }
    else
    {
        _S17 = false;
    }
    if(_S17)
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
    var v_0 : vec3<f32> = safeNormalize_0(frame_0.camera_0.xyz - _S11.worldPosition_1, n_2);
    var l_0 : vec3<f32> = frame_0.lightDirection_0.xyz;
    var h_0 : vec3<f32> = safeNormalize_0(v_0 + l_0, n_2);
    var nv_0 : f32 = saturate(dot(n_2, v_0));
    var nl_0 : f32 = saturate(dot(n_2, l_0));
    var nh_0 : f32 = saturate(dot(n_2, h_0));
    var _S21 : vec3<f32> = base_0.xyz;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), _S21, vec3<f32>(metallic_0));
    var fresnel_0 : vec3<f32> = f0_0 + (_S12 - f0_0) * vec3<f32>(pow(1.0f - saturate(dot(v_0, h_0)), 5.0f));
    var a_2 : f32 = roughness_0 * roughness_0;
    var a2_0 : f32 = a_2 * a_2;
    var denominator_0 : f32 = nh_0 * nh_0 * (a2_0 - 1.0f) + 1.0f;
    var _S22 : f32 = 1.0f - a2_0;
    var _S23 : vec3<f32> = vec3<f32>((1.0f - metallic_0));
    var _S24 : pixelOutput_0 = pixelOutput_0( vec4<f32>(((_S12 - fresnel_0) * _S23 * _S21 / vec3<f32>(3.14159274101257324f) + fresnel_0 * vec3<f32>((a2_0 / (3.14159274101257324f * denominator_0 * denominator_0))) * vec3<f32>((0.5f / max(nl_0 * sqrt(nv_0 * nv_0 * _S22 + a2_0) + nv_0 * sqrt(nl_0 * nl_0 * _S22 + a2_0), 9.99999997475242708e-07f)))) * frame_0.radiance_0.xyz * vec3<f32>(nl_0) + frame_0.ambient_0.xyz * _S21 * _S23 * vec3<f32>(mix(1.0f, ao_0, material_0.factors_0.w)) + emissive_1, alpha_1) );
    return _S24;
}
