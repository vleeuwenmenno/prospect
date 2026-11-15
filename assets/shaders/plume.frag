#version 450
#include "frame.glsl"

// A thrust plume, marched through the box plume.vert draws around it.
//
// A flame is a vacuum plume: light of its own, wide, soft and faint. It opens almost at once from the nozzle's exit
// into a bell, its gas thinning as the square of its width, so a view straight into it stays bounded. The hot core
// shines only just past the exit, with at most one faint shock node. Its colour is a density-weighted mix of core and
// edge colours, and its brightness is compressed against a cap, so however much of it a ray crosses it never burns
// out to flat white.
//
// A puff is cold gas and has no light of its own: a thin, dense spike at the nozzle that opens into a straight cone of
// expanding gas, lit by the star, faint and translucent. Fine streaks along the cone's lines stream out at the gas's
// speed, so the jet reads fast. The gas that left when the valve opened has only got so far, and while the valve's
// burst lasts its front is a denser slug. Every moving part is driven by the plume's own clock and seed
// (PlumeInstance), smooth in time, so nothing shimmers from frame to frame. Once the part stops, the last gas flies
// off down the plume and clears.
//
// An ion glow is an ion part's exhaust: a faint blue-violet glow over the grid inside its ring, a soft column that
// barely widens and fades along its length, and a thin straight beam down its axis. It is steady: no noise, no
// flicker, no sparks, no shock node. The beam is worked out from the ray's closest approach to the axis rather than
// marched, and never thinner than about a pixel, so it does not break up into steps or crawl as the view moves.
layout(location = 0) in vec3 worldPosition;
layout(location = 1) flat in vec3 faceNormal;
layout(location = 2) flat in vec4 plumeExit;    // camera-relative exit centre, exit radius
layout(location = 3) flat in vec4 plumeAxis;    // axis, plume length in metres
layout(location = 4) flat in vec4 plumeCore;    // core colour, brightness cap
layout(location = 5) flat in vec4 plumeEdge;    // edge colour, kind (0 flame, 1 puff, 2 ion)
layout(location = 6) flat in vec4 plumeSpark;   // spark colour, seconds since ignition
layout(location = 7) flat in vec4 plumeShape;   // spread, first shock node (a puff: its valve burst), its distance in metres, seed
layout(location = 8) flat in vec4 plumeMotion;  // seconds, flicker, spark seconds, spark speed in m/s
layout(location = 9) flat in vec4 plumeExtent;  // depth inside the nozzle, turbulence, sparks, box half width
layout(location = 10) flat in vec2 box;         // box length past the exit, camera inside the box
layout(location = 11) flat in vec4 plumeFlow;   // seconds since it stopped (< 0 firing), flow m/s, core reach m, opening m
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
    bool puff = plumeEdge.w > 0.5 && plumeEdge.w < 1.5;
    bool ion = plumeEdge.w > 1.5;
    float time = plumeMotion.x;
    float seed = plumeShape.w;
    float spread = plumeShape.x;
    float node = plumeShape.y;
    float nodeAt = plumeShape.z;
    float turbulence = plumeExtent.y;
    float released = plumeFlow.x;
    float speed = plumeFlow.y;
    float coreReach = max(plumeFlow.z, 1e-4);
    float opening = max(plumeFlow.w, 1e-4);
    vec3 core = plumeCore.rgb;
    vec3 edge = plumeEdge.rgb;
    // Once the part stops, the gas behind the last of it is empty: the front of that emptiness runs at the gas's speed.
    float emptied = released >= 0.0 ? released * speed : -1e9;
    // The streaming pattern: gas leaves the nozzle at its speed, so the noise runs out along the axis with it.
    float flowed = time * speed;

    // A puff's valve burst: plumeShape.y carries it for a puff, which has no shock node.
    float age = plumeSpark.w;
    float burstNow = puff ? node : 0.0;

    float density = 0.0;
    vec3 tint = vec3(0.0);
    if (len > 0.0)
    {
        float dt = (t1 - t0) / float(Steps);
        for (int i = 0; i < Steps; i++)
        {
            vec3 p = ro + rd * (t0 + (float(i) + 0.5) * dt);
            float r2 = dot(p.xy, p.xy);
            float z = p.z;
            if (z < 0.0)
            {
                // Inside the ring an ion grid glows evenly over its face, seen past the ring's rim.
                if (ion)
                {
                    if (released >= 0.0)
                        continue;
                    float g = exp(-r2 / (radius * radius) * 1.4) * 0.5;
                    density += g * dt / radius;
                    tint += mix(edge, core, 0.6) * g * dt / radius;
                    continue;
                }
                // Inside the bell: a flame's throat glows, seen only looking into the nozzle; a puff has nothing there.
                if (puff || released >= 0.0)
                    continue;
                float inner = radius * (1.0 + 0.55 * z / max(back, 1e-4));
                float d = exp(-r2 / (inner * inner) * 2.5) * rd.z * rd.z * 0.9;
                density += d * dt / radius;
                tint += core * d * dt / radius;
                continue;
            }
            float u = z / len;
            if (u >= 1.0)
                continue;
            float cleared = smoothstep(emptied, emptied + radius, z);
            if (cleared <= 0.0)
                continue;
            if (ion)
            {
                // A soft column of ions that barely widens, brightest over the grid and fading along its length.
                float wideI = radius * (1.0 + spread * u);
                float q2i = r2 / (wideI * wideI);
                if (q2i > 4.0)
                    continue;
                float thinI = radius * radius / (wideI * wideI);
                float fadeI = (1.0 - u) * (1.0 - u) * cleared;
                float column = exp(-q2i * 1.8) * thinI * fadeI;
                float grid = exp(-z / coreReach) * exp(-q2i * 1.2) * cleared;
                float d = column * 0.45 + grid * 0.8;
                density += d * dt / radius;
                tint += mix(edge, core, clamp(grid * 1.5, 0.0, 1.0)) * d * dt / radius;
                continue;
            }
            if (puff)
            {
                // A cold gas jet: a straight cone from the exit, its gas thinning as the square of its width, so it
                // is densest in a thin spike at the nozzle and fades out along its length. Gas that left when the
                // valve opened has only got so far; its front is a denser slug while the valve's burst lasts.
                float wideP = radius * (1.0 + spread * u);
                float q2p = r2 / (wideP * wideP);
                if (q2p > 4.0)
                    continue;
                float thinP = radius * radius / (wideP * wideP);
                // The front is domed: gas on the axis runs ahead of the gas at the edge.
                float front = age * speed * (1.0 - 0.3 * min(q2p, 1.0));
                float soft = radius + 0.2 * front;
                float reached = 1.0 - smoothstep(front - soft, front + soft, z);
                if (reached <= 0.0)
                    continue;
                vec2 aroundP = p.xy / wideP;
                // Streaks along the cone's lines, carried out at the gas's speed: fine across, long along the flow,
                // so the jet reads fast rather than as soft billows. Two octaves, driven only by the plume's clock.
                float streamP = (z - flowed) / radius;
                float s1 = noise3(vec3(aroundP * 5.0, streamP * 0.11) + seed * 7.13);
                float s2 = noise3(vec3(aroundP * 11.0, streamP * 0.23) + seed * 3.71);
                float streak = smoothstep(0.25, 0.9, 0.6 * s1 + 0.4 * s2);
                float broken = mix(1.0, streak * 1.8, turbulence * smoothstep(0.0, 0.15, u));
                // The potential core: a short, narrowing spike of unexpanded gas right at the exit.
                float coreU = z / coreReach;
                float spike = radius * max(0.55 - 0.45 * coreU, 0.08);
                float jet = exp(-r2 / (spike * spike)) * (1.0 - smoothstep(0.4, 1.0, coreU));
                float slug = burstNow * exp(-pow((z - front) / soft, 2.0)) * exp(-q2p * 1.5);
                float shell = exp(-q2p * 2.6) * thinP * broken;
                float edgeFade = smoothstep(1.0, 0.35, u);
                float d = (shell * (1.0 + 0.5 * burstNow) + jet * 0.9 + slug * thinP * 0.9) * edgeFade * cleared * reached;
                density += d * dt / radius;
                tint += mix(edge, core, clamp(jet * 1.5 + thinP * 0.6, 0.0, 1.0)) * d * dt / radius;
                continue;
            }
            float wide = radius * (1.0 + spread * (1.0 - exp(-z / opening)));
            float q2 = r2 / (wide * wide);
            if (q2 > 5.0)
                continue;
            // Gas thins as its section grows: what one exit held is spread over the plume's whole width.
            float thin = radius * radius / (wide * wide);
            float fade = smoothstep(1.0, 0.25, u) * cleared;
            vec2 across = p.xy / wide;
            float stream = (z - flowed) / radius;
            // Faint streaks along the flow; a vacuum plume is smooth, so the body is barely broken.
            float n = noise3(vec3(across * 2.2, stream * 0.18) + seed * 7.13);
            float broken = mix(1.0, 0.55 + 0.9 * n, turbulence);
            float body = exp(-q2 * 2.2) * thin * broken * fade;
            float hot = exp(-r2 / (radius * radius) * 2.2) * exp(-z / coreReach) * cleared;
            float shock = node * exp(-pow((z - nodeAt) / (0.4 * radius), 2.0)) * exp(-r2 / (radius * radius) * 5.0) * cleared;
            float d = body * 0.55 + hot * 3.0 + shock;
            float warmth = clamp(exp(-q2 * 1.4) * exp(-z / (coreReach * 3.0)) + hot, 0.0, 1.0);
            density += d * dt / radius;
            tint += mix(edge, core, warmth) * d * dt / radius;
        }
    }

    float brightness = plumeCore.w;
    vec3 hue = tint / max(density, 1e-5);
    // A slow, slight flicker, smooth in time and its own for every nozzle.
    float flicker = 1.0 + plumeMotion.y * (noise1(time * 9.0 + seed * 31.0) * 2.0 - 1.0);
    // The density is compressed against the cap: thin gas adds a little light, any amount never more than the cap.
    vec3 color = hue * (1.0 - exp(-density)) * brightness * flicker;
    if (puff)
    {
        // Cold gas has no light of its own: it scatters the star's, far more toward the star (fine droplets scatter
        // forward), and barely any ambient, so a jet seen against the light glows and one seen with it is faint.
        vec3 sun = frame.sunDirection.xyz;
        float forward = pow(max(dot(dir, sun), 0.0), 4.0);
        color *= frame.sunColor.rgb * (0.3 + 1.1 * forward) + vec3(0.02, 0.022, 0.026);
    }

    if (ion && len > 0.0)
    {
        // The beam: the ray's closest approach to the axis, never thinner than about a pixel, its light spread
        // over that width so a far beam keeps what it has. It fades along its length and, once the part stops,
        // empties from the grid outward with the rest.
        vec3 a = plumeExit.xyz;
        vec3 u = axis * len;
        float uu = dot(u, u);
        float ud = dot(u, dir);
        float s = clamp((ud * dot(dir, a) - dot(u, a)) / max(uu - ud * ud, 1e-6), 0.0, 1.0);
        vec3 q = a + u * s;
        float t = max(dot(q, dir), 0.0);
        float dist = length(q - dir * t);
        float thin = radius * 0.07;
        float w = max(thin, t * frame.lens.x * 2.4 / max(frame.lens.w, 1.0));
        float along = (1.0 - s) * sqrt(1.0 - s);
        float beam = exp(-(dist * dist) / (w * w)) * (thin / w) * along * smoothstep(emptied, emptied + radius, s * len);
        color += mix(edge, core, 0.85) * beam * brightness * 0.9;
    }

    // Ignition: a few faint sparks thrown from the throat in a cone, gone after the spark seconds.
    float sparkSeconds = plumeMotion.z;
    int sparks = int(plumeExtent.z + 0.5);
    if (sparks > 0 && age < sparkSeconds)
    {
        float glow = 0.0;
        float w = radius * 0.015 + 0.002;
        vec3 throat = plumeExit.xyz - axis * (back * 0.6);
        for (int k = 0; k < 16; k++)
        {
            if (k >= sparks)
                break;
            float h = seed * 13.17 + float(k) * 7.31;
            float around = hash1(h) * 6.2831853;
            float cone = hash1(h + 1.7) * 0.6;
            float sparkSpeed = plumeMotion.w * (0.45 + 0.55 * hash1(h + 3.1));
            float start = hash1(h + 4.9) * sparkSeconds * 0.35;
            float flown = age - start;
            if (flown <= 0.0)
                continue;
            vec3 d = axis * cos(cone) + (side * cos(around) + up * sin(around)) * sin(cone);
            vec3 head = throat + d * (sparkSpeed * flown);
            vec3 tail = head - d * min(sparkSpeed * flown, radius * 0.6);
            float dist = rayToSegment(dir, tail, head);
            glow += exp(-(dist * dist) / (w * w)) * (1.0 - flown / (sparkSeconds - start));
        }
        color += plumeSpark.rgb * glow * 1.6;
    }

    // Light added to the scene leaves its alpha, the lens mask, alone.
    outColor = vec4(color, 0.0);
}
