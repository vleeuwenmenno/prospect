#version 450
#include "ftl.glsl"

layout(set = 0, binding = 0) uniform sampler2D scene;

// Matches CompositeConstants in SceneRenderer.cs: 128 bytes, the push constant space every device guarantees.
layout(push_constant) uniform Push
{
    vec4 setup;    // x: exposure, y: aspect ratio, z: time in seconds, w: number of lenses
    vec4 own;      // the pilot's own jump: x drive kind, y stage, z progress through the stage
    vec4 lens[4];  // per opening: xy centre in NDC, z fully open radius in half screen heights, w code (see below)
    vec4 extra[2]; // per opening: progress through the stage and seed, two openings to a vector
} push;

layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;

// ACES filmic curve, Narkowicz fit.
vec3 filmic(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

// Bends the view around a mass at d = 0. The deflection falls off as 1/r, so the sky straight behind the opening
// is seen as a ring around it, stars near the ring drawn out into arcs. Surface brightness is kept, as a real lens
// keeps it. Works in the lens's own units; returns the offset to add to the scene lookup.
vec2 deflect(vec2 d, float einstein, float reach)
{
    float r2 = max(dot(d, d), 1e-8);
    float fade = 1.0 - smoothstep(reach * 0.6, reach, sqrt(r2));
    return -d * (einstein * einstein / r2) * fade;
}

// The far side of a rift: a dark red sky with orange gas and dense hard stars, a place that is clearly not here.
// Kept dark so the burning edge reads as the frame of a window.
vec3 riftInside(vec2 q, float seed, float time, float pixel, float scale)
{
    vec2 p = q * 1.3 + seed * 17.0;
    float warp = ftlFbm(p * 0.7 + vec2(time * 0.02, 0.0));
    float cloud = ftlFbm(p * 1.6 + warp * 2.0 - vec2(0.0, time * 0.03));
    vec3 color = vec3(0.012, 0.003, 0.002) + vec3(0.32, 0.075, 0.012) * smoothstep(0.4, 0.8, cloud);
    color += vec3(1.3, 0.45, 0.08) * pow(smoothstep(0.66, 0.92, cloud), 3.0);

    // Stars of the far side, each a hard point a little over a pixel wide however large the opening is.
    vec2 grid = p * 9.0;
    vec2 cell = floor(grid);
    vec2 offset = vec2(ftlHash2(cell + 7.1), ftlHash2(cell + 3.7)) * 0.6 + 0.2;
    float h = ftlHash2(cell);
    float size = pixel * 9.0 * 1.3 / scale * (0.8 + 1.4 * h);
    float star = step(0.9, h) * smoothstep(size, 0.0, length(fract(grid) - offset));
    return color + mix(vec3(1.6, 1.1, 0.7), vec3(1.4, 1.4, 1.5), fract(h * 7.0)) * star * 2.2;
}

// The far side of a wormhole: another sky, squeezed toward the rim as if seen on the inside of a sphere. Quiet
// colour; the lensing around it is the effect.
vec3 wormholeInside(vec2 q, vec2 parallax, float seed, float time, float pixel, float scale)
{
    float beyond = length(q);
    float r = min(beyond, 0.98);
    float squeeze = 1.0 / sqrt(1.0 - r * r);
    vec2 s = q * squeeze * 1.6;
    float turn = time * 0.02;
    s = mat2(cos(turn), -sin(turn), sin(turn), cos(turn)) * s + parallax + seed * 23.0;

    float gas = ftlFbm(s * 0.6);
    vec3 color = vec3(0.001, 0.002, 0.006) + vec3(0.008, 0.02, 0.04) * smoothstep(0.45, 0.85, gas);
    color += vec3(0.02, 0.008, 0.035) * smoothstep(0.6, 0.9, ftlFbm(s * 1.3 + 5.0));

    // Stars stay hard points about a pixel wide, however near the rim they are squeezed or large the throat is.
    vec2 grid = s * 5.0;
    vec2 cell = floor(grid);
    vec2 offset = vec2(ftlHash2(cell + 7.1), ftlHash2(cell + 3.7)) * 0.6 + 0.2;
    float h = ftlHash2(cell);
    float size = pixel * 5.0 * 1.6 / scale * squeeze * squeeze * squeeze * (0.8 + 1.2 * h);
    float star = step(0.9, h) * smoothstep(size, 0.0, length(fract(grid) - offset));
    color += vec3(1.0, 1.15, 1.5) * star * 1.6 * (1.0 - smoothstep(0.85, 0.97, r));
    // The throat's inner rim glows faintly; past it, while the pilot is inside, the far sky is darker.
    float within = 1.0 - smoothstep(0.97, 1.02, beyond);
    return color * mix(0.3, 1.0, within) + vec3(0.03, 0.06, 0.12) * pow(r, 10.0) * within;
}

// Where the edge of a rift runs, in pixels from the given point, positive inside.
float riftEdge(vec2 d, vec2 extent, float seed, float pixel, out vec2 e)
{
    e = d / extent;
    float re = length(e);
    vec2 dir = e / max(re, 1e-6);
    return (ftlRiftOutline(atan(e.y, e.x), seed) - re) * length(dir * extent) / pixel;
}

// A rift's edge burns: a hot line a pixel or two wide, and a short fringe outside it. Crisp, not a haze.
vec3 riftLine(float edge)
{
    return vec3(3.2, 1.4, 0.35) * exp(-abs(edge) / 0.9) + vec3(1.5, 0.4, 0.05) * exp(min(edge, 0.0) / 2.5) * (1.0 - clamp(edge + 0.5, 0.0, 1.0)) * 0.4;
}

// Just inside the edge, the far side glows where it was torn. Takes pixels inside the edge.
vec3 riftBurn(float inside)
{
    return vec3(1.5, 0.45, 0.07) * exp(-max(inside, 0.0) / 7.0) * 0.8;
}

// How strongly a rift bends the view, as an Einstein radius in units of its outline.
const float RiftEinstein = 0.85;

vec3 wormholeLine(float edge)
{
    return vec3(0.9, 1.2, 1.7) * exp(-abs(edge) / 0.8) * 1.2;
}

// An opening someone else's drive, or the pilot's own while it charges, has torn in the world.
void opening(int kind, int stage, vec2 d, vec2 centre, float radius, float progress, float seed, float time, float pixel, float mask,
             inout vec2 bend, inout vec4 cover, inout vec3 glow)
{
    float open = ftlOpening(stage, progress);
    if (open <= 0.001)
        return;

    if (kind == 3)
    {
        // The lens follows the rift's shape: it is worked out in the rift's stretched space and mapped back.
        vec2 extent = ftlRiftExtent(open) * radius;
        vec2 e;
        float edge = riftEdge(d, extent, seed, pixel, e);
        float inside = clamp(edge + 0.5, 0.0, 1.0) * mask;
        bend += deflect(e, RiftEinstein * ftlEase(open / 0.45), 3.4) * extent * mask * (1.0 - inside);
        // The far side drifts at half the speed of the opening, so it reads as deep, not painted on.
        vec3 far = riftInside((d + centre * 0.5) / radius, seed, time, pixel, radius) + riftBurn(edge);
        cover = mix(cover, vec4(far, 1.0), inside);
        glow += riftLine(edge) * mask;
    }
    else
    {
        float throat = radius * open;
        float edge = (throat - length(d)) / pixel;
        float inside = clamp(edge + 0.5, 0.0, 1.0) * mask;
        bend += deflect(d, throat * 1.3, throat * 4.0) * mask * (1.0 - inside);
        cover = mix(cover, vec4(wormholeInside(d / throat, centre * 1.5, seed, time, pixel, throat), 1.0), inside);
        glow += wormholeLine(edge) * mask;
    }
}

// The pilot going through their own rift or wormhole, over the transit and the arrival together. The opening they
// went into rushes out past the edges of the view; after a moment on the far side the destination opens in the
// middle and grows until it is all there is. Nothing in front of it, so no mask.
void passage(int kind, vec2 pos, vec2 centre, float radius, float progress, float seed, float time, float pixel,
             inout vec2 bend, inout vec4 cover, inout vec3 glow)
{
    // The entry slides to the middle of the view as it rushes out, wherever the nose was pointing; the exit opens
    // in the middle.
    float rush = ftlEase(progress / 0.12);
    float entry = radius * mix(1.0, 16.0, rush * rush);
    centre *= 1.0 - rush;
    vec2 d = pos - centre;
    float reveal = ftlEase((progress - 0.28) / 0.6);
    float exit = (kind == 3 ? 6.0 : 3.2) * pow(reveal, 1.5);

    float entryEdge;
    float exitEdge = -1e4;
    float bulge = 0.0;
    if (kind == 3)
    {
        vec2 extent = ftlRiftExtent(1.0) * entry;
        vec2 e;
        entryEdge = riftEdge(d, extent, seed, pixel, e);
        bend += deflect(e, RiftEinstein, 3.4) * extent * (1.0 - clamp(entryEdge + 0.5, 0.0, 1.0));
        if (exit > 0.0)
        {
            vec2 x;
            exitEdge = riftEdge(pos, vec2(0.72, 1.0) * exit, seed + 0.37, pixel, x);
            bulge = length(x);
        }
    }
    else
    {
        entryEdge = (entry - length(d)) / pixel;
        bend += deflect(d, entry * 1.3, entry * 4.0) * (1.0 - clamp(entryEdge + 0.5, 0.0, 1.0));
        if (exit > 0.0)
        {
            exitEdge = (exit - length(pos)) / pixel;
            bulge = length(pos) / exit;
        }
    }

    float inEntry = clamp(entryEdge + 0.5, 0.0, 1.0);
    float inExit = clamp(exitEdge + 0.5, 0.0, 1.0);

    // Through the exit the destination is squeezed toward the edge, the way the sky crowds the rim of a lens.
    bend += pos * 0.45 * bulge * bulge * inExit;

    // Inside the wormhole the far sky closes in slowly, its stars drifting outward past the pilot.
    float zoom = radius * mix(1.0, 2.5, rush) * (1.0 + 1.5 * progress);
    vec3 far = kind == 3
        ? riftInside((pos - centre * 0.5) / radius, seed, time, pixel, radius) + riftBurn(-exitEdge)
        : wormholeInside(d / zoom, centre * 1.5, seed, time, pixel, zoom);
    cover = mix(cover, vec4(far, 1.0), inEntry * (1.0 - inExit));
    vec3 line = kind == 3 ? riftLine(entryEdge) + riftLine(exitEdge) * inEntry : wormholeLine(entryEdge) + wormholeLine(exitEdge) * inEntry;
    glow += line;
}

// Hyperspace in transit: lines of light racing out of the point ahead on a deep blue, the only effect allowed to
// fill the view. A flash as the drive fires covers the cut.
vec3 hyperspace(vec3 scene, vec2 pos, float progress, float time, float pixel)
{
    float r = max(length(pos), 1e-4);
    float angle = atan(pos.y, pos.x);
    float depth = 0.35 / r;
    float streaks = 0.0;
    for (int layer = 0; layer < 2; layer++)
    {
        float count = layer == 0 ? 150.0 : 233.0;
        float lanes = angle / FtlTau * count + float(layer) * 0.5;
        float lane = floor(lanes);
        float seed = ftlHash(mod(lane, count) + float(layer) * 311.0);
        float across = abs(fract(lanes) - 0.5) * FtlTau / count * r / pixel;
        float line = 1.0 - smoothstep(0.4, 0.4 + 0.8 + 1.2 * r, across);
        float travel = fract(depth * (0.5 + seed) + time * (0.35 + 0.35 * seed) + seed * 7.0);
        float run = smoothstep(0.0, 0.04, travel) * (1.0 - smoothstep(0.12, 0.45, travel));
        streaks += line * run * step(0.3, seed) * (0.3 + 2.5 * pow(fract(seed * 13.7), 3.0));
    }

    // Near the vanishing point the lanes crowd into noise; let them come out of a calm, dark centre instead.
    streaks *= smoothstep(0.12, 0.6, r);
    vec3 tunnel = vec3(0.002, 0.006, 0.02) + vec3(0.4, 0.6, 1.0) * 0.012 / (r + 0.08) + vec3(0.65, 0.82, 1.0) * streaks * 1.8;
    float cover = ftlEase(progress / 0.05) * (1.0 - ftlEase((progress - 0.82) / 0.18));
    float flash = 1.0 - ftlEase(progress / 0.12);
    return mix(scene, tunnel, cover) + vec3(1.0, 1.05, 1.15) * flash * flash * 3.0;
}

void main()
{
    float aspect = push.setup.y;
    float time = push.setup.z;
    int count = int(push.setup.w + 0.5);
    float pixel = 2.0 / float(textureSize(scene, 0).y);
    vec2 pos = ndc * vec2(aspect, 1.0);
    float mask = smoothstep(0.55, 0.95, texture(scene, ndc * 0.5 + 0.5).a);

    // Lenses: the code is drive kind + 4 * stage, + 16 for the pilot's own passage.
    vec2 bend = vec2(0.0);
    vec4 cover = vec4(0.0);
    vec3 glow = vec3(0.0);
    for (int i = 0; i < 4; i++)
    {
        if (i >= count)
            break;
        vec4 lens = push.lens[i];
        vec4 extra = push.extra[i >> 1];
        float progress = (i & 1) == 0 ? extra.x : extra.z;
        float seed = (i & 1) == 0 ? extra.y : extra.w;
        int code = int(lens.w + 0.5);
        int kind = code & 3;
        int stage = (code >> 2) & 3;
        vec2 centre = lens.xy * vec2(aspect, 1.0);
        vec2 d = pos - centre;
        if ((code & 16) != 0)
            passage(kind, pos, centre, lens.z, progress, seed, time, pixel, bend, cover, glow);
        else if (mask > 0.0)
            opening(kind, stage, d, centre, lens.z, progress, seed, time, pixel, mask, bend, cover, glow);
    }

    // A bent lookup can leave the screen. Mirroring it back keeps a field of stars a field of stars, where clamping
    // would smear the edge pixels into streaks.
    vec2 lookup = 1.0 - abs(1.0 - abs((pos + bend) / vec2(aspect, 1.0) * 0.5 + 0.5));
    vec3 hdr = texture(scene, lookup).rgb;
    hdr = mix(hdr, cover.rgb, cover.a) + glow;

    if (int(push.own.x + 0.5) == 1 && int(push.own.y + 0.5) == 2)
        hdr = hyperspace(hdr, pos, push.own.z, time, pixel);

    vec3 mapped = filmic(hdr * push.setup.x);
    float vignette = 1.0 - 0.22 * pow(length(ndc) * 0.75, 2.5);
    vec3 display = pow(mapped * vignette, vec3(1.0 / 2.2));

    // A little noise hides banding in the dark gradients space is made of.
    float dither = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outColor = vec4(display + dither / 255.0, 1.0);
}
