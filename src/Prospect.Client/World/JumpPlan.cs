using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Why a selected ship cannot be sent on a jump to a point: what the course menu and the sector card say.</summary>
internal enum JumpBlock : byte
{
    /// <summary>Nothing: at least one selected ship can go.</summary>
    None,
    NoShips,
    NoDrive,

    /// <summary>The ship is in transit or arriving; the server refuses a new jump until it has landed.</summary>
    Busy,

    TooClose,
    TooFar,
    Invalid,
}

/// <summary>
/// What a jump to one point would do for the ships selected, worked out once with the same function the server
/// checks every jump with (<see cref="JumpRules.Evaluate"/>). The course menu in the command view, the map's
/// course menu and the pinned sector card all read this, so they cannot disagree with each other; the server
/// still decides when the order or the plot arrives.
/// </summary>
internal struct JumpPlan
{
    /// <summary>How many ships are selected, how many of them can go, and how those split by who flies them.</summary>
    public int Ships;
    public int Ready;
    public int ReadyPiloted;
    public int ReadyRemote;

    /// <summary>
    /// <see cref="JumpBlock.None"/> when a ship can go; otherwise why the ship the figures are about cannot.
    /// </summary>
    public JumpBlock Block;

    /// <summary>
    /// The range and risk figures are about <see cref="Reference"/>: the first ship that can go, else the first
    /// selected ship with a drive, else the ship being flown, so a card with nothing selected still shows them.
    /// </summary>
    public bool HasReference;
    public JumpEligibility Reference;
    public bool ReferencePiloted;
    public float MaxDistance;
    public double MinDistance;

    /// <summary>True when every ship that can go is the one being flown: choosing the jump only plots it.</summary>
    public readonly bool PlotsOnly => Ready > 0 && ReadyRemote == 0;

    /// <summary>True when a ship can go and the menu's row is a plot, not an order; with none able to go, by who is selected.</summary>
    public readonly bool IsPlot(bool ownShipOnlySelected) => Ready > 0 ? PlotsOnly : ownShipOnlySelected;

    /// <summary>True when the ship being flown plots and others jump by themselves.</summary>
    public readonly bool Mixed => ReadyPiloted > 0 && ReadyRemote > 0;

    /// <summary>
    /// Checks one hull against a point, from where it is now with its live drive: the server's range check, the
    /// hull being free to start, and the client's own rule that the point lies outside its sector.
    /// </summary>
    public static JumpBlock Check(SessionSnapshot snapshot, WorldView world, in HullView hull, Double3 target, out JumpEligibility eligibility)
    {
        var galaxy = snapshot.Galaxy;
        var from = world.HullPosition(hull);
        var flight = hull.IsOwn ? snapshot.Flight : JumpRules.DriveOf(snapshot.Flight, hull.Stats);
        var charted = galaxy is not null && snapshot.Exploration.IsCharted(galaxy.SectorOf(target));
        eligibility = JumpRules.Evaluate(from, target, flight, snapshot.Content.Exploration, charted);
        if (eligibility.Reason == JumpRefusal.NoDrive)
            return JumpBlock.NoDrive;
        if (hull.Jump is JumpPhase.Tunnel or JumpPhase.Arrive)
            return JumpBlock.Busy;
        return eligibility.Reason switch
        {
            JumpRefusal.TooClose => JumpBlock.TooClose,
            JumpRefusal.TooFar => JumpBlock.TooFar,
            JumpRefusal.None => JumpBlock.None,
            _ => JumpBlock.Invalid,
        };
    }

    /// <summary>Plans a jump to <paramref name="target"/> for <paramref name="selection"/>; with none selected the figures are the ship being flown's.</summary>
    public static JumpPlan Evaluate(SessionSnapshot snapshot, WorldView world, ReadOnlySpan<uint> selection, Double3 target)
    {
        var plan = new JumpPlan { Ships = selection.Length, MinDistance = snapshot.Content.Exploration.JumpMinimum, Block = JumpBlock.NoShips };

        // The reference ship: one that can go (rank 2), else one with a drive that is held back (1), else any (0).
        var rank = -1;
        for (var i = 0; i < selection.Length; i++)
        {
            if (!world.TryGetHull(selection[i], out var hull))
                continue;
            var block = Check(snapshot, world, hull, target, out var eligibility);
            if (block == JumpBlock.None)
            {
                plan.Ready++;
                if (hull.IsOwn)
                    plan.ReadyPiloted++;
                else
                    plan.ReadyRemote++;
            }

            var shipRank = block == JumpBlock.None ? 2 : block == JumpBlock.NoDrive ? 0 : 1;
            if (shipRank > rank)
            {
                rank = shipRank;
                plan.Reference = eligibility;
                plan.Block = block;
                plan.ReferencePiloted = hull.IsOwn;
                plan.MaxDistance = hull.IsOwn ? snapshot.Flight.FtlMaxDistance : hull.Stats.FtlMaxDistance;
            }
        }

        plan.HasReference = rank >= 0;
        if (plan.Ready > 0)
            plan.Block = JumpBlock.None;

        // Nothing selected still shows the ship being flown's range and risk, with the reason that nothing can go.
        var own = world.Own;
        if (selection.Length == 0 && own.Id != 0)
        {
            Check(snapshot, world, own, target, out plan.Reference);
            plan.HasReference = true;
            plan.ReferencePiloted = true;
            plan.MaxDistance = snapshot.Flight.FtlMaxDistance;
        }

        return plan;
    }
}
