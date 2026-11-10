#version 450
#include "frame.glsl"

// Per jump in view: where it is and how big; drive kind, phase, progress through the phase, and a seed.
layout(location = 0) in vec4 positionSize;
layout(location = 1) in vec4 params;

layout(location = 0) out vec2 corner;
layout(location = 1) out vec4 jump;

const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

// A quad that always faces the camera, at the place a hull is leaving from or arriving at.
void main()
{
    corner = corners[gl_VertexIndex];
    vec3 world = positionSize.xyz + (frame.cameraRight.xyz * corner.x + frame.cameraUp.xyz * corner.y) * positionSize.w;
    gl_Position = frame.viewProjection * vec4(world, 1.0);
    jump = params;
}
