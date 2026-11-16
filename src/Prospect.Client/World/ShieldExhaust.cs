using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;

namespace Prospect.Client.World;

internal readonly record struct ExhaustCapsule(uint Hull, Vector3 Start, Vector3 Direction, float Length, float Radius, float Intensity, Vector3 Color, float SplashSpread = 1.5f)
{
    public float DistanceSquared(Vector3 point)
    {
        var delta = point - Start;
        var along = Math.Clamp(Vector3.Dot(delta, Direction), 0f, Length);
        return (delta - Direction * along).LengthSquared();
    }
}

internal readonly record struct ExhaustBounds(Vector3 Min, Vector3 Max);

/// <summary>Actual shell contacts, shared by the plume's clipping plane and the shield's splash.</summary>
internal static class ShieldExhaust
{
    public const int Capacity = 8;
    public const int VectorsPerShield = 1 + Capacity * 3;
    // At least four maximum-size shells can be tested each frame. A partial search never installs a contact.
    public const int TriangleBudget = 262144;

    public static void Apply(ReadOnlySpan<ExhaustCapsule> sources, Span<PlumeInstance> plumes,
        ReadOnlySpan<ShieldDraw> shields, ReadOnlySpan<ShieldInstance> instances,
        ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices, Span<float4> output, float time, ReadOnlySpan<ExhaustBounds> bounds = default)
    {
        Span<float> distances = stackalloc float[ShieldVisuals.MaxShields * Capacity];
        Span<Matrix4x4> inverses = stackalloc Matrix4x4[ShieldVisuals.MaxShields];
        for (var s = 0; s < shields.Length; s++)
        {
            output[s * VectorsPerShield] = default;
            if (!sources.IsEmpty) inverses[s] = Inverse(instances[s], time);
        }
        var remaining = TriangleBudget;
        for (var p = 0; p < sources.Length; p++)
        {
            ref readonly var source = ref sources[p];
            ref var plume = ref plumes[p];
            plume.ShieldClip = default;
            if (source.Intensity <= 0) continue; // Strength zero is the explicit feature-off comparison.
            var nearest = source.Length;
            var target = -1;
            var normal = Vector3.Zero;
            var complete = true;
            for (var s = 0; s < shields.Length; s++)
            {
                ref readonly var shield = ref shields[s];
                if (shield.Hull == source.Hull || instances[s].Params.X <= 0.01f || source.DistanceSquared(shield.Center) > shield.Radius * shield.Radius) continue;
                var inverse = inverses[s];
                var start = Vector3.Transform(source.Start, inverse);
                var direction = Vector3.TransformNormal(source.Direction, inverse);
                var groups = Math.Max(1, shield.BoundsCount);
                for (var g = 0; g < groups; g++)
                {
                    if (shield.BoundsCount > 0 && !Meets(bounds[shield.BoundsOffset + g], start, direction, nearest)) continue;
                    var first = shield.ShellIndex + (shield.BoundsCount > 0 ? g * ShieldVisuals.ExhaustGroupIndices : 0);
                    var end = shield.BoundsCount > 0 ? Math.Min(first + ShieldVisuals.ExhaustGroupIndices, shield.ShellIndex + shield.IndexCount) : shield.ShellIndex + shield.IndexCount;
                    var triangles = (end - first) / 3;
                    if (triangles > remaining) { complete = false; break; }
                    remaining -= triangles;
                    for (var i = first; i < end; i += 3)
                    {
                        var a = vertices[shield.ShellVertex + indices[i]];
                        var b = vertices[shield.ShellVertex + indices[i + 1]];
                        var c = vertices[shield.ShellVertex + indices[i + 2]];
                        if (!Intersect(start, direction, V(a.Position), V(b.Position), V(c.Position), out var distance) || distance >= nearest) continue;
                        nearest = distance;
                        target = s;
                        normal = Vector3.Normalize(Vector3.TransformNormal(V(a.Normal) + V(b.Normal) + V(c.Normal), Matrix4x4.Transpose(inverse)));
                    }
                }
                if (!complete) break;
            }
            if (!complete || target < 0) continue;
            var hit = source.Start + source.Direction * nearest;
            // A source already inside a foreign shell has no visible outgoing stream.
            if (Vector3.Dot(normal, source.Direction) >= 0)
            {
                normal = -source.Direction;
                hit = source.Start;
                nearest = 0;
            }
            plume.ShieldClip = new float4(normal.X, normal.Y, normal.Z, -Vector3.Dot(normal, hit));
            var strength = source.Intensity * WeightAt(plume, nearest);
            if (strength <= 0) continue;
            var radius = RadiusAt(plume, nearest) * source.SplashSpread;
            Insert(target, nearest, hit, normal, radius, strength, source.Color, distances, output);
        }
    }

    // Matches the radius profiles in plume.frag, using the already-grown length/spread/opening of that exact instance.
    internal static float RadiusAt(in PlumeInstance plume, float distance) => plume.ExitRadius.W * (1f + plume.Shape.X *
        (plume.EdgeKind.W > 0.5f ? distance / plume.AxisLength.W : 1f - MathF.Exp(-distance / Math.Max(plume.Flow.W, 1e-4f))));

    internal static float WeightAt(in PlumeInstance plume, float distance)
    {
        var u = distance / plume.AxisLength.W;
        if (u < 0 || u >= 1) return 0;
        var weight = plume.EdgeKind.W > 1.5f ? (1 - u) * (1 - u) : 1 - Smooth(plume.EdgeKind.W > 0.5f ? 0.35f : 0.25f, 1, u);
        if (plume.Flow.X >= 0) weight *= Smooth(plume.Flow.X * plume.Flow.Y, plume.Flow.X * plume.Flow.Y + plume.ExitRadius.W, distance);
        if (plume.EdgeKind.W is > 0.5f and < 1.5f)
        {
            var front = plume.SparkAge.W * plume.Flow.Y;
            var soft = plume.ExitRadius.W + 0.2f * front;
            weight *= 1 - Smooth(front - soft, front + soft, distance);
        }
        return weight;
    }

    private static float Smooth(float lo, float hi, float value)
    {
        var t = Math.Clamp((value - lo) / Math.Max(hi - lo, 1e-6f), 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static bool Meets(in ExhaustBounds bounds, Vector3 origin, Vector3 direction, float length)
    {
        var enter = 0f;
        var exit = length;
        for (var axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(direction[axis]) < 1e-8f)
            {
                if (origin[axis] < bounds.Min[axis] - 1e-5f || origin[axis] > bounds.Max[axis] + 1e-5f) return false;
                continue;
            }
            var a = (bounds.Min[axis] - origin[axis]) / direction[axis];
            var b = (bounds.Max[axis] - origin[axis]) / direction[axis];
            enter = Math.Max(enter, Math.Min(a, b));
            exit = Math.Min(exit, Math.Max(a, b));
            if (enter > exit + 1e-5f) return false;
        }
        return true;
    }

    internal static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0;
        var e1 = b - a;
        var e2 = c - a;
        var cross = Vector3.Cross(direction, e2);
        var determinant = Vector3.Dot(e1, cross);
        if (MathF.Abs(determinant) < 1e-8f) return false;
        var delta = origin - a;
        var u = Vector3.Dot(delta, cross) / determinant;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(delta, e1);
        var v = Vector3.Dot(direction, q) / determinant;
        if (v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(e2, q) / determinant;
        return distance >= 0;
    }

    private static Matrix4x4 Inverse(in ShieldInstance instance, float time)
    {
        var r = instance.Recovery;
        var phase = time * r.Z * MathF.Tau + r.W;
        var squash = Vector3.One + new Vector3(MathF.Sin(phase), MathF.Sin(phase * 1.31f + 2.1f), MathF.Sin(phase * 0.83f + 4.2f)) * r.X;
        var a = instance.Row0; var b = instance.Row1; var c = instance.Row2;
        var transform = Matrix4x4.CreateScale(squash) * new Matrix4x4(a.X, b.X, c.X, 0, a.Y, b.Y, c.Y, 0, a.Z, b.Z, c.Z, 0, a.W, b.W, c.W, 1);
        Matrix4x4.Invert(transform, out var inverse);
        return inverse;
    }

    internal static void Insert(int shield, float distance, Vector3 hit, Vector3 normal, float radius, float strength, Vector3 color, Span<float> distances, Span<float4> output)
    {
        var data = output.Slice(shield * VectorsPerShield, VectorsPerShield);
        var scores = distances.Slice(shield * Capacity, Capacity);
        var count = (int)data[0].X;
        var index = count;
        while (index > 0 && distance < scores[index - 1]) index--;
        if (index >= Capacity) return;
        for (var i = Math.Min(count, Capacity - 1); i > index; i--)
        {
            scores[i] = scores[i - 1];
            data.Slice(1 + (i - 1) * 3, 3).CopyTo(data.Slice(1 + i * 3, 3));
        }
        scores[index] = distance;
        var at = 1 + index * 3;
        data[at] = new float4(hit.X, hit.Y, hit.Z, radius);
        data[at + 1] = new float4(normal.X, normal.Y, normal.Z, strength);
        data[at + 2] = new float4(color.X, color.Y, color.Z, 0);
        data[0] = new float4(Math.Min(count + 1, Capacity), 0, 0, 0);
    }

    private static Vector3 V(float3 value) => new(value.X, value.Y, value.Z);
}
