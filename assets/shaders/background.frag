#version 450
#include "frame.glsl"

layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;

float hash(vec3 p)
{
    p = fract(p * 0.3183099 + 0.1);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float noise(vec3 x)
{
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(mix(hash(i), hash(i + vec3(1, 0, 0)), f.x), mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
        mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x), mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y),
        f.z);
}

float fbm(vec3 p, int octaves)
{
    float sum = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < octaves; i++)
    {
        sum += amplitude * noise(p);
        p = p * 2.03 + 11.7;
        amplitude *= 0.5;
    }
    return sum;
}

void main()
{
    vec3 dir = normalize(
        frame.cameraForward.xyz
        + frame.cameraRight.xyz * ndc.x * frame.lens.x * frame.lens.y
        - frame.cameraUp.xyz * ndc.y * frame.lens.x);

    // The galaxy's disc: a band of unresolved stars across the sky, broken up by dust lanes.
    const vec3 galacticPole = normalize(vec3(0.25, 1.0, 0.15));
    float latitude = dot(dir, galacticPole);
    float band = exp(-latitude * latitude * 18.0);
    vec3 color = vec3(0.0);

    // The cloud detail is only worth computing where the band is bright enough to show it.
    if (band > 0.004)
    {
        float clouds = fbm(dir * 3.2, 4);
        float lanes = smoothstep(0.42, 0.62, fbm(dir * 6.5 + 4.0, 4));
        color = vec3(0.55, 0.62, 0.9) * band * (0.012 + 0.03 * clouds) * (1.0 - 0.75 * lanes);
    }

    // Faint coloured gas away from the disc.
    float gas = pow(fbm(dir * 1.7 + 20.0, 4), 3.0);
    color += mix(vec3(0.5, 0.12, 0.2), vec3(0.1, 0.25, 0.5), noise(dir * 0.9 + 3.0)) * gas * 0.05;

    // The sector's star: a hard disc with a wide glow.
    float toSun = dot(dir, frame.sunDirection.xyz);
    float disc = smoothstep(0.99985, 0.99994, toSun);
    float glow = pow(max(toSun, 0.0), 1400.0) * 0.9 + pow(max(toSun, 0.0), 90.0) * 0.03;
    color += frame.sunColor.rgb * (disc * 40.0 + glow);

    // The scene's alpha is the jump lens mask (see SceneRenderer); the sky starts it at zero.
    outColor = vec4(color, 0.0);
}
