#version 450
#include "frame.glsl"

layout(location = 0) out vec3 planePosition;

const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

// A large quad on the command plane, centred under the camera.
void main()
{
    const float extent = 120000.0;
    vec2 corner = corners[gl_VertexIndex] * extent;
    planePosition = vec3(corner.x, frame.gridPlane.x, corner.y);
    gl_Position = frame.viewProjection * vec4(planePosition, 1.0);
}
