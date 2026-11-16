#version 450
#include "frame.glsl"

// Per tractor beam (TractorInstance): camera-relative lens position and seed; camera-relative container centre and
// beam length; container orientation quaternion; container half-extents and bounding radius.
layout(location = 0) in vec4 fromSeed;
layout(location = 1) in vec4 toLength;
layout(location = 2) in vec4 containerQuat;
layout(location = 3) in vec4 containerHalf;

// TractorLookConstants: the figures of the tractor entry in propulsion.json.
layout(push_constant) uniform TractorLook
{
    vec4 strand; // rgb: linear strand colour, w: strand width (m)
    vec4 haze;   // rgb: linear haze colour, w: haze width (m)
    vec4 motion; // x: strand count, y: waver (share of the beam's radius), z: wavers per metre, w: waver speed
    vec4 flow;   // x: flow speed (m/s), y: strand opacity, z: haze opacity, w: container glow
    vec4 spread; // x: radius at the lens (m), y: radius at the container (share of its radius), z: aura radius (share), w: pulse spacing (m)
} look;

layout(location = 0) out vec3 worldPosition;
layout(location = 1) flat out vec3 faceNormal;
layout(location = 2) flat out vec4 beamFrom;
layout(location = 3) flat out vec4 beamTo;
layout(location = 4) flat out vec4 beamQuat;
layout(location = 5) flat out vec4 beamHalf;
layout(location = 6) flat out float inside;

// The box's six faces, two triangles each, as corners of the unit cube (bit 0: x, bit 1: y, bit 2: z).
const int corners[36] = int[](
    0, 2, 6, 0, 6, 4,   // -x
    1, 5, 7, 1, 7, 3,   // +x
    0, 4, 5, 0, 5, 1,   // -y
    2, 3, 7, 2, 7, 6,   // +y
    0, 1, 3, 0, 3, 2,   // -z
    4, 6, 7, 4, 7, 5);  // +z
const vec3 normals[6] = vec3[](vec3(-1, 0, 0), vec3(1, 0, 0), vec3(0, -1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1));

// A box along the beam from just behind the lens to past the container: the fragment shader works the strands, the
// haze and the aura out along each pixel's ray, so the box only says which pixels the beam may cover.
void main()
{
    vec3 delta = toLength.xyz - fromSeed.xyz;
    float len = max(toLength.w, 1e-4);
    vec3 axis = delta / len;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 up = cross(axis, side);

    float radius = max(containerHalf.w, 0.1);
    float beam = max(look.spread.x, radius * look.spread.y) * (1.0 + look.motion.y) + look.haze.w;
    float aura = max(radius * look.spread.z, 0.2) * 1.4;
    float halfWidth = max(beam, aura) + 0.1;
    float back = max(look.spread.x * 6.0, 0.2);
    float front = len + aura + 0.1;

    int corner = corners[gl_VertexIndex];
    vec3 local = vec3((corner & 1) != 0 ? halfWidth : -halfWidth, (corner & 2) != 0 ? halfWidth : -halfWidth, (corner & 4) != 0 ? front : -back);
    worldPosition = fromSeed.xyz + side * local.x + up * local.y + axis * local.z;
    vec3 n = normals[gl_VertexIndex / 6];
    faceNormal = side * n.x + up * n.y + axis * n.z;

    // With the camera in the box its far faces are drawn instead of its near ones.
    vec3 eye = -fromSeed.xyz;
    vec3 inBox = vec3(dot(eye, side), dot(eye, up), dot(eye, axis));
    inside = all(lessThan(abs(inBox.xy), vec2(halfWidth))) && inBox.z > -back && inBox.z < front ? 1.0 : 0.0;

    beamFrom = fromSeed;
    beamTo = vec4(toLength.xyz, len);
    beamQuat = containerQuat;
    beamHalf = containerHalf;
    gl_Position = frame.viewProjection * vec4(worldPosition, 1.0);
}
