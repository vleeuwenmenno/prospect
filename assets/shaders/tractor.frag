#version 450
#include "frame.glsl"

layout(push_constant) uniform TractorLook
{
    vec4 strand; // rgb: linear strand colour, w: strand width (m)
    vec4 haze;   // rgb: linear haze colour, w: haze width (m)
    vec4 motion; // x: strand count, y: wave amplitude (m), z: wave frequency, w: wave speed
    vec4 flow;   // x: flow speed, y: strand opacity, z: haze opacity, w: container glow
    vec4 spread; // x: emitter spread (m), y: target spread (container radius factor), z: container glow scale, w: unused
} look;

layout(location = 0) in vec3 worldPosition;
layout(location = 1) flat in vec3 faceNormal;
layout(location = 2) flat in vec4 beamFrom;
layout(location = 3) flat in vec4 beamTo;
layout(location = 4) flat in vec4 beamQuat;
layout(location = 5) flat in vec4 beamHalf;
layout(location = 6) flat in vec4 beamBox; // x: halfW, y: back, z: front, w: inside
layout(location = 0) out vec4 outColor;

const int Segments = 16;

vec3 rotateInv(vec4 q, vec3 v)
{
    vec3 u = -q.xyz;
    float s = q.w;
    return 2.0 * dot(u, v) * u + (s * s - dot(u, u)) * v + 2.0 * s * cross(u, v);
}

// Closest distance from the camera ray (origin along dir) to segment [a, b].
// Also returns ray parameter t, segment parameter s in [0, 1].
float rayToSegment(vec3 dir, vec3 a, vec3 b, out float tRay, out float sSeg)
{
    vec3 u = b - a;
    float uu = dot(u, u);
    float ud = dot(u, dir);
    float denom = max(uu - ud * ud, 1e-6);
    sSeg = clamp((ud * dot(dir, a) - dot(u, a)) / denom, 0.0, 1.0);
    vec3 q = a + u * sSeg;
    tRay = max(dot(q, dir), 0.0);
    return length(q - dir * tRay);
}

// Ray-AABB intersection in local box space. Returns true if hit with tNear <= tFar and tFar > 0.
bool intersectBox(vec3 ro, vec3 rd, vec3 halfExtents, out float tNear, out float tFar, out vec3 hitNormal)
{
    vec3 inv = 1.0 / (rd + sign(rd + 1e-7) * 1e-7);
    vec3 t0 = (-halfExtents - ro) * inv;
    vec3 t1 = (halfExtents - ro) * inv;
    vec3 tMin = min(t0, t1);
    vec3 tMax = max(t0, t1);
    tNear = max(max(tMin.x, tMin.y), tMin.z);
    tFar = min(min(tMax.x, tMax.y), tMax.z);
    hitNormal = -sign(rd) * step(tMin.yzx, tMin.xyz) * step(tMin.zxy, tMin.xyz);
    return tNear <= tFar && tFar > 0.0;
}

void main()
{
    vec3 dir = normalize(worldPosition);
    float facing = dot(faceNormal, dir);
    if (beamBox.w > 0.5 ? facing < 0.0 : facing > 0.0)
        discard;

    vec3 from = beamFrom.xyz;
    vec3 to = beamTo.xyz;
    float len = beamTo.w;
    vec3 axis = (to - from) / len;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0)));
    vec3 up = cross(axis, side);

    // Clip ray to the outer bounding box in beam-local space.
    vec3 eye = -from;
    vec3 ro = vec3(dot(eye, side), dot(eye, up), dot(eye, axis));
    vec3 rd = vec3(dot(dir, side), dot(dir, up), dot(dir, axis));
    vec3 lo = vec3(-beamBox.x, -beamBox.x, -beamBox.y);
    vec3 hi = vec3(beamBox.x, beamBox.x, beamBox.z);
    vec3 inv = 1.0 / (rd + sign(rd + 1e-7) * 1e-7);
    vec3 t0 = (lo - ro) * inv;
    vec3 t1 = (hi - ro) * inv;
    float tBoxMin = max(max(max(min(t0.x, t1.x), min(t0.y, t1.y)), min(t0.z, t1.z)), 0.0);
    float tBoxMax = min(min(max(t0.x, t1.x), max(t0.y, t1.y)), max(t0.z, t1.z));
    if (tBoxMax <= tBoxMin)
        discard;

    // Intersect with the held container's OBB so strands and haze occlude behind the container
    // and the container aura wraps cleanly around its silhouette and front rim.
    vec3 roBox = rotateInv(beamQuat, -to);
    vec3 rdBox = rotateInv(beamQuat, dir);
    float tHitNear, tHitFar;
    vec3 hitNormal;
    bool hitsContainer = intersectBox(roBox, rdBox, beamHalf.xyz, tHitNear, tHitFar, hitNormal);
    float tOcclude = hitsContainer ? max(tHitNear, 0.0) : tBoxMax;

    float time = frame.dust.w;
    float seed = beamFrom.w;
    float cRad = max(beamHalf.w, 0.1);
    float pixelScale = frame.lens.x * 2.0 / max(frame.lens.w, 1.0);

    int count = clamp(int(look.motion.x + 0.5), 1, 8);
    float waveAmp = look.motion.y * min(1.0, len / 2.2);
    float waveFreq = look.motion.z;
    float waveSpeed = look.motion.w;
    float flowSpeed = look.flow.x;

    vec3 totalLight = vec3(0.0);
    float landingGlow = 0.0;
    vec3 pHit = hitsContainer ? (roBox + rdBox * max(tHitNear, 0.0)) : vec3(0.0);

    // 1. Thin wavering cyan-teal energy strands with gentle travelling flow.
    float strandAccum = 0.0;
    for (int i = 0; i < count; i++)
    {
        float fi = float(i);
        float ang0 = fi * (6.2831853 / float(count)) + seed * 6.2831853;
        float ang1 = ang0 + 0.28 * sin(seed * 11.3 + fi * 1.7);
        vec3 r0 = (side * cos(ang0) + up * sin(ang0)) * look.spread.x;
        vec3 r1 = (side * cos(ang1) + up * sin(ang1)) * (cRad * look.spread.y);
        vec3 pStart = from + r0;
        vec3 pEnd = to + r1 - axis * (cRad * 0.22);

        if (hitsContainer)
        {
            vec3 endLocal = rotateInv(beamQuat, pEnd - to);
            float dLand = length(pHit - endLocal);
            float landR = max(cRad * 0.36, 0.12);
            landingGlow += exp(-(dLand * dLand) / (landR * landR) * 2.5);
        }

        float bestStrand = 0.0;
        vec3 prevP = pStart;
        for (int s = 1; s <= Segments; s++)
        {
            float u = float(s) / float(Segments);
            float env = sin(u * 3.14159265);
            float w1 = sin(u * waveFreq * 6.2831853 - time * waveSpeed + fi * 2.094 + seed * 4.7);
            float w2 = cos(u * (waveFreq * 1.35) * 6.2831853 + time * (waveSpeed * 0.85) + fi * 1.37 + seed * 3.1);
            vec3 wave = (side * w1 + up * w2) * (waveAmp * env);
            vec3 curP = mix(pStart, pEnd, u) + wave;

            float tSeg, segFrac;
            float d = rayToSegment(dir, prevP, curP, tSeg, segFrac);
            if (tSeg <= tOcclude + 0.02)
            {
                float rStrand = max(look.strand.w, max(tSeg, 0.2) * pixelScale * 1.1);
                float uAt = mix(float(s - 1) / float(Segments), u, segFrac);

                // Mostly transparent core: soft Gaussian falloff + subtle halo, no hard opaque edge.
                float core = exp(-(d * d) / (rStrand * rStrand) * 3.0);
                float halo = exp(-(d * d) / (rStrand * rStrand * 9.0) * 1.6) * 0.20;

                // Gentle travelling energy flow along the strand toward the emitter.
                float f1 = 0.5 + 0.5 * sin(uAt * 10.5 + time * flowSpeed + fi * 1.9 + seed * 5.0);
                float f2 = 0.5 + 0.5 * sin(uAt * 21.0 + time * flowSpeed * 1.4 - fi * 1.1);
                float flowMod = 0.50 + 0.50 * (f1 * 0.65 + f2 * 0.35);

                // Softly taper at the emitter & container ends.
                float endTaper = smoothstep(0.0, 0.08, uAt) * smoothstep(1.0, 0.88, uAt);
                float val = (core * 0.65 + halo) * flowMod * (0.40 + 0.60 * endTaper);
                bestStrand = max(bestStrand, val);
            }
            prevP = curP;
        }
        strandAccum += bestStrand;
    }
    totalLight += look.strand.rgb * (strandAccum * look.flow.y);

    // 2. Faint wide translucent haze around the strands, fading smoothly before the box edges.
    float tAxis, uAxis;
    float dAxis = rayToSegment(dir, from, to, tAxis, uAxis);
    if (tAxis <= tOcclude + cRad * 0.35)
    {
        float envR = mix(look.spread.x + look.haze.w * 0.35, cRad * look.spread.y + look.haze.w, uAxis);
        float hazeProfile = exp(-(dAxis * dAxis) / (envR * envR) * 2.4);
        float boxFade = smoothstep(beamBox.x, beamBox.x * 0.45, dAxis);
        float endFade = smoothstep(0.0, 0.14, uAxis) * smoothstep(1.0, 0.82, uAxis);
        totalLight += look.haze.rgb * (hazeProfile * boxFade * endFade * look.flow.z);
    }

    // 3. Soft glow/aura wrapped around the held container where the strands land.
    if (look.flow.w > 0.0)
    {
        float auraStrength = 0.0;
        if (hitsContainer)
        {
            float fresnel = pow(1.0 - clamp(abs(dot(hitNormal, rdBox)), 0.0, 1.0), 2.4);
            auraStrength = fresnel * 0.45 + min(landingGlow, 1.2) * 0.32;
        }
        else
        {
            float tClosest = clamp(dot(to, dir), tBoxMin, tBoxMax);
            vec3 pClosest = roBox + rdBox * tClosest;
            vec3 q = abs(pClosest) - beamHalf.xyz;
            float dBox = length(max(q, 0.0));
            float auraWidth = max(cRad * (look.spread.z - 1.0), 0.06) + 0.06;
            float auraMax = cRad * look.spread.z + 0.25;
            float edgeFade = smoothstep(auraMax, auraMax * 0.45, length(pClosest));
            auraStrength = exp(-(dBox * dBox) / (auraWidth * auraWidth) * 2.8) * edgeFade * 0.72;
        }
        vec3 auraColor = mix(look.haze.rgb, look.strand.rgb, 0.42);
        totalLight += auraColor * (auraStrength * look.flow.w);
    }

    // 4. Subtle emitter point glow where the strands emerge.
    float tEmit = max(dot(from, dir), 0.0);
    if (tEmit <= tOcclude + 0.02)
    {
        float dEmit = length(from - dir * tEmit);
        float emitR = max(look.spread.x * 1.6, tEmit * pixelScale * 2.0);
        float emitFade = smoothstep(beamBox.y, beamBox.y * 0.2, dEmit);
        float emitGlow = exp(-(dEmit * dEmit) / (emitR * emitR) * 2.8) * emitFade * 0.22 * look.flow.y;
        totalLight += look.strand.rgb * emitGlow;
    }

    if (dot(totalLight, totalLight) < 1e-6)
        discard;

    outColor = vec4(totalLight, 1.0);
}
