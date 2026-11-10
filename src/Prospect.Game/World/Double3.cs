using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// A position in the galaxy in metres, in double precision. Doubles keep sub-millimetre accuracy across the whole
/// galaxy; everything is converted to single precision only relative to the camera when it is drawn.
/// </summary>
public readonly record struct Double3(double X, double Y, double Z)
{
    public static Double3 Zero => default;

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Double3 operator +(Double3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Double3 operator *(Double3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

    public static Double3 Lerp(Double3 a, Double3 b, double t) => a + (b - a) * t;

    /// <summary>The offset from <paramref name="origin"/> to this position, safe to hold in single precision.</summary>
    public Vector3 RelativeTo(Double3 origin) => new((float)(X - origin.X), (float)(Y - origin.Y), (float)(Z - origin.Z));
}
