using System.Numerics;
using Brutal.Numerics;

namespace Prospect.Client.World;

internal readonly record struct ExhaustCapsule(uint Hull, Vector3 Start, Vector3 Direction, float Length, float Radius, float Intensity, Vector3 Color)
{
    public float DistanceSquared(Vector3 point)
    {
        var delta = point - Start;
        var along = Math.Clamp(Vector3.Dot(delta, Direction), 0f, Length);
        return (delta - Direction * along).LengthSquared();
    }

    // The shader uses this envelope, with slow noise inside it.
    public float StrengthAt(Vector3 point)
    {
        var delta = point - Start;
        var along = Vector3.Dot(delta, Direction);
        if (along < 0 || along >= Length || Radius <= 0) return 0;
        var radial = Math.Max(0, 1f - (delta - Direction * along).LengthSquared() / (Radius * Radius));
        var axial = 1f - along / Length;
        return Intensity * radial * radial * axial * axial;
    }
}

/// <summary>Nearest eight intersecting plume capsules per shield, in fixed mapped frame memory.</summary>
internal static class ShieldExhaust
{
    public const int Capacity = 8;
    public const int VectorsPerShield = 1 + Capacity * 3;

    public static int Select(uint hull, Vector3 center, float radius, ReadOnlySpan<ExhaustCapsule> plumes, Span<float4> output)
    {
        Span<float> distances = stackalloc float[Capacity];
        var count = 0;
        foreach (ref readonly var plume in plumes)
        {
            if (plume.Hull == hull || plume.Intensity <= 0 || plume.Length <= 0) continue;
            var distance = plume.DistanceSquared(center);
            if (distance > (radius + plume.Radius) * (radius + plume.Radius)) continue;
            distance = Math.Max(0f, MathF.Sqrt(distance) - plume.Radius);
            var index = count;
            while (index > 0 && distance < distances[index - 1]) index--;
            if (index >= Capacity) continue;
            for (var i = Math.Min(count, Capacity - 1); i > index; i--)
            {
                distances[i] = distances[i - 1];
                output.Slice(1 + (i - 1) * 3, 3).CopyTo(output.Slice(1 + i * 3, 3));
            }
            distances[index] = distance;
            var at = 1 + index * 3;
            output[at] = new float4(plume.Start.X, plume.Start.Y, plume.Start.Z, plume.Radius);
            output[at + 1] = new float4(plume.Direction.X, plume.Direction.Y, plume.Direction.Z, plume.Length);
            output[at + 2] = new float4(plume.Color.X, plume.Color.Y, plume.Color.Z, plume.Intensity);
            count = Math.Min(count + 1, Capacity);
        }
        output[0] = new float4(count, 0, 0, 0);
        return count;
    }
}
