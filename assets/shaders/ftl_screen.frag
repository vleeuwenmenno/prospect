#version 450
#include "frame.glsl"

layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;

float hash(float n)
{
    return fract(sin(n * 127.1) * 43758.5453);
}

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

// What the pilot sees of their own jump. Each drive has its own look; all three build during the spool, fill the
// view while the hull is in the tunnel, and clear as it arrives.
void main()
{
    int kind = int(frame.ftl.x + 0.5);
    int phase = int(frame.ftl.y + 0.5);
    float progress = frame.ftl.z;
    float time = frame.dust.w;

    float strength = phase == 1 ? progress * progress * progress : phase == 2 ? 1.0 : (1.0 - progress) * (1.0 - progress);
    float cover = phase == 2 ? 1.0 : phase == 1 ? smoothstep(0.75, 1.0, progress) : 1.0 - smoothstep(0.0, 0.5, progress);

    vec2 uv = ndc * vec2(frame.lens.y, 1.0);
    float radius = length(uv);
    float angle = atan(uv.y, uv.x);
    vec3 color = vec3(0.0);

    if (kind == 1)
    {
        // Hyperspace: the stars stretch into lines racing out from the point ahead.
        float lanes = angle * 260.0 / 6.2832;
        float lane = floor(lanes);
        float seed = hash(lane);
        float thin = 1.0 - smoothstep(0.1, 0.5, abs(fract(lanes) - 0.5) * 2.0);
        float bright = pow(seed, 9.0) * thin;
        float travel = fract(radius * (1.5 + seed * 2.0) - time * (3.0 + seed * 5.0) + seed * 17.0);
        float streak = bright * smoothstep(0.0, 0.25, travel) * (1.0 - smoothstep(0.55, 1.0, travel)) * smoothstep(0.04, 0.5, radius);
        float core = 0.035 / (radius + 0.02);
        color = vec3(0.65, 0.8, 1.0) * (streak * 7.0 + core * strength) + vec3(0.012, 0.02, 0.05) * cover;
    }
    else if (kind == 2)
    {
        // Wormhole: a turning throat, bright at the rim and dark at the far end.
        float depth = 1.0 / (radius + 0.08);
        float swirl = angle + depth * 0.55 + time * 1.3;
        float bands = 0.5 + 0.5 * sin(depth * 5.0 - time * 9.0);
        float ribs = noise(vec2(swirl * 3.0, depth * 2.0 - time * 4.0));
        float throat = smoothstep(0.0, 0.5, radius);
        color = mix(vec3(0.05, 0.4, 0.75), vec3(0.55, 0.3, 0.95), ribs) * (0.25 + 0.9 * bands * ribs) * throat * 1.6;
        color += vec3(0.5, 0.9, 1.0) * exp(-abs(radius - 0.55) * 9.0) * 0.5;
    }
    else
    {
        // Rift: a ragged tear, with the light of somewhere else behind it and cracks running off it.
        float wander = (noise(vec2(uv.y * 4.0, time * 3.0)) - 0.5) * 0.35 + (noise(vec2(uv.y * 17.0, time * 9.0)) - 0.5) * 0.08;
        float toTear = abs(uv.x - wander);
        float width = 0.02 + 0.5 * strength * strength;
        float tear = smoothstep(width, 0.0, toTear);
        float edge = 0.012 / (abs(toTear - width) + 0.012);
        float cracks = pow(noise(vec2(uv.x * 9.0 + time, uv.y * 26.0)), 9.0) * 6.0 * (1.0 - smoothstep(0.0, 0.8, toTear));
        color = vec3(0.75, 0.2, 0.95) * (edge * 0.8 + cracks) + vec3(1.0, 0.85, 1.0) * tear * (0.6 + 0.4 * noise(uv * 7.0 + time * 5.0));
    }

    outColor = vec4(color * max(strength, cover), cover);
}
