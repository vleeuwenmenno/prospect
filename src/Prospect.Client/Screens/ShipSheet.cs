using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>What the Ship window is describing this frame.</summary>
internal enum ShipSheetSubject : byte
{
    /// <summary>No hull to describe.</summary>
    None,

    /// <summary>The hull the player flies.</summary>
    Own,

    /// <summary>Another hull, the first selected in the command view.</summary>
    Selected,

    /// <summary>An asteroid selected in the command view with no hull selected.</summary>
    Rock,
}

/// <summary>
/// The Ship window's rules without drawing: which thing its sheet describes, and the sections it is made of in
/// order. Sections are a fixed list so a later section (action groups) is one more entry and one draw call.
/// </summary>
internal static class ShipSheet
{
    /// <summary>The sections of a hull's sheet, top to bottom.</summary>
    internal enum Section : byte
    {
        Identity,
        Flight,
        Power,
        Fuel,
        Shield,
        Cargo,
        Ore,
        Drones,
        Modules,
    }

    /// <summary>The number of <see cref="Section"/> values.</summary>
    public const int Sections = 9;

    /// <summary>
    /// The window follows the command view's selection the way the old data sheet did: the first selected hull,
    /// else a selected asteroid; in the pilot view, or with nothing selected, the hull flown.
    /// </summary>
    public static ShipSheetSubject Choose(ViewMode mode, bool hasOwn, bool selectedHullKnown, bool selectedRock)
    {
        if (mode == ViewMode.Command)
        {
            if (selectedHullKnown)
                return ShipSheetSubject.Selected;
            if (selectedRock)
                return ShipSheetSubject.Rock;
        }

        return hasOwn ? ShipSheetSubject.Own : ShipSheetSubject.None;
    }

    /// <summary>
    /// True when the section shows for a hull. Fuel, cargo and ore contents are told only for the hull flown; the
    /// figures that follow from the blocks (capacity, bins, bays, mounts) show for any hull.
    /// </summary>
    public static bool Shows(Section section, in HullStats stats, HullDesign design, bool isOwn) => section switch
    {
        Section.Fuel => isOwn && FuelKinds(stats) > 0,
        Section.Cargo => stats.CargoHold > 0f,
        Section.Ore => design.Bins.Length > 0,
        Section.Drones => design.Bays.Length > 0,
        Section.Modules => HasModules(stats, design),
        _ => true,
    };

    /// <summary>How many fuels the hull's remaining tanks can hold.</summary>
    public static int FuelKinds(in HullStats stats)
    {
        var count = 0;
        for (var i = 0; i < ConsoleFuel.Kinds; i++)
            if (stats.FuelCapacity[ConsoleFuel.KindAt(i)] > 0f)
                count++;
        return count;
    }

    /// <summary>True when the hull carries a scanner, a locator or a tractor beam.</summary>
    public static bool HasModules(in HullStats stats, HullDesign design) =>
        stats.GasScanner.Mount != 0 || stats.AsteroidScanner.Mount != 0 || stats.AsteroidLocator.Mount != 0 || design.Tractors.Length > 0;

    /// <summary>Kilograms of raw ore the design's bins hold when full, all together.</summary>
    public static double OreCapacity(HullDesign design)
    {
        var total = 0.0;
        foreach (var bin in design.Bins)
            total += bin.Capacity;
        return total;
    }

    /// <summary>Kilograms of raw ore in the bins a hull was told of.</summary>
    public static double OreHeld(ReadOnlySpan<OreBin> bins)
    {
        var total = 0.0;
        foreach (ref readonly var bin in bins)
            if (!bin.IsEmpty) total += bin.Kg;
        return total;
    }

    /// <summary>Drones the design's bays keep in all, from each bay block's own figures.</summary>
    public static int BayCapacity(HullDesign design)
    {
        var total = 0;
        foreach (var block in design.Bays)
            total += design.Types[block].Bay?.Capacity ?? 0;
        return total;
    }
}
