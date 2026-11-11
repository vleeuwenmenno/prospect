// Shared by the jump effects. The world-space mark, the stars and the composite pass all read a jump's stage
// through these functions, so the opening the lens bends the view around is the same one that is drawn.

const float FtlTau = 6.2831853;

// Hashes without sine: sin of a large argument loses precision on the GPU and turns noise into visible blocks.
float ftlHash(float n)
{
    n = fract(n * 0.1031);
    n *= n + 33.33;
    n *= n + n;
    return fract(n);
}

float ftlHash2(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float ftlNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(ftlHash2(i), ftlHash2(i + vec2(1, 0)), f.x), mix(ftlHash2(i + vec2(0, 1)), ftlHash2(i + vec2(1, 1)), f.x), f.y);
}

float ftlFbm(vec2 p)
{
    float sum = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 4; i++)
    {
        sum += amplitude * ftlNoise(p);
        p = p * 2.07 + 13.1;
        amplitude *= 0.5;
    }
    return sum;
}

float ftlEase(float x)
{
    x = clamp(x, 0.0, 1.0);
    return x * x * (3.0 - 2.0 * x);
}

// How far a rift or wormhole is open, from 0 to 1. Stage 1 is the drive working (align and charge), 2 the hull in
// transit, 3 the hull arriving. It grows through the whole charge so the build-up can be watched, opens at the far
// end while the hull is in transit, and closes behind it once it is through.
float ftlOpening(int stage, float progress)
{
    if (stage == 1)
        return mix(0.05, 1.0, ftlEase(progress));
    if (stage == 2)
        return ftlEase(progress);
    if (stage == 3)
        return 1.0 - ftlEase(progress);
    return 0.0;
}

// A rift starts as a crack and is pulled open: first it runs long, then it widens into a window. Half extents in
// units of the fully open radius.
vec2 ftlRiftExtent(float open)
{
    return vec2(mix(0.025, 0.72, ftlEase((open - 0.12) / 0.88)), mix(0.1, 1.0, ftlEase(open / 0.55)));
}

// Random heights around a ring, joined by straight runs. Straight runs between random points make sharp teeth,
// where smooth noise would make the soft wobble of a blob.
float ftlRing(float around, float count, float seed)
{
    float x = around * count;
    float i = floor(x);
    float a = ftlHash(mod(i, count) + seed * 97.0);
    float b = ftlHash(mod(i + 1.0, count) + seed * 97.0);
    return mix(a, b, x - i);
}

// The rift's torn outline: its radius at an angle, relative to the ellipse of its extent. Large teeth with smaller
// chips along them, and a few long cracks running out where the tear has split further.
float ftlRiftOutline(float angle, float seed)
{
    float around = angle / FtlTau + 0.5;
    float crack = ftlRing(around, 17.0, seed + 0.63);
    return 0.9
        + (ftlRing(around, 13.0, seed) - 0.5) * 0.5
        + (ftlRing(around, 37.0, seed + 0.31) - 0.5) * 0.2
        + (ftlRing(around, 89.0, seed + 0.17) - 0.5) * 0.07
        + pow(crack, 7.0) * 0.8;
}

// How far the hyperspace drive stretches the stars for its pilot, as a fraction of each star's distance from the
// middle of the view. Lines grow out of the stars late in the charge, stay long through the tunnel and pull back to
// points as the hull arrives. Takes the frame's ftl vector: kind, stage, progress.
float ftlStretch(vec4 ftl)
{
    if (int(ftl.x + 0.5) != 1)
        return 0.0;
    int stage = int(ftl.y + 0.5);
    float s = 0.0;
    if (stage == 1)
        s = ftlEase((ftl.z - 0.55) / 0.45);
    else if (stage == 2)
        s = 1.0;
    else if (stage == 3)
        s = 1.0 - ftlEase(ftl.z / 0.8);
    return 1.3 * s * s;
}
