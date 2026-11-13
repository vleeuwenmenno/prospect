using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>A decorative volume, wholly inside its sector. No simulation or network state.</summary>
public readonly record struct GasCloud(Vector3 Center, Vector3 HalfSize, float Seed);

/// <summary>One independently seeded, rare gas volume per sector.</summary>
public sealed class GasCloudModule : SectorModule
{
    /// <summary>The content discriminator.</summary>
    public const string TypeName = "gasClouds";
    /// <summary>The maximum number whose distance spheres can overlap.</summary>
    public const int MaxActive = 8;
    private readonly float _chance;
    /// <summary>Extinction per metre at full density.</summary>
    public float Extinction { get; }
    /// <summary>The bounded number of view samples.</summary>
    public int Samples { get; }
    /// <summary>Scattered light tint.</summary>
    public Vector3 Tint { get; }
    /// <summary>Noise drift in normalized cloud coordinates per second.</summary>
    public float Motion { get; }
    /// <summary>
    /// Silhouette: envelope fill (share of the box), lumpiness (how far the broad octave swells or shrinks it),
    /// warp (bend, in x half-extents) and lean (seeded offset of the envelope's centre, in box units).
    /// </summary>
    public Vector4 Shape { get; }
    /// <summary>
    /// Texture: edge softness (half-width of the coverage step), knot density, wisp strength and veil density
    /// (the thin gas between knots).
    /// </summary>
    public Vector4 Look { get; }
    /// <summary>The cloud's y and z half-extents as shares of its x half-extent.</summary>
    public static readonly Vector3 Proportions = new(1, 0.65f, 0.85f);
    /// <summary>
    /// The least a lump can shrink the envelope's squared radius; shared with gas.glsl. A swollen surface reaches
    /// fill / sqrt(MinSwell) from the lean centre.
    /// </summary>
    public const float MinSwell = 0.6f;
    private readonly float _size;
    private GasCloudModule(string id, float chance, float size, float extinction, int samples, Vector3 tint, float motion, Vector4 shape, Vector4 look) : base(id)
    { _chance = chance; _size = size; Extinction = extinction; Samples = samples; Tint = tint; Motion = motion; Shape = shape; Look = look; }

    /// <summary>Reads bounded generation and rendering budgets from shared galaxy content.</summary>
    public static GasCloudModule Read(string id, JsonElement json, string path)
    {
        float Number(string key, float min, float max) => Bounded(json, key, min, max);
        float Bounded(JsonElement from, string key, float min, float max)
        {
            var value = from.GetProperty(key).GetSingle();
            if (!float.IsFinite(value) || value < min || value > max)
                throw new InvalidDataException($"{path}: gas {key} outside [{min}, {max}]");
            return value;
        }
        var tint = json.GetProperty("tint"u8);
        var color = new Vector3(tint[0].GetSingle(), tint[1].GetSingle(), tint[2].GetSingle());
        if (!float.IsFinite(color.X + color.Y + color.Z) || Vector3.Min(color, Vector3.Zero) != Vector3.Zero || Vector3.Max(color, Vector3.One) != Vector3.One)
            throw new InvalidDataException($"{path}: invalid gas tint");
        var samples = json.GetProperty("samples"u8).GetInt32();
        if (samples < 16 || samples > 64) throw new InvalidDataException($"{path}: gas samples outside [16, 64]");
        var shapeJson = json.GetProperty("shape"u8);
        var shape = new Vector4(Bounded(shapeJson, "fill", 0.3f, 0.7f), Bounded(shapeJson, "lumpiness", 0, 3),
            Bounded(shapeJson, "warp", 0, 0.2f), Bounded(shapeJson, "lean", 0, 0.4f));
        // The tightest axis is y. The most swollen surface, the warp and half the lean must stay inside the box
        // face, so only wisps and outermost lumps reach the smooth fade and nothing is cut flat.
        if (shape.X / MathF.Sqrt(MinSwell) + shape.W / 2 + shape.Z / Proportions.Y > 1)
            throw new InvalidDataException($"{path}: gas shape fill, lean and warp reach past the box");
        var look = new Vector4(Bounded(shapeJson, "edge", 0.02f, 0.3f), Bounded(shapeJson, "knots", 0, 6),
            Bounded(shapeJson, "wisps", 0, 2), Bounded(shapeJson, "veil", 0, 0.5f));
        return new GasCloudModule(id, Number("chance", 0, 1), Number("halfSizeShare", 0.01f, 0.12f), Number("extinction", 0.00001f, 0.01f), samples, color, Number("motion", 0, 0.01f), shape, look);
    }

    /// <summary>Uses the same presence stream as generation.</summary>
    public override void Summarize(in SectorContext sector, ref SectorSummary summary)
    { var random = Stream(sector, 0); summary.GasCloudCount = random.NextSingle() < _chance ? 1 : 0; }

    /// <summary>Writes a fixed-size descriptor without allocating.</summary>
    public override void Generate(in SectorContext sector, SectorContent content)
    {
        content.GasCloud = default;
        if (content.Summary.GasCloudCount == 0) return;
        var random = Stream(sector, 1);
        var edge = (float)sector.Galaxy.SectorSize;
        var center = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) * (edge * 0.1f);
        content.GasCloud = new GasCloud(center, Proportions * (edge * _size), random.NextSingle() * 128f);
    }

    /// <summary>Finite visibility support guarantees no more than eight overlapping sector clouds.</summary>
    public static float Visibility(float distance, float sectorSize) => Math.Clamp((sectorSize * 0.4f - distance) / (sectorSize * 0.1f), 0, 1);
}
