#version 450
#include "frame.glsl"
#include "ftl.glsl"

// Set 0 is the frame constants, for the pilot's own jump and the camera; set 1 the scene.
layout(set = 1, binding = 0) uniform sampler2D scene;

// Matches CompositeConstants in SceneRenderer.cs: within 128 bytes, the push constant space every device guarantees.
layout(push_constant) uniform Push
{
    vec4 setup;    // x: exposure, y: aspect ratio, z: time in seconds, w: number of lenses
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

// An opening another hull's drive has torn in the world, or the pilot's own seen from the command view.
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

// The view direction through a point of the screen, in world space.
vec3 viewRay(vec2 at)
{
    return normalize(frame.cameraForward.xyz + frame.cameraRight.xyz * (at.x * frame.lens.x * frame.lens.y) - frame.cameraUp.xyz * (at.y * frame.lens.x));
}

// Where a camera-relative point lands, in half screen heights from the middle of the view. False when behind.
bool project(vec3 p, out vec2 at)
{
    float depth = dot(p, frame.cameraForward.xyz);
    at = vec2(dot(p, frame.cameraRight.xyz), -dot(p, frame.cameraUp.xyz)) / (max(depth, 1e-4) * frame.lens.x);
    return depth > 1e-3;
}

// Stars on the walls of a throat: hard points about a pixel wide wherever they are, faded out where the walls crowd
// toward the ends of the axis so they never shimmer. Square cells: count around, count / tau per unit along.
float throatStars(vec4 tube, vec2 wall, float count, float pixelAngle)
{
    vec2 grid = vec2(wall.x * count, wall.y * count / FtlTau);
    vec2 cell = floor(grid);
    vec2 key = vec2(mod(cell.x, count), cell.y);
    vec2 offset = vec2(ftlHash2(key + 7.1), ftlHash2(key + 3.7)) * 0.6 + 0.2;
    float h = ftlHash2(key);
    float pixelCells = pixelAngle / (FtlTau / count * max(tube.w, 1e-3));
    float size = pixelCells * (0.7 + 0.9 * h);
    float visible = 1.0 - smoothstep(0.12, 0.3, pixelCells);
    return step(0.9, h) * smoothstep(size, 0.0, length(fract(grid) - offset)) * visible;
}

// Inside a wormhole: a tube of dark sky along the jump axis with faint gas and hard stars on its walls, streaming
// past as the hull goes through. Quiet colour; the ends glow faintly with the light of the mouths.
vec3 wormholeThroat(vec3 v, float travel, float seed, float time, float pixelAngle)
{
    vec4 tube = ftlTube(v, frame.ftlAxis.xyz, frame.ftlUp.xyz);
    vec2 wall = vec2(tube.x + time * 0.004, tube.y - travel);
    float detail = smoothstep(0.04, 0.3, tube.w);
    float gas = ftlFbmAround(vec2(wall.x * 10.0, wall.y * 1.6 + seed * 31.0), 10.0);
    float veil = ftlFbmAround(vec2(wall.x * 20.0, wall.y * 3.1 + 5.0 + seed * 17.0), 20.0);
    vec3 color = vec3(0.001, 0.002, 0.006)
        + (vec3(0.008, 0.02, 0.04) * smoothstep(0.45, 0.85, gas) + vec3(0.02, 0.008, 0.035) * smoothstep(0.6, 0.9, veil)) * detail;
    color += vec3(1.0, 1.15, 1.5) * throatStars(tube, wall, 96.0, pixelAngle) * 1.6;
    float ends = exp(-tube.z * 4.0) + 0.5 * exp(-(FtlTau * 0.5 - tube.z) * 4.0);
    return color + vec3(0.03, 0.06, 0.12) * ends;
}

// Inside a rift: the same tube, dark red with orange gas and dense hard stars, a place that is clearly not here.
vec3 riftThroat(vec3 v, float travel, float seed, float time, float pixelAngle)
{
    vec4 tube = ftlTube(v, frame.ftlAxis.xyz, frame.ftlUp.xyz);
    vec2 wall = vec2(tube.x, tube.y - travel);
    float detail = smoothstep(0.04, 0.3, tube.w);
    float warp = ftlFbmAround(vec2(wall.x * 8.0, wall.y * 1.2 + seed * 17.0 + time * 0.02), 8.0);
    float cloud = ftlFbmAround(vec2(wall.x * 16.0, wall.y * 2.4 + warp * 2.0 - time * 0.03), 16.0);
    vec3 color = vec3(0.012, 0.003, 0.002)
        + (vec3(0.2, 0.045, 0.008) * smoothstep(0.45, 0.85, cloud) + vec3(1.0, 0.35, 0.06) * pow(smoothstep(0.7, 0.94, cloud), 3.0)) * detail;
    color += mix(vec3(1.6, 1.1, 0.7), vec3(1.4, 1.4, 1.5), 0.5) * throatStars(tube, wall, 128.0, pixelAngle) * 2.2;
    float ends = exp(-tube.z * 4.0) + 0.5 * exp(-(FtlTau * 0.5 - tube.z) * 4.0);
    return color + vec3(0.12, 0.03, 0.005) * ends;
}

// The pilot's own rift or wormhole, worked out from where its openings are in the world. Returns how much of the
// pixel is the real scene; the rest is the throat. While the drive works there is one opening, ahead: the real world
// on the near side, the throat beyond. After the drive has fired there is the one the hull comes out of: the throat
// on the near side, the destination beyond. Which side a pixel shows follows from where the camera is and what its
// ray passes through, so the camera can fly through an opening and the view changes with it, never in one frame.
// A wormhole's opening is a sphere; inside the sphere the far side is the half of the view ahead. A rift's is a flat
// window across the axis with a torn outline.
float ownPassage(int kind, vec3 v, vec2 pos, float pixelAngle, bool solid, bool marked, bool ownHull, inout vec2 bend, inout vec3 glow)
{
    bool entry = int(frame.ftl.y + 0.5) == 1;
    vec4 mouth = entry ? frame.ftlEntry : frame.ftlExit;
    float open = entry ? frame.ftlShape.y : frame.ftlShape.z;
    vec3 axis = frame.ftlAxis.xyz;
    vec3 up = frame.ftlUp.xyz;
    float seed = frame.ftl.w + (entry ? 0.0 : 0.37);
    vec3 c = mouth.xyz;
    float r = mouth.w;
    // The camera is behind the opening when its centre lies ahead along the axis.
    bool behind = dot(c, axis) > 0.0;
    float edge = -1e4;
    float far;
    vec2 lens = vec2(0.0);
    float line = 1.0;

    if (kind == 3)
    {
        float facing = dot(v, axis);
        float t = abs(facing) > 1e-5 ? dot(c, axis) / facing : -1.0;
        if (r > 0.0 && t > 0.0)
        {
            vec3 hit = v * t - c;
            vec3 side = cross(up, axis);
            vec2 extent = ftlRiftExtent(open) * r;
            vec2 e = vec2(dot(hit, side), dot(hit, up)) / extent;
            float re = length(e);
            vec2 dir = e / max(re, 1e-6);
            float perPixel = t * pixelAngle / max(abs(facing), 0.08);
            edge = (ftlRiftOutline(atan(e.y, e.x), seed) - re) * length(dir * extent) / perPixel;
            // Past the window the view bends around it, worked out in the window's plane and projected back.
            vec2 shift = deflect(e, RiftEinstein * ftlEase(open / 0.45), 3.4) * extent;
            vec2 bent;
            if (project(c + hit + side * shift.x + up * shift.y, bent))
                lens = (bent - pos) * (1.0 - clamp(edge + 0.5, 0.0, 1.0));
        }
        float through = clamp(edge + 0.5, 0.0, 1.0);
        far = behind ? through : 1.0 - through;
    }
    else
    {
        float d = length(c);
        if (r <= 0.0)
        {
            far = behind ? 0.0 : 1.0;
        }
        else if (d > r)
        {
            // The rim fades as the camera reaches the sphere, where it would become a line straight across the view.
            line = smoothstep(r, r * 1.4, d);
            float rim = asin(r / d);
            edge = (rim - acos(clamp(dot(v, c / d), -1.0, 1.0))) / pixelAngle;
            float hit = clamp(edge + 0.5, 0.0, 1.0);
            far = behind ? hit : 1.0 - hit;
            vec2 centre;
            if (project(c, centre))
            {
                // The lens bends the view around the sphere as it appears; it gives way as the sphere fills the view.
                float size = tan(rim) / frame.lens.x;
                lens = deflect(pos - centre, size * 1.3, size * 4.0) * (1.0 - smoothstep(1.0, 2.5, size)) * (1.0 - hit);
            }
        }
        else
        {
            edge = asin(clamp(dot(v, axis), -1.0, 1.0)) / pixelAngle;
            far = clamp(edge + 0.5, 0.0, 1.0);
            line = 0.0;
        }
    }

    // Something solid in front of the opening hides it: its pixel did not get the opening's mark. Close to the
    // opening the mark is clipped by the near plane, and nothing solid can be that close anyway.
    bool inFront = solid && !marked && length(c) > r * 1.5 + 0.5;
    if (inFront)
        far = behind ? 0.0 : 1.0;
    float real = entry ? 1.0 - far : far;
    if (ownHull)
        return 1.0;

    // The real world is bent where it is seen past the opening from the real side.
    if (entry == behind && !inFront)
        bend += lens;
    if (r > 0.0 && !inFront)
    {
        // The burning edge, with the throat side of it glowing where it was torn.
        float throatSide = entry == behind ? edge : -edge;
        glow += kind == 3 ? riftLine(edge) + riftBurn(throatSide) * step(0.0, throatSide) : wormholeLine(edge) * line;
    }
    return real;
}

// The hyperspace tunnel around the pilot: lines of light racing back from the point ahead of the nose on a deep
// blue, the only effect allowed to fill the view. Laid out around the jump axis, so it is the same wherever the
// pilot looks: lines stream past to the side and converge behind. Travel moves the lines; it speeds up and slows
// down with the jump.
vec3 hyperspaceTunnel(vec3 v, float travel, float pixelAngle)
{
    vec4 tube = ftlTube(v, frame.ftlAxis.xyz, frame.ftlUp.xyz);
    float streaks = 0.0;
    for (int layer = 0; layer < 2; layer++)
    {
        float count = layer == 0 ? 150.0 : 233.0;
        float lanes = tube.x * count + float(layer) * 0.5;
        float lane = floor(lanes);
        float seed = ftlHash(mod(lane, count) + float(layer) * 311.0);
        float across = abs(fract(lanes) - 0.5) * FtlTau / count * tube.w / pixelAngle;
        float line = 1.0 - smoothstep(0.4, 1.2 + 2.0 * tube.w, across);
        // The head of each line leads, away from the nose; its tail fades back toward it.
        float f = fract(travel * (0.7 + 0.6 * seed) - tube.y * (0.5 + seed) + seed * 7.0);
        float run = smoothstep(0.0, 0.04, f) * (1.0 - smoothstep(0.12, 0.45, f));
        streaks += line * run * step(0.3, seed) * (0.3 + 2.5 * pow(fract(seed * 13.7), 3.0));
    }

    // Near the points ahead and behind the lanes crowd into noise; let them come out of a calm, dark centre instead.
    float theta = tube.z;
    streaks *= smoothstep(0.1, 0.5, theta) * (1.0 - smoothstep(FtlTau * 0.5 - 0.5, FtlTau * 0.5 - 0.1, theta));
    return vec3(0.002, 0.006, 0.02) + vec3(0.4, 0.6, 1.0) * 0.0075 / (theta + 0.05) + vec3(0.65, 0.82, 1.0) * streaks * 1.8;
}

void main()
{
    float aspect = push.setup.y;
    float time = push.setup.z;
    int count = int(push.setup.w + 0.5);
    float pixel = 2.0 / float(textureSize(scene, 0).y);
    float pixelAngle = pixel * frame.lens.x;
    vec2 pos = ndc * vec2(aspect, 1.0);

    // The scene's alpha: one per opening mark in front of what is there, plus what is there in the fraction (0 sky,
    // 0.25 a solid surface, 0.5 the pilot's own hull). Read exactly, never filtered.
    float alpha = texelFetch(scene, ivec2(gl_FragCoord.xy), 0).a;
    float marks = floor(alpha + 0.05);
    float what = alpha - marks;
    bool marked = marks > 0.5;
    bool solid = what > 0.15;
    bool ownHull = what > 0.4;
    float mask = marked ? 1.0 : 0.0;

    // Openings of other hulls, and of the pilot's own when seen from the command view. The code is drive kind +
    // 4 * stage.
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
        vec2 centre = lens.xy * vec2(aspect, 1.0);
        if (mask > 0.0)
            opening(code & 3, (code >> 2) & 3, pos - centre, centre, lens.z, progress, seed, time, pixel, mask, bend, cover, glow);
    }

    // The pilot's own jump, from the inside.
    int ownKind = int(frame.ftl.x + 0.5);
    int ownStage = int(frame.ftl.y + 0.5);
    vec3 v = viewRay(ndc);
    float real = 1.0;
    vec3 throat = vec3(0.0);
    if (ownStage > 0 && (ownKind == 2 || ownKind == 3))
    {
        real = ownPassage(ownKind, v, pos, pixelAngle, solid, marked, ownHull, bend, glow);
        if (real < 1.0)
            throat = ownKind == 3 ? riftThroat(v, frame.ftlAxis.w, frame.ftl.w, time, pixelAngle) : wormholeThroat(v, frame.ftlAxis.w, frame.ftl.w, time, pixelAngle);
    }

    // A bent lookup can leave the screen. Mirroring it back keeps a field of stars a field of stars, where clamping
    // would smear the edge pixels into streaks.
    vec2 lookup = 1.0 - abs(1.0 - abs((pos + bend) / vec2(aspect, 1.0) * 0.5 + 0.5));
    // A lens only images what is behind it. Where the bent ray lands on something solid in front of every opening,
    // such as the pilot's own hull, the view is not bent.
    float landed = texelFetch(scene, ivec2(lookup * vec2(textureSize(scene, 0))), 0).a;
    float landedOn = landed - floor(landed + 0.05);
    if (landedOn > 0.4 || (landedOn > 0.15 && landed < 0.95))
        lookup = ndc * 0.5 + 0.5;
    vec3 hdr = texture(scene, lookup).rgb;
    hdr = mix(hdr, cover.rgb, cover.a);
    hdr = mix(throat, hdr, real) + glow;

    // Hyperspace covers everything but the pilot's own hull, which rides through it.
    float tunnel = ownKind == 1 && !ownHull ? frame.ftlUp.w : 0.0;
    if (tunnel > 0.0)
        hdr = mix(hdr, hyperspaceTunnel(v, frame.ftlAxis.w, pixelAngle), tunnel);

    vec3 mapped = filmic(hdr * push.setup.x);
    float vignette = 1.0 - 0.22 * pow(length(ndc) * 0.75, 2.5);
    vec3 display = pow(mapped * vignette, vec3(1.0 / 2.2));

    // A little noise hides banding in the dark gradients space is made of.
    float dither = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outColor = vec4(display + dither / 255.0, 1.0);
}
