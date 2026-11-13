using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.World;

/// <summary>
/// How far gas clouds are seen and how much work each level of detail spends on them, from
/// <c>content/visuals/gas.json</c>. Client data: it changes what is drawn, never what a cloud is.
///
/// A cloud's level of detail follows its distance in radii: the distance from the camera to its centre divided by
/// its largest half-extent. A cloud of any size therefore changes level at the same size on screen.
/// </summary>
internal sealed class GasTiers
{
    /// <summary>The most sectors the view range may be: it sets how many sectors the cloud list is made from.</summary>
    public const float MaxViewRangeSectors = 8;

    /// <summary>The most cloud draws a frame may queue, whatever the file says.</summary>
    public const int DrawCapacity = Render.GasQueue.Capacity;

    /// <summary>Distance in sector edges beyond which no cloud is drawn.</summary>
    public float ViewRangeSectors { get; init; }

    /// <summary>The last share of the view range over which a cloud fades away, 0.05 to 1.</summary>
    public float RangeFade { get; init; }

    /// <summary>Radii up to which a cloud is marched at full quality, as it always was up close.</summary>
    public float NearEnd { get; init; }

    /// <summary>Radii over which the full march hands over to the middle level.</summary>
    public float NearFade { get; init; }

    /// <summary>Radii up to which the middle level is used.</summary>
    public float MidEnd { get; init; }

    /// <summary>Radii over which the middle level hands over to the far one.</summary>
    public float MidFade { get; init; }

    /// <summary>View samples of the middle level, which keeps the full density but lights it with fewer probes.</summary>
    public int MidSamples { get; init; }

    /// <summary>View samples of the far level, a handful: the cloud is small on screen by then.</summary>
    public int FarSamples { get; init; }

    /// <summary>The far level's density: 2 with every octave, 1 with the finest one smoothed away.</summary>
    public int FarDetail { get; init; }

    /// <summary>The most cloud draws in one frame; the farthest are left out first.</summary>
    public int MaxDraws { get; init; }

    public static GasTiers Load() => Load(AppPaths.Content("visuals", "gas.json"));

    /// <summary>Reads and checks the levels; anything out of range is refused.</summary>
    public static GasTiers Load(string path)
    {
        var tiers = JsonSerializer.Deserialize<GasTiers>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"{path}: empty");
        if (!(tiers.ViewRangeSectors is >= 0.5f and <= MaxViewRangeSectors) || !(tiers.RangeFade is >= 0.05f and <= 1))
            throw new InvalidDataException($"{path}: viewRangeSectors is 0.5 to {MaxViewRangeSectors}, rangeFade 0.05 to 1");
        // Two radii is outside any box, whatever its proportions: the levels past the full one assume a camera
        // outside the cloud.
        if (!(tiers.NearEnd is >= 2f and <= 16) || !(tiers.NearFade is >= 0.1f and <= 16))
            throw new InvalidDataException($"{path}: nearEnd is 2 to 16 radii, nearFade 0.1 to 16");
        // The far level starts only once the full march has gone, so at most two levels of a cloud are ever drawn.
        if (!(tiers.MidEnd >= tiers.NearEnd + tiers.NearFade && tiers.MidEnd <= 64) || !(tiers.MidFade is >= 0.1f and <= 64))
            throw new InvalidDataException($"{path}: midEnd is nearEnd + nearFade to 64 radii, midFade 0.1 to 64");
        if (tiers.MidSamples is < 8 or > 64 || tiers.FarSamples is < 4 or > 32 || tiers.FarDetail is < 1 or > 2)
            throw new InvalidDataException($"{path}: midSamples is 8 to 64, farSamples 4 to 32, farDetail 1 or 2");
        if (tiers.MaxDraws is < 4 or > DrawCapacity)
            throw new InvalidDataException($"{path}: maxDraws is 4 to {DrawCapacity}");
        return tiers;
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// How much of a cloud each level draws, for a camera <paramref name="distance"/> metres from the centre of a
    /// cloud whose largest half-extent is <paramref name="radius"/>. The three shares add up to the cloud's
    /// visibility: 1 inside the view range, falling to 0 at its end. Every share is continuous in the distance, so
    /// no level appears or disappears at once.
    /// </summary>
    public void Shares(float distance, float radius, float sectorSize, out float near, out float mid, out float far)
    {
        var range = ViewRangeSectors * sectorSize;
        var visible = 1f - Smooth(range * (1f - RangeFade), range, distance);
        var radii = distance / MathF.Max(radius, 1f);
        var pastNear = Smooth(NearEnd, NearEnd + NearFade, radii);
        var pastMid = Smooth(MidEnd, MidEnd + MidFade, radii);
        near = visible * (1f - pastNear);
        mid = visible * (pastNear - pastMid);
        far = visible * pastMid;
    }

    private static float Smooth(float from, float to, float value)
    {
        var t = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
