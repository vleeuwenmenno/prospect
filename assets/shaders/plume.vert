#version 450
#include "frame.glsl"

// Per plume (PlumeInstance): the nozzle's exit, camera-relative, and its radius; the axis the plume runs along and
// its length; core colour and brightness; edge colour and kind (0 flame, 1 puff); spark colour and seconds since
// ignition; spread, shock diamonds (a puff: its valve burst), their spacing in metres and a seed; time, flicker, spark seconds and spark speed;
// how far back into the nozzle it starts, turbulence, spark count and the half width of the box it is drawn in;
// seconds since the part stopped (negative while it fires), how fast the gas streams, the core's reach and the opening.
layout(location = 0) in vec4 exitRadius;
layout(location = 1) in vec4 axisLength;
layout(location = 2) in vec4 coreBright;
layout(location = 3) in vec4 edgeKind;
layout(location = 4) in vec4 sparkAge;
layout(location = 5) in vec4 shape;
layout(location = 6) in vec4 motion;
layout(location = 7) in vec4 extent;
layout(location = 8) in vec4 flow;

layout(location = 0) out vec3 worldPosition;
layout(location = 1) flat out vec3 faceNormal;
layout(location = 2) flat out vec4 plumeExit;
layout(location = 3) flat out vec4 plumeAxis;
layout(location = 4) flat out vec4 plumeCore;
layout(location = 5) flat out vec4 plumeEdge;
layout(location = 6) flat out vec4 plumeSpark;
layout(location = 7) flat out vec4 plumeShape;
layout(location = 8) flat out vec4 plumeMotion;
layout(location = 9) flat out vec4 plumeExtent;
layout(location = 10) flat out vec2 box;
layout(location = 11) flat out vec4 plumeFlow;

// The box's six faces, two triangles each, as corners of the unit cube (bit 0: x, bit 1: y, bit 2: z).
const int corners[36] = int[](
    0, 2, 6, 0, 6, 4,   // -x
    1, 5, 7, 1, 7, 3,   // +x
    0, 4, 5, 0, 5, 1,   // -y
    2, 3, 7, 2, 7, 6,   // +y
    0, 1, 3, 0, 3, 2,   // -z
    4, 6, 7, 4, 7, 5);  // +z
const vec3 normals[6] = vec3[](vec3(-1, 0, 0), vec3(1, 0, 0), vec3(0, -1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1));

// A box around the plume, along its axis from just inside the nozzle to past its end. The fragment shader marches
// the plume through it, so the box only says which pixels it may cover.
void main()
{
    vec3 axis = axisLength.xyz;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 up = cross(axis, side);
    int corner = corners[gl_VertexIndex];
    float width = extent.w;
    float back = extent.x;
    // Past the plume's end, and while an ignition throws sparks, as far as they fly.
    float front = axisLength.w * 1.1 + width * 0.1;
    if (extent.z > 0.0 && sparkAge.w < motion.z)
        front = max(front, motion.w * motion.z * 1.05);
    vec3 local = vec3((corner & 1) != 0 ? width : -width, (corner & 2) != 0 ? width : -width, (corner & 4) != 0 ? front : -back);
    worldPosition = exitRadius.xyz + side * local.x + up * local.y + axis * local.z;
    vec3 n = normals[gl_VertexIndex / 6];
    faceNormal = side * n.x + up * n.y + axis * n.z;
    // The camera at the origin, in the box's frame.
    vec3 eye = -exitRadius.xyz;
    vec3 inBox = vec3(dot(eye, side), dot(eye, up), dot(eye, axis));
    float inside = all(lessThan(abs(inBox.xy), vec2(width))) && inBox.z > -back && inBox.z < front ? 1.0 : 0.0;
    plumeExit = exitRadius;
    plumeAxis = vec4(axis, axisLength.w);
    plumeCore = coreBright;
    plumeEdge = edgeKind;
    plumeSpark = sparkAge;
    plumeShape = shape;
    plumeMotion = motion;
    plumeExtent = extent;
    plumeFlow = flow;
    box = vec2(front, inside);
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
