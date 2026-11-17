#version 450
#include "frame.glsl"

// Per mining laser beam (LaserInstance, built by LaserBeamLook.Instance): everything is camera-relative, in metres.
layout(location = 0) in vec4 fromCore;   // xyz: the emitter, w: the core's width
layout(location = 1) in vec4 toState;    // xyz: where the beam ends, w: 1 when it cuts a rock, 0 when it fires into space
layout(location = 2) in vec4 coreColor;  // rgb: linear core colour, w: opacity
layout(location = 3) in vec4 glowColor;  // rgb: linear glow colour, w: the glow's width
layout(location = 4) in vec4 motion;     // x: seed 0..1, y: pulse speed (m/s), z: metres between pulses, w: where a free beam starts to fade (share)
layout(location = 5) in vec4 pulse;      // x: pulse depth, y: thinnest the beam is drawn (pixels), z: hit glow radius (m), w: hit glow strength
layout(location = 6) in vec4 hitColor;   // rgb: linear hit glow colour, w: sparks
layout(location = 7) in vec4 spark;      // rgb: linear spark colour, w: a streak's length (m)
layout(location = 8) in vec4 sparkShape; // x: streak width (m), y: speed (m/s), z: seconds a spark lives

layout(location = 0) out vec2 uv;
// x: what is drawn (0 beam, 1 hit glow, 2 spark), y: the core's share of the beam's half width, z: brightness kept when
// the beam is drawn wider than it is, w: 1 when the beam cuts.
layout(location = 1) flat out vec4 kindInfo;
layout(location = 2) flat out vec4 colA;
layout(location = 3) flat out vec4 colB;
// x: metres from the emitter, y: share of the beam's length.
layout(location = 4) out vec2 alongInfo;
layout(location = 5) flat out vec4 motionOut;
layout(location = 6) flat out vec4 pulseOut;
layout(location = 7) flat out vec4 hitOut;

// A quad's six corners as (along, across).
const vec2 corners[6] = vec2[](vec2(0, -1), vec2(1, -1), vec2(1, 1), vec2(0, -1), vec2(1, 1), vec2(0, 1));
const int BeamVertices = 6;
const int GlowFirst = 6;
const int SparkFirst = 12;

float hash(float n) { return fract(sin(n * 12.9898 + 78.233) * 43758.5453); }

// How many metres one pixel covers at a point.
float pixelMetres(vec3 p)
{
    float depth = max(dot(p, frame.cameraForward.xyz), 0.05);
    return 2.0 * frame.lens.x * depth / max(frame.lens.w, 1.0);
}

// A ribbon between two points that faces the camera.
vec3 ribbonSide(vec3 axis, vec3 at)
{
    vec3 ray = at / max(length(at), 1e-3);
    vec3 side = cross(axis, ray);
    float sl = length(side);
    return sl > 1e-3 ? side / sl : normalize(cross(axis, frame.cameraUp.xyz));
}

void hide()
{
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    uv = vec2(0.0);
    kindInfo = vec4(-1.0);
    colA = colB = hitOut = motionOut = pulseOut = vec4(0.0);
    alongInfo = vec2(0.0);
}

void main()
{
    int vi = gl_VertexIndex;
    float time = frame.dust.w;
    vec3 from = fromCore.xyz;
    vec3 to = toState.xyz;
    vec3 delta = to - from;
    float len = max(length(delta), 1e-4);
    vec3 axis = delta / len;
    bool cutting = toState.w > 0.5;
    motionOut = motion;
    pulseOut = pulse;
    colA = coreColor;
    colB = glowColor;
    hitOut = vec4(hitColor.rgb, pulse.w);

    if (vi < BeamVertices)
    {
        vec2 c = corners[vi];
        vec3 at = mix(from, to, c.x);
        float glowHalf = 0.5 * glowColor.w;
        float halfWidth = max(glowHalf, 0.5 * pulse.y * pixelMetres(at));
        vec3 point = at + ribbonSide(axis, at) * (c.y * halfWidth);
        gl_Position = frame.viewProjection * vec4(point, 1.0);
        uv = vec2(c.y, c.x);
        float coreFraction = clamp(0.5 * fromCore.w / halfWidth, 0.12, 1.0);
        kindInfo = vec4(0.0, coreFraction, max(glowHalf / halfWidth, 0.35), cutting ? 1.0 : 0.0);
        alongInfo = vec2(c.x * len, c.x);
        return;
    }

    if (!cutting)
    {
        hide();
        return;
    }

    if (vi < SparkFirst)
    {
        // The glow where the beam cuts: a disc facing the camera, drawn a little toward it so the rock does not bury half of it.
        vec2 c = corners[vi - GlowFirst];
        float flicker = 0.88 + 0.12 * sin(time * 53.0 + motion.x * 31.0) * sin(time * 27.0 + motion.x * 7.0);
        float radius = pulse.z * flicker;
        vec3 centre = to - normalize(to) * (pulse.z * 0.35);
        // The quad is the pointer's corner pair (along, across) turned into the camera's right and up.
        vec2 q = vec2(c.x * 2.0 - 1.0, c.y);
        vec3 point = centre + (frame.cameraRight.xyz * q.x + frame.cameraUp.xyz * q.y) * radius;
        gl_Position = frame.viewProjection * vec4(point, 1.0);
        uv = q;
        kindInfo = vec4(1.0, 0.0, flicker, 1.0);
        alongInfo = vec2(0.0);
        return;
    }

    int k = (vi - SparkFirst) / 6;
    if (float(k) >= hitColor.w)
    {
        hide();
        return;
    }

    // One spark: a streak that flies out of the cut, back over the beam's side, slows as it goes and dies. Every cycle
    // it takes a new direction, from hashes of its number and the cycle, so nothing is kept between frames.
    vec2 c = corners[(vi - SparkFirst) % 6];
    float life = max(sparkShape.z, 1e-3);
    float offset = hash(float(k) * 1.7 + motion.x * 19.0);
    float cycles = time / life + offset;
    float cycle = floor(cycles);
    float phase = cycles - cycle;
    float s = float(k) * 13.7 + cycle * 7.31 + motion.x * 101.0;
    // A direction in the hemisphere facing back along the beam, away from the surface it cuts.
    vec3 reference = abs(axis.y) < 0.9 ? vec3(0, 1, 0) : vec3(1, 0, 0);
    vec3 t1 = normalize(cross(axis, reference));
    vec3 t2 = cross(axis, t1);
    float turn = 6.2831853 * hash(s);
    float spread = 0.35 + 0.85 * hash(s + 3.1);
    vec3 dir = normalize(-axis * (0.3 + 0.7 * hash(s + 5.9)) + (t1 * cos(turn) + t2 * sin(turn)) * spread);
    float speed = sparkShape.y * (0.5 + 0.8 * hash(s + 9.3));
    float travelled = speed * life * (phase - 0.5 * phase * phase) * 2.0;
    vec3 head = to - normalize(to) * (pulse.z * 0.15) + dir * travelled;
    float streak = min(spark.w * (1.0 - 0.5 * phase), travelled);
    vec3 tail = head - dir * streak;
    vec3 spine = head - tail;
    vec3 spineAxis = length(spine) > 1e-4 ? normalize(spine) : dir;
    vec3 at = mix(tail, head, c.x);
    float halfWidth = max(0.5 * sparkShape.x, 0.5 * pulse.y * pixelMetres(at));
    vec3 point = at + ribbonSide(spineAxis, at) * (c.y * halfWidth);
    gl_Position = frame.viewProjection * vec4(point, 1.0);
    uv = vec2(c.y, c.x);
    float fade = (1.0 - phase) * (1.0 - phase);
    kindInfo = vec4(2.0, 0.0, fade * max(0.5 * sparkShape.x / halfWidth, 0.35), 1.0);
    colA = vec4(spark.rgb, 1.0);
    alongInfo = vec2(0.0, c.x);
}
