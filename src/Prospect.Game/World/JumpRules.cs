namespace Prospect.Game.World;

public enum JumpRefusal : byte { None, InvalidTarget, NoDrive, TooClose, TooFar, Busy }

public readonly record struct JumpEligibility(JumpRefusal Reason, bool Charted, double Distance, float Risk, float MaximumDamageFraction)
{
    public bool CanIssueOrder => Reason == JumpRefusal.None;
    public bool BlindRiskImplemented => true;
    public bool Blind => !Charted;
}

/// <summary>Transient navigation feedback. The plot belongs to Hull and never starts an order.</summary>
public struct NavigationView
{
    public uint Hull;
    public uint ResultHull;
    public bool HasPlot;
    public Double3 Target;
    public JumpRefusal Refusal;
}

public static class JumpRules
{
    public static JumpEligibility Evaluate(Double3 from, Double3 target, in FlightParams flight, ExplorationRules rules, bool charted)
    {
        var distance = (target - from).Length;
        var reason = !double.IsFinite(distance) ? JumpRefusal.InvalidTarget
            : flight.Ftl == FtlKind.None || flight.FtlPower <= 0 ? JumpRefusal.NoDrive
            : distance < rules.JumpMinimum ? JumpRefusal.TooClose
            : distance > flight.FtlMaxDistance ? JumpRefusal.TooFar
            : !float.IsFinite(HullSim.JumpTravelSeconds(from, target, flight)) ? JumpRefusal.InvalidTarget
            : JumpRefusal.None;
        var risk = reason != JumpRefusal.None || charted || distance <= rules.BlindSafeDistance ? 0f
            : (float)Math.Clamp((distance - rules.BlindSafeDistance) / (flight.FtlMaxDistance - rules.BlindSafeDistance), 0, 1);
        return new(reason, charted, distance, risk, risk * rules.BlindMaxDamage);
    }

    /// <summary>
    /// The figures <see cref="Evaluate"/> reads for a hull whose data sheet the client has but not its flight
    /// figures: the content-wide jump timings of <paramref name="basis"/> (every hull of a world shares them) with
    /// this hull's drive, its power and its range. A hull flown by the player passes its own flight figures instead.
    /// </summary>
    public static FlightParams DriveOf(in FlightParams basis, in HullStats stats)
    {
        var flight = basis;
        flight.Ftl = stats.Ftl;
        flight.FtlPower = stats.FtlPower;
        flight.FtlMaxDistance = stats.FtlMaxDistance;
        return flight;
    }

    /// <summary>Stable per-jump roll. Risk is probability; a hit takes the reported maximum fraction.</summary>
    public static float DamageFraction(in JumpEligibility eligibility, ulong seed, uint hull, long tick)
    {
        var value = seed ^ ((ulong)hull << 32) ^ (ulong)tick;
        unchecked
        {
            value += 0x9e3779b97f4a7c15UL;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            value ^= value >> 31;
        }
        return (value >> 40) / 16777216.0 < eligibility.Risk ? eligibility.MaximumDamageFraction : 0;
    }

    /// <summary>Proportional damage to surviving blocks, never losing one or crossing 10% intact design HP.</summary>
    public static void ApplyDamage(BlockStateStore store, BlockStateHandle handle, HullDesign design, float fraction)
    {
        var total = store.HitTotal(handle);
        var floor = design.IntactHitTotal * 0.1;
        if (fraction <= 0 || total <= floor) return;
        var scale = Math.Max(floor / total, 1 - Math.Clamp(fraction, 0, 1));
        var points = store.HitPoints(handle);
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i] <= 0) continue;
            // Round upward so summation cannot slip beneath the promised floor.
            var next = Math.Min(points[i], float.BitIncrement((float)(points[i] * scale)));
            store.SetHitPoints(handle, i, next);
        }
    }
}
