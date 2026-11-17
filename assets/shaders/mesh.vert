#version 450
#include "frame.glsl"

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;

// Per instance: a 3x4 model matrix (rows), camera-relative; base colour with material kind in alpha (0 rock, 1 hull,
// 2 the pilot's own hull), plus four times one more than the passage slot whose opening cuts the hull.
layout(location = 2) in vec4 row0;
layout(location = 3) in vec4 row1;
layout(location = 4) in vec4 row2;
layout(location = 5) in vec4 tint;
layout(location = 6) in vec4 rockMarks;
layout(location = 7) flat out uvec4 markMask;

layout(location = 0) out vec3 worldNormal;
layout(location = 1) out vec3 worldPosition;
layout(location = 2) out vec3 localPosition;
layout(location = 3) out vec4 color;
layout(location = 4) flat out int cut;
// Bare metal of a thrust part's model, from 0 (black) to 1, or -1 for ordinary plating: carried in the length of
// the mesh's normal, 2 plus the shade (PropulsionModels.Tag). Every other mesh has unit normals.
layout(location = 5) out float metal;
// The paint of a tank's vessel, RGB from 0 to 1, or -1 for anything else: a normal's length of 3.5 and over carries
// five bits a channel (PropulsionModels.Paint). Flat: a facet's three vertices carry the same paint.
layout(location = 6) flat out vec3 paint;
// Loose ore chunk (material 0.03 to 0.12, grade in it): rockMarks carries its look figures instead of rock marks
// (vein scale, vein cover at grade 0 and at grade 1, glint). crystal is 1 on an ore crystal's facets, whose normals
// have a length of 1.25 (OreChunkModels).
layout(location = 8) flat out vec4 oreFigures;
layout(location = 9) flat out float crystal;
// A light that brightens with thrust (a cold gas array's bands): how much of its full colour it gains now, 0 while its
// nozzle is idle. Such a facet's normal is 5.5 or over long (PropulsionModels.Lit) and points the way the nozzle does;
// on a hull, rockMarks carries which way its cold gas nozzles fire, in the hull's frame, and the gain at full thrust.
layout(location = 10) flat out float thrustLit;

void main()
{
    int slot = int((tint.a + 0.5) * 0.25);
    bool ore = tint.a > 0.02 && tint.a < 0.125;
    bool hull = tint.a - 4.0 * float(slot) > 0.5;
    markMask = ore || hull ? uvec4(0u) : uvec4(rockMarks);
    oreFigures = ore ? rockMarks : vec4(0.0);
    vec4 p = vec4(position, 1.0);
    worldPosition = vec3(dot(row0, p), dot(row1, p), dot(row2, p));
    // Blocks are scaled per axis and hulls drawn out along their length, so a normal is turned by the cofactor
    // matrix (the inverse transpose times the determinant): the model's columns crossed in pairs. The fragment
    // shader normalises it, so the determinant's size does not matter, and every scale here is positive.
    vec3 cx = vec3(row0.x, row1.x, row2.x);
    vec3 cy = vec3(row0.y, row1.y, row2.y);
    vec3 cz = vec3(row0.z, row1.z, row2.z);
    worldNormal = cross(cy, cz) * normal.x + cross(cz, cx) * normal.y + cross(cx, cy) * normal.z;
    localPosition = position;
    float tag = length(normal);
    metal = tag > 1.5 && tag < 3.25 ? tag - 2.0 : (tag >= 4.5 ? -2.0 : -1.0);
    paint = vec3(-1.0);
    thrustLit = 0.0;
    if (tag >= 5.5)
    {
        float code = floor((tag - 5.5) * 32768.0);
        paint = vec3(floor(code / 1024.0), mod(floor(code / 32.0), 32.0), mod(code, 32.0)) / 31.0;
        thrustLit = hull ? rockMarks.w * max(dot(rockMarks.xyz, normal / tag), 0.0) : 0.0;
    }
    else if (tag >= 4.5)
    {
        float code = floor((tag - 4.5) * 32768.0);
        paint = vec3(floor(code / 1024.0), mod(floor(code / 32.0), 32.0), mod(code, 32.0)) / 31.0;
    }
    else if (tag >= 3.25)
    {
        float code = floor((tag - 3.5) * 32768.0);
        paint = vec3(floor(code / 1024.0), mod(floor(code / 32.0), 32.0), mod(code, 32.0)) / 31.0;
    }
    crystal = ore && tag > 1.1 && tag < 1.5 ? 1.0 : 0.0;
    cut = slot - 1;
    color = vec4(tint.rgb, tint.a - 4.0 * float(slot));
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
