#version 450
#include "frame.glsl"
layout(location=0) in vec4 positionSize;
layout(location=1) in vec4 right;
layout(location=2) in vec4 up;
layout(location=3) in vec4 color;
layout(location=4) in vec4 shape;
layout(location=0) out vec2 uv;
layout(location=1) out vec4 tint;
layout(location=2) flat out vec2 kind;
const vec2 corners[6] = vec2[](vec2(-1,-1),vec2(1,-1),vec2(1,1),vec2(-1,-1),vec2(1,1),vec2(-1,1));
void main() {
    uv = corners[gl_VertexIndex];
    vec3 point = positionSize.xyz + positionSize.w * (right.xyz * uv.x + up.xyz * uv.y);
    gl_Position = frame.viewProjection * vec4(point,1);
    tint = color;
    kind = shape.xy;
}
