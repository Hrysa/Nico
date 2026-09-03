struct VertexOutput
{
    float4 position : SV_Position;
};

[shader("vertex")]
VertexOutput vertex_main(uint vertexIndex : SV_VertexID)
{
    static const float4 positions[3] =
    {
        float4(0.0, 0.55, 0.0, 2.0),
        float4(-0.5, -0.45, 0.0, 1.0),
        float4(0.5, -0.45, 0.0, 1.0),
    };

    VertexOutput output;
    output.position = positions[vertexIndex];
    return output;
}

[shader("fragment")]
float4 fragment_main() : SV_Target0
{
    return float4(0.95, 0.42, 0.16, 1.0);
}
