using System.Text.Json;
using Prospect.Client.Screens;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// The ship editor's own tuning, from <c>content/visuals/editor.json</c>: how the part list is grouped and below
/// which figures the editor warns that a design flies poorly. Client only; nothing here decides whether a design
/// is accepted.
/// </summary>
internal sealed class EditorTuning
{
    /// <summary>The part list's tabs, in order.</summary>
    public PartCategory[] Categories { get; init; } = [];

    /// <summary>Forward acceleration, in m/s², below which the editor warns.</summary>
    public float LowForwardAcceleration { get; init; }

    /// <summary>Top speed, in m/s, below which the editor warns.</summary>
    public float LowTopSpeed { get; init; }

    /// <summary>The slowest turn rate on any axis, in degrees a second, below which the editor warns.</summary>
    public float LowTurnDegrees { get; init; }

    /// <summary>
    /// How far, in pixels, the pointer moves up or down to move a face of the part to place by one cell, when the
    /// face's axis points too nearly along the view to be followed on screen; also the shortest a cell is taken to
    /// be along a face's axis on screen, so a far face is not moved by every pixel.
    /// </summary>
    public float SizePixelsPerCell { get; init; }

    /// <summary>
    /// Degrees from the view direction within which a face's axis, or from edge-on within which a rotation ring, is
    /// too foreshortened to follow: a face is then moved by dragging up and down, and a ring drag waits.
    /// </summary>
    public float EdgeOnDegrees { get; init; }

    /// <summary>Seconds the ghost of the part to place takes to glide into a new quarter turn. Zero turns it at once.</summary>
    public float TurnSeconds { get; init; }

    /// <summary>How near, in pixels, the pointer must be to a rotation ring of the part being turned to take it.</summary>
    public float RotateRingPixels { get; init; }

    /// <summary>Whether a part is a compact (drone) one: <see cref="BlockType.Drone"/>, the same flag the draft's placement rule reads.</summary>
    public bool Compact(BlockType type) => type.Drone;

    /// <summary>
    /// Whether a part belongs on a tab. Whether it fits the craft being edited is a separate rule, asked of
    /// <see cref="BlockType.FitsClass"/> by the palette, so the drone has the same tabs as a ship.
    /// </summary>
    public bool Lists(PartCategory category, BlockType type)
    {
        if (category.Roles is not { Length: > 0 } roles)
            return true;
        var role = EditorGeometry.Role(type);
        foreach (var listed in roles)
            if (listed == role)
                return true;
        return false;
    }

    public static EditorTuning Load() => Load(AppPaths.Content("visuals", "editor.json"));

    /// <summary>Reads and checks the tuning; anything out of range is refused.</summary>
    public static EditorTuning Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        EditorTuning tuning;
        try
        {
            tuning = JsonSerializer.Deserialize<EditorTuning>(File.ReadAllText(path), options) ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        if (tuning.Categories.Length is < 1 or > 12)
            throw new InvalidDataException($"{path}: 1 to 12 categories");
        foreach (var category in tuning.Categories)
        {
            if (string.IsNullOrWhiteSpace(category.Name) || category.Name.Length > 12)
                throw new InvalidDataException($"{path}: a category needs a name of 1 to 12 characters");
            if (!EditorIcons.TryParse(category.Icon, out var icon))
                throw new InvalidDataException($"{path}: category {category.Name} has an unknown icon \"{category.Icon}\"; known: {EditorIcons.Names}");
            if (string.IsNullOrWhiteSpace(category.Tip) || category.Tip.Length > 80)
                throw new InvalidDataException($"{path}: category {category.Name} needs a tip of 1 to 80 characters");
            category.Glyph = icon;
        }

        if (!(tuning.LowForwardAcceleration is >= 0 and <= 1000) || !(tuning.LowTopSpeed is >= 0 and <= 10000) || !(tuning.LowTurnDegrees is >= 0 and <= 360))
            throw new InvalidDataException($"{path}: warning thresholds are 0 to 1000 m/s², 0 to 10000 m/s and 0 to 360 deg/s");
        if (!(tuning.SizePixelsPerCell is >= 4 and <= 400))
            throw new InvalidDataException($"{path}: sizePixelsPerCell is 4 to 400");
        if (!(tuning.RotateRingPixels is >= 2 and <= 64))
            throw new InvalidDataException($"{path}: rotateRingPixels is 2 to 64");
        if (!(tuning.EdgeOnDegrees is >= 1 and <= 45))
            throw new InvalidDataException($"{path}: edgeOnDegrees is 1 to 45");
        if (!(tuning.TurnSeconds is >= 0 and <= 2))
            throw new InvalidDataException($"{path}: turnSeconds is 0 to 2");
        foreach (var category in tuning.Categories)
        {
            category.Label = System.Text.Encoding.UTF8.GetBytes(category.Name.ToUpperInvariant());
            category.NameText = System.Text.Encoding.UTF8.GetBytes(category.Name);
            category.TipText = System.Text.Encoding.UTF8.GetBytes(category.Tip);
        }
        return tuning;
    }
}

/// <summary>The icons a category can name in <c>editor.json</c>; each is drawn by <see cref="EditorIcons"/>.</summary>
internal enum EditorIcon : byte
{
    Grid,
    Cube,
    Bolt,
    Nozzle,
    Tank,
    Shield,
    Jump,
    Radar,
}

/// <summary>One category of the part list, shown as an icon on the rail: the roles it holds, its icon and its tooltip.</summary>
internal sealed class PartCategory
{
    public string Name { get; init; } = "";

    /// <summary>The icon's id, one of <see cref="EditorIcons.Names"/>; an unknown id is refused when the file loads.</summary>
    public string Icon { get; init; } = "";

    /// <summary>The one short line the tooltip says about what the category holds.</summary>
    public string Tip { get; init; } = "";

    /// <summary>The roles listed; none lists every role.</summary>
    public PartRole[]? Roles { get; init; }

    /// <summary>The icon, resolved once at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EditorIcon Glyph { get; set; }

    /// <summary>The name, encoded once at load, for the tooltip's title.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] NameText { get; set; } = [];

    /// <summary>The tip, encoded once at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] TipText { get; set; } = [];

    /// <summary>The category's name in upper case, encoded once at load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Label { get; set; } = [];
}
