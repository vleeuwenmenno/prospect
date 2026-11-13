#version 450
#include "frame.glsl"

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;

// Per shell (see ShieldInstance): the hull's camera-relative 3x4 transform as rows; x how far the shield is online,
// y how close it is to breaking, z how far its collapse has gone (0 none), w the hull's material; four ripples, each
// a hull-local point and an age in seconds, and their strengths; how it shivers while it charges back up.
layout(location = 2) in vec4 row0;
layout(location = 3) in vec4 row1;
layout(location = 4) in vec4 row2;
layout(location = 5) in vec4 params;
layout(location = 6) in vec4 ripple0;
layout(location = 7) in vec4 ripple1;
layout(location = 8) in vec4 ripple2;
layout(location = 9) in vec4 ripple3;
layout(location = 10) in vec4 strengths;
layout(location = 11) in vec4 recovery;

layout(location = 0) out vec3 worldNormal;
layout(location = 1) out vec3 worldPosition;
layout(location = 2) out vec3 localPosition;
layout(location = 3) flat out vec4 look;
layout(location = 4) flat out vec4 ripples[4];
layout(location = 8) flat out vec4 rippleStrength;
layout(location = 9) flat out int cut;
layout(location = 10) flat out int own;
layout(location = 11) flat out vec4 recovering;

void main()
{
    // A collapsing shield swells a little as it goes.
    vec3 p = position + normal * (params.z * 0.6);

    // Charging back up, the whole field shivers: squashed and stretched a little along its own axes, out of step
    // with each other. It is one linear map for every vertex of the shell, so faces that meet stay met.
    if (recovery.x > 0.0)
    {
        float phase = frame.dust.w * recovery.z * 6.2831853 + recovery.w;
        vec3 squash = vec3(sin(phase), sin(phase * 1.31 + 2.1), sin(phase * 0.83 + 4.2));
        p += p * squash * recovery.x;
    }

    vec4 at = vec4(p, 1.0);
    worldPosition = vec3(dot(row0, at), dot(row1, at), dot(row2, at));
    // A shield is drawn out along the hull's length as the hull is, so its normal is turned by the cofactor matrix,
    // as in mesh.vert; the fragment shader normalises it.
    vec3 cx = vec3(row0.x, row1.x, row2.x);
    vec3 cy = vec3(row0.y, row1.y, row2.y);
    vec3 cz = vec3(row0.z, row1.z, row2.z);
    worldNormal = cross(cy, cz) * normal.x + cross(cz, cx) * normal.y + cross(cx, cy) * normal.z;
    localPosition = position;
    look = params;
    ripples[0] = ripple0;
    ripples[1] = ripple1;
    ripples[2] = ripple2;
    ripples[3] = ripple3;
    rippleStrength = strengths;
    recovering = recovery;

    // The material is decoded as the hull's mesh decodes it: the kind, plus four times one more than the slot.
    int slot = int((params.w + 0.5) * 0.25);
    cut = slot - 1;
    own = params.w - 4.0 * float(slot) > 1.5 ? 1 : 0;
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
