using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>How full one fuel tank is, as the notices and the colours tell it.</summary>
internal enum FuelLevel : byte
{
    /// <summary>The hull has no tank for this fuel.</summary>
    None,

    /// <summary>Above the low mark.</summary>
    Ok,

    /// <summary>Under the low mark of <see cref="PowerReadout.FuelLowFraction"/>, not yet empty.</summary>
    Low,

    /// <summary>Nothing left.</summary>
    Empty,
}

/// <summary>
/// What the console says about the own hull's fuel, read from <see cref="FuelFlow"/> alone, apart from drawing so it
/// can be tested without fonts. Nothing here works a burn out: the simulation's smoothed rate is the only source. A flow
/// is signed, negative while a tank empties, so a source that fills one later reads right with no change here.
/// </summary>
internal static class ConsoleFuel
{
    /// <summary>The fuels in the order the console lists them.</summary>
    public const int Kinds = 3;

    /// <summary>The fuel at <paramref name="index"/>, 0 to <see cref="Kinds"/> - 1.</summary>
    public static FuelKind KindAt(int index) => (FuelKind)(index + 1);

    /// <summary>What a tank can hold, in kilograms: zero for no tank or a figure that is not a number.</summary>
    public static float Capacity(in FuelFlow fuel, FuelKind kind)
    {
        var capacity = fuel.Capacity[kind];
        return capacity > 0f && float.IsFinite(capacity) ? capacity : 0f;
    }

    /// <summary>What a tank holds, in kilograms, within its capacity.</summary>
    public static float Amount(in FuelFlow fuel, FuelKind kind) => FlightConsole.Shown(fuel.Amount[kind], Capacity(fuel, kind));

    /// <summary>The smoothed burn in kilograms a second, never negative.</summary>
    public static float Burn(in FuelFlow fuel, FuelKind kind)
    {
        var burn = fuel.Burn[kind];
        return burn > 0f && float.IsFinite(burn) ? burn : 0f;
    }

    /// <summary>
    /// The net change a second: the negative of the burn while it is above the steady figure, zero otherwise.
    /// </summary>
    public static float Flow(in FuelFlow fuel, FuelKind kind)
    {
        var burn = Burn(fuel, kind);
        return burn >= PowerReadout.Current.FuelSteadyKgPerSecond ? -burn : 0f;
    }

    /// <summary>How many fuels the hull has a tank for.</summary>
    public static int Count(in FuelFlow fuel)
    {
        var count = 0;
        for (var i = 0; i < Kinds; i++)
        {
            if (Capacity(fuel, KindAt(i)) > 0f)
                count++;
        }

        return count;
    }

    /// <summary>
    /// The cell of the fuel row at <paramref name="index"/> among the cells shown: one for each fuel the hull has a
    /// tank for, in <see cref="KindAt"/> order, side by side across the whole console with a row gap between them,
    /// slant included. A hull with no tank has one cell, at index 0.
    /// </summary>
    public static HudRect CellRect(in FlightConsoleLayout layout, in FuelFlow fuel, int index)
    {
        var cells = Math.Max(1, Count(fuel));
        var gap = layout.RowGap;
        var width = MathF.Floor((layout.Right - layout.LeftX - gap * (cells - 1)) / cells);
        var left = layout.LeftX + index * (width + gap);
        return new HudRect(left, layout.FuelTop, left + width, layout.FuelTop + layout.FuelRow);
    }

    /// <summary>
    /// The fuel whose cell is under <paramref name="pointer"/>: none in the gap between two cells, off the row, or on
    /// the one cell of a hull with no tank.
    /// </summary>
    public static FuelKind CellAt(in FlightConsoleLayout layout, in FuelFlow fuel, float2 pointer)
    {
        var shown = 0;
        for (var i = 0; i < Kinds; i++)
        {
            var kind = KindAt(i);
            if (!(Capacity(fuel, kind) > 0f))
                continue;
            if (CellRect(layout, fuel, shown).Contains(pointer))
                return kind;
            shown++;
        }

        return FuelKind.None;
    }

    /// <summary>The index among the shown cells of <paramref name="kind"/>'s cell, or -1 when the hull has no tank of it.</summary>
    public static int CellIndex(in FuelFlow fuel, FuelKind kind)
    {
        var shown = 0;
        for (var i = 0; i < Kinds; i++)
        {
            var at = KindAt(i);
            if (!(Capacity(fuel, at) > 0f))
                continue;
            if (at == kind)
                return shown;
            shown++;
        }

        return -1;
    }

    /// <summary>
    /// How many tanks of <paramref name="kind"/> the design has, and how many of them the hull has lost: a tank
    /// whose block is not in <paramref name="alive"/> is lost.
    /// </summary>
    public static int Tanks(HullDesign design, ReadOnlySpan<ulong> alive, FuelKind kind, out int lost)
    {
        var count = 0;
        lost = 0;
        foreach (var tank in design.Tanks)
        {
            if (tank.Kind != kind)
                continue;
            count++;
            if ((tank.Block >> 6) >= alive.Length || !BlockSet.Has(alive, tank.Block))
                lost++;
        }

        return count;
    }

    /// <summary>The level of one tank against the low mark in the content.</summary>
    public static FuelLevel Level(in FuelFlow fuel, FuelKind kind)
    {
        var capacity = Capacity(fuel, kind);
        if (!(capacity > 0f))
            return FuelLevel.None;
        var amount = Amount(fuel, kind);
        return amount <= 0f ? FuelLevel.Empty : amount < capacity * PowerReadout.Current.FuelLowFraction ? FuelLevel.Low : FuelLevel.Ok;
    }

    /// <summary>The worst level over every tank, for the tooltip's heading.</summary>
    public static FuelLevel Worst(in FuelFlow fuel)
    {
        var worst = FuelLevel.None;
        for (var i = 0; i < Kinds; i++)
        {
            var level = Level(fuel, KindAt(i));
            if (level > worst || worst == FuelLevel.None)
                worst = level;
        }

        return worst;
    }

    /// <summary>Minutes the tank lasts at the current burn; not a number at rest or when empty.</summary>
    public static float MinutesLeft(in FuelFlow fuel, FuelKind kind)
    {
        var flow = Flow(fuel, kind);
        var amount = Amount(fuel, kind);
        return flow < 0f && amount > 0f ? amount / -flow / 60f : float.NaN;
    }

    /// <summary>A tank's colour: its own accent, the warning colour once low or empty.</summary>
    public static uint Color(in FuelFlow fuel, FuelKind kind) =>
        Level(fuel, kind) >= FuelLevel.Low ? Theme.Bad : PowerReadout.Current.FuelRgb(kind);

    /// <summary>The fuel's name as the console prints it.</summary>
    public static ReadOnlySpan<byte> Name(FuelKind kind) => kind switch
    {
        FuelKind.Nitrogen => "NITROGEN"u8,
        FuelKind.Hydrazine => "HYDRAZINE"u8,
        _ => "BIPROP"u8,
    };

    /// <summary>The fuel's full name for the tooltip.</summary>
    public static ReadOnlySpan<byte> FullName(FuelKind kind) => kind switch
    {
        FuelKind.Nitrogen => "NITROGEN"u8,
        FuelKind.Hydrazine => "HYDRAZINE"u8,
        _ => "BIPROPELLANT"u8,
    };

    /// <summary>Kilograms, or tonnes from ten tonnes up: below that a tonne figure hides minutes of burn.</summary>
    public static ReadOnlySpan<byte> Mass(float kilograms) =>
        kilograms >= 10000f ? Kit.F($"{kilograms / 1000f:0.##} T") : Kit.F($"{kilograms:0} KG");

    /// <summary>Amount out of capacity in one unit, the capacity's.</summary>
    public static ReadOnlySpan<byte> Stored(float amount, float capacity) =>
        capacity >= 10000f ? Kit.F($"{amount / 1000f:0.##} / {capacity / 1000f:0.##} T") : Kit.F($"{amount:0} / {capacity:0} KG");

    /// <summary>Amount alone in the capacity's unit, for a cell too narrow for both.</summary>
    public static ReadOnlySpan<byte> Held(float amount, float capacity) =>
        capacity >= 10000f ? Kit.F($"{amount / 1000f:0.##} T") : Kit.F($"{amount:0} KG");

    /// <summary>A flow in kilograms a second, signed; "—" at rest.</summary>
    public static ReadOnlySpan<byte> FlowText(float flow)
    {
        if (flow == 0f)
            return "—"u8;
        return MathF.Abs(flow) >= 1f ? Kit.F($"{flow:+0.0;-0.0} KG/S") : MathF.Abs(flow) >= 0.1f ? Kit.F($"{flow:+0.00;-0.00} KG/S") : Kit.F($"{flow:+0.000;-0.000} KG/S");
    }

    /// <summary>Minutes left as the tooltip prints them; "—" at rest.</summary>
    public static ReadOnlySpan<byte> MinutesText(float minutes)
    {
        if (!float.IsFinite(minutes))
            return "—"u8;
        return minutes >= 600f ? Kit.F($"{minutes / 60f:0.#} H") : minutes >= 10f ? Kit.F($"{minutes:0} MIN") : Kit.F($"{minutes:0.0} MIN");
    }
}
