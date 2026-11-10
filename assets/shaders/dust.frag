#version 450

layout(location = 0) in float alpha;
layout(location = 1) in float across;
layout(location = 0) out vec4 outColor;

void main()
{
    float profile = 1.0 - across * across;
    outColor = vec4(vec3(0.75, 0.82, 1.0) * alpha * profile, 1.0);
}
