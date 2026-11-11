#version 450
#include "frame.glsl"
#include "ftl.glsl"

layout(location = 0) in vec2 corner;
layout(location = 1) in vec4 jump;
layout(location = 2) in vec3 along;
layout(location = 3) in float reach;
layout(location = 0) out vec4 outColor;

// Half the size of a mark in hull radii as its point and streak are drawn, whatever the quad: WorldView.MarkReach.
const float MarkReach = 2.5;

// The pulse a hull arriving from hyperspace sends out, in the quad's own units. A short flash at the centre, then a
// shock ring racing out and slowing, a dimmer wake inside it, all fading by the end of the arrival. Heft, 0..1,
// comes from the quad's size: a heavier hull's pulse reaches further and is brighter, up to a fixed bound.
vec3 arrivalPulse(float r, float t, float heft)
{
    float out3 = 1.0 - pow(1.0 - clamp(t, 0.0, 1.0), 3.0);
    float front = 0.05 + 0.75 * out3;
    float width = 0.012 + 0.06 * out3;
    float d = (r - front) / width;
    float ring = exp(-d * d);
    // Behind the front the air it passed still glows, softly and falling away toward the centre.
    float wake = exp(-max(front - r, 0.0) / (0.04 + 0.2 * out3)) * (1.0 - smoothstep(front - width, front + width, r));
    float fade = 1.0 - ftlEase(t);
    float flash = exp(-t * 12.0) * 0.003 / (r * r + 0.003);
    float gain = 0.6 + 0.9 * heft;
    return vec3(0.7, 0.85, 1.0) * gain * (ring * 0.9 * fade + wake * 0.3 * fade * fade) + vec3(1.0, 0.96, 0.9) * flash * 2.5 * gain;
}

// A jump in the world. Drive kind, stage (1 drive working, 2 in transit, 3 arriving), progress through the stage and
// a per-hull seed arrive in jump.
void main()
{
    int kind = int(jump.x + 0.5);
    int stage = int(jump.y + 0.5);
    float progress = jump.z;

    // Only hyperspace leaves light in the world here; a rift's or wormhole's opening is worked out per pixel in the
    // composite pass, against the scene's depth.
    if (kind != 1)
        discard;

    // An arrival's quad is grown to hold its pulse; the point and the streak keep the size they always have.
    bool arrival = reach > 0.0;
    vec2 at = arrival ? corner * (reach / MarkReach) : corner;
    float radius = length(at);

    // Hyperspace: a hard point of light with a thin flat streak through it along the jump. It gathers ahead of the nose through
    // the charge, appears at the far end just before the hull does, and flashes as it arrives: with a ring where a
    // drive fired, with a pulse sized to the hull where one arrives.
    float power = 0.0;
    float ring = 0.0;
    vec3 pulse = vec3(0.0);
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
        if (arrival)
            pulse = arrivalPulse(length(corner), progress, 1.0 - MarkReach / reach) * (1.0 - smoothstep(0.85, 1.0, length(corner)));
        else
            ring = exp(-abs(radius - (0.08 + 0.8 * ftlEase(progress))) * 160.0) * fade * fade;
    }

    float core = 0.0006 / (radius * radius + 0.0006);
    // The streak runs along the jump as seen from here: its full length from the side, short seen head-on.
    vec2 frameAxis = vec2(dot(at, along.xy), dot(at, vec2(-along.y, along.x)));
    float streakReach = mix(0.15, 1.0, along.z);
    float streak = exp(-abs(frameAxis.y) * 140.0) * (1.0 - smoothstep(0.0, streakReach, abs(frameAxis.x)));
    vec3 color = vec3(0.7, 0.85, 1.0) * (core * 5.0 * power + streak * 2.0 * power + ring * 0.7);
    outColor = vec4(color * (1.0 - smoothstep(0.85, 1.0, radius)) + pulse, 0.0);
}
