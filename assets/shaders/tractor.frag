#version 450
#include "frame.glsl"

// TractorLookConstants: the figures of the tractor entry in propulsion.json (see tractor.vert).
layout(push_constant) uniform TractorLook
{
    vec4 strand;
    vec4 haze;
    vec4 motion;
    vec4 flow;
    vec4 spread;
} look;

layout(location = 0) in vec3 worldPosition;
layout(location = 1) flat in vec3 faceNormal;
layout(location = 2) flat in vec4 beamFrom;
layout(location = 3) flat in vec4 beamTo;
layout(location = 4) flat in vec4 beamQuat;
layout(location = 5) flat in vec4 beamHalf;
layout(location = 6) flat in float inside;
layout(location = 0) out vec4 outColor;

// Each strand is a polyline of this many pieces from the lens to the container.
const int Segments = 24;
const float Tau = 6.2831853;

vec3 rotateInverse(vec4 q, vec3 v)
{
    vec3 u = -q.xyz;
    float s = q.w;
    return 2.0 * dot(u, v) * u + (s * s - dot(u, u)) * v + 2.0 * s * cross(u, v);
}

// Closest distance from the camera ray (from the camera along dir) to the segment [a, b]; the ray's distance there
// and the share of the segment.
float rayToSegment(vec3 dir, vec3 a, vec3 b, out float along, out float share)
{
    vec3 u = b - a;
    float uu = max(dot(u, u), 1e-8);
    float ud = dot(u, dir);
    float denom = max(uu - ud * ud, 1e-6 * uu);
    share = clamp((ud * dot(dir, a) - dot(u, a)) / denom, 0.0, 1.0);
    vec3 q = a + u * share;
    along = max(dot(q, dir), 0.0);
    return length(q - dir * along);
}

bool rayBox(vec3 origin, vec3 dir, vec3 halfExtents, out float near)
{
    vec3 inv = 1.0 / (dir + sign(dir + 1e-7) * 1e-7);
    vec3 t0 = (-halfExtents - origin) * inv;
    vec3 t1 = (halfExtents - origin) * inv;
    vec3 lo = min(t0, t1);
    vec3 hi = max(t0, t1);
    near = max(max(lo.x, lo.y), lo.z);
    float far = min(min(hi.x, hi.y), hi.z);
    return near <= far && far > 0.0;
}

void main()
{
    vec3 dir = normalize(worldPosition);
    float facing = dot(faceNormal, dir);
    if (inside > 0.5 ? facing < 0.0 : facing > 0.0)
        discard;

    vec3 from = beamFrom.xyz;
    vec3 to = beamTo.xyz;
    float len = beamTo.w;
    vec3 axis = (to - from) / len;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 up = cross(axis, side);

    // The held container hides what is behind it.
    vec3 boxOrigin = rotateInverse(beamQuat, -to);
    vec3 boxDir = rotateInverse(beamQuat, dir);
    float hitNear;
    bool onContainer = rayBox(boxOrigin, boxDir, beamHalf.xyz, hitNear);
    float hidden = onContainer ? max(hitNear, 0.0) : 1e9;

    float time = frame.dust.w;
    float seed = beamFrom.w;
    float radius = max(beamHalf.w, 0.1);
    // Metres a pixel covers one metre from the camera.
    float pixel = frame.lens.x * 2.0 / max(frame.lens.w, 1.0);

    int count = clamp(int(look.motion.x + 0.5), 1, 8);
    float startRadius = look.spread.x;
    float endRadius = max(radius * look.spread.y, startRadius);
    // The strands end a little inside the container's near side, which hides their tips.
    float reach = max(len - radius * 0.3, len * 0.5);
    float wavers = clamp(len * look.motion.z, 0.6, 8.0);
    float spacing = max(look.spread.w, 0.05);
    vec3 light = vec3(0.0);

    // 1. Thin strands that fan out from the lens to the container, twist slowly about the axis and waver, with light
    // flowing along them towards the ship.
    vec3 landing[8];
    for (int i = 0; i < count; i++)
    {
        float fi = float(i);
        float phase = fract(seed * 7.13 + fi * 0.618);
        float base = Tau * (fi + 0.35 * phase) / float(count) + seed * Tau + 0.5 * sin(time * 0.31 + fi * 1.7 + seed * 9.0);
        float twist = (1.4 + 0.6 * phase) * (i % 2 == 0 ? 1.0 : -1.0);
        float best = 0.0;
        float bestCore = 0.0;
        vec3 previous = from;
        float previousU = 0.0;
        for (int s = 1; s <= Segments; s++)
        {
            float u = float(s) / float(Segments);
            float spread = startRadius + (endRadius - startRadius) * pow(u, 1.35);
            float angle = base + twist * u;
            float ring = 0.62 + 0.3 * sin(Tau * (u * 0.7 + phase) + time * 0.4);
            float envelope = sin(u * 3.14159265);
            float w1 = sin(Tau * (u * wavers + phase) - time * look.motion.w + fi * 2.1);
            float w2 = cos(Tau * (u * wavers * 1.37 + phase * 0.5) + time * look.motion.w * 0.83 + fi * 1.3);
            vec3 radial = (side * cos(angle) + up * sin(angle)) * (spread * ring);
            vec3 waver = (side * w1 + up * w2) * (spread * look.motion.y * envelope);
            vec3 point = from + axis * (reach * u) + radial + waver;
            if (s == Segments)
                landing[i] = point;

            float along, share;
            float d = rayToSegment(dir, previous, point, along, share);
            if (along < hidden)
            {
                float at = mix(previousU, u, share);
                // Narrower than a pixel, a strand keeps its light and spreads it, so a far beam fades instead of
                // turning into a solid line.
                float width = max(look.strand.w, along * pixel * 0.7);
                float keep = clamp(look.strand.w / width, 0.18, 1.0);
                float core = exp(-(d * d) / (width * width) * 2.0);
                float halo = exp(-(d * d) / (width * width * 36.0) * 2.0) * 0.14;
                float metres = at * reach;
                float pulse = pow(0.5 + 0.5 * cos(Tau * ((metres + time * look.flow.x) / spacing + phase * 3.0)), 6.0);
                float taper = smoothstep(0.0, 0.05, at) * (1.0 - 0.45 * smoothstep(0.75, 1.0, at));
                float value = (core + halo) * keep * taper * (0.22 + 1.1 * pulse);
                if (value > best)
                {
                    best = value;
                    bestCore = core * keep * taper * pulse;
                }
            }
            previous = point;
            previousU = u;
        }
        light += look.strand.rgb * (best * look.flow.y) + vec3(bestCore * look.flow.y * 0.2);
    }

    // 2. A faint haze about the beam, widening with it.
    float alongAxis, uAxis;
    float dAxis = rayToSegment(dir, from, from + axis * reach, alongAxis, uAxis);
    if (alongAxis < hidden + radius * 0.3)
    {
        float hazeRadius = startRadius + (endRadius - startRadius) * pow(uAxis, 1.35) + look.haze.w * (0.3 + 0.7 * uAxis);
        float profile = exp(-(dAxis * dAxis) / (hazeRadius * hazeRadius) * 2.0);
        float ends = smoothstep(0.0, 0.08, uAxis) * smoothstep(1.0, 0.85, uAxis);
        float drift = 0.85 + 0.15 * sin(uAxis * len * 1.3 + time * look.flow.x * 0.7);
        light += look.haze.rgb * (profile * ends * drift * look.flow.z);
    }

    // 3. A soft round aura about the held container: the half behind it is hidden by it, so it reads darker there.
    if (look.flow.w > 0.0)
    {
        float alongCentre = max(dot(to, dir), 0.0);
        float dCentre = length(to - dir * alongCentre);
        float reachAura = max(radius * look.spread.z, 0.2);
        float shimmer = 0.85 + 0.15 * sin(time * 2.3 + seed * 20.0);
        // Brightest at the centre, so it shows as a soft halo round the container's outline, never as a disc.
        float aura = exp(-(dCentre * dCentre) / (reachAura * reachAura) * 3.0) * shimmer;
        if (onContainer)
        {
            aura *= 0.3;
            // Where the strands touch the container, its surface glows.
            vec3 hit = boxOrigin + boxDir * max(hitNear, 0.0);
            float spot = max(radius * 0.18, 0.06);
            for (int i = 0; i < count; i++)
            {
                vec3 end = rotateInverse(beamQuat, landing[i] - to);
                vec3 gap = hit - end;
                aura += exp(-dot(gap, gap) / (spot * spot) * 2.0) * 0.35;
            }
        }
        light += mix(look.haze.rgb, look.strand.rgb, 0.5) * (aura * look.flow.w);
    }

    // 4. The lens's glow where the strands leave it.
    float alongLens = max(dot(from, dir), 0.0);
    if (alongLens < hidden)
    {
        float dLens = length(from - dir * alongLens);
        float size = max(startRadius * 1.4, 0.02);
        float spreadSize = max(size, alongLens * pixel * 1.5);
        float keep = clamp((size * size) / (spreadSize * spreadSize), 0.15, 1.0);
        float glow = exp(-(dLens * dLens) / (spreadSize * spreadSize) * 2.0) * keep;
        light += (look.strand.rgb * 0.6 + vec3(0.12)) * (glow * look.flow.y);
    }

    if (dot(light, light) < 1e-7)
        discard;
    outColor = vec4(light, 1.0);
}
