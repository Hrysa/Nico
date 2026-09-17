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
};

@vertex
fn vertex_main( _S1 : vertexInput_0) -> VertexOutput_0
{
    return vertexOutput_0(vec4<f32>(_S1.position_2, 1.0f), _S1.normal_2, _S1.uv_2);
}

fn safeNormalize_0( value_0 : vec3<f32>,  fallback_0 : vec3<f32>) -> vec3<f32>
{
    var lengthSquared_0 : f32 = dot(value_0, value_0);
    var _S2 : vec3<f32>;
    if(lengthSquared_0 > 1.00000001686238353e-16f)
    {
        _S2 = value_0 * vec3<f32>(rsqrt_0(lengthSquared_0));
    }
    else
    {
        _S2 = fallback_0;
    }
    return _S2;
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
};

@fragment
fn fragment_main( _S3 : pixelInput_0, @builtin(front_facing) front_0 : bool, @builtin(position) position_3 : vec4<f32>) -> pixelOutput_0
{
    var base_0 : vec4<f32> = (textureSample((baseImage_0), (baseSampler_0), (_S3.uv_3))) * material_0.baseColor_0 * draw_0.color_0;
    var mr_0 : vec2<f32> = (textureSample((mrImage_0), (mrSampler_0), (_S3.uv_3))).yz;
    var _S4 : vec3<f32> = vec3<f32>(1.0f);
    var mapped_0 : vec3<f32> = (textureSample((normalImage_0), (normalSampler_0), (_S3.uv_3))).xyz * vec3<f32>(2.0f) - _S4;
    var ao_0 : f32 = (textureSample((aoImage_0), (aoSampler_0), (_S3.uv_3))).x;
    var emissive_1 : vec3<f32> = (textureSample((emissiveImage_0), (emissiveSampler_0), (_S3.uv_3))).xyz * material_0.emissive_0.xyz;
    var dx_0 : vec3<f32> = dpdx(_S3.worldPosition_1);
    var dy_0 : vec3<f32> = dpdy(_S3.worldPosition_1);
    var uvx_0 : vec2<f32> = dpdx(_S3.uv_3);
    var uvy_0 : vec2<f32> = dpdy(_S3.uv_3);
    var n_0 : vec3<f32> = safeNormalize_0(_S3.normal_3, vec3<f32>(0.0f, 1.0f, 0.0f));
    var _S5 : f32 = uvx_0.x;
    var _S6 : f32 = uvy_0.y;
    var _S7 : f32 = uvx_0.y;
    var _S8 : f32 = uvy_0.x;
    var determinant_0 : f32 = _S5 * _S6 - _S7 * _S8;
    var _S9 : bool;
    if((material_0.alpha_0.z) > 0.0f)
    {
        _S9 = (abs(determinant_0)) > 1.00000001335143196e-10f;
    }
    else
    {
        _S9 = false;
    }
    var alpha_1 : f32;
    var n_1 : vec3<f32>;
    if(_S9)
    {
        var _S10 : vec3<f32> = vec3<f32>(determinant_0);
        var rawT_0 : vec3<f32> = (dx_0 * vec3<f32>(_S6) - dy_0 * vec3<f32>(_S7)) / _S10;
        var rawB_0 : vec3<f32> = (dy_0 * vec3<f32>(_S5) - dx_0 * vec3<f32>(_S8)) / _S10;
        var t_0 : vec3<f32> = rawT_0 - n_0 * vec3<f32>(dot(n_0, rawT_0));
        if((dot(t_0, t_0)) > 1.00000001686238353e-16f)
        {
            var t_1 : vec3<f32> = normalize(t_0);
            var _S11 : vec3<f32> = cross(n_0, t_1);
            if((dot(_S11, rawB_0)) < 0.0f)
            {
                alpha_1 = -1.0f;
            }
            else
            {
                alpha_1 = 1.0f;
            }
            var b_0 : vec3<f32> = _S11 * vec3<f32>(alpha_1);
            var _S12 : vec2<f32> = mapped_0.xy * vec2<f32>(material_0.factors_0.z);
            mapped_0.x = _S12.x;
            mapped_0.y = _S12.y;
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
        _S9 = (base_0.w) < (material_0.alpha_0.y);
    }
    else
    {
        _S9 = false;
    }
    if(_S9)
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
    var v_0 : vec3<f32> = safeNormalize_0(frame_0.camera_0.xyz - _S3.worldPosition_1, n_2);
    var l_0 : vec3<f32> = frame_0.lightDirection_0.xyz;
    var h_0 : vec3<f32> = safeNormalize_0(v_0 + l_0, n_2);
    var nv_0 : f32 = saturate(dot(n_2, v_0));
    var nl_0 : f32 = saturate(dot(n_2, l_0));
    var nh_0 : f32 = saturate(dot(n_2, h_0));
    var _S13 : vec3<f32> = base_0.xyz;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), _S13, vec3<f32>(metallic_0));
    var fresnel_0 : vec3<f32> = f0_0 + (_S4 - f0_0) * vec3<f32>(pow(1.0f - saturate(dot(v_0, h_0)), 5.0f));
    var a_0 : f32 = roughness_0 * roughness_0;
    var a2_0 : f32 = a_0 * a_0;
    var denominator_0 : f32 = nh_0 * nh_0 * (a2_0 - 1.0f) + 1.0f;
    var _S14 : f32 = 1.0f - a2_0;
    var _S15 : vec3<f32> = vec3<f32>((1.0f - metallic_0));
    var _S16 : pixelOutput_0 = pixelOutput_0( vec4<f32>(((_S4 - fresnel_0) * _S15 * _S13 / vec3<f32>(3.14159274101257324f) + fresnel_0 * vec3<f32>((a2_0 / (3.14159274101257324f * denominator_0 * denominator_0))) * vec3<f32>((0.5f / max(nl_0 * sqrt(nv_0 * nv_0 * _S14 + a2_0) + nv_0 * sqrt(nl_0 * nl_0 * _S14 + a2_0), 9.99999997475242708e-07f)))) * frame_0.radiance_0.xyz * vec3<f32>(nl_0) + frame_0.ambient_0.xyz * _S13 * _S15 * vec3<f32>(mix(1.0f, ao_0, material_0.factors_0.w)) + emissive_1, alpha_1) );
    return _S16;
}
