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
        // The screen-space derivative gives the facet's normal up to its sign; the smooth normal settles the sign.
        vec3 facet = normalize(cross(dFdx(worldPosition), dFdy(worldPosition)));
        if (dot(facet, n) < 0.0)
            facet = -facet;
        n = normalize(mix(n, facet, 0.4));
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
    // Inside a hyperspace tunnel or a throat its light falls on the hull riding through it: blue, or a rift's orange.
    vec3 passage = frame.ftl.x > 2.5 ? vec3(0.16, 0.06, 0.015) : vec3(0.05, 0.08, 0.14);
    lit += albedo * passage * frame.ftlShape.w * (0.6 + 0.4 * fill);

    // In the command view, a bright line marks where the command plane cuts through the surface.
    if (frame.gridPlane.z > 0.01)
    {
        float toPlane = abs(worldPosition.y - frame.gridPlane.x);
        float width = max(fwidth(worldPosition.y) * 1.6, 0.03);
        float cut = 1.0 - smoothstep(width, width * 2.0, toPlane);
        float below = worldPosition.y < frame.gridPlane.x ? 0.55 : 1.0;
        lit = mix(lit * mix(1.0, below, frame.gridPlane.z), vec3(1.0, 0.69, 0.13) * 1.8, cut * frame.gridPlane.z);
    }

    // A solid surface clears the jump lens mask, so a hull in front of an opening stays sharp, and leaves a mark
    // of what it is in the fraction: 0.25 an asteroid, 0.375 a hull, 0.5 the pilot's own hull, which their jump
    // never covers.
    outColor = vec4(lit, color.a > 1.5 ? 0.5 : color.a > 0.5 ? 0.375 : 0.25);
}
