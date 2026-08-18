float3 ResolveSurfaceNormal(
    float3 interpolatedNormal,
    float4 interpolatedTangent,
    float3 sampledNormal,
    bool isFrontFace)
{
    float3 geometricNormal = normalize(interpolatedNormal) *
        (isFrontFace ? 1.0 : -1.0);
    float3 tangentCandidate = interpolatedTangent.xyz - geometricNormal *
        dot(interpolatedTangent.xyz, geometricNormal);
    float tangentLengthSquared = dot(tangentCandidate, tangentCandidate);
    float3 tangent;
    if (tangentLengthSquared > 0.000001)
    {
        tangent = tangentCandidate * rsqrt(tangentLengthSquared);
    }
    else
    {
        float3 helperAxis = abs(geometricNormal.y) < 0.999
            ? float3(0.0, 1.0, 0.0)
            : float3(1.0, 0.0, 0.0);
        tangent = normalize(cross(helperAxis, geometricNormal));
    }
    float3 bitangent = normalize(cross(geometricNormal, tangent)) *
        interpolatedTangent.w;
    return normalize(tangent * sampledNormal.x + bitangent * sampledNormal.y +
        geometricNormal * sampledNormal.z);
}
