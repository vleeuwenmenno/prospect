#version 450
#include "frame.glsl"
#include "hull_clip.glsl"

// What the scene held just before the shields were drawn, colour and depth, copied for this pass alone. The shell
// bends the view through it a little; where the bent lookup would land on something in front of the shield, it
// does not bend there, so nothing in front is ever pulled onto the shield.
layout(set = 1, binding = 0) uniform sampler2D behind;
layout(set = 1, binding = 1) uniform sampler2D behindDepth;

layout(location = 0) in vec3 worldNormal;
layout(location = 1) in vec3 worldPosition;
layout(location = 2) in vec3 localPosition;
layout(location = 3) flat in vec4 look;
layout(location = 4) flat in vec4 ripples[4];
layout(location = 8) flat in vec4 rippleStrength;
layout(location = 9) flat in int cut;
layout(location = 10) flat in int own;
layout(location = 0) out vec4 outColor;

// How shields look, from content/visuals/shields.json (ShieldLookConstants).
layout(push_constant) uniform Look
{
    vec4 calm;      // rgb: the calm colour; a: the rim's glow
    vec4 strained;  // rgb: the colour near breaking; a: pixels the rim bends what is behind
    vec4 collapse;  // rgb: the collapse's colour
    vec4 ripple;    // x: metres a second the ring spreads, y: metres across it, z: seconds it lasts
} shieldLook;

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

float ripple(vec4 at, float strength)
{
    float age = at.w;
    float seconds = shieldLook.ripple.z;
    if (strength <= 0.0 || age < 0.0 || age >= seconds)
        return 0.0;
    float d = length(localPosition - at.xyz);
    float ring = (d - age * shieldLook.ripple.x) / shieldLook.ripple.y;
    float life = 1.0 - age / seconds;
    // A ring that spreads and fades, and a brief flash where it struck.
    return strength * life * life * (exp(-ring * ring) + 1.5 * exp(-d * d * 0.6) * exp(-age * 9.0));
}

void main()
{
    if (hullCut(cut, own != 0, worldPosition))
        discard;

    float time = frame.dust.w;
    float online = look.x;
    float unstable = look.y;
    float collapse = look.z;

    vec3 n = normalize(worldNormal);
    vec3 view = normalize(-worldPosition);
    float facing = abs(dot(n, view));
    float rim = pow(1.0 - facing, 2.5);

    // A slow drift of noise over the shell: the shimmer of the field.
    float drift = noise(localPosition * 0.8 + vec3(0.0, time * 0.3, time * 0.17));
    float fine = noise(localPosition * 2.6 - vec3(time * 0.5, 0.0, time * 0.4));
    float field = drift * 0.65 + fine * 0.35;

    float hit = ripple(ripples[0], rippleStrength.x) + ripple(ripples[1], rippleStrength.y)
        + ripple(ripples[2], rippleStrength.z) + ripple(ripples[3], rippleStrength.w);

    // Near breaking, broad slow waves roll across the whole shell, never a flicker.
    float wave = 0.5 + 0.5 * sin(dot(localPosition, vec3(0.55, 0.9, 0.4)) * 1.1 - time * 1.6 + field * 2.5);
    float waver = unstable * (0.35 + 0.65 * wave);

    // Coming online, the field fills in through the noise; collapsing, it burns away through it.
    float present = online > 0.0 ? smoothstep(0.0, 0.25, online - (1.0 - field) * 0.75) : 0.0;
    float burning = collapse > 0.0 ? (1.0 - smoothstep(collapse - 0.15, collapse + 0.05, field)) * (1.0 - collapse) : 0.0;

    // Where the shield is not there at all (not yet faded in, or burnt away) nothing is written: no light, no
    // bend, no depth and no lens mark, so nothing drawn later and no jump lens takes it for something solid.
    if (max(present, burning) <= 0.001)
        discard;

    // Muted cyan, a little stronger and wavering near breaking; red as it collapses.
    vec3 tint = mix(shieldLook.calm.rgb, shieldLook.strained.rgb, unstable);
    float glow = present * (0.025 + shieldLook.calm.a * rim + 0.05 * field * rim + 0.10 * waver * (0.4 + rim)) + present * hit * 0.55;
    vec3 light = tint * glow + shieldLook.collapse.rgb * burning * (0.35 + 0.8 * rim) * 1.4;

    // The bend: along the shell's normal as seen on screen, strongest at the rim and in a ripple, a few pixels at most.
    vec2 size = frame.lens.zw;
    vec2 across = vec2(dot(n, frame.cameraRight.xyz), -dot(n, frame.cameraUp.xyz));
    float pixels = (0.6 + shieldLook.strained.a * rim + 3.0 * min(hit, 1.0) + 1.5 * waver) * (field - 0.35) * 2.0 * max(present, burning);
    vec2 here = gl_FragCoord.xy / size;
    vec2 there = (gl_FragCoord.xy + across * pixels) / size;
    // Reverse depth: greater is nearer. Any of the four texels the lookup blends that is in front of the shell
    // cancels the bend here.
    vec4 nearer = textureGather(behindDepth, there, 0);
    if (max(max(nearer.x, nearer.y), max(nearer.z, nearer.w)) > gl_FragCoord.z)
        there = here;
    vec3 bent = texture(behind, there).rgb - texture(behind, here).rgb;

    // The pilot's own shield in their hyperspace tunnel bends the old world only as much as the tunnel still lets
    // it be seen, by the coverage the tone mapper tunnels with, so the light it keeps there is its own.
    if (own != 0 && int(frame.ftl.x + 0.5) == 1)
        bent *= 1.0 - frame.ftlUp.w;

    // Added onto the scene: the bend's difference moves what is behind, the light adds on top. The alpha is the
    // lens mask, which only the second pass writes, by maximum and with the pilot's own shield drawn last: so it is
    // the nearest shield's, 0.375 another hull's and 0.4375 the pilot's own (ShieldComposite.OwnShieldMask), and the
    // own hull's 0.5 under it stays. Below 0.44 both count as "not the pilot's own hull" for the lenses.
    outColor = vec4(bent + light, own != 0 ? 0.4375 : 0.375);
}
