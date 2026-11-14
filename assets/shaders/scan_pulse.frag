#version 450
// The scan pulse: a front that spreads from where the scanning ship was, and shows only where it touches something.
// Asteroids and hulls get a crisp edge where the front crosses them and a grid in a band behind it; the larger ones
// on screen instead get a ripple of the same look that starts where the front touched them and crawls over them to
// their far side. Gas lights up with the grid where the front passes through it. Empty space shows nothing. Added to the displayed image
// after tone mapping, in its own pass, drawn only while a pulse shows: gas, shields and jump openings are composed
// before it and are not changed by it. Gas is the smooth envelope of one cloud, the one the sensors use (hosting.md),
// not the visible noise: it lights the front inside the cloud and dims what lies behind gas.
#include "frame.glsl"

// Set 1 is the tone mapper's; of it only the scene's depth is read. Reverse depth: 0 is the sky.
layout(set = 1, binding = 3) uniform sampler2D sceneDepth;

// Matches ScanPulseConstants in ScanPulse.cs.
layout(push_constant) uniform Pulse
{
    vec4 shell;      // xyz: origin relative to the camera; w: the front's radius in metres
    vec4 band;       // x: band depth behind the front in metres; y: strength 0..1; z: finest grid spacing in metres;
                     // w: smallest grid cell in pixels
    vec4 color;      // rgb: colour; w: line width in pixels
    vec4 light;      // x: front edge on surfaces; y: grid in the band; z: even band fill; w: grid in gas
    vec4 cloud;      // xyz: the cloud's centre relative to the camera; w: extinction per metre times dimming
    vec4 cloudRadii; // xyz: one over its envelope's radii, zero for no cloud; w: unused
    vec4 gas;        // x: the grid's spacing in gas in metres; y: even light in gas; z: how many ripples
} pulse;

// The surface ripples (ScanWaves): objects the front has touched, the largest on screen first. Matches
// ScanWaveObject in ScanPulse.cs; MaxWaves matches ScanWaves.Capacity.
const int MaxWaves = 32;
struct WaveObject
{
    vec4 sphere; // xyz: centre relative to the camera; w: radius in metres
    vec4 wave;   // x: how far the ripple has crawled over the surface from the point touched; y: its band's width,
                 // both in metres; z: its strength
};
layout(set = 2, binding = 0) uniform Waves { WaveObject objects[MaxWaves]; } waves;

layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;

const float Sky = 1e30;

// A thin line where the field f crosses a whole number, `pixels` wide whatever the distance.
float lineAt(float distance, float perPixel)
{
    float half_ = 0.5 * pulse.color.w * perPixel;
    return 1.0 - smoothstep(half_, half_ + perPixel, distance);
}

// The grid of planes a spacing apart along each world axis, anchored at the pulse's origin, at p (metres from the
// origin) whose change across a pixel is `step`. Where a cell would be under the smallest size on screen the spacing
// doubles, the finer lines fading out as it does, so the grid stays crisp near and far. The planes lie half the
// finest spacing off the origin, so none runs through the scanning ship (where it would sit on the view's centre
// like a crosshair); coarser planes stay among the finer ones.
float grid(vec3 p, vec3 step, float finest)
{
    float metres = max(length(step) * 0.7071 * pulse.band.w, 1e-4);
    float level = max(log2(metres / finest), 0.0);
    float coarse = floor(level);
    float share = level - coarse;
    float spacing = finest * exp2(coarse);
    float lines = 0.0;
    for (int k = 0; k < 2; k++)
    {
        float s = spacing * float(1 + k);
        vec3 u = (p + 0.5 * finest) / s;
        vec3 d = abs(fract(u - 0.5) - 0.5);
        vec3 w = step / s;
        float l = max(lineAt(d.x, w.x), max(lineAt(d.y, w.y), lineAt(d.z, w.z)));
        lines = max(lines, k == 0 ? l * (1.0 - share) : l);
    }
    return lines;
}

// The share of light that comes through the cloud's smooth envelope from the camera to t metres along v:
// extinction times max(0, 1 - ellipsoid radius squared), integrated exactly.
float throughGas(vec3 v, float t)
{
    if (pulse.cloud.w <= 0.0 || pulse.cloudRadii.x <= 0.0) return 1.0;
    vec3 a = -pulse.cloud.xyz * pulse.cloudRadii.xyz;
    vec3 b = v * pulse.cloudRadii.xyz;
    float qa = dot(b, b);
    float qb = dot(a, b);
    float qc = dot(a, a);
    float disc = qb * qb - qa * (qc - 1.0);
    if (disc <= 0.0) return 1.0;
    float root = sqrt(disc);
    float lo = max((-qb - root) / qa, 0.0);
    float hi = min((-qb + root) / qa, t);
    if (hi <= lo) return 1.0;
    float depth = (1.0 - qc) * (hi - lo) - qa * (hi * hi * hi - lo * lo * lo) / 3.0 - qb * (hi * hi - lo * lo);
    return exp(-pulse.cloud.w * max(depth, 0.0));
}

// The envelope's density at x relative to the camera: 1 at the core, 0 at its edge and beyond.
float gasAt(vec3 x)
{
    if (pulse.cloudRadii.x <= 0.0) return 0.0;
    vec3 q = (x - pulse.cloud.xyz) * pulse.cloudRadii.xyz;
    return max(1.0 - dot(q, q), 0.0);
}

// The front where the ray v crosses it t metres out, in gas: a coarser grid on the front, whose planes cut the
// sphere in circles like lines of latitude and longitude, and a faint even glow, both by density.
// Computed for every pixel and dropped after, so the grid's derivatives stay in uniform control flow.
float gasCrossing(vec3 v, float t, bool crosses, float scene)
{
    vec3 x = v * t;
    vec3 p = x - pulse.shell.xyz;
    float lit = (pulse.light.w * grid(p, fwidth(p), pulse.gas.x) + pulse.gas.y) * gasAt(x);
    return crosses && t > 0.0 && t < scene ? lit * throughGas(v, t) : 0.0;
}

void main()
{
    vec3 v = normalize(frame.cameraForward.xyz + frame.cameraRight.xyz * ndc.x * frame.lens.x * frame.lens.y - frame.cameraUp.xyz * ndc.y * frame.lens.x);
    float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    float scene = z > 0.0 ? frame.viewProjection[3][2] / z / dot(v, frame.cameraForward.xyz) : Sky;
    float radius = pulse.shell.w;

    // Asteroids and hulls: a crisp edge where the front crosses them, and behind it a band that shows the grid,
    // brightest at the front and gone at the band's depth.
    // A pixel across an outline, where neighbours lie at very different depths, takes none: its derivatives are
    // not the surface's, and would light the whole outline.
    vec3 p = v * min(scene, 1e7) - pulse.shell.xyz;
    vec3 change = fwidth(p);
    float r = length(p);
    float pixel = 2.0 * frame.lens.x / frame.lens.w;
    bool outline = length(change) > 24.0 * pixel * min(scene, 1e7);
    float edge = lineAt(abs(r - radius), fwidth(r));
    float behind = (radius - r) / pulse.band.x;
    float fade = behind >= 0.0 && behind <= 1.0 ? 1.0 - behind : 0.0;
    float lines = grid(p, change, pulse.band.z);
    float surface = pulse.band.y * (pulse.light.x * edge + (pulse.light.y * lines + pulse.light.z) * fade);

    // An object with a ripple: the ripple, not the front's band, crawls over it from the point the front touched
    // first (the surface point nearest the origin) to its far side. Its distance over the surface is the angle
    // around the object's centre times its radius. A pixel belongs to the object whose sphere it lies deepest in.
    vec3 at = v * min(scene, 1e7);
    int owner = -1;
    float deepest = 1.69; // within 1.3 radii: rocks are lumpy, and stand out of their nominal sphere
    int count = min(int(pulse.gas.z), MaxWaves);
    for (int i = 0; i < count; i++)
    {
        vec4 sphere = waves.objects[i].sphere;
        vec3 d = at - sphere.xyz;
        float depth = dot(d, d) / (sphere.w * sphere.w);
        if (depth < deepest) { deepest = depth; owner = i; }
    }
    WaveObject object = waves.objects[max(owner, 0)];
    vec3 outward = normalize(at - object.sphere.xyz);
    vec3 touched = normalize(pulse.shell.xyz - object.sphere.xyz);
    float over = object.sphere.w * acos(clamp(dot(outward, touched), -1.0, 1.0));
    float crawl = object.wave.x;
    float ring = lineAt(abs(over - crawl), fwidth(over)) * step(over, crawl + 1.0);
    float trail = (crawl - over) / object.wave.y;
    float wake = trail >= 0.0 && trail <= 1.0 ? 1.0 - trail : 0.0;
    float ripple = object.wave.z * (pulse.light.x * ring + (pulse.light.y * lines + pulse.light.z) * wake);
    surface = owner >= 0 ? ripple : surface;

    float light = scene < Sky && !outline ? surface * throughGas(v, scene) : 0.0;

    // Gas: where the front passes through the cloud, nearer than anything solid.
    vec3 c = pulse.shell.xyz;
    float along = dot(v, c);
    float h = radius * radius - max(dot(c, c) - along * along, 0.0);
    float root = sqrt(max(h, 0.0));
    light += pulse.band.y * (gasCrossing(v, along - root, h > 0.0, scene) + gasCrossing(v, along + root, h > 0.0, scene));

    outColor = vec4(pulse.color.rgb * light, 0.0);
}
