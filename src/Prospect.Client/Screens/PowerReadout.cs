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

    /// <summary>A fuel tank turns to the warning colour, and posts its notice, below this share of its capacity.</summary>
    public float FuelLowFraction { get; init; }

    /// <summary>Kilograms a second of burn below which a fuel reads as steady, so a burn smoothed towards zero shows no flow.</summary>
    public float FuelSteadyKgPerSecond { get; init; }

    /// <summary>The three fuels' accent colours as six hex digits: nitrogen, hydrazine, bipropellant.</summary>
    public string FuelNitrogenColor { get; init; } = "";

    public string FuelHydrazineColor { get; init; } = "";
    public string FuelBipropellantColor { get; init; } = "";

    /// <summary>
    /// The fuel callout (<see cref="FuelCallout"/>): the most labels before tanks are grouped, 1 to
    /// <see cref="FuelCalloutLayout.MaxLabels"/>.
    /// </summary>
    public int CalloutMaxLabels { get; init; }

    /// <summary>The outline drawn on each tank block: its thickness, how strongly it is drawn, and how strongly a lost tank's is.</summary>
    public float CalloutOutlineThickness { get; init; }

    public float CalloutOutlineAlpha { get; init; }
    public float CalloutLostAlpha { get; init; }

    /// <summary>The leader from a label to its tank: thickness, how strongly it is drawn, and the dot's radius at the tank.</summary>
    public float CalloutLeaderThickness { get; init; }

    public float CalloutLeaderAlpha { get; init; }
    public float CalloutAnchorDot { get; init; }

    /// <summary>
    /// Where the labels go: how far a column stands beyond the outermost tank on its side, the space between two
    /// labels, the space inside a label, the margin kept from the screen's edges, and the space kept above the
    /// pilot console.
    /// </summary>
    public float CalloutLabelOffset { get; init; }

    public float CalloutLabelGap { get; init; }
    public float CalloutLabelPad { get; init; }
    public float CalloutMargin { get; init; }
    public float CalloutConsoleGap { get; init; }

    /// <summary>The accent colour of one fuel as 0xRRGGBB; checked when the figures load.</summary>
    public uint FuelRgb(Prospect.Game.World.FuelKind kind) => uint.Parse(
        kind switch
        {
            Prospect.Game.World.FuelKind.Nitrogen => FuelNitrogenColor,
            Prospect.Game.World.FuelKind.Hydrazine => FuelHydrazineColor,
            _ => FuelBipropellantColor,
        },
        NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture);

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
        if (!(look.FuelLowFraction is > 0f and < 1f) || !(look.FuelSteadyKgPerSecond is > 0f and <= 1f))
            throw new InvalidDataException($"{path}: fuelLowFraction is above 0 and below 1, fuelSteadyKgPerSecond above 0 to 1");
        foreach (var color in (ReadOnlySpan<string>)[look.FuelNitrogenColor, look.FuelHydrazineColor, look.FuelBipropellantColor])
        {
            if (color.Length != 6 || !uint.TryParse(color, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException($"{path}: the fuel colours are six hex digits");
        }

        if (!(look.CalloutMaxLabels is >= 1 and <= FuelCalloutLayout.MaxLabels))
            throw new InvalidDataException($"{path}: calloutMaxLabels is 1 to {FuelCalloutLayout.MaxLabels}");
        if (!(look.CalloutOutlineThickness is >= 0.5f and <= 8f) || !(look.CalloutLeaderThickness is >= 0.5f and <= 8f) || !(look.CalloutAnchorDot is >= 0f and <= 8f))
            throw new InvalidDataException($"{path}: calloutOutlineThickness and calloutLeaderThickness are 0.5 to 8, calloutAnchorDot 0 to 8");
        if (!(look.CalloutOutlineAlpha is >= 0f and <= 1f) || !(look.CalloutLostAlpha is >= 0f and <= 1f) || !(look.CalloutLeaderAlpha is >= 0f and <= 1f))
            throw new InvalidDataException($"{path}: calloutOutlineAlpha, calloutLostAlpha and calloutLeaderAlpha are 0 to 1");
        if (!(look.CalloutLabelOffset is >= 0f and <= 400f) || !(look.CalloutLabelGap is >= 0f and <= 32f) || !(look.CalloutLabelPad is >= 0f and <= 32f)
            || !(look.CalloutMargin is >= 0f and <= 200f) || !(look.CalloutConsoleGap is >= 0f and <= 200f))
            throw new InvalidDataException($"{path}: calloutLabelOffset is 0 to 400, calloutLabelGap and calloutLabelPad 0 to 32, calloutMargin and calloutConsoleGap 0 to 200");

        return look;
    }
}
