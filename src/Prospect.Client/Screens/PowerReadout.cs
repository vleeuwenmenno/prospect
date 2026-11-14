using System.Globalization;
using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// How the console shows power flow, from <c>content/visuals/power.json</c>. Client data: it changes what is
/// drawn, never what the simulation spends. Figures in unscaled pixels unless a name says otherwise.
/// </summary>
internal sealed class PowerReadout
{
    private static PowerReadout? _current;

    /// <summary>Net megawatts below which the battery reads as steady.</summary>
    public float SteadyMegawatts { get; init; }

    /// <summary>The trend arrow beside the net figure: its height, width and the gap to the text.</summary>
    public float ArrowHeight { get; init; }

    public float ArrowWidth { get; init; }
    public float ArrowGap { get; init; }

    /// <summary>How strongly the shield's share is drawn on the flow strip, 0 to 1, and its line and end tick thickness.</summary>
    public float ShieldMarkAlpha { get; init; }

    public float ShieldMarkThickness { get; init; }
    public float ShieldMarkTick { get; init; }

    /// <summary>
    /// The scan modules' share on the flow strip, drawn after the shield's: its colour as six hex digits, how strongly
    /// it is drawn, 0 to 1, and its line and end tick thickness.
    /// </summary>
    public string ModuleMarkColor { get; init; } = "";

    public float ModuleMarkAlpha { get; init; }
    public float ModuleMarkThickness { get; init; }
    public float ModuleMarkTick { get; init; }

    /// <summary><see cref="ModuleMarkColor"/> as 0xRRGGBB; checked when the figures load.</summary>
    public uint ModuleMarkRgb => uint.Parse(ModuleMarkColor, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>The shipped figures, read once.</summary>
    public static PowerReadout Current => _current ??= Load(AppPaths.Content("visuals", "power.json"));

    /// <summary>Reads and checks a set of figures; anything out of range is refused.</summary>
    public static PowerReadout Load(string path)
    {
        var look = JsonSerializer.Deserialize<PowerReadout>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        if (!(look.SteadyMegawatts is > 0f and <= 10f))
            throw new InvalidDataException($"{path}: steadyMegawatts is above 0 to 10");
        if (!(look.ArrowHeight is >= 2f and <= 16f) || !(look.ArrowWidth is >= 2f and <= 16f) || !(look.ArrowGap is >= 0f and <= 16f))
            throw new InvalidDataException($"{path}: arrowHeight and arrowWidth are 2 to 16, arrowGap 0 to 16");
        if (!(look.ShieldMarkAlpha is >= 0f and <= 1f) || !(look.ShieldMarkThickness is >= 1f and <= 8f) || !(look.ShieldMarkTick is >= 1f and <= 8f))
            throw new InvalidDataException($"{path}: shieldMarkAlpha is 0 to 1, shieldMarkThickness and shieldMarkTick 1 to 8");
        if (look.ModuleMarkColor.Length != 6 || !uint.TryParse(look.ModuleMarkColor, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException($"{path}: moduleMarkColor is six hex digits");
        if (!(look.ModuleMarkAlpha is >= 0f and <= 1f) || !(look.ModuleMarkThickness is >= 1f and <= 8f) || !(look.ModuleMarkTick is >= 1f and <= 8f))
            throw new InvalidDataException($"{path}: moduleMarkAlpha is 0 to 1, moduleMarkThickness and moduleMarkTick 1 to 8");
        return look;
    }
}
