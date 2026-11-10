#version 450
#include "frame.glsl"

// Per star: direction on the sky and apparent size in pixels; colour and brightness.
layout(location = 0) in vec4 directionSize;
layout(location = 1) in vec4 colorIn;

layout(location = 0) out vec2 corner;
layout(location = 1) out vec3 color;

const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main()
{
    // w = 0 places the star at infinity: it turns with the camera and never moves with it.
    vec4 clip = frame.viewProjection * vec4(directionSize.xyz, 0.0);
    corner = corners[gl_VertexIndex];
    clip.xy += corner * directionSize.w / frame.lens.zw * 2.0 * clip.w;
    gl_Position = clip;
    color = colorIn.rgb;
}
