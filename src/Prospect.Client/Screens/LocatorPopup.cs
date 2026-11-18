using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The advanced locator's popup, apart from drawing so it can be tested without fonts. A click on the LOCATOR chip
/// opens it over the chip and a click on the chip again closes it; so do a press anywhere else that is not on the
/// popup and Escape (<see cref="ScreenHost.BackStep.LocatorPopup"/>). It belongs to one hull: another hull, or a hull
/// that no longer carries an advanced locator, closes it. What it edits is the player's own filter, which the client
/// keeps (<c>ClientSettings.LocatorFilter</c>) and sends with each pulse; the server keeps none of it.
/// </summary>
internal sealed class LocatorPopup
{
    private uint _hull;

    /// <summary>True while the popup is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Opens the popup, or closes it when it is open (a click on the chip).</summary>
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>Closes the popup.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Keeps the popup to the hull it was opened on, and to a hull that still carries an advanced locator (<paramref name="present"/>).</summary>
    public void Follow(uint hull, bool present)
    {
        if (hull != _hull) { _hull = hull; IsOpen = false; }
        if (!present) IsOpen = false;
    }

    /// <summary>
    /// A press that was neither on the popup nor on the chip closes it. The chip's own press toggles it instead
    /// (<see cref="Toggle"/>), so it is excluded here, as the drone popup's is.
    /// </summary>
    public void Press(bool pressed, bool overPopup, bool overChip)
    {
        if (pressed && IsOpen && !overPopup && !overChip) IsOpen = false;
    }

    /// <summary>The filter with one diameter bound stepped by <paramref name="steps"/> of the client's step, kept in order.</summary>
    public static LocatorFilter Step(in LocatorFilter filter, bool min, int steps, in LocatorFilterLook look) =>
        filter.Stepped(min, steps, look.StepMetres, look.MaxDiameterMetres);

    /// <summary>The filter with one ore's bit added or removed; the range is left as it is.</summary>
    public static LocatorFilter ToggleOre(in LocatorFilter filter, int resource) =>
        filter.WithOre(resource, !filter.HasOre(resource));

    /// <summary>The filter naming every ore this galaxy has, leaving the range as it is.</summary>
    public static LocatorFilter AllOres(Galaxy galaxy, in LocatorFilter filter) => filter with { Ores = LocatorFilter.AllOres(galaxy) };

    /// <summary>The filter naming no ore in particular, which reports every ore and bare rock alike.</summary>
    public static LocatorFilter NoOres(in LocatorFilter filter) => filter with { Ores = 0 };

    /// <summary>
    /// The diameter range as the popup words it: "ANY DIAMETER", "200 M AND UP", "UP TO 400 M" or "100 - 400 M".
    /// Encoded once per call by <see cref="Kit.F"/>, which keeps no memory free on the frame.
    /// </summary>
    public static ReadOnlySpan<byte> RangeWords(in LocatorFilter filter) =>
        filter.MinMetres == 0 && filter.MaxMetres == 0 ? "ANY DIAMETER"u8
        : filter.MaxMetres == 0 ? Kit.F($"{filter.MinMetres} M AND UP")
        : filter.MinMetres == 0 ? Kit.F($"UP TO {filter.MaxMetres} M")
        : Kit.F($"{filter.MinMetres} - {filter.MaxMetres} M");

    /// <summary>The ores the popup says it looks for: "ANY ORE", "1 ORE" or "4 ORES".</summary>
    public static ReadOnlySpan<byte> OreWords(in LocatorFilter filter)
    {
        var count = filter.OreCount;
        return count == 0 ? "ANY ORE"u8 : count == 1 ? "1 ORE"u8 : Kit.F($"{count} ORES");
    }

    /// <summary>The pulse the popup's button sends, with the filter as it stands; the server still decides.</summary>
    public static void Pulse(in LocatorFilter filter, PlayerInputBridge bridge) => bridge.PulseAsteroidLocator(filter);
}
