#version 450
#include "frame.glsl"
#include "ftl.glsl"

// Per star: direction on the sky and apparent size in pixels; colour and brightness.
layout(location = 0) in vec4 directionSize;
layout(location = 1) in vec4 colorIn;

layout(location = 0) out vec2 corner;
layout(location = 1) out vec3 color;
layout(location = 2) flat out float stretch;

const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main()
{
    // w = 0 places the star at infinity: it turns with the camera and never moves with it.
    vec4 clip = frame.viewProjection * vec4(directionSize.xyz, 0.0);
    corner = corners[gl_VertexIndex];
    color = colorIn.rgb;
    float size = directionSize.w;

    // Work in pixels from the middle of the view. A hyperspace jump draws each star as a line running outward
    // from where it is; with no jump the line has no length and the quad is the plain star sprite.
    vec2 halfScreen = frame.lens.zw * 0.5;
    float w = abs(clip.w) > 1e-6 ? clip.w : 1e-6;
    vec2 at = clip.xy / w * halfScreen;
    vec2 along = vec2(1.0, 0.0);
    float halfLength = 0.0;
    float reach = ftlStretch(frame.ftl);
    if (reach > 0.0 && clip.w > 0.0)
    {
        float distance = length(at);
        along = distance > 0.5 ? at / distance : along;
        halfLength = distance * reach * 0.5;
    }

    vec2 across = vec2(-along.y, along.x);
    vec2 pixel = at + along * (halfLength + corner.x * (halfLength + size)) + across * corner.y * size;
    clip.xy = pixel / halfScreen * w;
    gl_Position = clip;
    stretch = (halfLength + size) / size;
}
