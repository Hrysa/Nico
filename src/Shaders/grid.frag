#version 450

layout(push_constant) uniform GridPushConstants {
    mat4 viewProjection;
    mat4 inverseViewProjection;
} pc;

layout(location = 0) in vec3 nearWorld;
layout(location = 1) in vec3 farWorld;

layout(location = 0) out vec4 outColor;

float gridMask(vec2 worldPosition, float spacing) {
    vec2 coordinate = worldPosition / spacing;
    vec2 width = max(fwidth(coordinate), vec2(0.0001));
    vec2 distanceToLine = abs(fract(coordinate - 0.5) - 0.5) / width;
    return 1.0 - min(min(distanceToLine.x, distanceToLine.y), 1.0);
}

void main() {
    vec3 ray = farWorld - nearWorld;
    if (abs(ray.y) < 0.000001)
        discard;

    float rayDistance = -nearWorld.y / ray.y;
    if (rayDistance <= 0.0)
        discard;

    vec3 worldPosition = nearWorld + ray * rayDistance;
    float minor = gridMask(worldPosition.xz, 1.0);
    float major = gridMask(worldPosition.xz, 5.0);
    float line = max(minor * 0.48, major);
    if (line <= 0.001)
        discard;

    float distanceFromCamera = length(worldPosition - nearWorld);
    float fade = 1.0 - smoothstep(20.0, 120.0, distanceFromCamera);
    float horizonFade = smoothstep(0.0, 0.025, abs(ray.y));
    float alpha = line * fade * horizonFade;
    if (alpha <= 0.001)
        discard;

    vec3 minorColor = vec3(0.22, 0.22, 0.24);
    vec3 majorColor = vec3(0.38, 0.38, 0.41);
    vec3 color = mix(minorColor, majorColor, major);

    vec4 clip = pc.viewProjection * vec4(worldPosition, 1.0);
    gl_FragDepth = clip.z / clip.w;
    outColor = vec4(color, alpha);
}
