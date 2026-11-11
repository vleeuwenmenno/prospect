#version 450

layout(location = 0) in vec2 corner;
layout(location = 1) in vec3 color;
layout(location = 2) flat in float stretch;
layout(location = 0) out vec4 outColor;

void main()
{
    // Distance from the star's core in sprite sizes. A stretched star is a line: flat along its length, with the
    // sprite's round falloff at its ends and across it.
    float along = max(abs(corner.x) * stretch - (stretch - 1.0), 0.0);
    float falloff = exp(-(along * along + corner.y * corner.y) * 5.0);
    outColor = vec4(color * falloff, 0.0);
}
