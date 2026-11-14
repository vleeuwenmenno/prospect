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

    /// <summary>What a large cloud gains close to the camera, so that it is not a flat wall from inside.</summary>
    public GasCloseUp CloseUp { get; init; } = new();

    /// <summary>How a cold cloud differs from an ordinary one: colour, crisper structure and ice glints.</summary>
    public GasColdLook Cold { get; init; } = new();

    /// <summary>How a lightning cloud looks and how its flashes and bolts are drawn.</summary>
    public GasLightningLook Lightning { get; init; } = new();

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
        var close = tiers.CloseUp;
        if (!(close.FromHalfSize is >= 1000 and <= 1e6f) || !(close.FullHalfSize > close.FromHalfSize && close.FullHalfSize <= 2e6f))
            throw new InvalidDataException($"{path}: closeUp.fromHalfSize is 1000 to 1000000 m and below fullHalfSize");
        if (!(close.Strands is >= 0 and <= 1) || !(close.StepBend is >= 0 and <= 0.98f) || !(close.Reach is >= 0.1f and <= 4) || !(close.Clearing is >= 0 and <= 0.9f))
            throw new InvalidDataException($"{path}: closeUp.strands is 0 to 1, stepBend 0 to 0.98, reach 0.1 to 4 half-extents, clearing 0 to 0.9");
        var cold = tiers.Cold;
        if (!Colour(cold.Tint, 0, 2) || !(cold.Edge is >= 0.3f and <= 1.5f) || !(cold.Knots is >= 0 and <= 2) || !(cold.Wisps is >= 0 and <= 2)
            || !(cold.Veil is >= 0 and <= 2) || !(cold.Extinction is >= 0.25f and <= 2) || !(cold.Halo is >= 0 and <= 4))
            throw new InvalidDataException($"{path}: cold.tint is three numbers 0 to 2, edge 0.3 to 1.5, knots, wisps and veil 0 to 2, extinction 0.25 to 2, halo 0 to 4");
        if (!(cold.Sparkle is >= 0 and <= 20) || !(cold.SparkleCell is >= 10 and <= 2000) || !(cold.SparkleShare is >= 0 and <= 0.5f) || !(cold.SparkleSize is >= 0.5f and <= 50))
            throw new InvalidDataException($"{path}: cold.sparkle is 0 to 20, sparkleCell 10 to 2000 m, sparkleShare 0 to 0.5, sparkleSize 0.5 to 50 m");
        var lightning = tiers.Lightning;
        if (!Colour(lightning.Tint, 0, 2) || !Colour(lightning.FlashColor, 0, 100) || !Colour(lightning.BoltColor, 0, 1000))
            throw new InvalidDataException($"{path}: lightning.tint is three numbers 0 to 2, flashColor 0 to 100, boltColor 0 to 1000");
        if (!(lightning.FlashRadius is >= 0.02f and <= 1.5f) || lightning.Strokes is < 1 or > 8 || !(lightning.StrokeDecay is >= 0.01f and <= 1)
            || !(lightning.Flicker is >= 0 and <= 1) || !(lightning.FlashSpread is >= 0 and <= 0.8f))
            throw new InvalidDataException($"{path}: lightning.flashRadius is 0.02 to 1.5, strokes 1 to 8, strokeDecay 0.01 to 1 s, flicker 0 to 1, flashSpread 0 to 0.8");
        if (!(lightning.FlashReach is >= 100 and <= 50000))
            throw new InvalidDataException($"{path}: lightning.flashReach is 100 to 50000 m");
        if (!(lightning.BoltNear is >= 0 and <= 200000))
            throw new InvalidDataException($"{path}: lightning.boltNear is 0 to 200000 m");
        if (lightning.Bolts is < 0 or > 8 || !(lightning.BoltCell is >= 200 and <= 20000) || !(lightning.BoltCellChance is >= 0 and <= 1)
            || !(lightning.BoltLengthMin is >= 50 and <= 20000) || !(lightning.BoltLengthMax >= lightning.BoltLengthMin && lightning.BoltLengthMax <= 20000)
            || !(lightning.BoltSegment is >= 5 and <= 2000) || !(lightning.BoltJagged is >= 0 and <= 2) || !(lightning.BoltBranch is >= 0 and <= 1)
            || lightning.BoltDepth is < 0 or > 5)
            throw new InvalidDataException($"{path}: lightning.bolts is 0 to 8, boltCell 200 to 20000 m, boltCellChance 0 to 1, boltLengthMin 50 to 20000 m and not above boltLengthMax, boltSegment 5 to 2000 m, boltJagged 0 to 2, boltBranch 0 to 1, boltDepth 0 to 5");
        if (!(lightning.BoltBrightness is >= 0 and <= 20) || !(lightning.BoltWidth is >= 0.5f and <= 500) || !(lightning.BoltGlowWidth is >= 1 and <= 5000)
            || !(lightning.BoltGlow is >= 0 and <= 1) || !(lightning.BoltTip is >= 0 and <= 10))
            throw new InvalidDataException($"{path}: lightning.boltBrightness is 0 to 20, boltWidth 0.5 to 500 m, boltGlowWidth 1 to 5000 m, boltGlow 0 to 1, boltTip 0 to 10");
        if (!(lightning.BoltGrow is >= 0.01f and <= 2) || !(lightning.BoltHold is >= 0 and <= 2) || !(lightning.BoltFade is >= 0.01f and <= 2)
            || !(lightning.BoltAfterglow is >= 0 and <= 1) || !(lightning.BoltFlicker is >= 0 and <= 1) || !(lightning.BoltLight is >= 0 and <= 20)
            || !(lightning.BoltLightRadius is >= 10 and <= 20000) || !(lightning.FarFlash is >= 0 and <= 4))
            throw new InvalidDataException($"{path}: lightning.boltGrow is 0.01 to 2 s, boltHold 0 to 2 s, boltFade 0.01 to 2 s, boltAfterglow and boltFlicker 0 to 1, boltLight 0 to 20, boltLightRadius 10 to 20000 m, farFlash 0 to 4");
        return tiers;

        static bool Colour(float[] value, float min, float max) =>
            value is { Length: 3 } && value.All(v => float.IsFinite(v) && v >= min && v <= max);
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

    /// <summary>
    /// The close-up figures for a cloud with this x half-extent, as the shader takes them: nothing at or below
    /// <see cref="GasCloseUp.FromHalfSize"/>, rising evenly to the full figures at <see cref="GasCloseUp.FullHalfSize"/>.
    /// </summary>
    public Brutal.Numerics.float4 CloseUpFor(float halfSize)
    {
        var share = Math.Clamp((halfSize - CloseUp.FromHalfSize) / (CloseUp.FullHalfSize - CloseUp.FromHalfSize), 0f, 1f);
        return share <= 0 ? default : new Brutal.Numerics.float4(CloseUp.Strands * share, CloseUp.StepBend * share, CloseUp.Reach, CloseUp.Clearing * share);
    }

    private static float Smooth(float from, float to, float value)
    {
        var t = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// Close to the camera a large cloud's own octaves are kilometres across, so it reads as a wall. These figures add
/// finer strands there and shorten the first steps of the march to follow them. A cloud of ordinary size takes none.
/// </summary>
internal sealed class GasCloseUp
{
    /// <summary>The x half-extent in metres above which a cloud starts to take the close-up figures.</summary>
    public float FromHalfSize { get; init; } = 7000;

    /// <summary>The x half-extent in metres from which it takes them in full.</summary>
    public float FullHalfSize { get; init; } = 10000;

    /// <summary>How strongly the gas near the camera is broken into strands and gaps, 0 to 1.</summary>
    public float Strands { get; init; }

    /// <summary>How far the march's steps are drawn toward the camera, 0 (even) to 0.98.</summary>
    public float StepBend { get; init; }

    /// <summary>How far from the camera all of it reaches, in x half-extents.</summary>
    public float Reach { get; init; } = 1;

    /// <summary>How much the gas nearest the camera is thinned, 0 (not) to 0.9, so the eye reaches into it.</summary>
    public float Clearing { get; init; }
}

/// <summary>
/// A cold cloud, from <c>content/visuals/gas.json</c>: drawn bluish white, with crisper billows and a few ice
/// glints. Every factor multiplies the shared figures in <c>galaxy.json</c>, so a cold cloud keeps its shape.
/// </summary>
internal sealed class GasColdLook
{
    /// <summary>Multiplies the scattered light tint, per channel.</summary>
    public float[] Tint { get; init; } = [1, 1, 1];

    /// <summary>Multiplies the edge softness: below 1 the billows end more sharply.</summary>
    public float Edge { get; init; } = 1;

    /// <summary>Multiplies the knot density.</summary>
    public float Knots { get; init; } = 1;

    /// <summary>Multiplies the wisp strength.</summary>
    public float Wisps { get; init; } = 1;

    /// <summary>Multiplies the veil density, the thin gas between knots.</summary>
    public float Veil { get; init; } = 1;

    /// <summary>Multiplies extinction per metre.</summary>
    public float Extinction { get; init; } = 1;

    /// <summary>How strong the 22 degree ice halo around the star is, added to the scattering phase; 0 draws none.</summary>
    public float Halo { get; init; }

    /// <summary>How bright an ice glint is, in units of the star's light; 0 draws none.</summary>
    public float Sparkle { get; init; }

    /// <summary>The edge of the cells glints sit in, in metres: at most one glint per cell.</summary>
    public float SparkleCell { get; init; } = 120;

    /// <summary>The share of cells that hold a glint.</summary>
    public float SparkleShare { get; init; } = 0.04f;

    /// <summary>The size of a glinting crystal in metres: a glint is drawn a pixel across, and fades where the crystal is smaller.</summary>
    public float SparkleSize { get; init; } = 3;
}

/// <summary>
/// A lightning cloud, from <c>content/visuals/gas.json</c>. Its flashes follow the server's burst timing
/// (<c>GasSensors.LightningCycle</c>): a burst holds a few strokes, each a light deep in the cloud that fades, and
/// branching bolts that grow, hold and fade (<see cref="LightningBolts"/>).
/// </summary>
internal sealed class GasLightningLook
{
    /// <summary>Multiplies the scattered light tint, per channel: a storm cloud is a little darker.</summary>
    public float[] Tint { get; init; } = [1, 1, 1];

    /// <summary>The flash's light at its peak, in units of the star's light.</summary>
    public float[] FlashColor { get; init; } = [4, 4.5f, 6];

    /// <summary>How far a flash lights the gas, in x half-extents.</summary>
    public float FlashRadius { get; init; } = 0.3f;

    /// <summary>The most a deep stroke's light reaches, in metres, whatever the cloud's size.</summary>
    public float FlashReach { get; init; } = 3000;

    /// <summary>How far from the cloud's centre a stroke can be, in box units.</summary>
    public float FlashSpread { get; init; } = 0.45f;

    /// <summary>Strokes in one burst.</summary>
    public int Strokes { get; init; } = 3;

    /// <summary>How fast a stroke's light fades, in seconds to fall to a third.</summary>
    public float StrokeDecay { get; init; } = 0.12f;

    /// <summary>How much a stroke's light flickers while it fades, 0 to 1.</summary>
    public float Flicker { get; init; } = 0.5f;

    /// <summary>Bolts in the cloud's outer shell in each burst; these are what a viewer far off sees.</summary>
    public int Bolts { get; init; } = 2;

    /// <summary>
    /// The edge in metres of the cells around the camera that can strike, so a viewer inside a large cloud sees bolts
    /// near them; at least one cell near a viewer inside the cloud strikes each burst.
    /// </summary>
    public float BoltCell { get; init; } = 2500;

    /// <summary>
    /// How far in metres from a cloud's envelope a viewer outside still has strikes in the cells nearest them; further
    /// off only the shell's bolts show.
    /// </summary>
    public float BoltNear { get; init; } = 15000;

    /// <summary>The chance that a cell strikes in a burst.</summary>
    public float BoltCellChance { get; init; } = 0.1f;

    /// <summary>A bolt's main channel is this long at least, in metres.</summary>
    public float BoltLengthMin { get; init; } = 900;

    /// <summary>A bolt's main channel is this long at most, in metres.</summary>
    public float BoltLengthMax { get; init; } = 2600;

    /// <summary>The usual length of one jagged segment in metres; a long channel takes longer ones.</summary>
    public float BoltSegment { get; init; } = 60;

    /// <summary>How sharply a bolt turns at each segment, 0 straight to 2.</summary>
    public float BoltJagged { get; init; } = 0.7f;

    /// <summary>The chance per segment that a fork starts; lower on forks themselves.</summary>
    public float BoltBranch { get; init; } = 0.16f;

    /// <summary>How many levels of forks off forks a bolt has.</summary>
    public int BoltDepth { get; init; } = 3;

    /// <summary>A bolt's light colour at full strength, in units of the star's light.</summary>
    public float[] BoltColor { get; init; } = [30, 34, 45];

    /// <summary>Multiplies <see cref="BoltColor"/>.</summary>
    public float BoltBrightness { get; init; } = 1;

    /// <summary>A bolt's width in metres; never drawn under about a pixel, and fainter instead.</summary>
    public float BoltWidth { get; init; } = 6;

    /// <summary>The width in metres of the soft glow around a bolt.</summary>
    public float BoltGlowWidth { get; init; } = 120;

    /// <summary>How bright that glow is, against the bolt.</summary>
    public float BoltGlow { get; init; } = 0.05f;

    /// <summary>How much brighter the growing tip is than the channel behind it.</summary>
    public float BoltTip { get; init; } = 2;

    /// <summary>Seconds a bolt takes to grow along its main channel.</summary>
    public float BoltGrow { get; init; } = 0.09f;

    /// <summary>Seconds it holds at full strength, flickering.</summary>
    public float BoltHold { get; init; } = 0.12f;

    /// <summary>Seconds for it to fall to a third after the hold.</summary>
    public float BoltFade { get; init; } = 0.08f;

    /// <summary>How much of the main channel glows on after the stroke, 0 to 1.</summary>
    public float BoltAfterglow { get; init; } = 0.2f;

    /// <summary>How much a bolt flickers while it holds and fades, 0 to 1.</summary>
    public float BoltFlicker { get; init; } = 0.6f;

    /// <summary>How brightly a bolt lights the gas around it, against the flash colour.</summary>
    public float BoltLight { get; init; } = 1.5f;

    /// <summary>How far in metres a bolt lights the gas around it.</summary>
    public float BoltLightRadius { get; init; } = 900;

    /// <summary>Multiplies the flash at the middle and far levels, where a cloud is small and a flash is all of it.</summary>
    public float FarFlash { get; init; } = 1;
}
