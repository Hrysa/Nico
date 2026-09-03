const positions_0 : array<vec4<f32>, i32(3)> = array<vec4<f32>, i32(3)>( vec4<f32>(0.0f, 0.55000001192092896f, 0.0f, 2.0f), vec4<f32>(-0.5f, -0.44999998807907104f, 0.0f, 1.0f), vec4<f32>(0.5f, -0.44999998807907104f, 0.0f, 1.0f) );
struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
};

@vertex
fn vertex_main(@builtin(vertex_index) vertexIndex_0 : u32) -> VertexOutput_0
{
    var output_0 : VertexOutput_0;
    output_0.position_0 = positions_0[vertexIndex_0];
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn fragment_main() -> pixelOutput_0
{
    var _S1 : pixelOutput_0 = pixelOutput_0( vec4<f32>(0.94999998807907104f, 0.41999998688697815f, 0.15999999642372131f, 1.0f) );
    return _S1;
}
