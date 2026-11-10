#version 450
#include "frame.glsl"

layout(location = 0) in vec3 worldNormal;
layout(location = 1) in vec3 worldPosition;
layout(location = 2) in vec3 localPosition;
layout(location = 3) in vec4 color;
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

void main()
{
    vec3 n = normalize(worldNormal);
    vec3 view = normalize(-worldPosition);
    vec3 albedo = color.rgb;

    if (color.a < 0.5)
    {
        // Rock: mottled, with the facets of the mesh showing through for a rough, broken surface.
        vec3 facet = normalize(cross(dFdx(worldPosition), dFdy(worldPosition)));
        n = normalize(mix(n, facet, 0.55));
        float mottling = noise(localPosition * 9.0) * 0.6 + noise(localPosition * 31.0) * 0.4;
        albedo *= 0.55 + 0.75 * mottling;
    }
    else
    {
        // Hull plating: panel lines every block.
        vec3 cell = abs(fract(localPosition + 0.5) - 0.5);
        float seam = smoothstep(0.47, 0.5, max(max(cell.x * (1.0 - abs(n.x)), cell.y * (1.0 - abs(n.y))), cell.z * (1.0 - abs(n.z))));
        albedo *= 1.0 - 0.45 * seam;
    }

    vec3 sun = frame.sunDirection.xyz;
    float diffuse = max(dot(n, sun), 0.0);
    float fill = 0.5 + 0.5 * dot(n, -sun);
    vec3 halfway = normalize(sun + view);
    float specular = pow(max(dot(n, halfway), 0.0), 48.0) * (color.a < 0.5 ? 0.03 : 0.35);
    float rim = pow(1.0 - max(dot(n, view), 0.0), 3.0);

    vec3 lit = albedo * (frame.sunColor.rgb * diffuse + vec3(0.012, 0.016, 0.026) * fill)
        + frame.sunColor.rgb * specular * diffuse
        + vec3(0.02, 0.03, 0.05) * rim;
    outColor = vec4(lit, 1.0);
}
