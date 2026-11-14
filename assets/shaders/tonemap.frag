#version 450
#include "frame.glsl"
#include "ftl.glsl"
#include "ftl_own.glsl"
#include "gas.glsl"
#include "gas_half.glsl"

// Set 0 is the frame constants, for the camera and the jumps in view; set 1 the scene and, on frames with a lens,
// the scene as it was before anything solid was drawn (the sky) and before any hull was (the sky and asteroids).
layout(set = 1, binding = 0) uniform sampler2D scene;
layout(set = 1, binding = 1) uniform sampler2D sky;
layout(set = 1, binding = 2) uniform sampler2D rocks;
// The scene's depth, and the depth before any hull was drawn. Reverse depth: 0 is the sky.
layout(set = 1, binding = 3) uniform sampler2D sceneDepth;
layout(set = 1, binding = 4) uniform sampler2D rocksDepth;
// The scene just before the pilot's own shield was drawn. Written only on frames where their hyperspace tunnel
// covers the view and that shield is drawn; read only at its pixels on those frames.
layout(set = 1, binding = 5) uniform sampler2D ownBehind;
// Gas-free scene before shields, kept on every deferred gas frame. The ordinary fallbacks remain gas-free too.
layout(set = 1, binding = 6) uniform sampler2D beforeShields;
layout(set = 1, binding = 7) uniform sampler2D opaqueDepth;
// The deferred cloud marched at half resolution to opaqueDepth, when frame.gas.budget.w is below zero
// (GasConstants.HalfMarched); see gas_half.glsl.
layout(set = 1, binding = 8) uniform sampler2D halfGas;

// Bit of bentBy for the pilot's own opening; bits 0 to 3 are frame.openings.
const int OwnBit = 16;

// How far along the view's forward axis what a depth value holds is; the sky is infinitely far.
float viewDepth(float depth)
{
    return depth > 0.0 ? frame.viewProjection[3][2] / depth : Far;
}

// Where a view ray crosses the plane through c across axis, along the view; Far where it does not.
float planeDepth(vec3 v, vec3 c, vec3 axis)
{
    float facing = dot(v, axis);
    float t = abs(facing) > 1e-5 ? dot(c, axis) / facing : -1.0;
    return t > 0.0 ? rayDepth(v, t) : Far;
}

// Where a view ray comes to a sphere, as sphereRay has it, along the view; Far where that is behind the camera.
float sphereDepth(vec3 v, vec3 c, float r)
{
    float t = sphereRay(v, c, r);
    return t > 0.0 ? rayDepth(v, t) : Far;
}

// Matches CompositeConstants in SceneRenderer.cs.
layout(push_constant) uniform Push
{
    vec4 setup; // x: exposure, y: aspect ratio, z: time in seconds, w: number of openings in frame.openings
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

// An opening another hull's drive has torn in the world, or the pilot's own seen from the command view, worked out
// from where it is, the way it faces and the ray through this pixel, so it is seen in perspective from any side and
// stays put in the world however the camera turns. A rift is a flat window across the hull's axis with a torn
// outline, the same seen from either side; a wormhole a sphere. Where this pixel's ray meets the opening is compared
// with the depth of what is there: a hull or rock in front of the opening, at this pixel, is neither bent nor
// covered by it. Each opening is tested on its own, so where two overlap one never lets the other through a rock.
// Bending sets this opening's bit in bentBy, so a bent lookup can tell where each opening that bent it lies along
// the rays it reads.
void opening(int index, vec3 v, vec2 pos, float pixelAngle, float time, float depth, inout vec2 bend, inout int bentBy, inout vec4 cover, inout vec3 glow)
{
    vec4 place = frame.openings[index * 4];
    vec3 axis = frame.openings[index * 4 + 1].xyz;
    vec3 up = frame.openings[index * 4 + 2].xyz;
    vec4 params = frame.openings[index * 4 + 3];
    int kind = int(params.x + 0.5);
    float open = params.y;
    float seed = params.z;
    vec3 c = place.xyz;
    float radius = place.w;
    if (open <= 0.001 || radius <= 0.0)
        return;
    vec3 side = cross(up, axis);

    if (kind == 3)
    {
        float facing = dot(v, axis);
        float t = abs(facing) > 1e-5 ? dot(c, axis) / facing : -1.0;
        float at = rayDepth(v, t);
        if (t <= 0.0 || at >= depth)
            return;
        vec3 hit = v * t - c;
        vec2 extent = ftlRiftExtent(open) * radius;
        vec2 e = vec2(dot(hit, side), dot(hit, up)) / extent;
        float re = length(e);
        vec2 dir = e / max(re, 1e-6);
        // Pixels per unit across the window: seen edge on, the window narrows to its burning line.
        float perPixel = t * pixelAngle / max(abs(facing), 0.08);
        float edge = (ftlRiftOutline(atan(e.y, e.x), seed) - re) * length(dir * extent) / perPixel;
        float inside = clamp(edge + 0.5, 0.0, 1.0);
        // The view bends around the window, worked out in its plane and projected back.
        vec2 shift = deflect(e, RiftEinstein * ftlEase(open / 0.45), 3.4) * extent;
        vec2 bent;
        if (project(c + hit + side * shift.x + up * shift.y, bent))
        {
            vec2 contribution = (bent - pos) * (1.0 - inside);
            if (contribution != vec2(0.0))
            {
                bend += contribution;
                bentBy |= 1 << index;
            }
        }
        // The far side is laid on the window and shifts with the view through it, so it reads as deep, not
        // painted on.
        vec2 q = vec2(dot(hit, side), dot(hit, up)) / radius;
        q += vec2(dot(v, side), dot(v, up)) / max(abs(facing), 0.25) * 0.5;
        vec3 far = riftInside(q, seed, time, perPixel / radius, 1.0) + riftBurn(edge);
        cover = mix(cover, vec4(far, 1.0), inside);
        glow += riftLine(edge);
    }
    else
    {
        float throat = radius * open;
        float d = length(c);
        float t = sphereRay(v, c, throat);
        float at = rayDepth(v, t);
        if (d <= throat || t <= 0.0 || at >= depth)
            return;
        vec3 toward = c / d;
        float rim = asin(throat / d);
        float edge = (rim - acos(clamp(dot(v, toward), -1.0, 1.0))) / pixelAngle;
        float inside = clamp(edge + 0.5, 0.0, 1.0);
        vec2 centre;
        if (project(c, centre))
        {
            float size = tan(rim) / frame.lens.x;
            vec2 contribution = deflect(pos - centre, size * 1.3, size * 4.0) * (1.0 - inside);
            if (contribution != vec2(0.0))
            {
                bend += contribution;
                bentBy |= 1 << index;
            }
        }
        // Across the sphere as seen from here, with the hull's own up, so the far sky does not roll with the
        // camera; it shifts as the sphere is seen from another side.
        vec3 u = up - toward * dot(up, toward);
        u = dot(u, u) > 1e-6 ? normalize(u) : normalize(cross(toward, side));
        vec3 s = cross(u, toward);
        vec2 q = vec2(dot(v, s), dot(v, u)) / sin(rim);
        vec2 parallax = vec2(dot(toward, side), dot(toward, up)) * 1.5;
        cover = mix(cover, vec4(wormholeInside(q, parallax, seed, time, pixelAngle, sin(rim)), 1.0), inside);
        glow += wormholeLine(edge);
    }
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

// The pilot's own rift or wormhole, worked out from where its openings are in the world by ftlOwnRay. Returns how
// much of the pixel is the real scene; the rest is the throat. The camera goes wherever the pilot has it looking, so
// it may pass beside an opening rather than through it: the side it is on is eased (frame.ftlUp.w), and there the
// view dissolves to the far side. The pilot's own hull is shown as it is: its mesh pass has already left out what
// of it this pass would hide, with the same function.
float ownPassage(int kind, vec3 v, vec2 pos, float pixelAngle, float depth, bool ownHull, inout vec2 bend, inout int bentBy, inout vec3 glow)
{
    bool entry = int(frame.ftl.y + 0.5) == 1;
    vec4 mouth = entry ? frame.ftlEntry : frame.ftlExit;
    float open = entry ? frame.ftlShape.y : frame.ftlShape.z;
    vec3 axis = frame.ftlAxis.xyz;
    vec3 up = frame.ftlUp.xyz;
    vec3 c = mouth.xyz;
    float r = mouth.w;
    float past = frame.ftlUp.w;
    FtlOwnRay ray = ftlOwnRay(v, pixelAngle);
    float edge = ray.edge;

    // Something solid nearer than where this pixel's ray meets the opening hides it.
    bool inFront = depth < ray.at;
    float real = ftlOwnReal(ray, depth);
    if (ownHull)
        return 1.0;

    // Past the opening the view bends around it.
    vec2 lens = vec2(0.0);
    if (kind == 3)
    {
        if (ray.at < Far)
        {
            // Worked out in the window's plane and projected back.
            vec3 side = cross(up, axis);
            vec2 shift = deflect(ray.across, RiftEinstein * ftlEase(open / 0.45), 3.4) * ray.extent;
            vec2 bent;
            if (project(c + ray.hit + side * shift.x + up * shift.y, bent))
                lens = (bent - pos) * (1.0 - clamp(edge + 0.5, 0.0, 1.0));
        }
    }
    else
    {
        float d = length(c);
        vec2 centre;
        if (r > 0.0 && d > r && project(c, centre))
        {
            // The lens bends the view around the sphere as it appears; it gives way as the sphere fills the view.
            float size = tan(asin(r / d)) / frame.lens.x;
            lens = deflect(pos - centre, size * 1.3, size * 4.0) * (1.0 - smoothstep(1.0, 2.5, size)) * (1.0 - clamp(edge + 0.5, 0.0, 1.0));
        }
    }

    // The real world is bent where it is seen past the opening from the real side.
    float realSide = entry ? 1.0 - past : past;
    if (!inFront && lens * realSide != vec2(0.0))
    {
        bend += lens * realSide;
        bentBy |= OwnBit;
    }
    if (r > 0.0 && !inFront)
    {
        // The burning edge, with the throat side of it glowing where it was torn.
        vec3 burn = mix(riftBurn(-edge) * step(0.0, -edge), riftBurn(edge) * step(0.0, edge), realSide);
        glow += kind == 3 ? riftLine(edge) + burn : wormholeLine(edge) * ray.line;
    }
    return real;
}

// The hyperspace tunnel around the pilot: lines of light racing back from the point ahead of the nose on a deep
// blue, the only effect allowed to fill the view. Laid out around the jump axis, so it is the same wherever the
// pilot looks: lines stream past to the side and converge behind. Travel moves the lines; it speeds up and slows
// down with the jump. It is lit from ahead: bright toward the destination, dimming past the sides to a dark wake
// behind the vessel where the lines run off, faint but still there.
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

    // How far ahead this ray looks along the jump, as one smooth curve over the whole sphere: full ahead, most of it
    // to the side, a fraction straight back. It hangs on the jump's axis in the world, not on the view.
    float lit = smoothstep(-0.85, 0.35, cos(theta));
    vec3 deep = vec3(0.002, 0.006, 0.02) * mix(0.4, 1.0, lit);
    return deep + vec3(0.4, 0.6, 1.0) * 0.0075 / (theta + 0.05) + vec3(0.65, 0.82, 1.0) * streaks * 1.8 * mix(0.25, 1.0, lit);
}

// A sampled surface must be behind every opening that displaced its lookup, so the farthest valid depth is the
// threshold. A plane parallel to the sampled ray or behind the camera imposes no threshold on that ray. Sphere
// misses retain sphereRay's closest-approach depth, which defines the sphere's surrounding lens field.
float mouthDepth(int bentBy, vec3 v)
{
    float farthest = 0.0;
    for (int i = 0; i < 4; i++)
    {
        if ((bentBy & (1 << i)) == 0)
            continue;
        vec4 place = frame.openings[i * 4];
        vec4 params = frame.openings[i * 4 + 3];
        float at = int(params.x + 0.5) == 3 ? planeDepth(v, place.xyz, frame.openings[i * 4 + 1].xyz) : sphereDepth(v, place.xyz, place.w * params.y);
        if (at > 0.0 && at < Far)
            farthest = max(farthest, at);
    }
    if ((bentBy & OwnBit) != 0)
    {
        vec4 mouth = int(frame.ftl.y + 0.5) == 1 ? frame.ftlEntry : frame.ftlExit;
        float at = int(frame.ftl.x + 0.5) == 3 ? planeDepth(v, mouth.xyz, frame.ftlAxis.xyz) : sphereDepth(v, mouth.xyz, mouth.w);
        if (at > 0.0 && at < Far)
            farthest = max(farthest, at);
    }
    return farthest;
}

// Only the gas path needs depth-ordered mouths: gas between two lenses sees the nearer bend, not the farther.
// Five mouths (four external plus own) yield at most six bounded volume intervals.
struct GasEvent { float depth; vec2 bend; vec4 cover; vec3 glow; int mask; int id; bool own; };
float gasMouth(int id, vec3 ray)
{
    if(id==4) return ftlOwnRay(ray,2.0*frame.lens.x/frame.lens.w).at;
    vec4 place=frame.openings[id*4], params=frame.openings[id*4+3];
    return int(params.x+0.5)==3 ? planeDepth(ray,place.xyz,frame.openings[id*4+1].xyz) : sphereDepth(ray,place.xyz,place.w*params.y);
}
vec2 gasLookup(vec2 pos, vec2 bend, float aspect)
{
    return 1.0-abs(1.0-abs((pos+bend)/vec2(aspect,1.0)*0.5+0.5));
}
bool gasFixtureSource(out vec3 base, out float cutoff, out vec3 delta, out float shieldAt, out bool ownSource);
// Select the gas-free source for this prefix of lenses. Each fallback supplies its own cutoff, never hull depth.
vec3 gasReadSource(vec2 uv, int mask, bool unbent, out float cutoff, out vec3 delta, out float shieldAt, out bool ownSource)
{
    vec3 fixtureBase;
    if(gasFixtureSource(fixtureBase,cutoff,delta,shieldAt,ownSource)) return fixtureBase;
    bool sceneClear=true, rocksClear=true;
    if(mask!=0) {
        vec4 what=textureGather(scene,uv,3), depths=textureGather(sceneDepth,uv,0), rockDepths=textureGather(rocksDepth,uv,0);
        vec2 size=vec2(textureSize(scene,0)), corner=floor(uv*size-0.5);
        for(int i=0;i<4;i++) {
            vec2 at=clamp(corner+vec2(i==1 || i==2 ? 1.0:0.0,i<2 ? 1.0:0.0),vec2(0),size-1.0);
            float mouth=mouthDepth(mask,viewRay((at+0.5)/size*2.0-1.0));
            sceneClear=sceneClear && what[i]<0.44 && viewDepth(depths[i])>=mouth;
            rocksClear=rocksClear && viewDepth(rockDepths[i])>=mouth;
        }
    }
    if(mask==0 && unbent) {
        // No lens moves this pixel: read its own texels exactly, as the ordinary gas pass and the gas-free path do.
        // A filtered read at the interpolated position blended a sliver of the neighbour across hard silhouettes.
        ivec2 own=ivec2(gl_FragCoord.xy);
        vec4 here=texelFetch(scene,own,0);
        vec3 before=texelFetch(beforeShields,own,0).rgb;
        ownSource=here.a>0.44;
        cutoff=viewDepth(texelFetch(opaqueDepth,own,0).r);
        delta=here.rgb-before;
        shieldAt=viewDepth(texelFetch(sceneDepth,own,0).r);
        return before;
    }
    ownSource=sceneClear && texture(scene,uv).a>0.44;
    cutoff=sceneClear ? viewDepth(texture(opaqueDepth,uv).r) : rocksClear ? viewDepth(texture(rocksDepth,uv).r) : Far;
    vec3 base=sceneClear ? texture(beforeShields,uv).rgb : rocksClear ? texture(rocks,uv).rgb : texture(sky,uv).rgb;
    delta=sceneClear ? texture(scene,uv).rgb-base : vec3(0);
    shieldAt=sceneClear ? viewDepth(texture(sceneDepth,uv).r) : cutoff;
    return base;
}
// Camera-side own dissolve is not an occludable surface. Reapply it when a nearer source resets layers
// that already passed the own event; leave gas scattering outside this surface-only operation.
vec3 gasOwnSurface(vec3 surface, vec3 ray, float depth, bool ownSource, float pixelAngle)
{
    if(ownSource) return surface;
    float real=ftlOwnReal(ftlOwnRay(ray,pixelAngle),depth);
    if(real>=1.0) return surface;
    vec3 throat=int(frame.ftl.x+0.5)==3 ? riftThroat(ray,frame.ftlAxis.w,frame.ftl.w,frame.dust.w,pixelAngle)
        : wormholeThroat(ray,frame.ftlAxis.w,frame.ftl.w,frame.dust.w,pixelAngle);
    return mix(throat,surface,real);
}
vec3 gasComposite(GasData gas, GasEvent events[5], int count, vec2 pos, float aspect, float pixelAngle)
{
    // Insertion sort is bounded by five events. Keep the existing order for coincident mouths.
    for(int i=1;i<5;i++) {
        if(i>=count) break;
        GasEvent event=events[i]; int j=i;
        while(j>0 && events[j-1].depth>event.depth) { events[j]=events[j-1]; j--; }
        events[j]=event;
    }
    vec2 bends[6]; int masks[6];
    bends[0]=vec2(0); masks[0]=0;
    for(int i=0;i<5;i++) {
        if(i>=count) break;
        bends[i+1]=bends[i]+events[i].bend;
        masks[i+1]=masks[i]|events[i].mask;
    }
    int ownIndex=-1;
    for(int i=0;i<5;i++) { if(i>=count) break; if(events[i].own) ownIndex=i; }
    vec3 surface=vec3(0), scattering=vec3(0);
    int firstStage=count;
    // Start at the nearest opaque external cover. All farther volume work is provably invisible.
    for(int i=0;i<5;i++) {
        if(i>=count) break;
        if(!events[i].own && events[i].cover.a>=1.0) {
            firstStage=i;
            surface=events[i].cover.rgb+events[i].glow;
            if(ownIndex>i) surface=gasOwnSurface(surface,viewRay(gasLookup(pos,bends[i],aspect)*2.0-1.0),events[i].depth,false,pixelAngle);
            break;
        }
    }
    for(int stage=5;stage>=0;stage--) {
        if(stage>firstStage) continue;
        vec2 uv=gasLookup(pos,bends[stage],aspect);
        vec3 ray=viewRay(uv*2.0-1.0);
        float cutoff, shieldAt; vec3 delta; bool ownSource;
        vec3 base=gasReadSource(uv,masks[stage],bends[stage]==vec2(0),cutoff,delta,shieldAt,ownSource);
        float start=0.0;
        // Prefix depth is monotone even where a tilted, bent lookup changes the apparent order of planes.
        for(int i=0;i<5;i++) {
            if(i>=stage) break;
            float at=gasMouth(events[i].id,ray);
            start=max(start,at<Far ? at : events[i].depth);
        }
        float stop=cutoff;
        bool occludes=false;
        if(stage<count) {
            float at=gasMouth(events[stage].id,ray);
            // A ray parallel to a plane retains its ordered screen-space layer depth.
            if(at>=Far) at=events[stage].depth;
            float boundary=max(start,at);
            occludes=cutoff<boundary;
            stop=min(stop,boundary);
        }
        // The interval from the camera to this pixel's own opaque depth, on an unbent ray, is what the half-resolution
        // march holds; everywhere else, and where it holds nothing at this depth, the pixel marches itself.
        vec4 volume;
        bool fromHalf=frame.gas.budget.w<0.0 && gasThin>=1.0 && masks[stage]==0 && bends[stage]==vec2(0) && start<=0.0
            && stop==cutoff && !gasNearBolt(gas,ray,pixelAngle);
        if(!fromHalf || !gasHalfRead(halfGas,opaqueDepth,ivec2(gl_FragCoord.xy),cutoff,volume))
            volume=gasSegment(gas,ray,start,stop,pixelAngle);
        if(stage==count || occludes) {
            // Here the interval ends at the opaque depth, so a shield at or behind it is attenuated by the whole
            // interval: the volume above.
            float front=delta==vec3(0) ? 1.0 : shieldAt>=cutoff ? volume.a : gasSegment(gas,ray,start,shieldAt,pixelAngle).a;
            surface=volume.a*base+front*delta;
            if(occludes && ownIndex>=stage)
                surface=gasOwnSurface(surface,ray,cutoff,ownSource,pixelAngle);
            scattering=volume.rgb;
        } else {
            surface*=volume.a;
            scattering=gasOver(volume,scattering);
            // A translucent shield between mouths belongs to this interval, not the farther lens's source.
            if(delta!=vec3(0) && shieldAt>=start && shieldAt<stop)
                surface+=gasSegment(gas,ray,start,shieldAt,pixelAngle).a*delta;
        }
        if(stage>0) {
            GasEvent event=events[stage-1];
            vec3 coverLight=event.cover.rgb;
            if(!event.own && ownIndex>=stage)
                coverLight=gasOwnSurface(coverLight,ray,event.depth,false,pixelAngle);
            surface=mix(surface,coverLight,event.cover.a)+event.glow;
            // Own-world visibility was already applied at each sample. External cover replaces gas behind it.
            if(!event.own) scattering*=1.0-event.cover.a;
        }
    }
    return surface+scattering;
}

#include "gas_fixture.glsl"
// On only for a muted --gas-fixture run (SceneRenderer): everywhere else the driver drops the fixtures' second copy of
// composition before compiling.
layout(constant_id = 2) const bool GasFixtures = false;

void main()
{
    if(GasFixtures && frame.gas.budget.z>0.0) {
        outColor=gasFixtureResult();
        return;
    }
    float aspect = push.setup.y;
    float time = push.setup.z;
    int count = int(push.setup.w + 0.5);
    float pixel = 2.0 / float(textureSize(scene, 0).y);
    float pixelAngle = pixel * frame.lens.x;
    vec2 pos = ndc * vec2(aspect, 1.0);

    // The scene's alpha says what is there: 0 sky, 0.25 an asteroid, 0.375 a hull, 0.5 the pilot's own hull. Read
    // exactly, never filtered.
    ivec2 texel = ivec2(gl_FragCoord.xy);
    bool ownHull = texelFetch(scene, texel, 0).a > 0.44;

    int ownKind = int(frame.ftl.x + 0.5);
    int ownStage = int(frame.ftl.y + 0.5);
    GasData composeGas=frame.gas;
    if(ownHull && ownKind==1) {
        gasThin=1.0-clamp(frame.ftlUp.w,0.0,1.0);
        composeGas.halfSize.w*=gasThin;
    }
    bool ownPassageOn = ownStage > 0 && (ownKind == 2 || ownKind == 3);
    // Lens fallbacks need the sky/rock copies; deferred gas with shields also reads the final scene depth.
    bool lensing = count > 0 || ownPassageOn;
    float depth = (lensing || frame.gas.budget.x>0.0) ? viewDepth(texelFetch(sceneDepth, texel, 0).r) : Far;
    vec3 v = viewRay(ndc);

    // Openings of other hulls, and of the pilot's own when seen from the command view.
    vec2 bend = vec2(0.0);
    int bentBy = 0;
    vec4 cover = vec4(0.0);
    vec3 glow = vec3(0.0);
    GasEvent gasEvents[5];
    int gasEventCount=0;
    for (int i = 0; i < 4; i++)
    {
        if (i >= count)
            break;
        opening(i, v, pos, pixelAngle, time, depth, bend, bentBy, cover, glow);
        if(frame.gas.budget.x>0.0) {
            vec2 localBend=vec2(0); int localMask=0; vec4 localCover=vec4(0); vec3 localGlow=vec3(0);
            opening(i,v,pos,pixelAngle,time,depth,localBend,localMask,localCover,localGlow);
            if(localBend!=vec2(0) || localCover.a>0.0 || localGlow!=vec3(0)) {
                // opening accumulates premultiplied cover into a zero input; retain straight colour for layering.
                if(localCover.a>0.0) localCover.rgb/=localCover.a;
                gasEvents[gasEventCount++]=GasEvent(gasMouth(i,v),localBend,localCover,localGlow,localMask,i,false);
            }
        }
    }

    // The pilot's own jump, from the inside.
    float real = 1.0;
    vec3 throat = vec3(0.0);
    if (ownPassageOn)
    {
        vec2 oldBend=bend; int oldMask=bentBy; vec3 oldGlow=glow;
        real = ownPassage(ownKind, v, pos, pixelAngle, depth, ownHull, bend, bentBy, glow);
        if (real < 1.0)
            throat = ownKind == 3 ? riftThroat(v, frame.ftlAxis.w, frame.ftl.w, time, pixelAngle) : wormholeThroat(v, frame.ftlAxis.w, frame.ftl.w, time, pixelAngle);
        if(frame.gas.budget.x>0.0) {
            float at=gasMouth(4,v);
            if(at>=Far) at=0.0;
            gasEvents[gasEventCount++]=GasEvent(at,bend-oldBend,vec4(throat,1.0-real),glow-oldGlow,bentBy & ~oldMask,4,true);
        }
    }

    vec3 hdr;
    if(frame.gas.budget.x>0.0) {
        hdr=gasComposite(composeGas,gasEvents,gasEventCount,pos,aspect,pixelAngle);
    }
    else if (bend == vec2(0.0))
    {
        hdr = texelFetch(scene, texel, 0).rgb;
    }
    else
    {
        // A bent lookup can leave the screen. Mirroring it back keeps a field of stars a field of stars, where
        // clamping would smear the edge pixels into streaks.
        vec2 lookup = 1.0 - abs(1.0 - abs((pos + bend) / vec2(aspect, 1.0) * 0.5 + 0.5));
        // A lens only images what is behind its opening. Where the bent ray lands on something nearer than an
        // opening that bent it (the hull that opened it, seen from behind, or a rock in front), or on the pilot's
        // own hull, which no jump bends, what it would see there is hidden, and the scene as it was before hulls
        // were drawn stands in; where an asteroid in that is also in front, the sky. Falling back to the unbent
        // view instead would leave a hard-edged, magnified outline of the thing in front around it. All four texels
        // the filtered lookup blends are checked, each against where the openings lie along its own ray: the ray
        // through this pixel meets a tilted opening at another depth, and would let a rock in front through.
        vec4 landedWhat = textureGather(scene, lookup, 3);
        vec4 landedDepth = textureGather(sceneDepth, lookup, 0);
        vec4 rocksAt = textureGather(rocksDepth, lookup, 0);
        vec2 size = vec2(textureSize(scene, 0));
        vec2 corner = floor(lookup * size - 0.5);
        bool sceneClear = true;
        bool rocksClear = true;
        for (int i = 0; i < 4; i++)
        {
            // The gather's order: (0, 1), (1, 1), (1, 0), (0, 0) from the corner, clamped as the sampler clamps.
            vec2 gathered = clamp(corner + vec2(i == 1 || i == 2 ? 1.0 : 0.0, i < 2 ? 1.0 : 0.0), vec2(0.0), size - 1.0);
            float mouth = mouthDepth(bentBy, viewRay((gathered + 0.5) / size * 2.0 - 1.0));
            sceneClear = sceneClear && landedWhat[i] < 0.44 && viewDepth(landedDepth[i]) >= mouth;
            rocksClear = rocksClear && viewDepth(rocksAt[i]) >= mouth;
        }
        hdr = sceneClear ? texture(scene, lookup).rgb : rocksClear ? texture(rocks, lookup).rgb : texture(sky, lookup).rgb;
    }
    if(frame.gas.budget.x<1.0) {
        hdr = mix(hdr, cover.rgb, cover.a);
        hdr = mix(throat, hdr, real) + glow;
    }

    // Hyperspace covers everything but the pilot's own hull, which rides through it, and the light of their own
    // shield, which rides with it: where that shield is the nearest thing (mask 0.4375), what it added to the scene
    // over the copy taken just before it is lifted off, the rest tunnelled, and that light put back on top. Its bend
    // of the old world fades as the tunnel covers it (shield.frag), so at full cover only its light is left.
    float tunnel = ownKind == 1 && !ownHull ? frame.ftlUp.w : 0.0;
    if (tunnel > 0.0)
    {
        vec4 here = texelFetch(scene, texel, 0);
        vec3 ownShield = here.a > 0.41 && here.a < 0.47 ? here.rgb - texelFetch(ownBehind, texel, 0).rgb : vec3(0.0);
        vec3 shieldInHdr=ownShield;
        if(frame.gas.budget.x>0.0 && ownShield!=vec3(0))
            shieldInHdr*=gasSegment(composeGas,v,0.0,depth,pixelAngle).a;
        // Lift out the attenuated contribution, but put back the actual own light above the tunnel.
        hdr = mix(hdr - shieldInHdr, hyperspaceTunnel(v, frame.ftlAxis.w, pixelAngle), tunnel) + mix(shieldInHdr, ownShield, tunnel);
    }

    vec3 mapped = filmic(hdr * push.setup.x);
    float vignette = 1.0 - 0.22 * pow(length(ndc) * 0.75, 2.5);
    vec3 display = pow(mapped * vignette, vec3(1.0 / 2.2));

    // A little noise hides banding in the dark gradients space is made of.
    float dither = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
    outColor = vec4(display + dither / 255.0, 1.0);
}
