#version 450
#include "frame.glsl"

layout(location = 0) out float alpha;
layout(location = 1) out float across;

const float cell = 360.0;

vec3 hash3(uint n)
{
    uvec3 v = uvec3(n) * uvec3(1597334673u, 3812015801u, 2798796415u) + uvec3(n >> 3);
    v = (v ^ (v >> 15)) * 2246822519u;
    v ^= v >> 13;
    return vec3(v & 0xFFFFFFu) / float(0x1000000);
}

// Motes of dust fixed in space, wrapped into a cube around the camera so there are always some nearby. Each is
// drawn as a short streak along the camera's motion, which is what makes speed visible in empty space.
void main()
{
    vec3 home = hash3(uint(gl_InstanceIndex));
    vec3 position = (fract(home - frame.dust.xyz / cell) - 0.5) * cell;

    vec3 tail = position - frame.drift.xyz * 0.035;
    vec4 a = frame.viewProjection * vec4(position, 1.0);
    vec4 b = frame.viewProjection * vec4(tail, 1.0);

    int corner = gl_VertexIndex;
    bool atTail = corner == 1 || corner == 2 || corner == 4;
    float side = (corner == 0 || corner == 1 || corner == 3) ? -1.0 : 1.0;

    vec2 na = a.xy / max(a.w, 1e-4);
    vec2 nb = b.xy / max(b.w, 1e-4);
    vec2 along = nb - na;
    vec2 pixels = along * frame.lens.zw * 0.5;
    vec2 normal = length(pixels) > 0.5 ? normalize(vec2(-pixels.y, pixels.x)) : vec2(0.0, 1.0);
    if (length(pixels) <= 0.5)
        nb = na + vec2(2.0, 0.0) / frame.lens.zw;

    vec4 clip = atTail ? b : a;
    vec2 ndc = (atTail ? nb : na) + normal * side * 1.5 / frame.lens.zw * 2.0;
    gl_Position = vec4(ndc * clip.w, clip.z, clip.w);

    float distanceToCamera = length(position);
    float edge = 1.0 - smoothstep(cell * 0.3, cell * 0.5, distanceToCamera);
    float near = smoothstep(4.0, 25.0, distanceToCamera);
    alpha = edge * near * (0.05 + 0.3 * smoothstep(5.0, 150.0, frame.drift.w));
    across = side;

    // Anything with an end behind the camera would smear across the screen; drop it.
    if (a.w < 0.5 || b.w < 0.5)
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
}
