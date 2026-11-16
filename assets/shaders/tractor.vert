#version 450
#include "frame.glsl"

// Per tractor beam (TractorInstance): camera-relative emitter position and seed; camera-relative container centre and
// beam length; container orientation quaternion; container half-extents and bounding radius.
layout(location = 0) in vec4 fromSeed;
layout(location = 1) in vec4 toLength;
layout(location = 2) in vec4 containerQuat;
layout(location = 3) in vec4 containerHalf;

layout(push_constant) uniform TractorLook
{
    vec4 strand; // rgb: linear strand colour, w: strand width (m)
    vec4 haze;   // rgb: linear haze colour, w: haze width (m)
    vec4 motion; // x: strand count, y: wave amplitude (m), z: wave frequency, w: wave speed
    vec4 flow;   // x: flow speed, y: strand opacity, z: haze opacity, w: container glow
    vec4 spread; // x: emitter spread (m), y: target spread (container radius factor), z: container glow scale, w: unused
} look;

layout(location = 0) out vec3 worldPosition;
layout(location = 1) flat out vec3 faceNormal;
layout(location = 2) flat out vec4 beamFrom;
layout(location = 3) flat out vec4 beamTo;
layout(location = 4) flat out vec4 beamQuat;
layout(location = 5) flat out vec4 beamHalf;
layout(location = 6) flat out vec4 beamBox; // x: halfW, y: back, z: front, w: inside

const int corners[36] = int[](
    0, 2, 6, 0, 6, 4,   // -x
    1, 5, 7, 1, 7, 3,   // +x
    0, 4, 5, 0, 5, 1,   // -y
    2, 3, 7, 2, 7, 6,   // +y
    0, 1, 3, 0, 3, 2,   // -z
    4, 6, 7, 4, 7, 5);  // +z
const vec3 normals[6] = vec3[](vec3(-1, 0, 0), vec3(1, 0, 0), vec3(0, -1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1));

void main()
{
    vec3 delta = toLength.xyz - fromSeed.xyz;
    float len = max(toLength.w, 1e-4);
    vec3 axis = delta / len;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 up = cross(axis, side);

    float cRad = max(containerHalf.w, 0.1);
    float auraExtent = cRad * look.spread.z + 0.35;
    float halfW = max(max(look.haze.w * 2.2, auraExtent), max(look.spread.x, cRad * look.spread.y) + look.motion.y) + 0.25;
    float back = 0.28;
    float front = len + auraExtent;

    int corner = corners[gl_VertexIndex];
    vec3 local = vec3((corner & 1) != 0 ? halfW : -halfW, (corner & 2) != 0 ? halfW : -halfW, (corner & 4) != 0 ? front : -back);
    worldPosition = fromSeed.xyz + side * local.x + up * local.y + axis * local.z;
    vec3 n = normals[gl_VertexIndex / 6];
    faceNormal = side * n.x + up * n.y + axis * n.z;

    vec3 eye = -fromSeed.xyz;
    vec3 inBox = vec3(dot(eye, side), dot(eye, up), dot(eye, axis));
    float inside = all(lessThan(abs(inBox.xy), vec2(halfW))) && inBox.z > -back && inBox.z < front ? 1.0 : 0.0;

    beamFrom = fromSeed;
    beamTo = vec4(toLength.xyz, len);
    beamQuat = containerQuat;
    beamHalf = containerHalf;
    beamBox = vec4(halfW, back, front, inside);
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
