#version 450
#include "frame.glsl"
#include "ftl.glsl"

layout(location = 0) in vec2 corner;
layout(location = 1) in vec4 jump;
layout(location = 2) in vec3 along;
layout(location = 0) out vec4 outColor;

// A jump in the world. Drive kind, stage (1 drive working, 2 in transit, 3 arriving), progress through the stage and
// a per-hull seed arrive in jump.
void main()
{
    int kind = int(jump.x + 0.5);
    int stage = int(jump.y + 0.5);
    float progress = jump.z;
    float radius = length(corner);

    // Only hyperspace leaves light in the world here; a rift's or wormhole's opening is worked out per pixel in the
    // composite pass, against the scene's depth.
    if (kind != 1)
        discard;

    // Hyperspace: a hard point of light with a thin flat streak through it along the jump. It gathers ahead of the nose through
    // the charge, appears at the far end just before the hull does, and flashes with a ring as it arrives.
    float power = 0.0;
    float ring = 0.0;
    if (stage == 1)
    {
        power = pow(ftlEase(progress), 1.6);
    }
    else if (stage == 2)
    {
        power = ftlEase((progress - 0.65) / 0.35) * 0.6;
    }
    else if (stage == 3)
    {
        float fade = 1.0 - ftlEase(progress);
        power = fade * fade;
        ring = exp(-abs(radius - (0.08 + 0.8 * ftlEase(progress))) * 160.0) * fade * fade;
    }

    float core = 0.0006 / (radius * radius + 0.0006);
    // The streak runs along the jump as seen from here: its full length from the side, short seen head-on.
    vec2 frameAxis = vec2(dot(corner, along.xy), dot(corner, vec2(-along.y, along.x)));
    float reach = mix(0.15, 1.0, along.z);
    float streak = exp(-abs(frameAxis.y) * 140.0) * (1.0 - smoothstep(0.0, reach, abs(frameAxis.x)));
    vec3 color = vec3(0.7, 0.85, 1.0) * (core * 5.0 * power + streak * 2.0 * power + ring * 0.7);
    outColor = vec4(color * (1.0 - smoothstep(0.85, 1.0, radius)), 0.0);
}
