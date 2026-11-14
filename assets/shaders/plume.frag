#version 450
#include "frame.glsl"

// A thrust plume, marched through the box plume.vert draws around it. A flame is light added to the scene: a hot
// core, a body that cools toward its edge and end, bright shock diamonds along the core, and sparks while it ignites.
// A puff is cold gas: pale, faint and short, light only in that it is lit. Every moving part is driven by the plume's
// own clock and seed (PlumeInstance), smooth in time, so nothing shimmers from frame to frame.
layout(location = 0) in vec3 worldPosition;
layout(location = 1) flat in vec3 faceNormal;
layout(location = 2) flat in vec4 plumeExit;    // camera-relative exit centre, exit radius
layout(location = 3) flat in vec4 plumeAxis;    // axis, plume length in metres
layout(location = 4) flat in vec4 plumeCore;    // core colour, brightness
layout(location = 5) flat in vec4 plumeEdge;    // edge colour, kind (0 flame, 1 puff)
layout(location = 6) flat in vec4 plumeSpark;   // spark colour, seconds since ignition
layout(location = 7) flat in vec4 plumeShape;   // spread, shock diamonds, their spacing in metres, seed
layout(location = 8) flat in vec4 plumeMotion;  // seconds, flicker, spark seconds, spark speed in m/s
layout(location = 9) flat in vec4 plumeExtent;  // depth inside the nozzle, turbulence, sparks, box half width
layout(location = 10) flat in vec2 box;         // box length past the exit, camera inside the box
layout(location = 0) out vec4 outColor;

const int Steps = 16;

float hash1(float n)
{
    return fract(sin(n * 12.9898) * 43758.5453);
}

float hash3(vec3 p)
{
    p = fract(p * 0.3183099 + 0.1);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float noise1(float x)
{
    float i = floor(x);
    float f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(hash1(i), hash1(i + 1.0), f);
}

float noise3(vec3 x)
{
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(mix(hash3(i), hash3(i + vec3(1, 0, 0)), f.x), mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
        mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x), mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y),
        f.z);
}

// The distance from the camera ray (through the origin along dir) to the segment from a to b.
float rayToSegment(vec3 dir, vec3 a, vec3 b)
{
    vec3 u = b - a;
    float uu = dot(u, u);
    float ud = dot(u, dir);
    float s = clamp((ud * dot(dir, a) - dot(u, a)) / max(uu - ud * ud, 1e-6), 0.0, 1.0);
    vec3 q = a + u * s;
    float t = max(dot(q, dir), 0.0);
    return length(q - dir * t);
}

void main()
{
    vec3 dir = normalize(worldPosition);
    // From outside, only the faces the ray enters by; from inside, the faces it leaves by. Each pixel marches once.
    float facing = dot(faceNormal, dir);
    if (box.y > 0.5 ? facing < 0.0 : facing > 0.0)
        discard;

    vec3 axis = plumeAxis.xyz;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 up = cross(axis, side);
    vec3 eye = -plumeExit.xyz;
    vec3 ro = vec3(dot(eye, side), dot(eye, up), dot(eye, axis));
    vec3 rd = vec3(dot(dir, side), dot(dir, up), dot(dir, axis));
    float width = plumeExtent.w;
    float back = plumeExtent.x;
    vec3 lo = vec3(-width, -width, -back);
    vec3 hi = vec3(width, width, box.x);
    vec3 inv = 1.0 / (abs(rd) + 1e-6) * sign(rd + 1e-12);
    vec3 ta = (lo - ro) * inv;
    vec3 tb = (hi - ro) * inv;
    vec3 tn = min(ta, tb);
    vec3 tf = max(ta, tb);
    float t0 = max(max(max(tn.x, tn.y), tn.z), 0.0);
    float t1 = min(min(tf.x, tf.y), tf.z);
    if (t1 <= t0)
        discard;

    float radius = plumeExit.w;
    float len = plumeAxis.w;
    bool puff = plumeEdge.w > 0.5;
    float time = plumeMotion.x;
    float seed = plumeShape.w;
    float spread = plumeShape.x;
    float diamonds = plumeShape.y;
    float spacing = plumeShape.z;
    float turbulence = plumeExtent.y;
    vec3 core = plumeCore.rgb;
    vec3 edge = plumeEdge.rgb;
    // The nozzle's wall hides its inside from the side; looking into it, the throat glows.
    float into = rd.z * rd.z;

    vec3 light = vec3(0.0);
    if (len > 0.0)
    {
        float dt = (t1 - t0) / float(Steps);
        // Gas leaves the nozzle fast: the pattern runs out along the axis about a dozen exit radii a second.
        float flow = time * (puff ? 18.0 : 12.0);
        for (int i = 0; i < Steps; i++)
        {
            vec3 p = ro + rd * (t0 + (float(i) + 0.5) * dt);
            float r2 = dot(p.xy, p.xy);
            float z = p.z;
            if (z < 0.0)
            {
                // Inside the nozzle, narrowing toward the throat.
                if (puff)
                    continue;
                float inner = radius * (1.0 + 0.55 * z / max(back, 1e-4));
                float q2 = r2 / (inner * inner);
                light += mix(edge, core, 0.8) * (exp(-q2 * 2.5) * into * 1.4) * dt / radius;
                continue;
            }
            float u = z / len;
            if (u >= 1.0)
                continue;
            float wide = radius * (1.0 + spread * u);
            float q2 = r2 / (wide * wide);
            if (q2 > 6.0)
                continue;
            float n = noise3(vec3(p.xy / radius * 1.3, z / radius * 0.6 - flow) + seed * 7.13);
            float n2 = noise3(vec3(p.xy / radius * 2.9, z / radius * 1.4 - flow * 1.7) + seed * 3.71);
            float broken = mix(1.0, 0.25 + 1.5 * (0.65 * n + 0.35 * n2), turbulence);
            float fade = smoothstep(1.0, 0.3, u) * (1.0 - 0.6 * u);
            // Spreading gas thins: what one exit radius held is spread over the plume's wider section.
            float thin = 1.0 / (1.0 + spread * u * (puff ? 2.5 : 1.0));
            float body = exp(-q2 * 2.4) * fade * broken * thin;
            if (puff)
            {
                light += mix(edge, core, exp(-q2 * 3.0)) * body * (1.0 - u) * dt / radius;
                continue;
            }
            float hot = exp(-q2 * 12.0) * pow(1.0 - u, 3.0) * mix(1.0, broken, 0.5);
            // Shock diamonds: the core pinches and brightens in a row of nodes that fade down the plume.
            float node = pow(abs(cos(3.14159265 * z / spacing)), 10.0);
            float diamond = diamonds * node * exp(-q2 * 16.0 / (0.35 + 0.65 * node)) * smoothstep(0.6, 0.1, u);
            vec3 cool = mix(edge, core, exp(-q2 * 4.0) * (1.0 - u) * 0.6);
            light += (cool * body * 0.22 + core * (hot * 1.4 + diamond * 1.6)) * dt / radius;
        }
    }

    float brightness = plumeCore.w;
    // A slow flicker of the whole plume, smooth in time and its own for every nozzle.
    float flicker = 1.0 + plumeMotion.y * (noise1(time * 11.0 + seed * 31.0) * 2.0 - 1.0);
    vec3 color = puff ? light * brightness * flicker : (vec3(1.0) - exp(-light)) * brightness * flicker;

    // Ignition: sparks thrown from the throat in a cone, gone after the spark seconds.
    float age = plumeSpark.w;
    float sparkSeconds = plumeMotion.z;
    int sparks = int(plumeExtent.z + 0.5);
    if (sparks > 0 && age < sparkSeconds)
    {
        float glow = 0.0;
        float w = radius * 0.02 + 0.003;
        vec3 throat = plumeExit.xyz - axis * (back * 0.6);
        for (int k = 0; k < 16; k++)
        {
            if (k >= sparks)
                break;
            float h = seed * 13.17 + float(k) * 7.31;
            float around = hash1(h) * 6.2831853;
            float cone = hash1(h + 1.7) * 0.7;
            float speed = plumeMotion.w * (0.45 + 0.55 * hash1(h + 3.1));
            float start = hash1(h + 4.9) * sparkSeconds * 0.35;
            float flown = age - start;
            if (flown <= 0.0)
                continue;
            vec3 d = axis * cos(cone) + (side * cos(around) + up * sin(around)) * sin(cone);
            vec3 head = throat + d * (speed * flown);
            vec3 tail = head - d * min(speed * flown, radius * 0.9);
            float dist = rayToSegment(dir, tail, head);
            glow += exp(-(dist * dist) / (w * w)) * (1.0 - flown / (sparkSeconds - start));
        }
        color += plumeSpark.rgb * glow * 6.0;
    }

    // Light added to the scene leaves its alpha, the lens mask, alone.
    outColor = vec4(color, 0.0);
}
