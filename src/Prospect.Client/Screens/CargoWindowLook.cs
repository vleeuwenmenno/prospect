using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// How the Cargo window draws its tabs and the ORE tab, from <c>content/visuals/cargo_window.json</c>. Client data,
/// never simulation. Figures are unscaled pixels unless a name says otherwise; the ore tiles take their colour from
/// the galaxy's resources, never from here.
/// </summary>
internal sealed class CargoWindowLook
{
    private static CargoWindowLook? _current;

    /// <summary>A tab's width and height.</summary>
    public float TabWidth { get; init; }

    public float TabHeight { get; init; }

    /// <summary>An ore bin tile's width and height.</summary>
    public float OreTileWidth { get; init; }

    public float OreTileHeight { get; init; }

    /// <summary>The height of a tile's fill bar.</summary>
    public float OreBarHeight { get; init; }

    /// <summary>The height of one ore's row in the totals.</summary>
    public float OreRowHeight { get; init; }

    /// <summary>How strongly the ore's colour tints a tile, and how strongly an empty tile is drawn (0 to 1).</summary>
    public float OreTintAlpha { get; init; }

    public float OreEmptyAlpha { get; init; }

    /// <summary>The shipped figures, read once.</summary>
    public static CargoWindowLook Current => _current ??= Load(AppPaths.Content("visuals", "cargo_window.json"));

    /// <summary>Reads and checks a set of figures; anything out of range is refused.</summary>
    public static CargoWindowLook Load(string path)
    {
        var look = JsonSerializer.Deserialize<CargoWindowLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        if (!(look.TabWidth is >= 40f and <= 240f) || !(look.TabHeight is >= 16f and <= 64f))
            throw new InvalidDataException($"{path}: tabWidth is 40 to 240 and tabHeight 16 to 64");
        if (!(look.OreTileWidth is >= 80f and <= 320f) || !(look.OreTileHeight is >= 40f and <= 160f))
            throw new InvalidDataException($"{path}: oreTileWidth is 80 to 320 and oreTileHeight 40 to 160");
        if (!(look.OreBarHeight is >= 2f and <= 12f) || look.OreBarHeight * 3f > look.OreTileHeight)
            throw new InvalidDataException($"{path}: oreBarHeight is 2 to 12 and under a third of the tile");
        if (!(look.OreRowHeight is >= 14f and <= 48f))
            throw new InvalidDataException($"{path}: oreRowHeight is 14 to 48");
        if (!(look.OreTintAlpha is >= 0f and <= 1f) || !(look.OreEmptyAlpha is >= 0f and <= 1f))
            throw new InvalidDataException($"{path}: oreTintAlpha and oreEmptyAlpha are 0 to 1");
        return look;
    }
}
