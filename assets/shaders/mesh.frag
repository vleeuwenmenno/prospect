#version 450
#include "frame.glsl"
#include "hull_clip.glsl"

layout(location = 0) in vec3 worldNormal;
layout(location = 1) in vec3 worldPosition;
layout(location = 2) in vec3 localPosition;
layout(location = 3) in vec4 color;
layout(location = 4) flat in int cut;
layout(location = 5) in float metal;
layout(location = 6) flat in vec3 paint;
layout(location = 0) out vec4 outColor;
layout(location = 7) flat in uvec4 markMask;
layout(location = 8) flat in vec4 oreFigures;
layout(location = 9) flat in float crystal;
// How much of its full colour a light that follows thrust gains now (mesh.vert); 0 for every other light.
layout(location = 10) flat in float thrustLit;
struct RockMark { vec4 centerRadius; vec4 normalScorch; vec4 glow; };
layout(std430, set = 0, binding = 3) readonly buffer RockMarks { RockMark marks[]; } rock;

// On for the hull pipeline: a hull going through a jump's opening is cut away where it is past it, before anything
// is stored, so the scene, its depth and the lenses that read them never see the part that is gone. Off for
// asteroids, which keep their early depth test.
layout(constant_id = 0) const bool CutHulls = false;

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

// Shade the actual rock fragments: no decal plane, depth copy, hatch texture or detached geometry.
vec3 exhaustMarks(vec3 lit, vec3 normal)
{
    for (int group = 0; group < 4; group++)
    {
        uint bits = markMask[group];
        while (bits != 0u)
        {
            int bit = findLSB(bits);
            bits &= bits - 1u;
            RockMark mark = rock.marks[group * 16 + bit];
            vec3 delta = worldPosition - mark.centerRadius.xyz;
            float r = length(delta) / mark.centerRadius.w;
            float edge = (1.0 - smoothstep(0.15, 1.0, r)) * smoothstep(0.0, 0.35, dot(normal, mark.normalScorch.xyz));
            float shimmer = mark.glow.w >= 0.0 ? 0.7 + 0.3 * sin(mark.glow.w * 3.0 + r * 8.0) : 1.0;
            lit = lit * (1.0 - edge * mark.normalScorch.w) + mark.glow.rgb * (edge * shimmer);
        }
    }
    return lit;
}

void main()
{
    // Cut before anything is stored, so the scene, its depth and the lenses that read them never see what is gone.
    if (CutHulls && hullCut(cut, color.a > 1.5, worldPosition))
        discard;

    // Sparks belong to the outside scene, without the pilot hull's exemption from jump effects.
    if (color.a < -0.5) { outColor = vec4(color.rgb, 0.375); return; }

    vec3 n = normalize(worldNormal);
    vec3 view = normalize(-worldPosition);
    vec3 albedo = color.rgb;

    // Ice (material 0.25, WorldView.MaterialIce): an asteroid to everything that reads the scene, lit as a clear,
    // faceted crystal: flat faces, light wrapping into it and shining through where the star is behind it, streaks
    // along the shard, a sharp glint and a bright rim.
    if (color.a > 0.125 && color.a < 0.5)
    {
        vec3 facet = normalize(cross(dFdx(worldPosition), dFdy(worldPosition)));
        if (dot(facet, n) < 0.0)
            facet = -facet;
        vec3 sun = frame.sunDirection.xyz;
        // Streaks run along the shard (its local x): long and thin in the noise, so they read as grain in clear ice.
        float streak = noise(localPosition * vec3(2.5, 38.0, 38.0)) * 0.6 + noise(localPosition * vec3(7.0, 90.0, 90.0)) * 0.4;
        vec3 body = albedo * (0.8 + 0.4 * streak);
        float facing = max(dot(facet, view), 0.0);
        float wrap = max((dot(facet, sun) + 0.4) / 1.4, 0.0);
        // Seen against the star, light comes through the ice, bluer than it went in.
        float through = pow(max(dot(-view, sun), 0.0), 6.0) * (0.35 + 0.65 * (1.0 - facing));
        vec3 halfway = normalize(sun + view);
        float glint = pow(max(dot(facet, halfway), 0.0), 220.0) * step(0.0, dot(facet, sun));
        float fresnel = 0.04 + 0.96 * pow(1.0 - facing, 5.0);
        // Light scattered inside the crystal reaches its shaded side too, bluer than it went in, so a shard in shadow
        // still reads as ice rather than as a dark rock.
        vec3 inside = vec3(0.45, 0.68, 1.0) * frame.sunColor.rgb * (0.16 + 0.1 * streak) * (1.0 - 0.5 * wrap);
        vec3 ice = body * (frame.sunColor.rgb * wrap * 0.8 + vec3(0.03, 0.045, 0.07)) + body * inside
            + vec3(0.35, 0.6, 0.85) * frame.sunColor.rgb * through * 1.2 * (0.6 + 0.4 * streak)
            + frame.sunColor.rgb * glint * 2.0
            + vec3(0.4, 0.62, 0.9) * fresnel * (0.25 + 0.6 * wrap);
        vec3 passageIce = frame.ftl.x > 2.5 ? vec3(0.16, 0.06, 0.015) : vec3(0.05, 0.08, 0.14);
        ice += body * passageIce * frame.ftlShape.w;
        if (frame.gridPlane.z > 0.01)
        {
            float toPlane = abs(worldPosition.y - frame.gridPlane.x);
            float width = max(fwidth(worldPosition.y) * 1.6, 0.03);
            float cutLine = 1.0 - smoothstep(width, width * 2.0, toPlane);
            float below = worldPosition.y < frame.gridPlane.x ? 0.55 : 1.0;
            ice = mix(ice * mix(1.0, below, frame.gridPlane.z), vec3(1.0, 0.69, 0.13) * 1.8, cutLine * frame.gridPlane.z);
        }
        outColor = vec4(exhaustMarks(ice, n), 0.25);
        return;
    }

    // A loose ore chunk (WorldView.OreChunks): a broken lump of bare rock with the ore showing as veins, more of them
    // the richer the chunk, and as crystals standing out of its break faces. color.rgb is the ore's own colour.
    bool ore = color.a > 0.02 && color.a < 0.125;
    float oreMask = 0.0;
    if (ore)
    {
        float grade = clamp((color.a - 0.03) / 0.09, 0.0, 1.0);
        float mottling = noise(localPosition * 4.0) * 0.6 + noise(localPosition * 13.0) * 0.4;
        // Each break face is a little lighter or darker than its neighbours, so the broken shape reads.
        float facetTone = 0.8 + 0.4 * hash(floor(normalize(worldNormal) * 5.0 + 0.5) + 3.0);
        vec3 rock = vec3(0.27, 0.25, 0.225) * (0.7 + 0.5 * mottling) * facetTone;
        // Veins: thin lines where a warped noise passes through its middle value, wider the richer the chunk.
        float cover = mix(oreFigures.y, oreFigures.z, grade);
        vec3 p = localPosition * oreFigures.x;
        p += 0.7 * vec3(noise(p * 0.6), noise(p * 0.6 + 3.1), noise(p * 0.6 + 6.2));
        float ridge = abs(2.0 * noise(p) - 1.0);
        float width = cover * 0.5;
        oreMask = 1.0 - smoothstep(width, width + 0.025, ridge);
        oreMask = crystal > 0.5 ? 1.0 : oreMask;
        vec3 body = color.rgb * (0.85 + 0.3 * noise(localPosition * 12.0));
        albedo = mix(rock, body, oreMask);
    }

    if (color.a < 0.5)
    {
        // Rock: mottled, with the facets of the mesh showing through for a rough, broken surface.
        // The screen-space derivative gives the facet's normal up to its sign; the smooth normal settles the sign.
        vec3 facet = normalize(cross(dFdx(worldPosition), dFdy(worldPosition)));
        if (dot(facet, n) < 0.0)
            facet = -facet;
        n = normalize(mix(n, facet, ore ? 1.0 : 0.4));
        if (!ore)
        {
            float mottling = noise(localPosition * 9.0) * 0.6 + noise(localPosition * 31.0) * 0.4;
            albedo *= 0.55 + 0.75 * mottling;
        }
    }
    else if (paint.r >= 0.0 && metal < -1.5)
    {
        // Emissive paint (such as a tractor turret's recessed lens): glows softly regardless of star angle.
        // A light that follows thrust (a cold gas array's bands) gains its own hue at full strength, times thrustLit,
        // while its nozzle fires: a band that is all but dark at idle still lights up.
        vec3 hue = paint / max(max(paint.r, paint.g), max(paint.b, 1.0 / 31.0));
        vec3 glow = (mix(paint, albedo, 0.08) + hue * thrustLit) * 1.9;
        outColor = vec4(glow, color.a > 1.5 ? 0.5 : color.a > 0.5 ? 0.375 : 0.25);
        return;
    }
    else if (paint.r >= 0.0)
    {
        // A tank's painted vessel: its own colour, a touch of the instance's so the editor's tones still show, no
        // panel lines.
        albedo = mix(paint, albedo, 0.12);
    }
    else if (metal >= 0.0)
    {
        // Bare metal of a nozzle or its collar: grey with a touch of the hull's colour, no panel lines.
        albedo = mix(vec3(metal), albedo * metal * 1.6, 0.2);
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
    // Bare metal shines with its shade: a flat-shaded facet lights up whole, so a dark throat or nozzle lining must
    // not flash white when it lines up with the star.
    float specular = metal >= 0.0 ? pow(max(dot(n, halfway), 0.0), 40.0) * 0.3 * (0.15 + metal)
        : paint.r >= 0.0 ? pow(max(dot(n, halfway), 0.0), 32.0) * 0.2
        : pow(max(dot(n, halfway), 0.0), 48.0) * (color.a < 0.5 ? 0.03 : 0.35);
    float rim = pow(1.0 - max(dot(n, view), 0.0), 3.0);

    vec3 light;
    if (color.a < 0.5)
        light = frame.sunColor.rgb * diffuse + vec3(0.012, 0.016, 0.026) * fill;
    else
    {
        // Hulls: the star is held below the tone curve's shoulder, so sunlit facets keep their differences instead of
        // all reading near white, and a cool fill that turns with the facet (strongest facing away from the star)
        // keeps the shaded side's shapes readable without lifting it to a flat grey.
        light = frame.sunColor.rgb * diffuse * 0.72 + vec3(0.046, 0.054, 0.07) * (0.15 + 0.85 * fill * fill);
    }
    vec3 lit = albedo * light
        + frame.sunColor.rgb * specular * diffuse
        + vec3(0.02, 0.03, 0.05) * rim;
    if (ore && oreMask > 0.0)
    {
        // Ore is metallic and glints: a broad sheen, a sharp flash as a tumbling facet lines up with the star, and a
        // faint glow of its own that twinkles, so it reads against the rock even in shadow.
        float facing = max(dot(n, halfway), 0.0);
        float sheen = pow(facing, mix(18.0, 40.0, crystal)) * 0.5;
        float flash = pow(facing, 220.0) * 6.0;
        float phase = hash(floor(localPosition * 6.0)) * 6.2831;
        float glow = 0.05 + 0.04 * sin(frame.dust.w * 2.5 + phase);
        vec3 shine = frame.sunColor.rgb * (sheen + flash) * diffuse * oreFigures.w;
        lit += color.rgb * oreMask * (shine + glow * oreFigures.w * (0.6 + 0.8 * crystal) + 0.3 * crystal * rim);
    }
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
    if (color.a < 0.5 && !ore) lit = exhaustMarks(lit, n);
    outColor = vec4(lit, color.a > 1.5 ? 0.5 : color.a > 0.5 ? 0.375 : 0.25);
}
