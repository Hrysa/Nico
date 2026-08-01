#version 450

layout(push_constant) uniform GridPushConstants {
    mat4 viewProjection;
    mat4 inverseViewProjection;
} pc;

layout(location = 0) out vec3 nearWorld;
layout(location = 1) out vec3 farWorld;

vec3 unproject(vec2 position, float depth) {
    vec4 world = pc.inverseViewProjection * vec4(position, depth, 1.0);
    return world.xyz / world.w;
}

void main() {
    vec2 positions[3] = vec2[](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );

    vec2 position = positions[gl_VertexIndex];
    nearWorld = unproject(position, 0.0);
    farWorld = unproject(position, 1.0);
    gl_Position = vec4(position, 0.0, 1.0);
}
