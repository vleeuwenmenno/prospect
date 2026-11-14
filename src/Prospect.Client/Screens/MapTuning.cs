using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>Cells at least this many pixels wide show a charted sector's asteroid fields and gas clouds.</summary>
    public float FieldDetailCell { get; init; }

    /// <summary>How bright a stale contact is drawn relative to a live one, 0 to 1.</summary>
    public float StaleFade { get; init; }

    /// <summary>Seconds a finished, cancelled or refused scan stays on the flight display.</summary>
    public float ScanResultSeconds { get; init; }

    /// <summary>The scan readout on the flight and command displays: width in unscaled pixels, strength of the running scan and result, and of the muted cooldown line under them, 0 to 1.</summary>
    public float ScanStripWidth { get; init; }
    public float ScanStripAlpha { get; init; }
    public float ScanCooldownAlpha { get; init; }

    /// <summary>Unscaled pixels between the jump strip at the top centre and the target readout or scan strip above it.</summary>
    public float JumpStripGap { get; init; }

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

    /// <summary>How faint the line between linked ships is, and how faint the outline of the plain range where gas cut a sensor's reach is, 0 to 1.</summary>
    public float LinkLineAlpha { get; init; }
    public float GasRangeAlpha { get; init; }

    /// <summary>
    /// A blind jump's hit chance, 0 to 1, from which the course menu and the sector card draw it in the warning
    /// colour instead of amber, and how many seconds the server's refusal of a plot or a jump stays on screen.
    /// </summary>
    public float CourseRiskDanger { get; init; }
    public float CourseRefusalSeconds { get; init; }

    /// <summary>The colour of asteroid fields and of gas clouds on the map, as six hex digits (RRGGBB).</summary>
    public string FieldColor { get; init; } = "FFB020";
    public string CloudColor { get; init; } = "B78CF0";

    /// <summary>How strongly a field is filled (its whole support, then its denser core) and outlined, 0 to 1.</summary>
    public float FieldFillAlpha { get; init; }
    public float FieldCoreAlpha { get; init; }
    public float FieldLineAlpha { get; init; }

    /// <summary>How much of a field's width its core outline keeps, 0.1 to 1.</summary>
    public float FieldCoreShare { get; init; }

    /// <summary>How strongly a gas cloud's fringe and its body are filled, and its body outlined, 0 to 1.</summary>
    public float CloudFringeAlpha { get; init; }
    public float CloudBodyAlpha { get; init; }
    public float CloudLineAlpha { get; init; }

    /// <summary>The star icon's radius in unscaled pixels, how strongly its shape and its colour dot are drawn.</summary>
    public float StarIconSize { get; init; }
    public float StarIconAlpha { get; init; }
    public float StarDotAlpha { get; init; }

    /// <summary>The icon shape of each star class, by the class's name in <c>galaxy.json</c>: circle, square, diamond, sparkle or asterisk.</summary>
    public List<StarIconStyle> StarIcons { get; init; } = [];

    /// <summary>How near, in unscaled pixels, the pointer must be to one of the player's hulls on the map to name it, and how strong the label is.</summary>
    public float UnitHoverPixels { get; init; }
    public float UnitLabelAlpha { get; init; }

    /// <summary>The map legend's row height in unscaled pixels.</summary>
    public float LegendRowHeight { get; init; }

    /// <summary>The colour of asteroid fields as 0xRRGGBB.</summary>
    [JsonIgnore]
    public uint FieldRgb => Rgb(FieldColor);

    /// <summary>The colour of gas clouds as 0xRRGGBB.</summary>
    [JsonIgnore]
    public uint CloudRgb => Rgb(CloudColor);

    /// <summary>The shape the map draws for a star class, a circle for one the file does not name.</summary>
    public StarIconShape ShapeOf(string starClass)
    {
        foreach (var icon in StarIcons)
        {
            if (icon.Star == starClass)
                return icon.ParsedShape;
        }

        return StarIconShape.Circle;
    }

    private static uint Rgb(string hex) => uint.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var value) && hex.Length == 6 ? value : 0xFFFFFF;

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
        if (!(tuning.FieldDetailCell is >= 20 and <= 400))
            throw new InvalidDataException($"{path}: fieldDetailCell is 20 to 400");
        if (!(tuning.ScanResultSeconds is >= 0 and <= 120) || !(tuning.CommandFogReach is >= 1 and <= 20))
            throw new InvalidDataException($"{path}: scanResultSeconds is 0 to 120, commandFogReach 1 to 20");
        if (!(tuning.CommandRingWidth is >= 0.5f and <= 8) || tuning.CommandFadeRings is < 1 or > 16 || !(tuning.CommandFadeWidth is >= 1 and <= 64))
            throw new InvalidDataException($"{path}: commandRingWidth is 0.5 to 8, commandFadeRings 1 to 16, commandFadeWidth 1 to 64");
        if (!Unit(tuning.LinkLineAlpha) || !Unit(tuning.GasRangeAlpha) || !(tuning.RadarLinkMeters is >= 0 and <= 200000))
            throw new InvalidDataException($"{path}: linkLineAlpha and gasRangeAlpha are 0 to 1, radarLinkMeters 0 to 200000");
        if (!(tuning.ScanStripWidth is >= 120 and <= 600) || !Unit(tuning.ScanStripAlpha) || !Unit(tuning.ScanCooldownAlpha))
            throw new InvalidDataException($"{path}: scanStripWidth is 120 to 600, scanStripAlpha and scanCooldownAlpha 0 to 1");
        if (!(tuning.JumpStripGap is >= 0 and <= 64))
            throw new InvalidDataException($"{path}: jumpStripGap is 0 to 64");
        if (!(tuning.CourseRefusalSeconds is >= 0 and <= 60))
            throw new InvalidDataException($"{path}: courseRefusalSeconds is 0 to 60");
        static bool Hex(string value) => value.Length == 6 && uint.TryParse(value, System.Globalization.NumberStyles.AllowHexSpecifier, null, out _);
        if (!Hex(tuning.FieldColor) || !Hex(tuning.CloudColor))
            throw new InvalidDataException($"{path}: fieldColor and cloudColor are six hex digits");
        if (!Unit(tuning.FieldFillAlpha) || !Unit(tuning.FieldCoreAlpha) || !Unit(tuning.FieldLineAlpha) || !Unit(tuning.CloudFringeAlpha)
            || !Unit(tuning.CloudBodyAlpha) || !Unit(tuning.CloudLineAlpha) || !Unit(tuning.StarIconAlpha) || !Unit(tuning.StarDotAlpha) || !Unit(tuning.UnitLabelAlpha))
            throw new InvalidDataException($"{path}: feature, star icon and label strengths are 0 to 1");
        if (!(tuning.FieldCoreShare is >= 0.1f and <= 1f))
            throw new InvalidDataException($"{path}: fieldCoreShare is 0.1 to 1");
        if (!(tuning.StarIconSize is >= 2 and <= 16) || !(tuning.UnitHoverPixels is >= 4 and <= 64) || !(tuning.LegendRowHeight is >= 12 and <= 40))
            throw new InvalidDataException($"{path}: starIconSize is 2 to 16, unitHoverPixels 4 to 64, legendRowHeight 12 to 40");
        foreach (var icon in tuning.StarIcons)
        {
            if (string.IsNullOrWhiteSpace(icon.Star) || !Enum.TryParse<StarIconShape>(icon.Shape, true, out _))
                throw new InvalidDataException($"{path}: every star icon names a star class and a shape (circle, square, diamond, sparkle, asterisk)");
        }

        return tuning;
    }
}

/// <summary>The shapes a star class's icon can take on the map.</summary>
internal enum StarIconShape : byte
{
    Circle,
    Square,
    Diamond,
    Sparkle,
    Asterisk,
}

/// <summary>One star class's icon: the class's name in the galaxy content and the shape's name.</summary>
internal sealed class StarIconStyle
{
    public string Star { get; init; } = "";
    public string Shape { get; init; } = "";

    /// <summary>The shape, a circle where the name is not one.</summary>
    [JsonIgnore]
    public StarIconShape ParsedShape => Enum.TryParse<StarIconShape>(Shape, true, out var shape) ? shape : StarIconShape.Circle;
}
