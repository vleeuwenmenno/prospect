#version 450
// The scan pulse: a thin spherical front that spreads from where the scanning ship was, added to the displayed image
// after tone mapping. Its own pass, drawn only while a pulse shows, over the finished scene and under the interface:
// gas, shields and jump openings are composed before it and are not changed by it. It reads the scene's depth to
// stop at what is solid and to light the asteroids and hulls the front passes over. Gas dims it through the smooth
// envelope of the nearest cloud, the one the sensors use (hosting.md), not the visible noise.
#include "frame.glsl"

// Set 1 is the tone mapper's; of it only the scene's depth is read. Reverse depth: 0 is the sky.
layout(set = 1, binding = 3) uniform sampler2D sceneDepth;

// Matches ScanPulseConstants in ScanPulse.cs.
layout(push_constant) uniform Pulse
{
    vec4 shell;      // xyz: origin relative to the camera; w: the front's radius in metres
    vec4 shape;      // x: front width in metres; y: strength 0..1; z: surface glow length behind the front; w: lattice cells
    vec4 color;      // rgb: colour; w: light where a ray crosses the front face on
    vec4 light;      // x: front light on surfaces; y: glow behind it; z: share of the light in the lattice's lines; w: rim limit
    vec4 cloud;      // xyz: nearest cloud's centre relative to the camera; w: extinction per metre times dimming, 0 none
    vec4 cloudRadii; // xyz: one over the envelope's radii; w: lattice line width as a share of a cell
} pulse;

layout(location = 0) in vec2 ndc;
layout(location = 0) out vec4 outColor;

const float Tau = 6.28318530718;
const float GoldenRatio = 1.61803398875;
const float Sky = 1e30;

// The nearest and the second nearest of a spherical Fibonacci lattice of `cells` points to the unit direction p, as
// chord lengths (Keinert et al. 2015, "Spherical Fibonacci Mapping"). The lattice's cells are close to hexagons, with
// no seam or pole, so their edges, where the two are equal, draw a faint even net over the front.
vec2 latticeDistances(vec3 p, float cells)
{
    float k = max(2.0, floor(log2(cells * Tau * 0.5 * sqrt(5.0) * (1.0 - p.z * p.z)) / log2(GoldenRatio + 1.0)));
    float fk = pow(GoldenRatio, k) / sqrt(5.0);
    vec2 f = vec2(round(fk), round(fk * GoldenRatio));
    vec2 ka = 2.0 * f / cells;
    vec2 kb = Tau * (fract((f + 1.0) * GoldenRatio) - (GoldenRatio - 1.0));
    mat2 inverseBasis = mat2(ka.y, -ka.x, kb.y, -kb.x) / (ka.y * kb.x - ka.x * kb.y);
    vec2 corner = floor(inverseBasis * vec2(atan(p.y, p.x), p.z - 1.0 + 1.0 / cells));
    float nearest = 8.0;
    float second = 8.0;
    for (int s = 0; s < 4; s++)
    {
        float id = clamp(dot(f, vec2(s & 1, s >> 1) + corner), 0.0, cells - 1.0);
        float phi = Tau * fract(id * GoldenRatio);
        float cosTheta = 1.0 - (2.0 * id + 1.0) / cells;
        float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
        vec3 q = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
        float d = dot(q - p, q - p);
        if (d < nearest) { second = nearest; nearest = d; }
        else if (d < second) second = d;
    }
    return sqrt(vec2(nearest, second));
}

// How much of the front's light falls on the net at the unit direction p from the origin: 1 on a line, the rest of
// the light spread evenly over the cells. Fades to even where the net would be finer than the pixels can show.
float lattice(vec3 p)
{
    vec2 d = latticeDistances(p, pulse.shape.w);
    float edge = d.y - d.x;
    float cell = 3.5449 / sqrt(pulse.shape.w); // sqrt(4 pi / cells): a cell's width as a chord
    float width = cell * pulse.cloudRadii.w;
    float blur = fwidth(edge);
    float line = 1.0 - smoothstep(width, width + 1.5 * blur, edge);
    float shown = clamp(1.0 - blur / (cell * 0.35), 0.0, 1.0);
    float share = pulse.light.z * shown;
    return mix(1.0, line * 2.2, share);
}

// The share of light that comes through the nearest cloud's smooth envelope from the camera to t metres along v:
// extinction times max(0, 1 - ellipsoid radius squared), integrated exactly.
float throughGas(vec3 v, float t)
{
    if (pulse.cloud.w <= 0.0) return 1.0;
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

// The light of the front where the ray v crosses it t metres out: brighter where it is seen edge on, as a thin
// glowing shell is, up to the rim limit, and laid out over the net.
float crossing(vec3 v, float t, float scene)
{
    vec3 out_ = v * t - pulse.shell.xyz;
    vec3 normal = out_ / max(pulse.shell.w, 1e-3);
    float facing = abs(dot(v, normal));
    float rim = min(1.0 / max(facing, 1e-3), pulse.light.w);
    float net = lattice(normalize(out_));
    return t > 0.0 && t < scene ? rim * net * throughGas(v, t) : 0.0;
}

void main()
{
    vec3 v = normalize(frame.cameraForward.xyz + frame.cameraRight.xyz * ndc.x * frame.lens.x * frame.lens.y - frame.cameraUp.xyz * ndc.y * frame.lens.x);
    float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    float scene = z > 0.0 ? frame.viewProjection[3][2] / z / dot(v, frame.cameraForward.xyz) : Sky;

    vec3 c = pulse.shell.xyz;
    float radius = pulse.shell.w;
    float width = pulse.shape.x;

    // Where the ray crosses the front's sphere; just outside it, a ray that misses still catches the rim's edge,
    // softly, so the outline is never a hard step.
    float along = dot(v, c);
    float closest2 = max(dot(c, c) - along * along, 0.0);
    float h = radius * radius - closest2;
    float root = sqrt(max(h, 0.0));
    float front = crossing(v, along - root, scene) + crossing(v, along + root, scene);
    float miss = sqrt(closest2) - radius;
    float edge = h < 0.0 && along > 0.0 && along < scene ? pulse.light.w * exp(-miss * miss / (0.25 * width * width)) * throughGas(v, along) : 0.0;
    float shellLight = pulse.color.w * (h >= 0.0 ? front : edge);

    // Asteroids and hulls: a band where the front passes over them, laid out over the net, and a faint glow behind.
    // Worked out for the sky too and then dropped, so the net's derivatives stay in uniform control flow.
    vec3 at = v * min(scene, 1e7) - c;
    float r = length(at);
    float off = (r - radius) / width;
    float band = exp(-off * off);
    float behind = r < radius ? exp(-(radius - r) / max(pulse.shape.z, 1e-3)) : 0.0;
    float surface = (pulse.light.x * band * lattice(at / max(r, 1e-3)) + pulse.light.y * behind) * throughGas(v, scene);
    float surfaceLight = scene < Sky ? surface : 0.0;

    outColor = vec4(pulse.color.rgb * (shellLight + surfaceLight) * pulse.shape.y, 0.0);
}
