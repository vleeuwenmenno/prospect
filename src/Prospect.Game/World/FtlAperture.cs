using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The pilot's own rift or wormhole seen from inside its jump, from the same six vectors the GPU is given for it
/// (the frame's <c>ftl</c>, <c>ftlAxis</c>, <c>ftlUp</c>, <c>ftlEntry</c>, <c>ftlExit</c> and <c>ftlShape</c>) and
/// the camera's forward axis. It mirrors <c>assets/shaders/ftl_own.glsl</c> line for line, so picking leaves out of
/// the pilot's own hull exactly what its mesh pass does; change both together. Points and centres are relative to
/// the camera. The default is no opening, which hides nothing.
/// </summary>
public readonly struct FtlAperture(Vector4 ftl, Vector4 axis, Vector4 up, Vector4 entry, Vector4 exit, Vector4 shape, Vector3 forward)
{
    private const float Far = 1e30f;
    private const float Tau = 6.2831853f;

    /// <summary>Whether a rift or wormhole of the pilot's own is being drawn from inside.</summary>
    public bool Active => ftl.Y > 0.5f && (int)(ftl.X + 0.5f) is 2 or 3;

    private bool Entry => (int)(ftl.Y + 0.5f) == 1;

    /// <summary>The opening now, relative to the camera: the entry while the drive works, the exit after.</summary>
    public Vector4 Mouth => Entry ? entry : exit;

    /// <summary>How far the camera is on the far side of the opening, 0 to 1, eased.</summary>
    public float Past => up.W;

    /// <summary>The jump's axis, the hull's nose.</summary>
    public Vector3 Axis => new(axis.X, axis.Y, axis.Z);

    /// <summary>
    /// How much of a point of the pilot's own hull shows, from 0 to 1; see <c>ftlOwnHullShows</c>. The mesh pass
    /// keeps that share of its pixels; picking counts a point as shown above one half.
    /// </summary>
    public float HullShows(Vector3 p, float pixelAngle)
    {
        if (!Active)
            return 1f;
        var entry = Entry;
        var mouth = Mouth;
        var c = new Vector3(mouth.X, mouth.Y, mouth.Z);
        var past = up.W;
        var distance = p.Length();
        var v = distance > 1e-6f ? p / distance : forward;
        var real = Real(Ray(v, pixelAngle), RayDepth(v, distance));
        var throatSide = Vector3.Dot(p - c, Axis) * (entry ? 1f : -1f);
        var inThroat = entry ? past : 1f - past;
        return throatSide > 0f ? (1f - real) * inThroat : real;
    }

    private readonly record struct OwnRay(float Far, float At);

    private float Real(OwnRay ray, float depth)
    {
        var far = depth < ray.At ? up.W : ray.Far;
        return Entry ? 1f - far : far;
    }

    private float RayDepth(Vector3 v, float t) => t * Vector3.Dot(v, forward);

    private static float SphereRay(Vector3 v, Vector3 c, float r)
    {
        var b = Vector3.Dot(c, v);
        var h = r * r - (Vector3.Dot(c, c) - b * b);
        return h > 0f ? b - MathF.Sqrt(h) : b;
    }

    private OwnRay Ray(Vector3 v, float pixelAngle)
    {
        var kind = (int)(ftl.X + 0.5f);
        var entry = Entry;
        var mouth = Mouth;
        var open = entry ? shape.Y : shape.Z;
        var axisVector = Axis;
        var upVector = new Vector3(up.X, up.Y, up.Z);
        var seed = ftl.W + (entry ? 0f : 0.37f);
        var c = new Vector3(mouth.X, mouth.Y, mouth.Z);
        var r = mouth.W;
        var past = up.W;

        var edge = -1e4f;
        var at = Far;
        float far;
        if (kind == 3)
        {
            var facing = Vector3.Dot(v, axisVector);
            var t = MathF.Abs(facing) > 1e-5f ? Vector3.Dot(c, axisVector) / facing : -1f;
            if (r > 0f && t > 0f)
            {
                at = RayDepth(v, t);
                var hit = v * t - c;
                var side = Vector3.Cross(upVector, axisVector);
                var extent = RiftExtent(open) * r;
                var across = new Vector2(Vector3.Dot(hit, side), Vector3.Dot(hit, upVector)) / extent;
                var re = across.Length();
                var dir = across / MathF.Max(re, 1e-6f);
                var perPixel = t * pixelAngle / MathF.Max(MathF.Abs(facing), 0.08f);
                edge = (RiftOutline(MathF.Atan2(across.Y, across.X), seed) - re) * (dir * extent).Length() / perPixel;
            }
            var through = Math.Clamp(edge + 0.5f, 0f, 1f);
            far = Mix(through, 1f - through, past);
        }
        else
        {
            var d = c.Length();
            if (r <= 0f)
            {
                far = past;
            }
            else if (d > r)
            {
                var t = SphereRay(v, c, r);
                at = t > 0f ? RayDepth(v, t) : Far;
                var rim = MathF.Asin(r / d);
                edge = (rim - MathF.Acos(Math.Clamp(Vector3.Dot(v, c / d), -1f, 1f))) / pixelAngle;
                var hit = Math.Clamp(edge + 0.5f, 0f, 1f);
                far = Mix(hit, 1f - hit, past);
            }
            else
            {
                var toward = d > 1e-4f ? c / d : axisVector;
                var w = SmoothStep(0.5f * r, r, d);
                var n = axisVector * (1f - w) + toward * ((1f - 2f * past) * w);
                n = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : axisVector;
                edge = MathF.Asin(Math.Clamp(Vector3.Dot(v, n), -1f, 1f)) / pixelAngle;
                far = Math.Clamp(edge + 0.5f, 0f, 1f);
            }
        }
        return new OwnRay(far, at);
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float low, float high, float x)
    {
        var t = Math.Clamp((x - low) / (high - low), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float Mod(float x, float y) => x - y * MathF.Floor(x / y);

    private static float Ease(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    // ftlHash, ftlRing, ftlRiftExtent and ftlRiftOutline in ftl.glsl.
    private static float Hash(float n)
    {
        n = Fract(n * 0.1031f);
        n *= n + 33.33f;
        n *= n + n;
        return Fract(n);
    }

    private static Vector2 RiftExtent(float open) =>
        new(Mix(0.025f, 0.72f, Ease((open - 0.12f) / 0.88f)), Mix(0.1f, 1f, Ease(open / 0.55f)));

    private static float Ring(float around, float count, float seed)
    {
        var x = around * count;
        var i = MathF.Floor(x);
        var a = Hash(Mod(i, count) + seed * 97f);
        var b = Hash(Mod(i + 1f, count) + seed * 97f);
        return Mix(a, b, x - i);
    }

    private static float RiftOutline(float angle, float seed)
    {
        var around = angle / Tau + 0.5f;
        var crack = Ring(around, 17f, seed + 0.63f);
        return 0.9f
            + (Ring(around, 13f, seed) - 0.5f) * 0.5f
            + (Ring(around, 37f, seed + 0.31f) - 0.5f) * 0.2f
            + (Ring(around, 89f, seed + 0.17f) - 0.5f) * 0.07f
            + MathF.Pow(crack, 7f) * 0.8f;
    }
}
