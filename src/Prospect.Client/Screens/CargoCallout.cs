using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// Which container of the flown hull's hold is called out on the ship, apart from drawing so it can be tested without
/// fonts. It follows the Cargo window's selection (<see cref="CargoHoldView.SelectedIndex"/>) and lives only while
/// that window is shown: closing the window, a change of hull or of the hold (a jettison, a pickup, a refit) and
/// Escape through <see cref="ScreenHost.BackStep.CargoCallout"/> all clear it.
/// </summary>
internal sealed class CargoCallout
{
    private uint _hull;
    private long _tick;

    /// <summary>The hold index called out, or -1 while closed.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>True while a container is called out.</summary>
    public bool IsOpen => Index >= 0;

    /// <summary>
    /// Reads this frame's state. A selection in a shown window, inside the <paramref name="count"/> containers of
    /// <paramref name="hull"/>'s hold at <paramref name="tick"/>, opens the callout; the hull or the hold changing
    /// under an open callout closes it, as the window's own selection will clear on its next update.
    /// </summary>
    public void Sync(bool windowShown, uint hull, long tick, int count, int selected)
    {
        if (!windowShown || hull == 0 || (uint)selected >= (uint)count)
        {
            Index = -1;
            return;
        }

        if (Index != selected)
        {
            Index = selected;
            _hull = hull;
            _tick = tick;
        }
        else if (hull != _hull || tick != _tick)
        {
            Index = -1;
        }
    }

    /// <summary>Closes the callout.</summary>
    public void Close() => Index = -1;

    /// <summary>
    /// The called-out container's box in the hull's frame, from the packed cell the renderer draws it in
    /// (<see cref="CargoSlots"/>). False when <paramref name="index"/> names no container or the container names no
    /// rack or size of the design and catalogue.
    /// </summary>
    public static bool TryBox(HullDesign design, CargoCatalog catalog, ReadOnlySpan<HoldContainer> hold, int index, out Vector3 centre, out Vector3 half)
    {
        centre = default;
        half = default;
        if ((uint)index >= (uint)hold.Length || !CargoSlots.Valid(design, catalog, hold[index]))
            return false;
        centre = CargoSlots.Centre(design, catalog, hold[index]);
        half = CargoSlots.Half(catalog, hold[index]);
        return true;
    }
}
