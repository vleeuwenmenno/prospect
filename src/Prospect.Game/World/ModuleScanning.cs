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
    public long StartedTick, CompletesTick;
    /// <summary>Each family cools on its own: the owner's cooldown for one never blocks the others.</summary>
    public long GasCooldownUntilTick, AsteroidCooldownUntilTick, LocatorCooldownUntilTick;
    public Double3 Origin;
    public bool Partial;
}

/// <summary>Cooldown access by family, so a caller never picks the field by hand.</summary>
public static class ModuleScanViewExtensions
{
    /// <summary>The tick at which <paramref name="family"/> may scan again; zero for an unknown family.</summary>
    public static long CooldownUntil(this in ModuleScanView scan, ModuleFamily family) => family switch
    {
        ModuleFamily.GasScanner => scan.GasCooldownUntilTick,
        ModuleFamily.AsteroidScanner => scan.AsteroidCooldownUntilTick,
        ModuleFamily.AsteroidLocator => scan.LocatorCooldownUntilTick,
        _ => 0,
    };

    /// <summary>Sets one family's cooldown; an unknown family is ignored.</summary>
    public static void SetCooldown(this ref ModuleScanView scan, ModuleFamily family, long tick)
    {
        switch (family)
        {
            case ModuleFamily.GasScanner: scan.GasCooldownUntilTick = tick; break;
            case ModuleFamily.AsteroidScanner: scan.AsteroidCooldownUntilTick = tick; break;
            case ModuleFamily.AsteroidLocator: scan.LocatorCooldownUntilTick = tick; break;
        }
    }
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
        if (equipment.Block == 0 || !double.IsFinite(equipment.PowerDraw) || equipment.PowerDraw <= 0) return false;
        var cost = (float)(equipment.PowerDraw / TickLoop.SimulationHz);
        if (!float.IsFinite(energy) || energy < cost) return false;
        energy -= cost;
        return true;
    }

    /// <summary>Spends and accounts for the very same active draw, shared by authority and prediction.</summary>
    public static bool Spend(ref float energy, in ModuleEquipment equipment, ref PowerFlow flow)
    {
        if (!Spend(ref energy, equipment)) return false;
        flow.Modules += (float)equipment.PowerDraw;
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

/// <summary>Shared validation for retained observations, independent of transport and save layout.</summary>
public static class ModuleObservationRules
{
    /// <summary>Structural validation independent of a world's resource catalogue.</summary>
    public static bool Valid(in ModuleObservation record)
    {
        if (record.Owner == 0 || record.Family is < ModuleFamily.GasScanner or > ModuleFamily.AsteroidLocator
            || record.Quality > ModuleQuality.AlienTech || record.Index < 0 || record.Index > ushort.MaxValue
            || record.RecordedTick < 0 || record.ContentRevision != 0 || !Finite(record.Position)
            || !float.IsFinite(record.Radius) || record.Radius < 0 || !float.IsFinite(record.Mass) || record.Mass < 0
            || !Unit(record.Grade) || record.Resource is < -1 or >= 32 || record.Gas.Region >= 8
            || !Unit(record.Gas.Purity) || !Unit(record.Gas.Density)) return false;
        var sum = 0f;
        for (var i = 0; i < GasComposition.Capacity; i++)
        { if (!Unit(record.Gas.Shares[i])) return false; sum += record.Gas.Shares[i]; }
        if (sum > 1.00001f) return false;
        if (record.Family == ModuleFamily.GasScanner)
            return record.Index == record.Gas.Region && record.Resource == -1 && record.Radius == 0 && record.Mass == 0 && record.Grade == 0;
        if (sum != 0 || record.Gas.Purity != 0 || record.Gas.Density != 0 || record.Gas.Region != 0) return false;
        return record.Family != ModuleFamily.AsteroidLocator || record.Radius == 0 && record.Mass == 0 && record.Grade == 0;
    }

    private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
    private static bool Finite(Double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
