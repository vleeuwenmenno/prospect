using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// How the target panel draws its shield and hull bars, from <c>content/visuals/target.json</c>: the console's
/// slanted segments, smaller. Client data, never simulation. Figures are unscaled pixels unless a name says otherwise.
/// </summary>
internal sealed class TargetLook
{
    private static TargetLook? _current;

    /// <summary>How many segments a bar has.</summary>
    public int Segments { get; init; }

    /// <summary>The height of a row (icon, bar and reading together) and of the bar within it.</summary>
    public float RowHeight { get; init; }

    public float BarHeight { get; init; }

    /// <summary>The space between two segments, and between the two rows.</summary>
    public float Gap { get; init; }

    public float RowGap { get; init; }

    /// <summary>How far a bar's top edge leans over its height; the console's is 10 over 36.</summary>
    public float Slant { get; init; }

    /// <summary>The icon's side, and the space after it before the bar.</summary>
    public float IconSize { get; init; }

    public float IconGap { get; init; }

    /// <summary>The space between the bar and the reading at its right.</summary>
    public float ReadingGap { get; init; }

    /// <summary>The shipped figures, read once.</summary>
    public static TargetLook Current => _current ??= Load(AppPaths.Content("visuals", "target.json"));

    /// <summary>Reads and checks a set of figures; anything out of range is refused.</summary>
    public static TargetLook Load(string path)
    {
        var look = JsonSerializer.Deserialize<TargetLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        if (look.Segments is < 2 or > 20)
            throw new InvalidDataException($"{path}: segments is 2 to 20");
        if (!(look.RowHeight is >= 6f and <= 32f) || !(look.BarHeight is >= 3f and <= 24f) || look.BarHeight > look.RowHeight)
            throw new InvalidDataException($"{path}: rowHeight is 6 to 32 and barHeight is 3 to 24, no taller than the row");
        if (!(look.Gap is >= 0f and <= 8f) || !(look.RowGap is >= 0f and <= 16f) || !(look.ReadingGap is >= 0f and <= 32f) || !(look.IconGap is >= 0f and <= 32f))
            throw new InvalidDataException($"{path}: gap is 0 to 8, rowGap 0 to 16, readingGap and iconGap 0 to 32");
        if (!(look.Slant is >= 0f and <= 1f) || !(look.IconSize is >= 6f and <= 20f))
            throw new InvalidDataException($"{path}: slant is 0 to 1 and iconSize 6 to 20");
        return look;
    }
}
