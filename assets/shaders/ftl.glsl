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

// Value noise that wraps every period cells along x, for patterns laid around the jump axis without a seam.
float ftlNoiseAround(vec2 p, float period)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float x0 = mod(i.x, period);
    float x1 = mod(i.x + 1.0, period);
    return mix(mix(ftlHash2(vec2(x0, i.y)), ftlHash2(vec2(x1, i.y)), f.x), mix(ftlHash2(vec2(x0, i.y + 1.0)), ftlHash2(vec2(x1, i.y + 1.0)), f.x), f.y);
}

float ftlFbmAround(vec2 p, float period)
{
    float sum = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 4; i++)
    {
        sum += amplitude * ftlNoiseAround(p, period);
        p = vec2(p.x * 2.0, p.y * 2.0 + 13.1);
        period *= 2.0;
        amplitude *= 0.5;
    }
    return sum;
}

// A view direction in coordinates around the jump axis, the way the walls of a tunnel along it are laid out.
// x: around the axis, 0 to 1. y: along it, the log of the tangent of half the angle from the nose, which runs from
// minus infinity at the nose to infinity straight behind and moves evenly as things stream past. z: the angle from
// the nose in radians. w: its sine, how much of the view one step around is.
vec4 ftlTube(vec3 v, vec3 axis, vec3 up)
{
    vec3 side = cross(up, axis);
    float c = clamp(dot(v, axis), -0.99999, 0.99999);
    float theta = acos(c);
    float around = atan(dot(v, up), dot(v, side)) / FtlTau + 0.5;
    return vec4(around, log(tan(theta * 0.5)), theta, sqrt(1.0 - c * c));
}
