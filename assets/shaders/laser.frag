#version 450
#include "frame.glsl"

// What laser.vert hands over: see there.
layout(location = 0) in vec2 uv;
layout(location = 1) flat in vec4 kindInfo;
layout(location = 2) flat in vec4 colA;
layout(location = 3) flat in vec4 colB;
layout(location = 4) in vec2 alongInfo;
layout(location = 5) flat in vec4 motionIn;
layout(location = 6) flat in vec4 pulseIn;
layout(location = 7) flat in vec4 hitIn;
layout(location = 0) out vec4 outColor;

const float Tau = 6.2831853;

void main()
{
    float time = frame.dust.w;
    vec3 light = vec3(0.0);

    if (kindInfo.x < 0.5)
    {
        // The beam: a core in a glow, across the ribbon; pulses running out of the emitter; a free beam fades out.
        float x = abs(uv.x);
        float core = exp(-(x * x) / (kindInfo.y * kindInfo.y) * 2.0);
        float glow = exp(-x * x * 4.5);
        float metres = alongInfo.x;
        float wave = 0.5 + 0.5 * cos(Tau * (metres - time * motionIn.y) / max(motionIn.z, 0.01) + motionIn.x * Tau);
        float pulse = 1.0 - pulseIn.x * (1.0 - wave);
        float fade = kindInfo.w > 0.5 ? 1.0 : 1.0 - smoothstep(motionIn.w, 1.0, alongInfo.y);
        // The beam leaves the lens softly, not as a cut-off ribbon.
        float start = smoothstep(0.0, 0.25, metres);
        light = (colB.rgb * glow * 1.1 + colA.rgb * core * 2.2) * (pulse * fade * start * colA.a * kindInfo.z);
    }
    else if (kindInfo.x < 1.5)
    {
        // The glow where it cuts: a wide soft halo round a white-hot heart, flickering.
        float r2 = dot(uv, uv);
        float halo = exp(-r2 * 3.2);
        float heart = exp(-r2 * 16.0);
        light = (hitIn.rgb * halo * hitIn.w + colA.rgb * heart * 1.6 * hitIn.w) * kindInfo.z;
    }
    else
    {
        // A spark: a streak that is brightest at its head (the quad's far end) and across its middle.
        float x = abs(uv.x);
        float across = exp(-x * x * 3.0);
        float along = smoothstep(0.0, 1.0, alongInfo.y);
        light = colA.rgb * (3.0 * across * (0.25 + 0.75 * along) * kindInfo.z);
    }

    if (dot(light, light) < 1e-6)
        discard;
    outColor = vec4(light, 1.0);
}
