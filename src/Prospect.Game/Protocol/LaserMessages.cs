using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>
/// The pilot's firing intent, sent reliably by the client whenever it changes (at most once a tick): which action
/// groups fire (zero for none), and the aim, a ray from the camera through the pointer. The ray's origin is given
/// relative to the flown hull's position, in world axes, and its direction is a unit vector in world axes. The server
/// resolves it to each laser's own line.
/// </summary>
public readonly record struct LaserAimMessage(uint Hull, ushort Groups, Vector3 Offset, Vector3 Direction)
{
    /// <summary>Id, hull, groups, offset and direction.</summary>
    public const int MaxWireSize = 1 + 4 + 2 + 12 + 12;

    /// <summary>Writes the whole message.</summary>
    public void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.LaserAim);
        writer.WriteUInt32(Hull);
        writer.WriteUInt16(Groups);
        writer.WriteSingle(Offset.X); writer.WriteSingle(Offset.Y); writer.WriteSingle(Offset.Z);
        writer.WriteSingle(Direction.X); writer.WriteSingle(Direction.Y); writer.WriteSingle(Direction.Z);
    }

    /// <summary>Refuses a malformed message: unknown group bits, figures that are no numbers, a direction that is no unit vector.</summary>
    public static bool TryRead(ref NetReader reader, out LaserAimMessage message)
    {
        var hull = reader.ReadUInt32();
        var groups = reader.ReadUInt16();
        var offset = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var direction = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        message = new(hull, groups, offset, direction);
        var length = direction.LengthSquared();
        return reader.IsValid && reader.Remaining == 0 && hull != 0 && (groups & ~ActionGroups.AllMask) == 0
            && float.IsFinite(offset.LengthSquared()) && float.IsFinite(length) && Math.Abs(length - 1) < 1e-3f;
    }
}

/// <summary>
/// Changes the action group of one groupable part of the flown hull without a rebuild: the part is named by its place
/// in the design's <see cref="HullDesign.Lasers"/> list. <see cref="ActionGroups.NotOverridden"/> clears the change, so
/// the design's group stands again.
/// </summary>
public readonly record struct SetActionGroupMessage(uint Hull, byte Part, byte Group)
{
    /// <summary>Id, hull, part and group.</summary>
    public const int MaxWireSize = 1 + 4 + 1 + 1;

    /// <summary>Writes the whole message.</summary>
    public void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.SetActionGroup);
        writer.WriteUInt32(Hull); writer.WriteByte(Part); writer.WriteByte(Group);
    }

    /// <summary>Refuses a malformed message; the server checks the hull and the part.</summary>
    public static bool TryRead(ref NetReader reader, out SetActionGroupMessage message)
    {
        message = new(reader.ReadUInt32(), reader.ReadByte(), reader.ReadByte());
        return reader.IsValid && reader.Remaining == 0 && message.Hull != 0 && message.Part < ActionGroupsMessage.MaxParts
            && (message.Group == ActionGroups.NotOverridden || ActionGroups.Valid(message.Group));
    }
}

/// <summary>
/// The flown hull's per-hull action group changes, sent reliably when they change or the flown hull does: one byte a
/// groupable part, in the order of <see cref="HullDesign.Lasers"/>, <see cref="ActionGroups.NotOverridden"/> where the
/// design's group stands.
/// </summary>
public static class ActionGroupsMessage
{
    /// <summary>Groupable parts of one hull whose group can be changed without a rebuild, in design order.</summary>
    public const int MaxParts = 64;

    /// <summary>Id, hull, count and the parts.</summary>
    public const int MaxWireSize = 1 + 4 + 1 + MaxParts;

    /// <summary>Writes the whole message.</summary>
    public static void Write(ref NetWriter writer, uint hull, scoped ReadOnlySpan<byte> groups)
    {
        writer.WriteByte((byte)MessageId.ActionGroups);
        writer.WriteUInt32(hull);
        writer.WriteByte((byte)groups.Length);
        foreach (var group in groups) writer.WriteByte(group);
    }

    /// <summary>Reads into <paramref name="groups"/>, which holds <see cref="MaxParts"/>; refuses a value that is no group.</summary>
    public static bool TryRead(ref NetReader reader, scoped Span<byte> groups, out uint hull, out int count)
    {
        hull = reader.ReadUInt32();
        count = reader.ReadByte();
        if (!reader.IsValid || count > MaxParts || count > groups.Length || reader.Remaining != count) return false;
        for (var i = 0; i < count; i++)
        {
            groups[i] = reader.ReadByte();
            if (groups[i] != ActionGroups.NotOverridden && !ActionGroups.Valid(groups[i])) return false;
        }
        return reader.IsValid;
    }
}

/// <summary>What a beam is doing, as the client draws it.</summary>
public enum LaserBeamState : byte
{
    /// <summary>Firing into space: the beam ends at the laser's range.</summary>
    Firing = 1,

    /// <summary>Cutting a rock: the beam ends on its surface.</summary>
    Cutting = 2,
}

/// <summary>One beam a client is told of: the hull, which of its lasers, what it does and where it ends.</summary>
public struct LaserBeamView
{
    /// <summary>The hull's packed handle.</summary>
    public uint Hull;

    /// <summary>The laser's place in the design's <see cref="HullDesign.Lasers"/> list.</summary>
    public byte Part;

    public LaserBeamState State;

    /// <summary>Where the beam ends, relative to the hull's position, in world axes.</summary>
    public Vector3 End;
}

/// <summary>
/// The beams of the hulls a client may see, sent every tick one fires (unreliable: superseded every tick). An empty
/// set follows the last beam; a client also lets a beam go that is not refreshed for a few ticks.
/// </summary>
public static class LaserBeamsMessage
{
    /// <summary>Most beams in one message.</summary>
    public const int MaxBeams = 64;

    /// <summary>Bytes a beam takes.</summary>
    public const int RecordBytes = 4 + 1 + 1 + 12;

    /// <summary>Id, tick, count and the beams.</summary>
    public const int MaxWireSize = 1 + 8 + 1 + MaxBeams * RecordBytes;

    /// <summary>Writes the whole message.</summary>
    public static void Write(ref NetWriter writer, long tick, ReadOnlySpan<LaserBeamView> beams)
    {
        writer.WriteByte((byte)MessageId.LaserBeams);
        writer.WriteInt64(tick);
        writer.WriteByte((byte)beams.Length);
        foreach (ref readonly var beam in beams)
        {
            writer.WriteUInt32(beam.Hull); writer.WriteByte(beam.Part); writer.WriteByte((byte)beam.State);
            writer.WriteSingle(beam.End.X); writer.WriteSingle(beam.End.Y); writer.WriteSingle(beam.End.Z);
        }
    }

    /// <summary>Refuses truncation, surplus bytes, unknown states and figures that are no numbers.</summary>
    public static bool TryRead(ref NetReader reader, scoped Span<LaserBeamView> beams, out long tick, out int count)
    {
        tick = reader.ReadInt64();
        count = reader.ReadByte();
        if (!reader.IsValid || tick < 0 || count > MaxBeams || count > beams.Length || reader.Remaining != count * RecordBytes) return false;
        for (var i = 0; i < count; i++)
        {
            ref var beam = ref beams[i];
            beam.Hull = reader.ReadUInt32();
            beam.Part = reader.ReadByte();
            beam.State = (LaserBeamState)reader.ReadByte();
            beam.End = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (beam.Hull == 0 || beam.Part >= ActionGroupsMessage.MaxParts || beam.State is not (LaserBeamState.Firing or LaserBeamState.Cutting)
                || !float.IsFinite(beam.End.LengthSquared()) || beam.End.LengthSquared() > 1e8f) return false;
        }
        return reader.IsValid;
    }
}

/// <summary>A complete sensor-filtered set of loose ore chunks, sent reliably every few ticks, as loose containers are.</summary>
public static class OreChunkMessage
{
    /// <summary>
    /// Bytes a chunk takes: id, position, velocity, orientation, spin, resource, grade and mass, then the tractor
    /// that holds it (hull, mount, stage and whether it pulls).
    /// </summary>
    public const int RecordBytes = 4 + 24 + 12 + 16 + 12 + 1 + 4 + 4 + 4 + 2 + 1 + 1;

    /// <summary>Id, tick and count.</summary>
    public const int HeaderBytes = 1 + 8 + 2;

    /// <summary>The largest message: every chunk the pool holds.</summary>
    public const int MaxWireSize = HeaderBytes + OreChunks.Capacity * RecordBytes;

    /// <summary>Writes one chunk; the saved world uses the same record.</summary>
    public static void WriteRecord(ref NetWriter w, in OreChunk c)
    {
        w.WriteUInt32(c.Id);
        w.WriteDouble(c.Position.X); w.WriteDouble(c.Position.Y); w.WriteDouble(c.Position.Z);
        w.WriteSingle(c.Velocity.X); w.WriteSingle(c.Velocity.Y); w.WriteSingle(c.Velocity.Z);
        w.WriteSingle(c.Orientation.X); w.WriteSingle(c.Orientation.Y); w.WriteSingle(c.Orientation.Z); w.WriteSingle(c.Orientation.W);
        w.WriteSingle(c.AngularVelocity.X); w.WriteSingle(c.AngularVelocity.Y); w.WriteSingle(c.AngularVelocity.Z);
        w.WriteByte((byte)c.Resource);
        w.WriteSingle(c.Grade);
        w.WriteSingle(c.Mass);
        w.WriteUInt32(c.TractorHull); w.WriteUInt16((ushort)c.TractorMount);
        w.WriteByte(c.TractorStage); w.WriteByte(c.TractorPowered ? (byte)1 : (byte)0);
    }

    /// <summary>Reads one chunk; validation belongs to the enclosing message or saved-world contract.</summary>
    public static OreChunk ReadRecord(ref NetReader r)
    {
        var c = new OreChunk
        {
            Id = r.ReadUInt32(),
            Position = new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
            Velocity = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Orientation = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            AngularVelocity = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Resource = r.ReadByte(),
            Grade = r.ReadSingle(),
            Mass = r.ReadSingle(),
            TractorHull = r.ReadUInt32(),
            TractorMount = r.ReadUInt16(),
            TractorStage = r.ReadByte(),
        };
        var powered = r.ReadByte();
        c.TractorPowered = powered == 1;

        // A flag that is neither on nor off makes the claim one ValidClaim refuses.
        if (powered > 1) c.TractorStage = byte.MaxValue;
        return c;
    }

    /// <summary>A claim a server could have sent: none at all, or a hull, one of a design's mounts and a stage.</summary>
    public static bool ValidClaim(GameContent content, in OreChunk c) => c.TractorHull == 0
        ? c.TractorMount == 0 && c.TractorStage == 0 && !c.TractorPowered
        : c.TractorHull >> 16 != 0 && (uint)c.TractorMount < (uint)content.Blocks.Limits.MaxMounts && c.TractorStage <= 1;

    /// <summary>Writes a full set; an empty set withdraws every earlier chunk.</summary>
    public static void Write(ref NetWriter writer, long tick, ReadOnlySpan<OreChunk> items)
    {
        writer.WriteByte((byte)MessageId.OreChunks);
        writer.WriteInt64(tick);
        writer.WriteUInt16((ushort)items.Length);
        foreach (ref readonly var c in items) WriteRecord(ref writer, c);
    }

    /// <summary>Refuses truncation, surplus bytes, repeated identities and invalid physical or payload values.</summary>
    public static bool TryRead(ref NetReader reader, GameContent content, scoped Span<OreChunk> items, out long tick, out int count)
    {
        tick = reader.ReadInt64();
        count = reader.ReadUInt16();
        if (!reader.IsValid || tick < 0 || count > OreChunks.Capacity || count > items.Length || reader.Remaining != count * RecordBytes) return false;
        for (var i = 0; i < count; i++)
        {
            var c = ReadRecord(ref reader);
            if (c.Id == 0 || !OreChunks.Valid(content, c) || !ValidClaim(content, c)) return false;
            for (var j = 0; j < i; j++) if (items[j].Id == c.Id) return false;
            items[i] = c;
        }
        return reader.IsValid;
    }
}
