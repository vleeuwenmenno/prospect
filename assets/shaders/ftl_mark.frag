#version 450
#include "frame.glsl"
#include "ftl.glsl"

layout(location = 0) in vec2 corner;
layout(location = 1) in vec4 jump;
layout(location = 0) out vec4 outColor;

// A jump in the world. Drive kind, stage (1 drive working, 2 in transit, 3 arriving), progress through the stage and
// a per-hull seed arrive in jump.
void main()
{
    int kind = int(jump.x + 0.5);
    int stage = int(jump.y + 0.5);
    float progress = jump.z;
    float radius = length(corner);

    if (kind != 1)
    {
        // A rift or a wormhole draws nothing here. It only marks, in alpha, where the composite pass may bend the
        // view and draw the opening. The quad is depth tested, so a hull in front of the opening keeps the mask
        // clear and is neither bent nor covered.
        outColor = vec4(0.0, 0.0, 0.0, radius < 1.0 ? 1.0 : 0.0);
        return;
    }

    // Hyperspace: a hard point of light with a thin flat streak through it. It gathers ahead of the nose through
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
    float streak = exp(-abs(corner.y) * 140.0) * (1.0 - smoothstep(0.0, 1.0, abs(corner.x)));
    vec3 color = vec3(0.7, 0.85, 1.0) * (core * 5.0 * power + streak * 2.0 * power + ring * 0.7);
    outColor = vec4(color * (1.0 - smoothstep(0.85, 1.0, radius)), 0.0);
}
