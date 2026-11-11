#version 450
#include "frame.glsl"

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;

// Per shell (see ShieldInstance): the hull's camera-relative 3x4 transform as rows; x how far the shield is online,
// y how close it is to breaking, z how far its collapse has gone (0 none), w the hull's material; four ripples, each
// a hull-local point and an age in seconds, and their strengths.
layout(location = 2) in vec4 row0;
layout(location = 3) in vec4 row1;
layout(location = 4) in vec4 row2;
layout(location = 5) in vec4 params;
layout(location = 6) in vec4 ripple0;
layout(location = 7) in vec4 ripple1;
layout(location = 8) in vec4 ripple2;
layout(location = 9) in vec4 ripple3;
layout(location = 10) in vec4 strengths;

layout(location = 0) out vec3 worldNormal;
layout(location = 1) out vec3 worldPosition;
layout(location = 2) out vec3 localPosition;
layout(location = 3) flat out vec4 look;
layout(location = 4) flat out vec4 ripples[4];
layout(location = 8) flat out vec4 rippleStrength;
layout(location = 9) flat out int cut;
layout(location = 10) flat out int own;

void main()
{
    // A collapsing shield swells a little as it goes.
    vec3 p = position + normal * (params.z * 0.6);
    vec4 at = vec4(p, 1.0);
    worldPosition = vec3(dot(row0, at), dot(row1, at), dot(row2, at));
    worldNormal = vec3(dot(row0.xyz, normal), dot(row1.xyz, normal), dot(row2.xyz, normal));
    localPosition = position;
    look = params;
    ripples[0] = ripple0;
    ripples[1] = ripple1;
    ripples[2] = ripple2;
    ripples[3] = ripple3;
    rippleStrength = strengths;

    // The material is decoded as the hull's mesh decodes it: the kind, plus four times one more than the slot.
    int slot = int((params.w + 0.5) * 0.25);
    cut = slot - 1;
    own = params.w - 4.0 * float(slot) > 1.5 ? 1 : 0;
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
