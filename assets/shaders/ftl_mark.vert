#version 450
#include "frame.glsl"

// Per jump in view: where it is and how big; drive kind, phase, progress through the phase, and a seed; the hull's
// nose in world space.
layout(location = 0) in vec4 positionSize;
layout(location = 1) in vec4 params;
layout(location = 2) in vec4 axis;

layout(location = 0) out vec2 corner;
layout(location = 1) out vec4 jump;
layout(location = 2) out vec3 along;

const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

// A quad that always faces the camera, at the place a hull is leaving from or arriving at.
void main()
{
    corner = corners[gl_VertexIndex];
    vec3 world = positionSize.xyz + (frame.cameraRight.xyz * corner.x + frame.cameraUp.xyz * corner.y) * positionSize.w;
    gl_Position = frame.viewProjection * vec4(world, 1.0);
    jump = params;
    // The jump's axis as it lies across the view, in the quad's corners, and how much of it does: a jump seen
    // from the side shows its full length, one seen head-on only its point.
    vec2 across = vec2(dot(axis.xyz, frame.cameraRight.xyz), dot(axis.xyz, frame.cameraUp.xyz));
    float length2 = dot(across, across);
    along = length2 > 1e-6 ? vec3(across * inversesqrt(length2), sqrt(length2)) : vec3(1.0, 0.0, 0.0);
}
