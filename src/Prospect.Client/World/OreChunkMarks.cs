using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brutal.Numerics;
using Prospect.Engine.IO;

namespace Prospect.Client.World;

/// <summary>
/// Client-only figures for the marks on loose ore chunks, from <c>content/visuals/ore_chunks.json</c>: the diamond, how
/// it fades with distance, the edge arrows for chunks off screen, the radius of the "chunks near" count and the pointer's
/// grab. Distances are metres, sizes unscaled pixels.
/// </summary>
internal sealed class OreChunkLook
{
    private static OreChunkLook? _current;

    /// <summary>Half the diamond's height when it marks a chunk too small on screen to frame.</summary>
    public float MarkerPixels { get; set; } = 7f;

    /// <summary>How far a diamond stands off the chunk's drawn edge when the chunk is big on screen.</summary>
    public float FramePaddingPixels { get; set; } = 3f;

    /// <summary>Stroke width of the diamond and the arrows.</summary>
    public float LineWidth { get; set; } = 1.5f;

    /// <summary>Opacity of the diamond's fill, against its outline's own.</summary>
    public float FillAlpha { get; set; } = 0.22f;

    /// <summary>A mark is at full strength out to this distance.</summary>
    public float FadeStartMetres { get; set; } = 400f;

    /// <summary>A mark has faded to <see cref="MinAlpha"/> at this distance and stays there.</summary>
    public float FadeEndMetres { get; set; } = 3000f;

    /// <summary>The faintest a mark within <see cref="CullMetres"/> gets.</summary>
    public float MinAlpha { get; set; } = 0.3f;

    /// <summary>Beyond this distance a chunk has no mark at all.</summary>
    public float CullMetres { get; set; } = 6000f;

    /// <summary>Whether chunks off screen show as arrows at the screen's edge.</summary>
    public bool EdgeArrows { get; set; } = true;

    /// <summary>Length of an edge arrow.</summary>
    public float ArrowPixels { get; set; } = 9f;

    /// <summary>How far an edge arrow sits in from the screen's edge.</summary>
    public float EdgeMarginPixels { get; set; } = 40f;

    /// <summary>Chunks within this distance of the flown hull are counted on the DRONES chip.</summary>
    public float NearMetres { get; set; } = 500f;

    /// <summary>The pointer is on a chunk when within this many pixels of it, or of its drawn edge when that is wider.</summary>
    public float HoverPixels { get; set; } = 14f;

    /// <summary>How strongly a mark is drawn when the bins cannot take its chunk: its own alpha times this.</summary>
    public float BlockedAlpha { get; set; } = 0.55f;

    /// <summary>Opacity of a blocked diamond's fill against its outline's own; zero leaves the diamond hollow.</summary>
    public float BlockedFillAlpha { get; set; }

    /// <summary>Half the arm of the cross struck through a mark whose chunk the bins cannot take.</summary>
    public float BlockedMarkPixels { get; set; } = 5f;

    /// <summary>The colour of that cross, 0xRRGGBB, read from the file's <c>blockedColor</c>. It says the same thing in the
    /// chunk's tooltip and on the DRONES chip.</summary>
    [JsonIgnore]
    public uint BlockedRgb { get; private set; } = 0xFF5A52;

    [JsonPropertyName("blockedColor")]
    public string BlockedColorFile { get; set; } = "FF5A52";

    /// <summary>The shipped file, read once.</summary>
    public static OreChunkLook Current => _current ??= Load(AppPaths.Content("visuals", "ore_chunks.json"));

    /// <summary>Reads and validates a look file; throws <see cref="InvalidDataException"/> when a figure is out of range.</summary>
    public static OreChunkLook Load(string path)
    {
        var value = JsonSerializer.Deserialize<OreChunkLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("ore_chunks.json is empty.");
        value.Validate();
        return value;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless every figure is sensible.</summary>
    public void Validate()
    {
        static bool Within(float v, float low, float high) => float.IsFinite(v) && v >= low && v <= high;
        if (!(Within(MarkerPixels, 2, 64) && Within(FramePaddingPixels, 0, 32) && Within(LineWidth, 0.5f, 8) && Within(FillAlpha, 0, 1)
            && Within(MinAlpha, 0.05f, 1) && Within(ArrowPixels, 3, 64) && Within(EdgeMarginPixels, 0, 400) && Within(HoverPixels, 1, 100)
            && Within(NearMetres, 1, 100_000) && Within(BlockedAlpha, 0.05f, 1) && Within(BlockedFillAlpha, 0, 1)
            && Within(BlockedMarkPixels, 1, 32)))
            throw new InvalidDataException("ore_chunks.json: a size, alpha or radius is out of range.");
        if (BlockedColorFile.Length != 6 || !uint.TryParse(BlockedColorFile, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var blocked))
            throw new InvalidDataException("ore_chunks.json: blockedColor is six hex digits.");
        BlockedRgb = blocked;
        if (!(FadeStartMetres > 0 && FadeStartMetres < FadeEndMetres && FadeEndMetres <= CullMetres && Within(CullMetres, 1, 1_000_000)))
            throw new InvalidDataException("ore_chunks.json: fadeStartMetres must be above 0, below fadeEndMetres, which is at most cullMetres.");
    }
}

/// <summary>
/// One loose ore chunk as the frame sees it: where it is drawn, how far, how big, and what it is. Worked out once a
/// frame in the world's update from the pose the mesh is drawn at, so a mark sits on the chunk.
/// </summary>
internal struct OreMarker
{
    /// <summary>The chunk's id.</summary>
    public uint Id;

    /// <summary>The galaxy's index of its ore.</summary>
    public int Resource;

    /// <summary>Share of the mass that is ore, and the whole mass in kilograms.</summary>
    public float Grade, Mass;

    /// <summary>The centre in window pixels; meaningful only when <see cref="OnScreen"/>.</summary>
    public float2 Screen;

    /// <summary>The centre in metres along the camera's right, up and forward axes.</summary>
    public Vector3 View;

    /// <summary>Metres from the camera.</summary>
    public float Distance;

    /// <summary>Metres from the flown hull.</summary>
    public float OwnDistance;

    /// <summary>The drawn lump's radius on screen, in window pixels.</summary>
    public float PixelRadius;

    /// <summary>True when the centre is in front of the camera and inside the window.</summary>
    public bool OnScreen;

    /// <summary>What the flown hull's bins would do with this chunk, worked out where the marker is placed.</summary>
    public OreIntakeState Intake;
}

/// <summary>What the hover tooltip says about a chunk. Plain data, drawn by the screens.</summary>
internal readonly record struct OreChunkInfo(uint Id, int Resource, float Grade, float Mass, float Distance, OreIntakeState Intake)
{
    /// <summary>True when a chunk is hovered.</summary>
    public bool Any => Id != 0;

    /// <summary>Kilograms of ore in it.</summary>
    public float OreKg => Mass * Grade;
}

/// <summary>
/// The rules of the marks on loose ore chunks, apart from any window: where a chunk lands on screen, how a mark fades
/// with distance, which chunks are near the ship and which one the pointer is on. Takes no memory.
/// </summary>
internal static class OreChunkMarks
{
    /// <summary>
    /// Fills <paramref name="marker"/>'s place on screen from its <see cref="OreMarker.View"/>: in front of the camera
    /// (past <paramref name="nearPlane"/>) and inside the window, with the lump's radius <paramref name="half"/> as pixels.
    /// </summary>
    public static void Place(ref OreMarker marker, float half, float tanHalfFov, float2 screen, float nearPlane)
    {
        var view = marker.View;
        marker.Distance = view.Length();
        var depth = view.Z;
        marker.PixelRadius = depth > 0 ? half / (depth * tanHalfFov) * screen.Y * 0.5f : 0f;
        marker.OnScreen = false;
        if (depth < nearPlane) return;
        var x = view.X / (depth * tanHalfFov * (screen.X / screen.Y));
        var y = -view.Y / (depth * tanHalfFov);
        marker.Screen = new float2((x * 0.5f + 0.5f) * screen.X, (y * 0.5f + 0.5f) * screen.Y);
        marker.OnScreen = marker.Screen.X >= 0 && marker.Screen.Y >= 0 && marker.Screen.X <= screen.X && marker.Screen.Y <= screen.Y;
    }

    /// <summary>
    /// How strong a mark is at <paramref name="distance"/>: full out to the fade start, falling in a line to the faintest
    /// at the fade end, there until the cull distance, and none beyond it.
    /// </summary>
    public static float Alpha(float distance, OreChunkLook look)
    {
        if (!(distance <= look.CullMetres)) return 0f;
        if (distance <= look.FadeStartMetres) return 1f;
        if (distance >= look.FadeEndMetres) return look.MinAlpha;
        var t = (distance - look.FadeStartMetres) / (look.FadeEndMetres - look.FadeStartMetres);
        return 1f + (look.MinAlpha - 1f) * t;
    }

    /// <summary>How many chunks are within the look's near radius of the flown hull.</summary>
    public static int CountNear(ReadOnlySpan<OreMarker> markers, OreChunkLook look)
    {
        var count = 0;
        var limit = look.NearMetres;
        for (var i = 0; i < markers.Length; i++)
            if (markers[i].OwnDistance <= limit) count++;
        return count;
    }

    /// <summary>
    /// The nearest chunk to the camera the pointer is on, or -1: on screen, not culled, and within the grab radius of its
    /// centre (the lump's drawn radius when that is wider). <paramref name="pixelScale"/> converts the look's pixels.
    /// </summary>
    public static int Pick(ReadOnlySpan<OreMarker> markers, float2 pointer, OreChunkLook look, float pixelScale)
    {
        var best = -1;
        var bestDistance = float.PositiveInfinity;
        var grab = look.HoverPixels * pixelScale;
        for (var i = 0; i < markers.Length; i++)
        {
            ref readonly var marker = ref markers[i];
            if (!marker.OnScreen || !(marker.Distance <= look.CullMetres) || marker.Distance >= bestDistance) continue;
            var radius = MathF.Max(grab, marker.PixelRadius);
            var dx = marker.Screen.X - pointer.X;
            var dy = marker.Screen.Y - pointer.Y;
            if (dx * dx + dy * dy > radius * radius) continue;
            best = i;
            bestDistance = marker.Distance;
        }

        return best;
    }

    /// <summary>
    /// Half the diamond's height for a marker: the look's size, or the lump's drawn radius plus the padding when the lump
    /// is bigger, so a close chunk is framed and not covered.
    /// </summary>
    public static float DiamondHalf(in OreMarker marker, OreChunkLook look, float pixelScale) =>
        MathF.Max(look.MarkerPixels * pixelScale, (marker.PixelRadius + look.FramePaddingPixels * pixelScale) * 1.25f);
}

/// <summary>
/// Chunks off screen gathered by the direction they lie in: eight sectors round the view's centre, each with how many
/// are in it, the nearest one's colour and distance, and the exact direction of that nearest one. Fixed storage; take no
/// memory.
/// </summary>
internal sealed class OreEdgeArrows
{
    /// <summary>The direction sectors; also the most arrows drawn.</summary>
    public const int Sectors = 8;

    private readonly int[] _count = new int[Sectors];
    private readonly float[] _nearest = new float[Sectors];
    private readonly int[] _resource = new int[Sectors];
    private readonly float2[] _direction = new float2[Sectors];

    /// <summary>Forgets the last frame's chunks.</summary>
    public void Clear()
    {
        Array.Clear(_count);
        Array.Fill(_nearest, float.PositiveInfinity);
    }

    /// <summary>Chunks in sector <paramref name="sector"/>.</summary>
    public int CountOf(int sector) => _count[sector];

    /// <summary>Metres to the nearest chunk of the sector.</summary>
    public float NearestOf(int sector) => _nearest[sector];

    /// <summary>The galaxy's ore index of the sector's nearest chunk.</summary>
    public int ResourceOf(int sector) => _resource[sector];

    /// <summary>The unit direction on screen (y down) to the sector's nearest chunk.</summary>
    public float2 DirectionOf(int sector) => _direction[sector];

    /// <summary>The sector a screen direction (y down) falls in; sector 0 is centred on the right, counting clockwise on screen.</summary>
    public static int SectorOf(float2 direction)
    {
        var angle = MathF.Atan2(direction.Y, direction.X);
        var step = MathF.Tau / Sectors;
        var index = (int)MathF.Round(angle / step);
        return (index % Sectors + Sectors) % Sectors;
    }

    /// <summary>
    /// Adds a chunk off screen. Its direction is that of its view-space x and y (a chunk straight behind, with none, points
    /// up); a chunk that is on screen is not added.
    /// </summary>
    public void Add(in OreMarker marker)
    {
        if (marker.OnScreen) return;
        var direction = new float2(marker.View.X, -marker.View.Y);
        var length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        direction = length > 1e-4f ? direction / length : new float2(0, -1);
        var sector = SectorOf(direction);
        _count[sector]++;
        if (marker.Distance >= _nearest[sector]) return;
        _nearest[sector] = marker.Distance;
        _resource[sector] = marker.Resource;
        _direction[sector] = direction;
    }

    /// <summary>
    /// Where the arrow of <paramref name="sector"/> sits: the point where its direction from the window's centre crosses
    /// the window inset by <paramref name="margin"/>.
    /// </summary>
    public float2 Place(int sector, float2 display, float margin)
    {
        var centre = display * 0.5f;
        var direction = _direction[sector];
        var halfX = MathF.Max(1f, centre.X - margin);
        var halfY = MathF.Max(1f, centre.Y - margin);
        var tx = MathF.Abs(direction.X) > 1e-5f ? halfX / MathF.Abs(direction.X) : float.PositiveInfinity;
        var ty = MathF.Abs(direction.Y) > 1e-5f ? halfY / MathF.Abs(direction.Y) : float.PositiveInfinity;
        return centre + direction * MathF.Min(tx, ty);
    }
}
