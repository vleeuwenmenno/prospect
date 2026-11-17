using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// How the interface shows action groups, from <c>content/visuals/action_groups.json</c>: the strip's cell, its
/// colours for a group that is off, on, firing, cutting and overheated, its heat bar, the words of every part kind and
/// the popup's figures.
/// Client data; nothing here decides what fires, which is the server's. Words are encoded to UTF-8 once, at load.
/// </summary>
internal sealed class ActionGroupLook
{
    private static ActionGroupLook? _current;
    private static readonly string[] KindIds = ["laser"];

    private byte[][] _kinds = [];

    [JsonPropertyName("kinds")]
    public KindJson[] KindFile { get; init; } = [];

    /// <summary>A cell's width and height in unscaled pixels; the cell shrinks to <see cref="MinCellWidth"/> on a narrow screen.</summary>
    public float CellWidth { get; init; }

    public float CellHeight { get; init; }

    public float MinCellWidth { get; init; }

    /// <summary>The space between cells, and between the strip and what lies under it, in unscaled pixels.</summary>
    public float Gap { get; init; }

    public float RowGap { get; init; }

    /// <summary>How strongly a cell is drawn while its group is off, on, and on while the fire button is held.</summary>
    public float OffAlpha { get; init; }

    public float OnAlpha { get; init; }

    public float FiringAlpha { get; init; }

    /// <summary>The cell's accent for an off group, an on one, one firing, and the mark of a laser that is cutting rock.</summary>
    public uint OffRgb { get; private set; }

    public uint OnRgb { get; private set; }

    public uint FiringRgb { get; private set; }

    public uint CuttingRgb { get; private set; }

    [JsonPropertyName("offColor")]
    public string OffColorFile { get; init; } = "";

    [JsonPropertyName("onColor")]
    public string OnColorFile { get; init; } = "";

    [JsonPropertyName("firingColor")]
    public string FiringColorFile { get; init; } = "";

    [JsonPropertyName("cuttingColor")]
    public string CuttingColorFile { get; init; } = "";

    /// <summary>The size of the cutting mark's square, in unscaled pixels.</summary>
    public float MarkPixels { get; init; }

    /// <summary>The height of a cell's heat bar, in unscaled pixels, and how strongly its empty track shows.</summary>
    public float HeatBarPixels { get; init; }

    public float HeatTrackAlpha { get; init; }

    /// <summary>The heat bar's colour while cool, from <see cref="HeatWarmFrom"/> and from <see cref="HeatHotFrom"/> (shares of full heat).</summary>
    public uint HeatCoolRgb { get; private set; }

    public uint HeatWarmRgb { get; private set; }

    public uint HeatHotRgb { get; private set; }

    public float HeatWarmFrom { get; init; }

    public float HeatHotFrom { get; init; }

    /// <summary>The accent of a cell whose lasers are all shut down by heat, how strongly it is drawn, and the colour of a cold cloud's line.</summary>
    public uint OverheatedRgb { get; private set; }

    public float OverheatedAlpha { get; init; }

    public uint ColdRgb { get; private set; }

    [JsonPropertyName("heatCoolColor")]
    public string HeatCoolColorFile { get; init; } = "";

    [JsonPropertyName("heatWarmColor")]
    public string HeatWarmColorFile { get; init; } = "";

    [JsonPropertyName("heatHotColor")]
    public string HeatHotColorFile { get; init; } = "";

    [JsonPropertyName("overheatedColor")]
    public string OverheatedColorFile { get; init; } = "";

    [JsonPropertyName("coldColor")]
    public string ColdColorFile { get; init; } = "";

    /// <summary>What an overheated cell says in place of its parts, and the shorter word for a narrow cell.</summary>
    [JsonPropertyName("overheatedLabel")]
    public string OverheatedLabelFile { get; init; } = "";

    [JsonPropertyName("overheatedShortLabel")]
    public string OverheatedShortLabelFile { get; init; } = "";

    private byte[] _overheated = [], _overheatedShort = [];

    /// <summary>The overheated cell's word, encoded.</summary>
    [JsonIgnore]
    public ReadOnlySpan<byte> OverheatedLabel => _overheated;

    /// <summary>The overheated cell's shorter word, encoded.</summary>
    [JsonIgnore]
    public ReadOnlySpan<byte> OverheatedShortLabel => _overheatedShort;

    /// <summary>The width of a cell's tooltip, in unscaled pixels.</summary>
    public float TipWidth { get; init; }

    /// <summary>The heat bar's colour at <paramref name="heat"/> (0 to 1): cool, warm or hot, in steps so it never passes through a muddy blend.</summary>
    public uint HeatRgb(float heat) => heat >= HeatHotFrom ? HeatHotRgb : heat >= HeatWarmFrom ? HeatWarmRgb : HeatCoolRgb;

    /// <summary>The popup's width, how many parts it shows before it scrolls, and the height of a selector row, in unscaled pixels.</summary>
    public float PopupWidth { get; init; }

    public int PopupVisibleRows { get; init; }

    public float SelectorHeight { get; init; }

    /// <summary>The shipped figures, read once.</summary>
    public static ActionGroupLook Current => _current ??= Load(AppPaths.Content("visuals", "action_groups.json"));

    /// <summary>The word of a part kind, upper case and encoded.</summary>
    public ReadOnlySpan<byte> Kind(ActionPartKind kind) => _kinds[Math.Min((int)kind, _kinds.Length - 1)];

    /// <summary>Reads and checks a set of figures; anything out of range is refused.</summary>
    public static ActionGroupLook Load(string path)
    {
        ActionGroupLook look;
        try
        {
            look = JsonSerializer.Deserialize<ActionGroupLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        look.Build(path);
        return look;
    }

    private static uint Rgb(string path, string what, string hex)
    {
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var value))
            throw new InvalidDataException($"{path}: {what} colour is six hex digits");
        return value;
    }

    private static bool Unit(float value) => value is >= 0 and <= 1;

    private void Build(string path)
    {
        if (!(CellWidth is >= 60 and <= 240) || !(CellHeight is >= 20 and <= 80) || !(MinCellWidth is >= 24 and <= 120) || MinCellWidth > CellWidth
            || !(Gap is >= 0 and <= 32) || !(RowGap is >= 0 and <= 48))
            throw new InvalidDataException($"{path}: cellWidth is 60 to 240, cellHeight 20 to 80, minCellWidth 24 to 120 and no more than cellWidth, gap 0 to 32, rowGap 0 to 48");
        if (!Unit(OffAlpha) || !Unit(OnAlpha) || !Unit(FiringAlpha))
            throw new InvalidDataException($"{path}: alphas are 0 to 1");
        if (!(MarkPixels is >= 2 and <= 16) || !(PopupWidth is >= 300 and <= 700) || PopupVisibleRows is < 1 or > 16 || !(SelectorHeight is >= 18 and <= 48))
            throw new InvalidDataException($"{path}: markPixels is 2 to 16, popupWidth 300 to 700, popupVisibleRows 1 to 16, selectorHeight 18 to 48");
        if (!(HeatBarPixels is >= 1 and <= 8) || !Unit(HeatTrackAlpha) || !Unit(OverheatedAlpha) || !(HeatWarmFrom is > 0 and < 1) || !(HeatHotFrom < 1) || !(HeatHotFrom > HeatWarmFrom)
            || !(TipWidth is >= 240 and <= 600))
            throw new InvalidDataException($"{path}: heatBarPixels is 1 to 8, heatTrackAlpha and overheatedAlpha 0 to 1, heatWarmFrom above 0, heatHotFrom above it and below 1, tipWidth 240 to 600");
        if (string.IsNullOrWhiteSpace(OverheatedLabelFile) || OverheatedLabelFile.Length > 12 || string.IsNullOrWhiteSpace(OverheatedShortLabelFile) || OverheatedShortLabelFile.Length > 6)
            throw new InvalidDataException($"{path}: overheatedLabel is 1 to 12 characters and overheatedShortLabel 1 to 6");
        _overheated = Encoding.UTF8.GetBytes(OverheatedLabelFile);
        _overheatedShort = Encoding.UTF8.GetBytes(OverheatedShortLabelFile);
        HeatCoolRgb = Rgb(path, "heatCool", HeatCoolColorFile);
        HeatWarmRgb = Rgb(path, "heatWarm", HeatWarmColorFile);
        HeatHotRgb = Rgb(path, "heatHot", HeatHotColorFile);
        OverheatedRgb = Rgb(path, "overheated", OverheatedColorFile);
        ColdRgb = Rgb(path, "cold", ColdColorFile);
        OffRgb = Rgb(path, "off", OffColorFile);
        OnRgb = Rgb(path, "on", OnColorFile);
        FiringRgb = Rgb(path, "firing", FiringColorFile);
        CuttingRgb = Rgb(path, "cutting", CuttingColorFile);
        _kinds = new byte[KindIds.Length][];
        for (var k = 0; k < KindIds.Length; k++)
        {
            var row = Array.Find(KindFile, x => x.Kind == KindIds[k]) ?? throw new InvalidDataException($"{path}: no word for the part kind {KindIds[k]}");
            if (string.IsNullOrWhiteSpace(row.Label) || row.Label.Length > 12)
                throw new InvalidDataException($"{path}: a part kind needs a word of 1 to 12 characters");
            _kinds[k] = Encoding.UTF8.GetBytes(row.Label);
        }
    }

    public sealed class KindJson { public string Kind { get; init; } = ""; public string Label { get; init; } = ""; }
}
