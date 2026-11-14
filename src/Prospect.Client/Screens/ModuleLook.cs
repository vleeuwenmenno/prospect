using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// How the interface shows ship modules, from <c>content/visuals/modules.json</c>: the colour and word of every
/// quality tier, the families' names, gas and cloud colours, the locator's resource classes and the figures of the
/// module strip and the locator overlay. Client data; nothing here decides what a scan finds, which is the server's.
/// Everything the frame draws is encoded to UTF-8 once, at load.
/// </summary>
internal sealed class ModuleLook
{
    private static ModuleLook? _current;
    private static readonly string[] FamilyIds = ["", "gasScanner", "asteroidScanner", "asteroidLocator"];

    private ModuleTier[] _tiers = [];
    private ModuleFamilyText[] _families = [];
    private ModuleClass[] _classes = [];
    private ModuleKind[] _kinds = [];
    private Dictionary<string, uint> _gasColors = [];
    private uint[] _gasColorCache = [];
    private IReadOnlyList<string>? _gasColorNames;
    private int[] _classOf = [];
    private Resource[]? _classOfResources;

    [JsonPropertyName("tiers")]
    public TierJson[] TierFile { get; init; } = [];

    [JsonPropertyName("families")]
    public FamilyJson[] FamilyFile { get; init; } = [];

    [JsonPropertyName("gases")]
    public GasJson[] GasFile { get; init; } = [];

    [JsonPropertyName("cloudKinds")]
    public KindJson[] KindFile { get; init; } = [];

    [JsonPropertyName("locatorClasses")]
    public ClassJson[] ClassFile { get; init; } = [];

    /// <summary>The locator overlay's figures.</summary>
    public LocatorLook Locator { get; init; } = new();

    /// <summary>The module strip's figures.</summary>
    public StripLook Strip { get; init; } = new();

    /// <summary>The width, in unscaled pixels, of a gas bar on the sector card.</summary>
    public float CardBarWidth { get; init; }

    /// <summary>The GAS window's size, in unscaled pixels.</summary>
    public float GasWindowWidth { get; init; }

    public float GasWindowHeight { get; init; }

    /// <summary>A share left unresolved by the scanner is only shown when it is at least this many percentage points.</summary>
    public float UnresolvedPercent { get; init; }

    /// <summary>Height, in unscaled pixels, the target panel grows by to hold an asteroid analysis, and by for the line that says it is unscanned.</summary>
    public float TargetPanelExtra { get; init; }

    public float TargetPanelUnknownExtra { get; init; }

    /// <summary>The shipped figures, read once.</summary>
    public static ModuleLook Current => _current ??= Load(AppPaths.Content("visuals", "modules.json"));

    /// <summary>The tier of a quality.</summary>
    public ModuleTier Tier(ModuleQuality quality) => _tiers[Math.Min((int)quality, _tiers.Length - 1)];

    /// <summary>The words of a family; an unknown family reads as the first.</summary>
    public ModuleFamilyText Family(ModuleFamily family) => _families[(int)family is > 0 and < 4 ? (int)family - 1 : 0];

    /// <summary>A cloud kind's word and colour.</summary>
    public ModuleKind Kind(GasCloudKind kind) => _kinds[Math.Min((int)kind, _kinds.Length - 1)];

    /// <summary>The locator classes, in the order the legend lists them.</summary>
    [JsonIgnore]
    public ReadOnlySpan<ModuleClass> Classes => _classes;

    /// <summary>The colour a gas is drawn in, by its position in the composition's names; grey for one the file does not colour.</summary>
    public uint GasColor(IReadOnlyList<string> names, int index)
    {
        if (!ReferenceEquals(names, _gasColorNames))
        {
            _gasColorCache = new uint[names.Count];
            for (var i = 0; i < names.Count; i++)
                _gasColorCache[i] = _gasColors.TryGetValue(names[i], out var rgb) ? rgb : 0x9AA3AE;
            _gasColorNames = names;
        }

        return (uint)index < (uint)_gasColorCache.Length ? _gasColorCache[index] : 0x9AA3AE;
    }

    /// <summary>
    /// The locator class an asteroid's dominant resource falls in: an index into <see cref="Classes"/>. Bare rock
    /// (resource -1) and a resource no class lists fall in the class that is marked bare.
    /// </summary>
    public int ClassOf(Galaxy galaxy, int resource)
    {
        var resources = galaxy.Resources;
        if (!ReferenceEquals(resources, _classOfResources))
        {
            _classOf = new int[resources.Length];
            for (var r = 0; r < resources.Length; r++)
            {
                _classOf[r] = -1;
                for (var c = 0; c < _classes.Length && _classOf[r] < 0; c++)
                    foreach (var id in _classes[c].Resources)
                        if (id == resources[r].Id)
                            _classOf[r] = c;
            }

            _classOfResources = resources;
        }

        if ((uint)resource < (uint)_classOf.Length && _classOf[resource] >= 0)
            return _classOf[resource];
        for (var c = 0; c < _classes.Length; c++)
            if (_classes[c].Bare)
                return c;
        return 0;
    }

    /// <summary>Reads and checks a set of figures; anything out of range is refused.</summary>
    public static ModuleLook Load(string path)
    {
        ModuleLook look;
        try
        {
            look = JsonSerializer.Deserialize<ModuleLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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

    private static byte[] Bytes(string path, string what, string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > max)
            throw new InvalidDataException($"{path}: {what} needs a word of 1 to {max} characters");
        return Encoding.UTF8.GetBytes(text);
    }

    private void Build(string path)
    {
        string[] qualities = ["Common", "Uncommon", "Rare", "Epic", "Legendary", "Alien Tech"];
        if (TierFile.Length != qualities.Length)
            throw new InvalidDataException($"{path}: one tier for each of the six qualities, in order");
        _tiers = new ModuleTier[qualities.Length];
        for (var i = 0; i < qualities.Length; i++)
        {
            if (TierFile[i].Quality != qualities[i])
                throw new InvalidDataException($"{path}: tier {i} must be {qualities[i]}");
            _tiers[i] = new ModuleTier(Bytes(path, "a tier", TierFile[i].Label, 16), Rgb(path, "a tier", TierFile[i].Color));
        }

        _families = new ModuleFamilyText[3];
        for (var f = 1; f < FamilyIds.Length; f++)
        {
            var row = Array.Find(FamilyFile, x => x.Family == FamilyIds[f]) ?? throw new InvalidDataException($"{path}: no text for family {FamilyIds[f]}");
            _families[f - 1] = new ModuleFamilyText(Bytes(path, "a family", row.Label, 24), Bytes(path, "a family", row.Short, 12));
        }

        _gasColors = [];
        foreach (var gas in GasFile)
            _gasColors[gas.Gas] = Rgb(path, "a gas", gas.Color);
        string[] kinds = ["Normal", "Cold", "Lightning"];
        _kinds = new ModuleKind[kinds.Length];
        for (var k = 0; k < kinds.Length; k++)
        {
            var row = Array.Find(KindFile, x => x.Kind == kinds[k]) ?? throw new InvalidDataException($"{path}: no cloud kind {kinds[k]}");
            _kinds[k] = new ModuleKind(Bytes(path, "a cloud kind", row.Label, 12), Rgb(path, "a cloud kind", row.Color));
        }

        if (ClassFile.Length is < 2 or > 8 || Array.FindAll(ClassFile, c => c.Bare).Length != 1)
            throw new InvalidDataException($"{path}: 2 to 8 locator classes, exactly one of them bare rock");
        _classes = new ModuleClass[ClassFile.Length];
        for (var c = 0; c < ClassFile.Length; c++)
            _classes[c] = new ModuleClass(ClassFile[c].Name, Bytes(path, "a class", ClassFile[c].Name.ToUpperInvariant(), 20), Rgb(path, "a class", ClassFile[c].Color), ClassFile[c].Bare, ClassFile[c].Resources ?? []);

        var locator = Locator;
        if (!(locator.VisionSeconds is >= 1 and <= 60) || !(locator.FadeSeconds is >= 0 and <= 60) || !(locator.WaveSeconds is >= 0 and <= 10))
            throw new InvalidDataException($"{path}: visionSeconds is 1 to 60, fadeSeconds 0 to 60, waveSeconds 0 to 10");
        if (!(locator.MarkerPixels is >= 1 and <= 24) || !(locator.RingPixels is >= 1 and <= 48) || !(locator.MinPixels is >= 1 and <= 24) || !(locator.RetainedMarkerPixels is >= 1 and <= 24))
            throw new InvalidDataException($"{path}: marker sizes are 1 to 48 pixels");
        if (!Unit(locator.VisionAlpha) || !Unit(locator.RetainedAlpha) || locator.LabelNearest is < 0 or > 32)
            throw new InvalidDataException($"{path}: alphas are 0 to 1, labelNearest 0 to 32");
        var strip = Strip;
        if (!(strip.ChipWidth is >= 60 and <= 300) || !(strip.ChipHeight is >= 24 and <= 80) || !(strip.Gap is >= 0 and <= 32)
            || !(strip.ResultSeconds is >= 0 and <= 60) || !Unit(strip.ReadyAlpha) || !Unit(strip.IdleAlpha))
            throw new InvalidDataException($"{path}: strip chip width is 60 to 300, height 24 to 80, gap 0 to 32, resultSeconds 0 to 60, alphas 0 to 1");
        if (!(CardBarWidth is >= 40 and <= 240) || !(GasWindowWidth is >= 300 and <= 900) || !(GasWindowHeight is >= 200 and <= 1200))
            throw new InvalidDataException($"{path}: cardBarWidth is 40 to 240, gasWindowWidth 300 to 900, gasWindowHeight 200 to 1200");
        if (!(UnresolvedPercent is >= 0 and <= 20) || !(TargetPanelExtra is >= 0 and <= 300) || !(TargetPanelUnknownExtra is >= 0 and <= 300))
            throw new InvalidDataException($"{path}: unresolvedPercent is 0 to 20, targetPanelExtra and targetPanelUnknownExtra 0 to 300");
    }

    private static bool Unit(float value) => value is >= 0 and <= 1;

    public sealed class TierJson { public string Quality { get; init; } = ""; public string Label { get; init; } = ""; public string Color { get; init; } = ""; }
    public sealed class FamilyJson { public string Family { get; init; } = ""; public string Label { get; init; } = ""; public string Short { get; init; } = ""; }
    public sealed class GasJson { public string Gas { get; init; } = ""; public string Color { get; init; } = ""; }
    public sealed class KindJson { public string Kind { get; init; } = ""; public string Label { get; init; } = ""; public string Color { get; init; } = ""; }
    public sealed class ClassJson { public string Name { get; init; } = ""; public string Color { get; init; } = ""; public bool Bare { get; init; } public string[]? Resources { get; init; } }

    /// <summary>The locator overlay's figures; seconds are real time, sizes unscaled pixels.</summary>
    public sealed class LocatorLook
    {
        /// <summary>How long a pulse's markers stay at full strength before they fade.</summary>
        public float VisionSeconds { get; init; }

        /// <summary>How long they take to fade down to the retained strength.</summary>
        public float FadeSeconds { get; init; }

        /// <summary>How long the first wave takes to reveal the asteroids, nearest first.</summary>
        public float WaveSeconds { get; init; }

        public float MarkerPixels { get; init; }
        public float RingPixels { get; init; }
        public float MinPixels { get; init; }
        public float VisionAlpha { get; init; }

        /// <summary>The strength of an old pulse's markers in the command view, and their size.</summary>
        public float RetainedAlpha { get; init; }

        public float RetainedMarkerPixels { get; init; }

        /// <summary>How many of the nearest asteroids carry a class label while the pulse is fresh.</summary>
        public int LabelNearest { get; init; }
    }

    /// <summary>The module strip's figures.</summary>
    public sealed class StripLook
    {
        public float ChipWidth { get; init; }
        public float ChipHeight { get; init; }
        public float Gap { get; init; }

        /// <summary>Seconds a finished, cancelled or refused request stays on its chip.</summary>
        public float ResultSeconds { get; init; }

        public float ReadyAlpha { get; init; }
        public float IdleAlpha { get; init; }
    }
}

/// <summary>A quality tier's word, upper case and encoded, and its colour.</summary>
internal readonly record struct ModuleTier(byte[] Label, uint Rgb);

/// <summary>A family's full and short names, upper case and encoded.</summary>
internal readonly record struct ModuleFamilyText(byte[] Label, byte[] Short);

/// <summary>A cloud kind's word and colour.</summary>
internal readonly record struct ModuleKind(byte[] Label, uint Rgb);

/// <summary>One locator class: a group of resources the locator can tell apart only this coarsely.</summary>
internal readonly record struct ModuleClass(string Name, byte[] Label, uint Rgb, bool Bare, string[] Resources);
