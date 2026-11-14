using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>Authoritative execution and refusal states for module requests.</summary>
public enum ModuleScanState : byte
{
    Idle, Preparing, Scanning, Completed, Cancelled, NoEquipment, NoPower, OutOfRange,
    Cooldown, InvalidTarget, Busy, KnowledgeLimit, Unavailable
}

/// <summary>One scan intent. Gas targets name the anchor; asteroid targets name sector and index.</summary>
public readonly record struct ModuleScanRequest(ModuleFamily Family, SectorCoord Sector, int Index = 0);

/// <summary>Progress belongs to one controlled hull and one request serial, never to a rendered target.</summary>
public struct ModuleScanView
{
    public uint Serial, Hull;
    public ModuleFamily Family;
    public ModuleScanState State, Refusal;
    public ModuleQuality Quality;
    public SectorCoord Sector;
    public int Index;
    public long StartedTick, CompletesTick, CooldownUntilTick;
    public Double3 Origin;
    public bool Partial;
}

/// <summary>A retained observation, not a live object. Gas index is its octant; asteroid index is generated identity.</summary>
public struct ModuleObservation
{
    public uint Owner;
    public ModuleFamily Family;
    public ModuleQuality Quality;
    public SectorCoord Sector;
    public int Index;
    public long RecordedTick;
    /// <summary>Reserved for future mining mutations. Zero is today's immutable composition revision.</summary>
    public uint ContentRevision;
    public Double3 Position;
    public float Radius, Mass, Grade;
    /// <summary>One resource per generated asteroid, or -1 for bare rock. Its host-rock share is one; Grade is ore fraction.</summary>
    public int Resource;
    public GasSample Gas;

    /// <summary>Stable knowledge key; locator and detailed analysis are distinct observations.</summary>
    public readonly bool SameKey(in ModuleObservation other) => Family == other.Family && Sector == other.Sector && Index == other.Index;
}

/// <summary>Shared bounds and numeric scan rules; quality is already evaluated from the installed block.</summary>
public static class ModuleScanRules
{
    public const int KnowledgePerOwner = 1024;
    public const int PulseResults = 256;
    public const int MaxOwners = 64;

    /// <summary>Seconds become at least one simulation interval.</summary>
    public static long Ticks(double seconds) => Math.Max(1, (long)Math.Ceiling(seconds * TickLoop.SimulationHz));

    /// <summary>Surface distance allows scanning a large asteroid or cloud from near its edge.</summary>
    public static bool InRange(Double3 from, Double3 target, double radius, double range)
    {
        var d = target - from;
        var reach = radius + range;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z <= reach * reach;
    }

    /// <summary>Consumes one full interval's active energy or refuses without spending a partial interval.</summary>
    public static bool Spend(ref float energy, in ModuleEquipment equipment)
    {
        var cost = (float)(equipment.PowerDraw / TickLoop.SimulationHz);
        if (!float.IsFinite(energy) || energy < cost) return false;
        energy -= cost;
        return true;
    }

    /// <summary>Lower accuracy rounds more coarsely. These are observations, not random claims of precision.</summary>
    public static float Rounded(float value, double accuracy) =>
        (float)(Math.Round(value / (0.01 / accuracy), MidpointRounding.AwayFromZero) * (0.01 / accuracy));

    /// <summary>Copies only detail allowed by the equipment; locator never publishes detailed composition or mass.</summary>
    public static void Disclose(ref ModuleObservation observation, in ModuleEquipment equipment)
    {
        observation.Quality = equipment.Quality;
        if (observation.Family == ModuleFamily.AsteroidLocator)
        { observation.Grade = 0; observation.Mass = 0; observation.Radius = 0; observation.Gas = default; return; }
        observation.Grade = Math.Clamp(Rounded(observation.Grade, equipment.Accuracy), 0, 1);
        if (observation.Family != ModuleFamily.GasScanner) return;
        // Omitted gases stay unnamed with a zero share; the UI shows the remainder as unresolved, never renormalized.
        var threshold = (float)(0.01 / equipment.Accuracy);
        for (var i = 0; i < GasComposition.Capacity; i++)
            observation.Gas.Shares[i] = observation.Gas.Shares[i] < threshold ? 0 :
                Math.Clamp((float)(Math.Floor(observation.Gas.Shares[i] / (0.01 / equipment.Accuracy)) * (0.01 / equipment.Accuracy)), 0, 1);
        observation.Gas.Purity = Math.Clamp(Rounded(observation.Gas.Purity, equipment.Accuracy), 0, 1);
        observation.Gas.Density = Math.Clamp(Rounded(observation.Gas.Density, equipment.Accuracy), 0, 1);
    }
}
