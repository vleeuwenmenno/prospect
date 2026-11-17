using System.Runtime.CompilerServices;

namespace Prospect.Game.World;

/// <summary>Room for the heat of each laser of one hull, in the order of <see cref="HullDesign.Lasers"/>.</summary>
[InlineArray(LaserHeat.MaxLasers)]
public struct LaserHeatLevels
{
    private float _element0;
}

/// <summary>
/// The heat of a hull's mining lasers, kept with the hull (<see cref="HullInfo.Lasers"/>) as boost heat is kept in its
/// state: one figure from 0 to 1 for each laser, a bit for each that overheated and is shut down, whether the lasers
/// wait for the battery, and the lasers asked to fire in the step to come. The server decides it; a client predicts
/// its own hull's with the same rule and is told the server's figures with every snapshot. Not saved: a loaded
/// world's lasers are cool and wait for nothing.
/// </summary>
public struct LaserHeatState
{
    /// <summary>Each laser's heat, from 0 (cool) to 1 (overheated).</summary>
    public LaserHeatLevels Heat;

    /// <summary>A bit a laser, set when it reached full heat: it does not fire until it has cooled to its <see cref="LaserSpec.UnlockHeat"/>.</summary>
    public ulong Locked;

    /// <summary>
    /// A bit a laser that is to fire in the hull's next step, prepared before it like <see cref="HullInfo.LaserDemand"/>
    /// and never durable. A laser heats only in a step that also paid for the lasers.
    /// </summary>
    public ulong Firing;

    /// <summary>
    /// Set when the lasers were asked to fire and the hull could not pay for them: the battery ran empty. While it is
    /// set no laser of the hull fires or asks for power, until the battery holds
    /// <see cref="FlightParams.LaserEnergyFloor"/> again, as boost waits for its own floor.
    /// </summary>
    public bool AwaitingPower;

    /// <summary>Whether laser <paramref name="part"/> is shut down by heat.</summary>
    public readonly bool IsLocked(int part) => (uint)part < LaserHeat.MaxLasers && (Locked & 1UL << part) != 0;

    /// <summary>The heat of laser <paramref name="part"/>, or zero for a laser the hull does not have.</summary>
    public readonly float Of(int part) => (uint)part < LaserHeat.MaxLasers ? Heat[part] : 0f;
}

/// <summary>
/// How a mining laser heats and cools: the one rule, run by <see cref="HullField.Step"/> for the server's hulls and
/// for the hull a client predicts. A laser gains <see cref="LaserSpec.HeatRate"/> a second in a step it fired in (it
/// was asked to and the hull paid for its lasers) and sheds <see cref="LaserSpec.CoolRate"/> a second otherwise. At
/// full heat it locks and stays shut down until it has cooled to <see cref="LaserSpec.UnlockHeat"/>, as an overheated
/// boost drive does. Inside a cold gas cloud heat builds and sheds by the hull's cold cloud figures
/// (<see cref="FlightParams.ColdHeating"/>, <see cref="FlightParams.ColdCooling"/>).
/// <para>
/// It also holds the lasers' wait for power. A step in which lasers were asked to fire and the hull could not pay for
/// them (the battery is empty) sets <see cref="LaserHeatState.AwaitingPower"/>; it is cleared by the first step that
/// ends with the battery at <see cref="FlightParams.LaserEnergyFloor"/> or above. Whoever prepares a hull's lasers
/// (the server's plan, a client's prediction) asks <see cref="MayFire"/> and leaves every laser out while it waits,
/// so lasers on an empty battery stay off and let it fill instead of flickering on what the generator makes.
/// </para>
/// Plain single-precision arithmetic in laser order; it takes no memory.
/// </summary>
public static class LaserHeat
{
    /// <summary>
    /// The most lasers of one hull that have heat: the groupable parts a hull's action groups reach
    /// (<c>ActionGroupsMessage.MaxParts</c>), which is also the most that ever fire.
    /// </summary>
    public const int MaxLasers = 64;

    /// <summary>How many of <paramref name="design"/>'s lasers have heat.</summary>
    public static int Count(HullDesign design) => Math.Min(design.Lasers.Length, MaxLasers);

    /// <summary>
    /// Whether a hull's lasers may be asked to fire in the step to come: not while they wait for the battery. The
    /// one question the server's plan and a client's prediction ask before they prepare any laser.
    /// </summary>
    public static bool MayFire(in LaserHeatState lasers) => !lasers.AwaitingPower;

    /// <summary>
    /// Advances every laser's heat, and the lasers' wait for power, one step of <paramref name="dt"/> seconds.
    /// <paramref name="paid"/> says the flight step paid for the hull's lasers (<see cref="PowerFlow.Laser"/>),
    /// <paramref name="energy"/> is what the battery holds after that step in megajoules, <paramref name="cold"/>
    /// says the hull is inside a cold cloud, and <paramref name="flight"/> gives the cold cloud's two figures and
    /// the battery's floor.
    /// </summary>
    public static void Step(ref LaserHeatState lasers, HullDesign design, bool paid, float energy, bool cold, in FlightParams flight, float dt)
    {
        var count = Count(design);
        if (count == 0) return;

        // Asked and not paid: the battery is empty, and the lasers wait until it holds its floor again. A step that
        // was asked nothing can only end the wait, and one that was paid leaves it as it is.
        if (lasers.Firing != 0 && !paid)
            lasers.AwaitingPower = true;
        else if (lasers.AwaitingPower && energy >= flight.LaserEnergyFloor)
            lasers.AwaitingPower = false;

        var heating = cold ? flight.ColdHeating : 1f;
        var cooling = cold ? flight.ColdCooling : 1f;
        for (var part = 0; part < count; part++)
        {
            var bit = 1UL << part;
            var locked = (lasers.Locked & bit) != 0;
            var firing = paid && !locked && (lasers.Firing & bit) != 0;
            ref var heat = ref lasers.Heat[part];
            if (!firing && !locked && heat <= 0f) continue;
            var spec = design.Mounts[design.Lasers[part]].Type.Laser!;
            heat = Math.Clamp(heat + (firing ? spec.HeatRate * heating : -spec.CoolRate * cooling) * dt, 0f, 1f);
            if (heat >= 1f)
                lasers.Locked |= bit;
            else if (locked && heat <= spec.UnlockHeat)
                lasers.Locked &= ~bit;
        }
    }

    /// <summary>
    /// What the lasers of a hull keep of the heat of the hull it replaces. A repair is the same design
    /// (<paramref name="sameDesign"/>), so each laser keeps its own. An edit may have moved, added or removed lasers,
    /// so every laser of the new design <paramref name="to"/> starts as hot as the old hull's hottest, and shut down
    /// if one of the old ones was: rebuilding a ship never cools a laser. A wait for power is kept by a hull that
    /// still has a laser: rebuilding never fills a battery either. Nothing is asked to fire.
    /// </summary>
    public static LaserHeatState Carry(in LaserHeatState old, bool sameDesign, HullDesign to)
    {
        if (sameDesign)
            return old with { Firing = 0 };

        var count = Count(to);
        var all = count >= MaxLasers ? ulong.MaxValue : (1UL << count) - 1;
        var hottest = Hottest(old, ulong.MaxValue, out _);
        var carried = new LaserHeatState { AwaitingPower = old.AwaitingPower && count > 0 };
        for (var part = 0; part < count; part++) carried.Heat[part] = hottest;
        if (old.Locked != 0 && hottest > 0f) carried.Locked = all;
        return carried;
    }

    /// <summary>
    /// The hottest laser among those <paramref name="parts"/> names (a bit a laser), and whether every one of them
    /// is locked: what a group of lasers shows as one bar.
    /// </summary>
    public static float Hottest(in LaserHeatState lasers, ulong parts, out bool allLocked)
    {
        var hottest = 0f;
        for (var part = 0; part < MaxLasers; part++)
            if ((parts & 1UL << part) != 0) hottest = Math.Max(hottest, lasers.Heat[part]);
        allLocked = parts != 0 && (lasers.Locked & parts) == parts;
        return hottest;
    }
}
