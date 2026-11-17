using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>Complete pilot intent for one authoritative world interval.</summary>
public struct InputRecord
{
    public long TargetTick;
    public uint Sequence;
    public FlightInput Input;
    public ushort OrderId;
    public HullOrder Order;
}

/// <summary>Redundant, immutable interval records scoped to a server-issued control epoch.</summary>
public static class InputMessage
{
    public const int MaxRecords = 4;
    public const int MaxWireSize = 6 + MaxRecords * (8 + 4 + FlightInput.WireSize + 2 + 1 + 24);

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<InputRecord> records, uint epoch)
    {
        writer.WriteByte((byte)MessageId.Input);
        writer.WriteUInt32(epoch);
        writer.WriteByte((byte)records.Length);
        foreach (var record in records)
        {
            writer.WriteInt64(record.TargetTick);
            writer.WriteUInt32(record.Sequence);
            record.Input.Write(ref writer);
            writer.WriteUInt16(record.OrderId);
            writer.WriteByte((byte)record.Order.Kind);
            if (record.Order.Kind != OrderKind.None)
                WorldWire.WritePosition(ref writer, record.Order.Target);
        }
    }

    public static bool TryRead(ref NetReader reader, scoped Span<InputRecord> records, out int count, out uint epoch)
    {
        epoch = reader.ReadUInt32();
        count = reader.ReadByte();
        if (count is < 1 or > MaxRecords || count > records.Length) return false;
        for (var i = 0; i < count; i++)
        {
            records[i] = default;
            records[i].TargetTick = reader.ReadInt64();
            records[i].Sequence = reader.ReadUInt32();
            records[i].Input = FlightInput.Read(ref reader);
            records[i].OrderId = reader.ReadUInt16();
            var kind = reader.ReadByte();
            if (kind > (byte)OrderKind.Jump) return false;
            records[i].Order.Kind = (OrderKind)kind;
            if (kind != 0 && !WorldWire.TryReadPosition(ref reader, out records[i].Order.Target)) return false;
        }
        return reader.IsValid;
    }
}

/// <summary>An order for one hull, as the command view issues it.</summary>
public struct FleetOrder
{
    public uint Hull;
    public HullOrder Order;
}

/// <summary>Client to server, reliable: orders for hulls the player owns. The server checks ownership.</summary>
public static class FleetOrdersMessage
{
    public const int MaxOrders = 64;

    /// <summary>Message id, count, and 64 hull ids with orders and three double coordinates.</summary>
    public const int MaxWireSize = 2 + MaxOrders * (4 + 1 + 24);

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<FleetOrder> orders)
    {
        writer.WriteByte((byte)MessageId.FleetOrders);
        writer.WriteByte((byte)orders.Length);
        foreach (var order in orders)
        {
            writer.WriteUInt32(order.Hull);
            writer.WriteByte((byte)order.Order.Kind);
            if (order.Order.Kind != OrderKind.None)
                WorldWire.WritePosition(ref writer, order.Order.Target);
        }
    }

    public static bool TryRead(ref NetReader reader, scoped Span<FleetOrder> orders, out int count)
    {
        count = reader.ReadByte();
        if (count > MaxOrders)
            return false;

        for (var i = 0; i < count; i++)
        {
            orders[i].Hull = reader.ReadUInt32();
            var kind = reader.ReadByte();
            if (kind > (byte)OrderKind.Jump)
                return false;
            orders[i].Order = new HullOrder { Kind = (OrderKind)kind };
            if (kind != (byte)OrderKind.None && !WorldWire.TryReadPosition(ref reader, out orders[i].Order.Target))
                return false;
        }

        return reader.IsValid;
    }
}

/// <summary>Server to client, reliable: asteroid motion records. See <see cref="RockDelta"/>.</summary>
public static class RockDeltasMessage
{
    public const int MaxDeltas = 32;
    public const int WireSize = 2 + MaxDeltas * 46;

    public static void Write(ref NetWriter writer, scoped ReadOnlySpan<RockDelta> deltas)
    {
        writer.WriteByte((byte)MessageId.RockDeltas);
        writer.WriteByte((byte)deltas.Length);
        foreach (var delta in deltas)
        {
            writer.WriteInt32(delta.Rock.Sector.X);
            writer.WriteInt32(delta.Rock.Sector.Y);
            writer.WriteInt32(delta.Rock.Sector.Z);
            writer.WriteUInt16((ushort)delta.Rock.Index);
            WorldWire.WriteVector(ref writer, delta.Offset);
            WorldWire.WriteVector(ref writer, delta.Velocity);
            writer.WriteInt64(delta.StartTick);
        }
    }

    public static bool TryRead(ref NetReader reader, scoped Span<RockDelta> deltas, out int count)
    {
        count = reader.ReadByte();
        if (count > MaxDeltas)
            return false;

        for (var i = 0; i < count; i++)
        {
            var sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            deltas[i].Rock = new RockRef(sector, reader.ReadUInt16());
            if (!WorldWire.TryReadVector(ref reader, out deltas[i].Offset) || !WorldWire.TryReadVector(ref reader, out deltas[i].Velocity))
                return false;
            deltas[i].StartTick = reader.ReadInt64();
        }

        return reader.IsValid;
    }
}

/// <summary>A hull as it appears in a snapshot.</summary>
public struct HullRecord
{
    /// <summary>
    /// Id, owner, design, block revision and losses, position, velocity, orientation, spin, heat, flags, the jump's four figures,
    /// energy, shield, hull and shield delay, the shield's newest hit and the age of its last change, what the
    /// thrust parts did, and the order's target. A hull's tanks are not here: 255 records without health must fit
    /// one snapshot beside its header, so the receiver's own hull has its tanks in the
    /// <see cref="SnapshotHeader"/> and no other hull's are sent.
    /// </summary>
    public const int MaxHealth = GeometryAdmission.MaxBlocks;
    public const int MaxWireSize = 1 + 26 + 3 + MaxHealth * 6 + 4 + 4 + 1 + 4 + 4 + 1 + 4 * MaxLosses + 24 + 12 + 16 + 12 + 4 + 1 + 4 + 4 + 4 + 8 + 16 + 6 + 1 + ThrustBytes + 24;

    /// <summary>A hull's <see cref="ThrustState"/>: three throttles and a byte of flags, in every record.</summary>
    public const int ThrustBytes = 4;

    public uint Id;
    public uint Owner;
    public byte Design;

    /// <summary>Maximum losses accompanying one pose; larger backlogs recover on the bulk lane.</summary>
    public const int MaxLosses = 16;
    /// <summary>The block revision required by this pose.</summary>
    public uint Revision;
    /// <summary>The revision immediately before the first loss.</summary>
    public uint LossStart;
    /// <summary>Independent cosmetic revision; missing visual data never blocks a pose.</summary>
    public uint VisualVersion;
    /// <summary>Latest authoritative local contact, independent of recovery.</summary>
    public CollisionSpark Spark;
    /// <summary>Number of file-order block ids carried.</summary>
    public byte LossCount;
    /// <summary>Inline bounded loss payload.</summary>
    [System.Runtime.CompilerServices.InlineArray(MaxLosses)]
    public struct LossBuffer { private int _element0; }
    public LossBuffer Losses;
    /// <summary>Complete sparse remaining health: unlisted live blocks retain their intact health.</summary>
    public bool HasHealth;
    public ushort HealthCount;
    [System.Runtime.CompilerServices.InlineArray(MaxHealth)]
    public struct HealthIds { private ushort _element0; }
    [System.Runtime.CompilerServices.InlineArray(MaxHealth)]
    public struct HealthValues { private float _element0; }
    public HealthIds DamagedBlocks;
    public HealthValues RemainingHealth;


    /// <summary>
    /// The hull's motion and charge. <see cref="HullState.Fuel"/> does not travel in a record and reads empty:
    /// the receiver's own hull takes its tanks from <see cref="SnapshotHeader.OwnTanks"/>.
    /// </summary>
    public HullState State;
    /// <summary>Environment flags sampled at this record’s server tick.</summary>
    public HullEnvironment Environment;
    public HullOrder Order;
    /// <summary>Conservative actual record bound, excluding unused health entries.</summary>
    public readonly int WireBound => MaxWireSize - (MaxHealth - HealthCount) * 6;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)Environment);
        writer.WriteByte(HasHealth ? (byte)1 : (byte)0);
        writer.WriteUInt16(HealthCount);
        for (var i = 0; i < HealthCount; i++)
        {
            writer.WriteUInt16(DamagedBlocks[i]);
            writer.WriteSingle(RemainingHealth[i]);
        }
        writer.WriteUInt32(Id);
        writer.WriteUInt32(Owner);
        writer.WriteByte(Design);
        writer.WriteUInt32(Revision);
        writer.WriteUInt32(LossStart);
        writer.WriteUInt32(VisualVersion);
        Spark.Write(ref writer);
        writer.WriteByte(LossCount);
        for (var i = 0; i < LossCount; i++) writer.WriteInt32(Losses[i]);
        WorldWire.WritePosition(ref writer, State.Position);
        WorldWire.WriteVector(ref writer, State.Velocity);
        writer.WriteSingle(State.Orientation.X);
        writer.WriteSingle(State.Orientation.Y);
        writer.WriteSingle(State.Orientation.Z);
        writer.WriteSingle(State.Orientation.W);
        WorldWire.WriteVector(ref writer, State.AngularVelocity);
        writer.WriteSingle(State.Heat);
        writer.WriteByte((byte)((State.BoostLocked ? 1 : 0) | (byte)Order.Kind << 1 | (byte)State.Jump << 3 | (State.Boosting ? 1 << 6 : 0) | (State.ShieldOffline ? 1 << 7 : 0)));
        writer.WriteSingle(State.JumpTimer);
        writer.WriteSingle(State.JumpCalculation);
        writer.WriteSingle(State.JumpTunnelSeconds);
        writer.WriteDouble(State.JumpTunnelElapsed);
        writer.WriteSingle(State.Energy);
        writer.WriteSingle(State.Shield);
        writer.WriteSingle(State.Hull);
        writer.WriteSingle(State.ShieldCooldown);
        writer.WriteByte(State.ShieldHit.Serial);
        writer.WriteByte(State.ShieldHit.Strength);
        writer.WriteByte((byte)State.ShieldHit.X);
        writer.WriteByte((byte)State.ShieldHit.Y);
        writer.WriteByte((byte)State.ShieldHit.Z);
        writer.WriteByte(State.ShieldHit.AgeTicks);
        writer.WriteByte(State.ShieldChangeTicks);
        writer.WriteByte((byte)State.Thrust.X);
        writer.WriteByte((byte)State.Thrust.Y);
        writer.WriteByte((byte)State.Thrust.Z);
        writer.WriteByte(State.Thrust.Flags);
        if (Order.Kind != OrderKind.None)
            WorldWire.WritePosition(ref writer, Order.Target);
    }

    public static bool TryRead(ref NetReader reader, out HullRecord record)
    {
        record = default;
        record.Environment = (HullEnvironment)reader.ReadByte();
        if (((byte)record.Environment & ~7) != 0 || (record.Environment & HullEnvironment.SensorsDisrupted) != 0 && (record.Environment & HullEnvironment.InLightningCloud) == 0) return false;
        var health = reader.ReadByte();
        record.HasHealth = health == 1;
        record.HealthCount = reader.ReadUInt16();
        if (health > 1 || record.HealthCount > MaxHealth || (!record.HasHealth && record.HealthCount != 0)) return false;
        for (var i = 0; i < record.HealthCount; i++)
        {
            record.DamagedBlocks[i] = reader.ReadUInt16();
            record.RemainingHealth[i] = reader.ReadSingle();
            if (!float.IsFinite(record.RemainingHealth[i]) || record.RemainingHealth[i] <= 0 ||
                (i > 0 && record.DamagedBlocks[i] <= record.DamagedBlocks[i - 1])) return false;
        }
        record.Id = reader.ReadUInt32();
        record.Owner = reader.ReadUInt32();
        record.Design = reader.ReadByte();
        record.Revision = reader.ReadUInt32();
        record.LossStart = reader.ReadUInt32();
        record.VisualVersion = reader.ReadUInt32();
        record.Spark = CollisionSpark.Read(ref reader);
        record.LossCount = reader.ReadByte();
        if (record.LossCount > MaxLosses || record.LossStart > record.Revision || record.LossCount > record.Revision - record.LossStart)
            return false;
        for (var i = 0; i < record.LossCount; i++) record.Losses[i] = reader.ReadInt32();
        var ok = WorldWire.TryReadPosition(ref reader, out record.State.Position);
        ok &= WorldWire.TryReadVector(ref reader, out record.State.Velocity);
        var q = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var lengthSquared = q.LengthSquared();
        ok &= float.IsFinite(lengthSquared) && lengthSquared > 0.5f && lengthSquared < 2f;
        record.State.Orientation = ok ? Quaternion.Normalize(q) : Quaternion.Identity;
        ok &= WorldWire.TryReadVector(ref reader, out record.State.AngularVelocity);
        record.State.Heat = Math.Clamp(reader.ReadSingle(), 0f, 1f);
        var flags = reader.ReadByte();
        record.State.BoostLocked = (flags & 1) != 0;
        record.State.Boosting = (flags & 1 << 6) != 0;
        record.State.ShieldOffline = (flags & 1 << 7) != 0;
        record.State.Jump = (JumpPhase)Math.Min(flags >> 3 & 7, (int)JumpPhase.Arrive);
        record.State.JumpTimer = reader.ReadSingle();
        ok &= float.IsFinite(record.State.JumpTimer);
        record.State.JumpCalculation = reader.ReadSingle();
        ok &= float.IsFinite(record.State.JumpCalculation) && record.State.JumpCalculation >= 0;
        record.State.JumpTunnelSeconds = reader.ReadSingle();
        record.State.JumpTunnelElapsed = reader.ReadDouble();
        ok &= float.IsFinite(record.State.JumpTunnelSeconds) && record.State.JumpTunnelSeconds >= 0
            && record.State.JumpTunnelSeconds <= HullSim.MaxJumpTravelSeconds;
        ok &= double.IsFinite(record.State.JumpTunnelElapsed) && record.State.JumpTunnelElapsed >= 0
            && record.State.JumpTunnelElapsed <= record.State.JumpTunnelSeconds + (double)HullSim.Dt;
        if (record.State.Jump is JumpPhase.Align or JumpPhase.Charge or JumpPhase.None)
            ok &= record.State.JumpTunnelElapsed == 0;
        record.State.Energy = reader.ReadSingle();
        record.State.Shield = reader.ReadSingle();
        record.State.Hull = reader.ReadSingle();
        record.State.ShieldCooldown = reader.ReadSingle();
        ok &= float.IsFinite(record.State.Energy) && float.IsFinite(record.State.Shield) && float.IsFinite(record.State.Hull) && float.IsFinite(record.State.ShieldCooldown);
        ok &= record.State.Shield >= 0 && record.State.ShieldCooldown >= 0;
        record.State.ShieldHit.Serial = reader.ReadByte();
        record.State.ShieldHit.Strength = reader.ReadByte();
        record.State.ShieldHit.X = (sbyte)reader.ReadByte();
        record.State.ShieldHit.Y = (sbyte)reader.ReadByte();
        record.State.ShieldHit.Z = (sbyte)reader.ReadByte();
        record.State.ShieldHit.AgeTicks = reader.ReadByte();
        record.State.ShieldChangeTicks = reader.ReadByte();
        ok &= record.State.ShieldHit.AgeTicks <= ShieldHit.MaxAgeTicks && record.State.ShieldChangeTicks <= ShieldHit.MaxAgeTicks;
        record.State.Thrust.X = (sbyte)reader.ReadByte();
        record.State.Thrust.Y = (sbyte)reader.ReadByte();
        record.State.Thrust.Z = (sbyte)reader.ReadByte();
        record.State.Thrust.Flags = reader.ReadByte();
        ok &= record.State.Thrust.IsValid;
        var kind = flags >> 1 & 3;
        ok &= kind <= (int)OrderKind.Jump;
        if (kind != 0 && ok)
        {
            record.Order.Kind = (OrderKind)kind;
            ok &= WorldWire.TryReadPosition(ref reader, out record.Order.Target);
        }

        return ok && reader.IsValid;
    }
}

/// <summary>
/// Server to client, unreliable: the header of a world snapshot. It is followed by <see cref="HullCount"/> hull
/// records.
/// </summary>
public struct SnapshotHeader
{
    /// <summary>
    /// The fixed fields, the count of the own hull's tanks and four bytes for each of them, then the count of its
    /// lasers, two bytes of heat for each and a bit for each that is shut down.
    /// </summary>
    public const int MaxWireSize = 25 + 1 + TankFuel.MaxTanks * sizeof(float) + 1 + LaserHeat.MaxLasers * sizeof(ushort) + LaserHeat.MaxLasers / 8;
    public long ServerTick;
    public uint ControlEpoch;
    public ushort AppliedOrderId;

    /// <summary>The last input of the receiving client that the server has simulated.</summary>
    public uint AckedInput;

    /// <summary>The receiving client's own hull.</summary>
    public uint OwnHull;
    public byte HullCount;

    /// <summary>
    /// True while the receiving client's own hull is its escape drone, even after the drone was edited into
    /// another design. Only a drone may ask for a new ship, so the interface offers that only then.
    /// </summary>
    public bool OwnIsDrone;

    /// <summary>
    /// How many tanks the design of <see cref="OwnHull"/> has, at most <see cref="TankFuel.MaxTanks"/>, and the
    /// kilograms in each, in the order of <see cref="HullDesign.Tanks"/>; a tank that is lost holds nothing. The
    /// receiver predicts that hull with the server's own flight code, which burns from each tank, so it is sent
    /// every tank's own figure, in single precision: nearer than one part in ten million to what the server
    /// keeps, and never nothing while the tank holds something. No other hull's tanks are sent.
    /// </summary>
    public byte OwnTankCount;
    public TankLevels OwnTanks;

    /// <summary>Room for the kilograms in each tank of one hull.</summary>
    [System.Runtime.CompilerServices.InlineArray(TankFuel.MaxTanks)]
    public struct TankLevels { private float _element0; }

    /// <summary>
    /// How many lasers the design of <see cref="OwnHull"/> has heat for, at most <see cref="LaserHeat.MaxLasers"/>,
    /// each one's heat in sixteen bits (<see cref="OwnLaserHeat"/>, in the order of <see cref="HullDesign.Lasers"/>),
    /// and a bit for each that overheated and is shut down. The receiver steps its own hull's laser heat on with the
    /// server's rule (<see cref="LaserHeat.Step"/>), so it is sent the figures of the same tick as the hull's state.
    /// No other hull's laser heat is sent.
    /// </summary>
    public byte OwnLaserCount;
    public LaserHeatWire OwnLaserHeat;
    public ulong OwnLasersLocked;

    /// <summary>Room for the heat of each laser of one hull as it travels: 0 is cool, 65535 overheated.</summary>
    [System.Runtime.CompilerServices.InlineArray(LaserHeat.MaxLasers)]
    public struct LaserHeatWire { private ushort _element0; }

    /// <summary>Takes the first <paramref name="count"/> lasers of a hull's heat as they are sent.</summary>
    public void SetLasers(in LaserHeatState lasers, int count)
    {
        count = Math.Clamp(count, 0, LaserHeat.MaxLasers);
        OwnLaserCount = (byte)count;
        for (var part = 0; part < count; part++)
            OwnLaserHeat[part] = (ushort)MathF.Round(Math.Clamp(lasers.Heat[part], 0f, 1f) * ushort.MaxValue);
        OwnLasersLocked = count == LaserHeat.MaxLasers ? lasers.Locked : lasers.Locked & ((1UL << count) - 1);
    }

    /// <summary>The lasers' heat as the receiver keeps it; nothing is asked to fire.</summary>
    public readonly LaserHeatState Lasers()
    {
        var lasers = new LaserHeatState { Locked = OwnLasersLocked };
        for (var part = 0; part < OwnLaserCount; part++)
            lasers.Heat[part] = OwnLaserHeat[part] / (float)ushort.MaxValue;
        return lasers;
    }

    /// <summary>The bytes this header takes: fewer than <see cref="MaxWireSize"/> for a hull with fewer tanks or lasers.</summary>
    public readonly int WireSize => 25 + 1 + OwnTankCount * sizeof(float) + 1 + OwnLaserCount * sizeof(ushort) + (OwnLaserCount + 7) / 8;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.Snapshot);
        writer.WriteInt64(ServerTick);
        writer.WriteUInt32(ControlEpoch);
        writer.WriteUInt16(AppliedOrderId);
        writer.WriteUInt32(AckedInput);
        writer.WriteUInt32(OwnHull);
        writer.WriteByte(HullCount);
        writer.WriteByte(OwnIsDrone ? (byte)1 : (byte)0);
        writer.WriteByte(OwnTankCount);
        for (var i = 0; i < OwnTankCount; i++)
            writer.WriteSingle(OwnTanks[i]);
        writer.WriteByte(OwnLaserCount);
        for (var i = 0; i < OwnLaserCount; i++)
            writer.WriteUInt16(OwnLaserHeat[i]);
        for (var i = 0; i < (OwnLaserCount + 7) / 8; i++)
            writer.WriteByte((byte)(OwnLasersLocked >> i * 8));
    }

    public static bool TryRead(ref NetReader reader, out SnapshotHeader header)
    {
        header = default;
        header.ServerTick = reader.ReadInt64();
        header.ControlEpoch = reader.ReadUInt32();
        header.AppliedOrderId = reader.ReadUInt16();
        header.AckedInput = reader.ReadUInt32();
        header.OwnHull = reader.ReadUInt32();
        header.HullCount = reader.ReadByte();
        var flags = reader.ReadByte();
        header.OwnIsDrone = flags == 1;
        header.OwnTankCount = reader.ReadByte();

        // Each figure a number that is not negative; whether it fits its tank is for whoever knows the design.
        var tanks = true;
        for (var i = 0; i < header.OwnTankCount; i++)
        {
            var kilograms = reader.ReadSingle();
            header.OwnTanks[i] = kilograms;
            tanks &= float.IsFinite(kilograms) && kilograms >= 0f;
        }

        // Lasers: no more than a hull has heat for, and no bit for a laser that was not sent.
        header.OwnLaserCount = reader.ReadByte();
        if (header.OwnLaserCount > LaserHeat.MaxLasers) return false;
        for (var i = 0; i < header.OwnLaserCount; i++)
            header.OwnLaserHeat[i] = reader.ReadUInt16();
        for (var i = 0; i < (header.OwnLaserCount + 7) / 8; i++)
            header.OwnLasersLocked |= (ulong)reader.ReadByte() << i * 8;
        var lasers = header.OwnLaserCount == LaserHeat.MaxLasers || header.OwnLasersLocked >> header.OwnLaserCount == 0;

        return reader.IsValid && flags <= 1 && header.ServerTick >= 0 && header.ServerTick <= long.MaxValue - 256 && tanks && lasers;
    }
}

internal static class WorldWire
{
    /// <summary>No coordinate in the galaxy is anywhere near this large; anything beyond it is corrupt or hostile.</summary>
    private const double MaxCoordinate = 1e13;

    public static void WritePosition(ref NetWriter writer, Double3 value)
    {
        writer.WriteDouble(value.X);
        writer.WriteDouble(value.Y);
        writer.WriteDouble(value.Z);
    }

    public static bool TryReadPosition(ref NetReader reader, out Double3 value)
    {
        value = new Double3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        return Math.Abs(value.X) < MaxCoordinate && Math.Abs(value.Y) < MaxCoordinate && Math.Abs(value.Z) < MaxCoordinate;
    }

    public static void WriteVector(ref NetWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    public static bool TryReadVector(ref NetReader reader, out Vector3 value)
    {
        value = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
