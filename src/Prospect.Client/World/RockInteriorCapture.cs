using System.Numerics;
using System.Text;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// Render-only capture fixture (<c>--open rock-interior</c>): a real rock of the world, cut in half through its
/// centre, the cut face coloured by <see cref="RockInterior"/>'s layers and ores. Nothing in the simulation changes.
/// Built once when the view opens; only scripted captures make one.
/// </summary>
internal sealed class RockInteriorCapture
{
    /// <summary>The far half of the rock's drawn surface, at unit radius in the rock's own frame, cut at z = 0.</summary>
    public readonly MeshVertex[] Shell;

    /// <summary>The cut face at z = 0, one painted square a cell, glowing in its layer's colour.</summary>
    public readonly MeshVertex[] Cut;

    public readonly float Radius;
    public readonly RockRef Rock;

    /// <summary>What the legend says, one line each, UTF-8.</summary>
    public readonly byte[][] Lines;

    public Double3 Anchor;

    private const int CutCells = 360;

    private RockInteriorCapture(Galaxy galaxy, RockRef rock, in Asteroid asteroid)
    {
        Rock = rock;
        Radius = asteroid.Radius;
        RockInterior.Describe(galaxy, rock, asteroid, out var interior);
        Shell = BuildShell(asteroid.Shape);
        Cut = BuildCut(galaxy, interior, asteroid);
        Lines = Legend(galaxy, interior, asteroid);
    }

    /// <summary>
    /// Finds a rock of the size asked for (<c>small</c> under 25 m, <c>medium</c> 60 to 400 m, <c>giant</c> over
    /// 1,000 m), of the resource asked for if any, preferring one with a core pocket, in the sectors around home.
    /// Null when there is none.
    /// </summary>
    public static RockInteriorCapture? Find(Galaxy galaxy, string size, string? resource)
    {
        var (low, high) = size switch { "small" => (0f, 25f), "giant" => (1000f, float.MaxValue), _ => (60f, 400f) };
        var wanted = -1;
        if (resource is not null)
            for (var i = 0; i < galaxy.Resources.Length; i++)
                if (galaxy.Resources[i].Id == resource)
                    wanted = i;
        var content = new SectorContent();
        RockRef? fallback = null;
        var fallbackAsteroid = default(Asteroid);
        Span<SectorCoord> first = [default, new(3, 0, 1), new(8, 0, -8), new(-40, 0, -29)];
        for (var n = 0; n < 4 + 9 * 9; n++)
        {
            var coord = n < first.Length ? first[n] : new SectorCoord((n - 4) % 9 - 4, 0, (n - 4) / 9 - 4);
            galaxy.Generate(coord, content);
            for (var i = 0; i < content.AsteroidCount; i++)
            {
                ref readonly var rock = ref content.Asteroids[i];
                if (rock.Radius < low || rock.Radius > high || rock.Resource < 0 || (wanted >= 0 && rock.Resource != wanted))
                    continue;
                var at = new RockRef(coord, i);
                RockInterior.Describe(galaxy, at, rock, out var interior);
                // A rock that is not too rich keeps its layers' contrast, which is what the capture is for.
                if (interior.PocketMix != 0 && rock.Ore <= 0.5f)
                    return new RockInteriorCapture(galaxy, at, rock);
                if (fallback is null)
                {
                    fallback = at;
                    fallbackAsteroid = rock;
                }
            }

            if (n >= 12 && fallback is { } found)
                return new RockInteriorCapture(galaxy, found, fallbackAsteroid);
        }

        return fallback is { } last ? new RockInteriorCapture(galaxy, last, fallbackAsteroid) : null;
    }

    private static MeshVertex Vertex(Vector3 position, Vector3 normal) =>
        new() { Position = new Brutal.Numerics.float3(position.X, position.Y, position.Z), Normal = new Brutal.Numerics.float3(normal.X, normal.Y, normal.Z) };

    private static MeshVertex[] BuildShell(int shape)
    {
        var (directions, indices) = AsteroidMesh.Sphere(AsteroidMesh.Subdivisions);
        var points = new Vector3[directions.Length];
        for (var i = 0; i < points.Length; i++)
            points[i] = AsteroidShape.Point(shape, directions[i]);
        var vertices = new List<MeshVertex>(indices.Length / 2 + 4096);
        Span<Vector3> kept = stackalloc Vector3[4];
        for (var t = 0; t < indices.Length; t += 3)
        {
            var a = points[indices[t]];
            var b = points[indices[t + 1]];
            var c = points[indices[t + 2]];
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            // Keep the part of the triangle behind the cut (z <= 0), clipped exactly to the plane.
            var count = 0;
            for (var e = 0; e < 3; e++)
            {
                var p = e == 0 ? a : e == 1 ? b : c;
                var q = e == 0 ? b : e == 1 ? c : a;
                if (p.Z <= 0)
                    kept[count++] = p;
                if ((p.Z <= 0) != (q.Z <= 0))
                    kept[count++] = Vector3.Lerp(p, q, p.Z / (p.Z - q.Z)) with { Z = 0 };
            }

            for (var k = 2; k < count; k++)
            {
                vertices.Add(Vertex(kept[0], normal));
                vertices.Add(Vertex(kept[k - 1], normal));
                vertices.Add(Vertex(kept[k], normal));
            }
        }

        return [.. vertices];
    }

    private static MeshVertex[] BuildCut(Galaxy galaxy, in RockInterior interior, in Asteroid asteroid)
    {
        var vertices = new List<MeshVertex>(CutCells * CutCells * 5);
        var cell = 2f / CutCells;
        for (var j = 0; j < CutCells; j++)
        {
            for (var i = 0; i < CutCells; i++)
            {
                var x = -1f + cell * (i + 0.5f);
                var y = -1f + cell * (j + 0.5f);
                var r = MathF.Sqrt(x * x + y * y);
                if (r > 1f || (r > 0 && r > AsteroidShape.RadiusAlong(asteroid.Shape, new Vector3(x / r, y / r, 0))))
                    continue;
                interior.Sample(new Vector3(x, y, 0) * asteroid.Radius, out var sample);
                var colour = Colour(galaxy, interior, sample);
                var normal = Vector3.UnitZ * PropulsionModels.Paint([colour.X, colour.Y, colour.Z]);
                var x0 = x - cell * 0.5f;
                var y0 = y - cell * 0.5f;
                var x1 = x + cell * 0.5f;
                var y1 = y + cell * 0.5f;
                vertices.Add(Vertex(new Vector3(x0, y0, 0), normal));
                vertices.Add(Vertex(new Vector3(x1, y0, 0), normal));
                vertices.Add(Vertex(new Vector3(x1, y1, 0), normal));
                vertices.Add(Vertex(new Vector3(x0, y0, 0), normal));
                vertices.Add(Vertex(new Vector3(x1, y1, 0), normal));
                vertices.Add(Vertex(new Vector3(x0, y1, 0), normal));
            }
        }

        return [.. vertices];
    }

    /// <summary>
    /// The colour of a sample: the layer's rock tinted toward the colour of its strongest ore by that ore's grade;
    /// veins and the core pocket in their ore's own colour; ice pale blue, volatile gas orange.
    /// </summary>
    private static Vector3 Colour(Galaxy galaxy, in RockInterior interior, in RockSample sample)
    {
        var body = interior.IceBody ? new Vector3(0.5f, 0.62f, 0.72f) : sample.Layer == RockLayer.Crust ? new Vector3(0.5f, 0.4f, 0.3f) : new Vector3(0.16f, 0.15f, 0.15f);
        Vector3 colour;
        if (sample.Unstable)
            colour = sample.Pocket == RockPocketKind.Ice ? new Vector3(0.62f, 0.85f, 1f) : new Vector3(1f, 0.45f, 0.08f);
        else if (sample.Resource < 0)
            colour = body;
        else
        {
            var ore = galaxy.Resources[sample.Resource].Color;
            // Veins and the pocket in their ore's colour, made more saturated so they stand out of a tinted mantle.
            var grey = (ore.X + ore.Y + ore.Z) / 3f;
            var vivid = Vector3.Clamp(new Vector3(grey) + (ore - new Vector3(grey)) * 2.2f, Vector3.Zero, Vector3.One);
            colour = sample.Layer is RockLayer.Vein or RockLayer.Pocket
                ? vivid
                : Vector3.Lerp(body, ore, Math.Clamp(sample.Grade * 1.2f, 0f, 0.7f));
        }

        return colour;
    }

    private static byte[][] Legend(Galaxy galaxy, in RockInterior interior, in Asteroid asteroid)
    {
        string Name(int resource) => resource < 0 ? "-" : galaxy.Resources[resource].Name.ToUpperInvariant();
        var bulk = new StringBuilder($"{Name(asteroid.Resource)} {asteroid.Grade * 100:F1} %");
        if (asteroid.Mix1 >= 0)
            bulk.Append($", {Name(asteroid.Mix1)} {asteroid.Mix1Share * 100:F1} %");
        if (asteroid.Mix2 >= 0)
            bulk.Append($", {Name(asteroid.Mix2)} {asteroid.Mix2Share * 100:F1} %");
        var unstable = 0;
        for (var p = 0; p < interior.PocketCount; p++)
            if (interior.PocketKind(p) != RockPocketKind.Ore)
                unstable++;
        var pocket = interior.PocketMix == 0 ? "no core pocket"
            : $"core pocket {Name(interior.PocketMix == 2 ? asteroid.Mix2 : asteroid.Mix1)} {(interior.PocketMix == 2 ? interior.PocketShares.Z : interior.PocketShares.Y) * 100:F0} %, {interior.PocketVolume * 100:F1} % of volume";
        string[] lines =
        [
            $"ROCK INTERIOR  {interior.Radius:F0} m radius, shape {asteroid.Shape}",
            $"bulk: {bulk}, rock {(1f - asteroid.Ore) * 100:F1} %",
            $"crust {interior.CrustVolume * 100:F0} % of volume, {Name(asteroid.Resource)} {interior.CrustShares.X * 100:F1} %",
            $"mantle {Name(asteroid.Resource)} {interior.MantleShares.X * 100:F1} %",
            $"{interior.VeinCount} veins, {interior.VeinVolume * 100:F1} % of volume, {Name(asteroid.Resource)} {interior.VeinShares.X * 100:F1} %",
            $"{pocket}; {unstable} unstable pocket{(unstable == 1 ? "" : "s")}",
        ];
        var bytes = new byte[lines.Length][];
        for (var i = 0; i < lines.Length; i++)
            bytes[i] = Encoding.UTF8.GetBytes(lines[i]);
        return bytes;
    }

    /// <summary>
    /// The rock's turn this frame: its cut face (local +z) toward the camera, swung a little to the side so the
    /// shell's depth shows, and its local +y along the camera's up.
    /// </summary>
    public static Quaternion Facing(Vector3 forward, Vector3 right, Vector3 up)
    {
        var z = Vector3.Normalize(-forward * 0.94f + right * 0.34f);
        var x = Vector3.Normalize(Vector3.Cross(up, z));
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }
}
