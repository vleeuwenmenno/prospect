#version 450

layout(location = 0) in vec2 corner;
layout(location = 1) in vec3 color;
layout(location = 0) out vec4 outColor;

void main()
{
    float falloff = exp(-dot(corner, corner) * 5.0);
    outColor = vec4(color * falloff, 1.0);
}
