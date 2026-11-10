#version 450

layout(set = 0, binding = 0) uniform sampler2D scene;
layout(push_constant) uniform Push
{
    float exposure;
} push;

layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;

// ACES filmic curve, Narkowicz fit.
vec3 filmic(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec2 uv = ndc * 0.5 + 0.5;
    vec3 hdr = texture(scene, uv).rgb * push.exposure;
    vec3 mapped = filmic(hdr);

    float vignette = 1.0 - 0.22 * pow(length(ndc) * 0.75, 2.5);
    vec3 display = pow(mapped * vignette, vec3(1.0 / 2.2));

    // A little noise hides banding in the dark gradients space is made of.
    float dither = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outColor = vec4(display + dither / 255.0, 1.0);
}
