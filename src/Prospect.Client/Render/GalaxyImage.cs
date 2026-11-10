using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// Paints the galaxy seen from above, for the backdrop of the galaxy map. It is drawn from the same density
/// function that places asteroid fields, so the bright arms on the map are where the rock is.
/// </summary>
internal static class GalaxyImage
{
    public const int Size = 1024;

    /// <summary>The image covers this many galaxy radii from the core to its edge, leaving a dark margin.</summary>
    public const float Extent = 1.2f;

    public static byte[] Generate(GalaxyShape shape)
    {
        var pixels = new byte[Size * Size * 4];
        var arm = new Vector3(0.52f, 0.66f, 1.0f);
        var core = new Vector3(1.0f, 0.86f, 0.62f);

        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var u = (x + 0.5f) / Size * 2f - 1f;
                var v = (y + 0.5f) / Size * 2f - 1f;
                var density = shape.Density(shape.Center.X + u * shape.Radius * Extent, shape.Center.Z + v * shape.Radius * Extent);

                // Clouds break the arms up; finer dust carves dark lanes into them.
                var clouds = Noise.Fractal(new Vector3(u * 9f, v * 9f, 3.7f), 5);
                var dust = Noise.Fractal(new Vector3(u * 26f, v * 26f, 11.3f), 4);
                var brightness = density * (0.35f + 1.3f * clouds) * (1f - 0.55f * Math.Clamp((dust - 0.45f) * 4f, 0f, 1f));

                var r = MathF.Sqrt(u * u + v * v) * Extent;
                var color = Vector3.Lerp(arm, core, Math.Clamp(1f - r / 0.3f, 0f, 1f));

                // A scattering of individual bright stars, more of them where the galaxy is dense.
                var hash = (uint)(x * 374761393 + y * 668265263);
                hash = (hash ^ hash >> 13) * 1274126177;
                var sparkle = ((hash ^ hash >> 16) & 0xFFFF) / 65535f;
                if (sparkle > 0.9975f - density * 0.012f)
                    brightness += 0.25f + 0.6f * density;

                var lit = Vector3.Clamp(color * brightness * 1.25f, Vector3.Zero, Vector3.One);
                var index = (y * Size + x) * 4;
                pixels[index] = (byte)(lit.X * 255f);
                pixels[index + 1] = (byte)(lit.Y * 255f);
                pixels[index + 2] = (byte)(lit.Z * 255f);
                pixels[index + 3] = (byte)(Math.Clamp(brightness * 1.6f, 0f, 1f) * 255f);
            }

        return pixels;
    }
}
