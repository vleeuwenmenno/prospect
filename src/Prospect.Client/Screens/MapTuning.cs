using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// How the galaxy map and the command view show exploration, from <c>content/visuals/map.json</c>: fog, sensor
/// coverage, the tilted view, the sector card and the scan readout. Client only; nothing here decides what the
/// player knows, which is the server's.
/// </summary>
internal sealed class MapTuning
{
    /// <summary>How dark the veil over space the player has not charted is, 0 to 1.</summary>
    public float FogAlpha { get; init; }

    /// <summary>How strongly a charted sector is tinted, 0 to 1.</summary>
    public float ChartedAlpha { get; init; }

    /// <summary>Fill and outline strength of live sensor coverage, 0 to 1.</summary>
    public float LiveFillAlpha { get; init; }
    public float LiveRingAlpha { get; init; }

    /// <summary>The smallest a sensor ring is drawn, in unscaled pixels, so a far-out map still shows it.</summary>
    public float MinCoveragePixels { get; init; }

    /// <summary>The layer falloff below which the disc is taken to hold no matter: the map's vertical edge.</summary>
    public float EdgeFalloff { get; init; }

    /// <summary>The tilted view's pitch, in degrees from looking straight down.</summary>
    public float TiltDegrees { get; init; }

    /// <summary>How far apart layers are drawn in the tilted view, in sector widths.</summary>
    public float TiltLayerSpacing { get; init; }

    /// <summary>How many layers above and below the chosen one the tilted view also shows.</summary>
    public int TiltLayers { get; init; }

    /// <summary>Seconds the map takes to tilt or flatten.</summary>
    public float TiltSeconds { get; init; }

    /// <summary>The sector card's width, its visible ship rows before it scrolls, and its gap from the sector, unscaled.</summary>
    public float CardWidth { get; init; }
    public int CardListRows { get; init; }
    public float CardGap { get; init; }

    /// <summary>Cells at least this many pixels wide show a charted sector's asteroid fields.</summary>
    public float FieldDetailCell { get; init; }

    /// <summary>At most this many charted sectors have their fields worked out in one frame; the rest wait.</summary>
    public int FieldSectorsPerFrame { get; init; }

    /// <summary>How bright a stale contact is drawn relative to a live one, 0 to 1.</summary>
    public float StaleFade { get; init; }

    /// <summary>Seconds a finished, cancelled or refused scan stays on the flight display.</summary>
    public float ScanResultSeconds { get; init; }

    /// <summary>How dark the command view's fog outside sensor coverage is, and how far out it reaches in sensor radii.</summary>
    public float CommandFogAlpha { get; init; }
    public float CommandFogReach { get; init; }

    /// <summary>The command view's sensor boundary line: strength, 0 to 1, and width in unscaled pixels.</summary>
    public float CommandRingAlpha { get; init; }
    public float CommandRingWidth { get; init; }

    /// <summary>How many faint strokes make the fade outside the boundary, and how wide each is in unscaled pixels.</summary>
    public int CommandFadeRings { get; init; }
    public float CommandFadeWidth { get; init; }

    /// <summary>
    /// How far apart, in metres between hull centres, ships' radars link into one shared picture: selected ships
    /// within this of each other, directly or through a chain, are drawn as one merged coverage outline. Display
    /// only; every ship's own radar still reports to the server by itself.
    /// </summary>
    public float RadarLinkMeters { get; init; }

    /// <summary>How faint the line between linked ships is, and how faint the tint where gas cut a sensor's range is, 0 to 1.</summary>
    public float LinkLineAlpha { get; init; }
    public float GasTintAlpha { get; init; }

    /// <summary>
    /// A blind jump's hit chance, 0 to 1, from which the course menu and the sector card draw it in the warning
    /// colour instead of amber, and how many seconds the server's refusal of a plot or a jump stays on screen.
    /// </summary>
    public float CourseRiskDanger { get; init; }
    public float CourseRefusalSeconds { get; init; }

    public static MapTuning Load() => Load(AppPaths.Content("visuals", "map.json"));

    /// <summary>Reads and checks the tuning; anything out of range is refused.</summary>
    public static MapTuning Load(string path)
    {
        MapTuning tuning;
        try
        {
            tuning = JsonSerializer.Deserialize<MapTuning>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        static bool Unit(float value) => value is >= 0f and <= 1f;
        if (!Unit(tuning.FogAlpha) || !Unit(tuning.ChartedAlpha) || !Unit(tuning.LiveFillAlpha) || !Unit(tuning.LiveRingAlpha)
            || !Unit(tuning.StaleFade) || !Unit(tuning.CommandFogAlpha) || !Unit(tuning.CommandRingAlpha) || !Unit(tuning.CourseRiskDanger))
            throw new InvalidDataException($"{path}: alphas and fades are 0 to 1");
        if (!(tuning.MinCoveragePixels is >= 0 and <= 64))
            throw new InvalidDataException($"{path}: minCoveragePixels is 0 to 64");
        if (!(tuning.EdgeFalloff is > 0 and < 1))
            throw new InvalidDataException($"{path}: edgeFalloff is between 0 and 1");
        if (!(tuning.TiltDegrees is >= 10 and <= 80) || !(tuning.TiltLayerSpacing is >= 0.25f and <= 8) || tuning.TiltLayers is < 0 or > 8
            || !(tuning.TiltSeconds is >= 0 and <= 2))
            throw new InvalidDataException($"{path}: tilt is 10 to 80 degrees, spacing 0.25 to 8, 0 to 8 layers, 0 to 2 s");
        if (!(tuning.CardWidth is >= 240 and <= 640) || tuning.CardListRows is < 2 or > 32 || !(tuning.CardGap is >= 0 and <= 64))
            throw new InvalidDataException($"{path}: card is 240 to 640 wide, 2 to 32 rows, gap 0 to 64");
        if (!(tuning.FieldDetailCell is >= 20 and <= 400) || tuning.FieldSectorsPerFrame is < 1 or > 64)
            throw new InvalidDataException($"{path}: fieldDetailCell is 20 to 400, fieldSectorsPerFrame 1 to 64");
        if (!(tuning.ScanResultSeconds is >= 0 and <= 120) || !(tuning.CommandFogReach is >= 1 and <= 20))
            throw new InvalidDataException($"{path}: scanResultSeconds is 0 to 120, commandFogReach 1 to 20");
        if (!(tuning.CommandRingWidth is >= 0.5f and <= 8) || tuning.CommandFadeRings is < 1 or > 16 || !(tuning.CommandFadeWidth is >= 1 and <= 64))
            throw new InvalidDataException($"{path}: commandRingWidth is 0.5 to 8, commandFadeRings 1 to 16, commandFadeWidth 1 to 64");
        if (!Unit(tuning.LinkLineAlpha) || !Unit(tuning.GasTintAlpha) || !(tuning.RadarLinkMeters is >= 0 and <= 200000))
            throw new InvalidDataException($"{path}: linkLineAlpha and gasTintAlpha are 0 to 1, radarLinkMeters 0 to 200000");
        if (!(tuning.CourseRefusalSeconds is >= 0 and <= 60))
            throw new InvalidDataException($"{path}: courseRefusalSeconds is 0 to 60");
        return tuning;
    }
}
