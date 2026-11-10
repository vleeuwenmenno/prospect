#version 450
#include "frame.glsl"

layout(location = 0) in vec2 corner;
layout(location = 1) in vec4 jump;
layout(location = 0) out vec4 outColor;

float hash2(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash2(i), hash2(i + vec2(1, 0)), f.x), mix(hash2(i + vec2(0, 1)), hash2(i + vec2(1, 1)), f.x), f.y);
}

// What everyone else sees of a jump: the drive's mark opening where a hull leaves and flashing where it arrives.
void main()
{
    int kind = int(jump.x + 0.5);
    int phase = int(jump.y + 0.5);
    float progress = jump.z;
    float time = frame.dust.w + jump.w * 31.0;

    // Opens through the spool, holds a moment as the hull goes, and bursts then fades on arrival.
    float open = phase == 1 ? progress : phase == 2 ? 1.0 - progress : 1.0 - progress;
    float burst = phase == 3 ? (1.0 - progress) * (1.0 - progress) : phase == 2 ? (1.0 - progress) : 0.0;

    float radius = length(corner);
    float angle = atan(corner.y, corner.x);
    float edgeFade = 1.0 - smoothstep(0.8, 1.0, radius);
    vec3 color = vec3(0.0);

    if (kind == 1)
    {
        // Hyperspace: a hard flare, a flat streak of light and a ring thrown outward.
        float flare = 0.02 / (radius * radius + 0.004);
        float streak = exp(-abs(corner.y) * 26.0) * exp(-abs(corner.x) * 2.2);
        float ring = exp(-abs(radius - (0.15 + 0.7 * burst)) * 30.0) * burst;
        color = vec3(0.7, 0.85, 1.0) * (flare * open * 0.8 + streak * (open + burst * 2.0) + ring * 2.0);
    }
    else if (kind == 2)
    {
        // Wormhole: a round gate, its rim turning, dark in the middle.
        float size = 0.62 * open;
        float rim = exp(-abs(radius - size) * 22.0);
        float turn = noise(vec2(angle * 4.0 + time * 2.5, radius * 6.0 - time * 3.0));
        float inside = (1.0 - smoothstep(size - 0.08, size, radius)) * 0.45 * turn;
        color = vec3(0.2, 0.75, 1.0) * rim * (0.6 + 0.9 * turn) * 1.8 + vec3(0.35, 0.2, 0.8) * inside + vec3(0.8, 0.95, 1.0) * burst * 0.03 / (radius * radius + 0.01);
    }
    else
    {
        // Rift: a vertical tear with ragged, flickering edges.
        float wander = (noise(vec2(corner.y * 5.0, time * 4.0)) - 0.5) * 0.3;
        float toTear = abs(corner.x - wander);
        float span = 1.0 - smoothstep(0.75 * open, 0.9 * open + 0.01, abs(corner.y));
        float width = 0.03 + 0.1 * open;
        float tear = smoothstep(width, 0.0, toTear) * span;
        float edge = 0.02 / (abs(toTear - width) + 0.02) * span;
        color = vec3(0.8, 0.25, 1.0) * edge * (0.7 + 0.6 * noise(vec2(corner.y * 30.0, time * 12.0))) + vec3(1.0, 0.9, 1.0) * tear * (1.0 + burst * 3.0);
    }

    outColor = vec4(color * edgeFade, 1.0);
}
