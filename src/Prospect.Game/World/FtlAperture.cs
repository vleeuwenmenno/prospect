using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The pilot's own rift or wormhole seen from inside its jump, from the same six vectors the GPU is given for it
/// (the frame's <c>ftl</c>, <c>ftlAxis</c>, <c>ftlUp</c>, <c>ftlEntry</c>, <c>ftlExit</c> and <c>ftlShape</c>) and
/// the camera's forward axis. It mirrors <c>assets/shaders/ftl_own.glsl</c> line for line, so picking leaves out of
/// the pilot's own hull exactly what its mesh pass does; change both together. Points and centres are relative to
/// the camera. The default is no opening, which hides nothing.
/// </summary>
public readonly struct FtlAperture(Vector4 ftl, Vector4 axis, Vector4 up, Vector4 entry, Vector4 exit, Vector4 shape,
    Vector3 forward, Vector3 right, Vector3 cameraUp, Vector4 lens)
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

    /// <summary>The view's lens as the frame has it: tan of half the vertical field, aspect, viewport width, height.</summary>
    public Vector4 Lens => lens;

    /// <summary>The angle one pixel of the viewport spans, as the mesh pass takes it.</summary>
    public float PixelAngle => 2f / Math.Max(lens.W, 1f) * lens.X;

    /// <summary>
    /// The value the mesh pass compares a point's <see cref="HullShows"/> with at a pixel of the viewport, given as
    /// <c>gl_FragCoord</c> (the pixel's corner plus a half); the pixel is drawn where the point shows more.
    /// </summary>
    public static float Pattern(Vector2 fragCoord) =>
        Fract(52.9829189f * Fract(fragCoord.X * 0.06711056f + fragCoord.Y * 0.00583715f));

    /// <summary>Where a view ray from the camera lands in the viewport, in pixels from its top left corner.</summary>
    public Vector2 PixelOf(Vector3 ray)
    {
        var depth = MathF.Max(Vector3.Dot(ray, forward), 1e-6f);
        var x = Vector3.Dot(ray, right) / (depth * lens.X * lens.Y);
        var y = -Vector3.Dot(ray, cameraUp) / (depth * lens.X);
        return new Vector2((x * 0.5f + 0.5f) * lens.Z, (y * 0.5f + 0.5f) * lens.W);
    }

    /// <summary>The unit view ray through a point of the viewport, in pixels from its top left corner.</summary>
    public Vector3 RayThrough(Vector2 pixel)
    {
        var x = pixel.X / lens.Z * 2f - 1f;
        var y = pixel.Y / lens.W * 2f - 1f;
        return Vector3.Normalize(forward + right * (x * lens.X * lens.Y) - cameraUp * (y * lens.X));
    }

    /// <summary>Whether the mesh pass draws a point of the pilot's own hull at the viewport pixel holding it.</summary>
    public bool Drawn(Vector3 p, Vector2 pixel) =>
        !Active || HullShows(p, PixelAngle) > Pattern(new Vector2(MathF.Floor(pixel.X) + 0.5f, MathF.Floor(pixel.Y) + 0.5f));

    /// <summary>The jump's axis, the hull's nose.</summary>
    public Vector3 Axis => new(axis.X, axis.Y, axis.Z);

    /// <summary>
    /// How much of a point of the pilot's own hull shows, from 0 to 1; see <c>ftlOwnHullShows</c>. The mesh pass
    /// keeps a pixel where this is above that pixel's <see cref="Pattern"/>.
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
        var window = Window(Ray(v, pixelAngle), RayDepth(v, distance));
        var worldView = entry ? 1f - past : past;
        var throatSide = Vector3.Dot(p - c, Axis) * (entry ? 1f : -1f);
        return throatSide > 0f ? (1f - window) * (1f - worldView) : 1f - window * worldView;
    }

    /// <summary>
    /// How much of the opening's window lies between the camera and a point, from 0 to 1: the share of the pixel
    /// whose ray meets the opening, inside its outline, before the point. <c>ftlOwnWindow</c>.
    /// </summary>
    public float WindowBefore(Vector3 p, float pixelAngle)
    {
        if (!Active)
            return 0f;
        var distance = p.Length();
        var v = distance > 1e-6f ? p / distance : forward;
        return Window(Ray(v, pixelAngle), RayDepth(v, distance));
    }

    private readonly record struct OwnRay(float At, float Edge);

    private static float Window(OwnRay ray, float depth) => depth < ray.At ? 0f : Math.Clamp(ray.Edge + 0.5f, 0f, 1f);

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

        // ftlOwnRay, for what the hull needs of it: where the ray meets the opening and how far inside its outline.
        var edge = -1e4f;
        var at = Far;
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
        }
        else
        {
            var d = c.Length();
            if (r > 0f && d > r)
            {
                var t = SphereRay(v, c, r);
                at = t > 0f ? RayDepth(v, t) : Far;
                var rim = MathF.Asin(r / d);
                edge = (rim - MathF.Acos(Math.Clamp(Vector3.Dot(v, c / d), -1f, 1f))) / pixelAngle;
            }
            // Inside the sphere the ray meets no surface in front of it (Far), so no window is in front of anything.
        }
        return new OwnRay(at, edge);
    }

    // GLSL's mix, rounded the same way.
    private static float Mix(float a, float b, float t) => a * (1f - t) + b * t;

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
