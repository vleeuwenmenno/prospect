using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>
/// A decorative volume in world space. <see cref="Center"/> is relative to the centre of the sector that anchors
/// the cloud, the sector its centre lies in; the box may reach into the sectors around it. No simulation or
/// network state.
/// </summary>
public readonly record struct GasCloud(Vector3 Center, Vector3 HalfSize, float Seed);

/// <summary>A cloud together with the sector that anchors it, which is what its centre is relative to.</summary>
public readonly record struct GasCloudPlacement(SectorCoord Anchor, GasCloud Cloud);

/// <summary>
/// Rare gas volumes in world space. A sector anchors at most one, anywhere in it; most are a few kilometres
/// across and a few are huge, up to a sector wide, so a cloud can lie across sector walls. Which sector anchors
/// a cloud is drawn from that sector's own stream, so any sector's clouds are known without generating it.
/// </summary>
public sealed class GasCloudModule : SectorModule
{
    /// <summary>The content discriminator.</summary>
    public const string TypeName = "gasClouds";
    /// <summary>The most clouds the renderer queues in a frame.</summary>
    public const int MaxActive = 8;
    /// <summary>The most clouds that can reach one sector: one for each sector around it, and its own.</summary>
    public const int MaxPerSector = 27;
    /// <summary>How far outside its x half-extent a cloud is drawn, as a share of the sector edge.</summary>
    public const float VisibilityMargin = 0.28f;
    /// <summary>The width of the band a cloud fades in over, as a share of the sector edge.</summary>
    public const float VisibilityFade = 0.1f;
    /// <summary>The x half-extent, as a share of the sector edge, that <see cref="Extinction"/> is tuned for.</summary>
    public const float StandardHalfSizeShare = 0.12f;

    private readonly record struct Range(float Min, float Max)
    {
        public float At(float t) => Min + (Max - Min) * t;
    }

    private readonly float _chance;
    private readonly float _centerReach;
    private readonly Range _size;
    private readonly float _hugeChance;
    private readonly Range _hugeSize;
    private readonly int _rivalReach;
    /// <summary>Extinction per metre at full density, for a cloud of the standard size.</summary>
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

    private GasCloudModule(string id, float chance, float centerReach, Range size, float hugeChance, Range hugeSize, float extinction, int samples, Vector3 tint, float motion, Vector4 shape, Vector4 look) : base(id)
    {
        _chance = chance;
        _centerReach = centerReach;
        _size = size;
        _hugeChance = hugeChance;
        _hugeSize = hugeSize;
        Extinction = extinction;
        Samples = samples;
        Tint = tint;
        Motion = motion;
        Shape = shape;
        Look = look;

        // Two clouds are rivals when a camera could see both. Their centres are then closer than both reaches
        // together, and each centre is within half a sector of its anchor's.
        var largest = Math.Max(size.Max, hugeChance > 0 ? hugeSize.Max : 0);
        _rivalReach = Math.Max(1, (int)MathF.Ceiling(2 * (largest + VisibilityMargin) + centerReach) - 1);
    }

    /// <summary>Reads bounded generation and rendering budgets from shared galaxy content.</summary>
    public static GasCloudModule Read(string id, JsonElement json, string path)
    {
        float Number(string key, float min, float max) => Bounded(json, key, min, max);
        float Bounded(JsonElement from, string key, float min, float max) => Checked(from.GetProperty(key).GetSingle(), key, min, max);
        float Checked(float value, string key, float min, float max)
        {
            if (!float.IsFinite(value) || value < min || value > max)
                throw new InvalidDataException($"{path}: gas {key} outside [{min}, {max}]");
            return value;
        }
        // A cloud's box stays within one sector of the sector that anchors it: half a sector to its centre and
        // half a sector of its own. A sector then finds every cloud that reaches it among its neighbours'.
        Range Shares(JsonElement from, string key)
        {
            var pair = from.GetProperty(key);
            var range = new Range(Checked(pair[0].GetSingle(), key, 0.01f, 0.5f), Checked(pair[1].GetSingle(), key, 0.01f, 0.5f));
            if (range.Min > range.Max) throw new InvalidDataException($"{path}: gas {key} must run from small to large");
            return range;
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
        var huge = json.GetProperty("huge"u8);
        return new GasCloudModule(id, Number("chance", 0, 1), Number("centerReach", 0, 1), Shares(json, "halfSizeShare"),
            Bounded(huge, "chance", 0, 1), Shares(huge, "halfSizeShare"), Number("extinction", 0.00001f, 0.01f), samples, color,
            Number("motion", 0, 0.01f), shape, look);
    }

    /// <summary>Counts the cloud the sector anchors, by the same rule as generation.</summary>
    public override void Summarize(in SectorContext sector, ref SectorSummary summary) =>
        summary.GasCloudCount = TryGetCloud(sector.Galaxy, sector.Coord, out _) ? 1 : 0;

    /// <summary>Writes the cloud the sector anchors, a fixed-size descriptor, without allocating.</summary>
    public override void Generate(in SectorContext sector, SectorContent content)
    {
        content.Summary.GasCloudCount = TryGetCloud(sector.Galaxy, sector.Coord, out var cloud) ? 1 : 0;
        content.GasCloud = cloud;
    }

    /// <summary>
    /// The cloud a sector anchors, if it has one: the one whose centre lies in the sector. Pure and cheap; it
    /// asks only the streams of the sector and of the sectors near enough to hold a rival.
    /// </summary>
    public bool TryGetCloud(Galaxy galaxy, SectorCoord anchor, out GasCloud cloud)
    {
        if (!Candidate(galaxy, anchor, out cloud))
            return false;

        // A camera sees one cloud at a time, which is all the renderer composes. Where two could be seen at once
        // the larger stays. A rival counts even when a third outranks it in turn: the rule never has to look
        // further than this, and only ever removes a cloud too many.
        var edge = (float)galaxy.SectorSize;
        var reach = cloud.HalfSize.X + VisibilityMargin * edge;
        for (var dz = -_rivalReach; dz <= _rivalReach; dz++)
            for (var dy = -_rivalReach; dy <= _rivalReach; dy++)
                for (var dx = -_rivalReach; dx <= _rivalReach; dx++)
                {
                    if ((dx | dy | dz) == 0)
                        continue;
                    if (!Candidate(galaxy, anchor.Offset(dx, dy, dz), out var rival))
                        continue;
                    if (rival.HalfSize.X < cloud.HalfSize.X || (rival.HalfSize.X == cloud.HalfSize.X && !Before(dx, dy, dz)))
                        continue;

                    // Plain arithmetic in a fixed order: whether a cloud exists hangs on this comparison.
                    var x = dx * edge + rival.Center.X - cloud.Center.X;
                    var y = dy * edge + rival.Center.Y - cloud.Center.Y;
                    var z = dz * edge + rival.Center.Z - cloud.Center.Z;
                    var apart = reach + rival.HalfSize.X + VisibilityMargin * edge;
                    if (x * x + y * y + z * z < apart * apart)
                    {
                        cloud = default;
                        return false;
                    }
                }

        return true;

        // Of two clouds of one size the one in the lower sector stays, so that exactly one of the two does.
        static bool Before(int dx, int dy, int dz) => dz != 0 ? dz < 0 : dy != 0 ? dy < 0 : dx < 0;
    }

    /// <summary>The cloud a sector's own streams draw, before rivals are looked at.</summary>
    private bool Candidate(Galaxy galaxy, SectorCoord anchor, out GasCloud cloud)
    {
        cloud = default;
        if (Stream(galaxy, anchor, 0).NextSingle() >= _chance)
            return false;

        var random = Stream(galaxy, anchor, 1);
        var edge = (float)galaxy.SectorSize;
        var spread = edge * _centerReach;
        var center = new Vector3((random.NextSingle() - 0.5f) * spread, (random.NextSingle() - 0.5f) * spread, (random.NextSingle() - 0.5f) * spread);
        var seed = random.NextSingle() * 128f;

        // Both rolls are always drawn, so that the share of huge clouds never moves the size of an ordinary one.
        var huge = random.NextSingle() < _hugeChance;
        var size = random.NextSingle();
        var half = edge * (huge ? _hugeSize : _size).At(size);
        cloud = new GasCloud(center, new Vector3(Proportions.X * half, Proportions.Y * half, Proportions.Z * half), seed);
        return true;
    }

    /// <summary>
    /// Lists the clouds whose box reaches a sector, the one it anchors and those centred in the sectors around
    /// it, in a fixed order. Returns how many were written; <paramref name="output"/> needs room for
    /// <see cref="MaxPerSector"/> to be sure of all of them. Pure, allocates nothing.
    /// </summary>
    public int Intersecting(Galaxy galaxy, SectorCoord sector, Span<GasCloudPlacement> output)
    {
        var half = (float)(galaxy.SectorSize * 0.5);
        var edge = (float)galaxy.SectorSize;
        var count = 0;
        for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var anchor = sector.Offset(dx, dy, dz);
                    if (count == output.Length || !TryGetCloud(galaxy, anchor, out var cloud))
                        continue;
                    if (MathF.Abs(dx * edge + cloud.Center.X) > half + cloud.HalfSize.X
                        || MathF.Abs(dy * edge + cloud.Center.Y) > half + cloud.HalfSize.Y
                        || MathF.Abs(dz * edge + cloud.Center.Z) > half + cloud.HalfSize.Z)
                        continue;
                    output[count++] = new GasCloudPlacement(anchor, cloud);
                }

        return count;
    }

    /// <summary>
    /// Lists the clouds whose box comes within <paramref name="radius"/> metres of a point in the world, wherever
    /// their centres lie, nearest sector layer first in a fixed order. Returns how many were written, at most the
    /// length of <paramref name="output"/>. It asks every sector the sphere touches and one more around them: a
    /// few hundred for a radius of two sectors. Pure, allocates nothing; meant for a worker or for once per
    /// change of the camera's sector, not for every frame.
    /// </summary>
    public int Intersecting(Galaxy galaxy, Double3 center, double radius, Span<GasCloudPlacement> output)
    {
        if (!double.IsFinite(center.X + center.Y + center.Z) || !(radius >= 0) || output.IsEmpty)
            return 0;

        // A cloud's box stays within one sector edge of its anchor's centre.
        var reach = Math.Min(radius, MaxQueryRadiusSectors * galaxy.SectorSize) + galaxy.SectorSize;
        var low = galaxy.SectorOf(new Double3(center.X - reach, center.Y - reach, center.Z - reach));
        var high = galaxy.SectorOf(new Double3(center.X + reach, center.Y + reach, center.Z + reach));
        var count = 0;
        for (var z = low.Z; z <= high.Z; z++)
            for (var y = low.Y; y <= high.Y; y++)
                for (var x = low.X; x <= high.X; x++)
                {
                    var anchor = new SectorCoord(x, y, z);
                    if (!TryGetCloud(galaxy, anchor, out var cloud))
                        continue;
                    var offset = (galaxy.CenterOf(anchor) + cloud.Center) - center;
                    var outsideX = Math.Max(0.0, Math.Abs(offset.X) - cloud.HalfSize.X);
                    var outsideY = Math.Max(0.0, Math.Abs(offset.Y) - cloud.HalfSize.Y);
                    var outsideZ = Math.Max(0.0, Math.Abs(offset.Z) - cloud.HalfSize.Z);
                    if (outsideX * outsideX + outsideY * outsideY + outsideZ * outsideZ > radius * radius)
                        continue;
                    output[count++] = new GasCloudPlacement(anchor, cloud);
                    if (count == output.Length)
                        return count;
                }

        return count;
    }

    /// <summary>The largest radius, in sector edges, <see cref="Intersecting(Galaxy, Double3, double, Span{GasCloudPlacement})"/> looks over.</summary>
    public const int MaxQueryRadiusSectors = 8;

    /// <summary>
    /// How much of a cloud shows from <paramref name="distance"/> metres off its centre: all of it close by,
    /// fading to nothing <see cref="VisibilityMargin"/> of a sector edge beyond its x half-extent
    /// <paramref name="reach"/>. Clouds are kept far enough apart that only one shows at a time.
    /// </summary>
    public static float Visibility(float distance, float reach, float sectorSize) =>
        Math.Clamp((reach + sectorSize * VisibilityMargin - distance) / (sectorSize * VisibilityFade), 0, 1);

    /// <summary>The same for a cloud of the standard size.</summary>
    public static float Visibility(float distance, float sectorSize) => Visibility(distance, sectorSize * StandardHalfSizeShare, sectorSize);

    /// <summary>
    /// Extinction per metre for a cloud. A cloud larger than the standard one is thinner by as much, so that a
    /// huge cloud is as deep to look through as an ordinary one and not a wall.
    /// </summary>
    public float ExtinctionOf(in GasCloud cloud, float sectorSize) => Extinction * Math.Min(1f, sectorSize * StandardHalfSizeShare / cloud.HalfSize.X);
}
