using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>What a player asks of a hull's drone bays.</summary>
public enum DroneCommand : byte
{
    /// <summary>Launch drones to hold station: at the named point, or each in front of its own hatch.</summary>
    Launch = 1,

    /// <summary>Call every drone of the hull home through its hatch, and stop launches still waiting.</summary>
    Recall = 2,

    /// <summary>
    /// Send enough drones to carry the free container <see cref="DroneCommandMessage.Subject"/> back to the hull's
    /// tractor beam, which stows it.
    /// </summary>
    Fetch = 3,
}

/// <summary>What the server made of a drone command, or of a launch it was carrying out.</summary>
public enum DroneReply : byte
{
    None = 0,

    /// <summary>Drones are on their way out; the count says how many were taken on.</summary>
    Launching = 1,

    /// <summary>Drones are on their way home; the count says how many.</summary>
    Recalling = 2,

    /// <summary>The hull has no drone bay left.</summary>
    NoBay = 3,

    /// <summary>Every bay with a hatch to launch from is empty.</summary>
    Empty = 4,

    /// <summary>Every bay with drones in it has its hatch blocked by the hull's own blocks or parts.</summary>
    HatchBlocked = 5,

    /// <summary>The player has as many drones out as one player may.</summary>
    PlayerLimit = 6,

    /// <summary>The world holds as many hulls as it keeps room for.</summary>
    WorldFull = 7,

    /// <summary>The hull is jumping, so no drone leaves it.</summary>
    InTransit = 8,

    /// <summary>The point asked for is farther from the hull than drones may hold.</summary>
    TooFar = 9,

    /// <summary>The hull has no drone out to call home.</summary>
    NoneOut = 10,

    /// <summary>Drones are on their way to fetch a container; the count says how many carry it.</summary>
    Fetching = 11,

    /// <summary>The container is gone, or out of the player's sight.</summary>
    NotFound = 12,

    /// <summary>A tractor beam or other drones already have the container.</summary>
    Taken = 13,

    /// <summary>Someone else keeps a claim on the container.</summary>
    Claimed = 14,

    /// <summary>The hull has no tractor beam to hand the container to, or none that sees in front of it.</summary>
    NoTractor = 15,

    /// <summary>No rack the hull's beams load has room for the container.</summary>
    NoRoom = 16,

    /// <summary>The container is heavier than the hull's bays' drones, or its beams, could ever take together.</summary>
    TooHeavy = 17,

    /// <summary>Not enough drones are inside or holding station now to lift the container.</summary>
    TooFew = 18,

    /// <summary>The carriers lost a drone, or their ship, and let the container go.</summary>
    Dropped = 19,

    /// <summary>Drones handed the container to the tractor beam and are coming home.</summary>
    Delivered = 20,
}

/// <summary>A player's drone command for one of their hulls. Reliable; the server checks everything again.</summary>
/// <remarks><see cref="Subject"/> is the free container a fetch is for, zero for every other command.</remarks>
public readonly record struct DroneCommandMessage(uint Hull, DroneCommand Command, byte Count, bool HasPoint, Vector3 Point, uint Subject = 0)
{
    /// <summary>Id, hull, command, count, flag, point and subject.</summary>
    public const int MaxWireSize = 1 + 4 + 1 + 1 + 1 + 12 + 4;

    /// <summary>Writes the whole command.</summary>
    public void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.DroneCommand);
        writer.WriteUInt32(Hull); writer.WriteByte((byte)Command); writer.WriteByte(Count); writer.WriteByte(HasPoint ? (byte)1 : (byte)0);
        writer.WriteSingle(Point.X); writer.WriteSingle(Point.Y); writer.WriteSingle(Point.Z);
        writer.WriteUInt32(Subject);
    }

    /// <summary>Refuses a malformed command before the server looks at the hull.</summary>
    public static bool TryRead(ref NetReader reader, out DroneCommandMessage command)
    {
        var hull = reader.ReadUInt32(); var kind = (DroneCommand)reader.ReadByte(); var count = reader.ReadByte(); var flag = reader.ReadByte();
        var point = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var subject = reader.ReadUInt32();
        command = new DroneCommandMessage(hull, kind, count, flag == 1, point, subject);
        return reader.IsValid && reader.Remaining == 0 && hull != 0 && kind is DroneCommand.Launch or DroneCommand.Recall or DroneCommand.Fetch
            && flag <= 1 && (kind != DroneCommand.Launch || count > 0)
            && (kind == DroneCommand.Fetch) == (subject != 0) && (kind != DroneCommand.Fetch || (count == 0 && flag == 0))
            && float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) && (flag == 1 || point == Vector3.Zero);
    }
}

/// <summary>One bay of the flown hull as its pilot sees it.</summary>
public struct BayEntry
{
    /// <summary>The bay's block in the hull's design.</summary>
    public ushort Block;

    /// <summary>Drones the bay keeps when full, has inside, has out and has waiting to launch.</summary>
    public byte Capacity, Stocked, Out, Pending;

    /// <summary>True while the bay's block is still there.</summary>
    public bool Live;

    /// <summary>True while the hatch faces open space.</summary>
    public bool HatchClear;

    /// <summary>The tick the drone being rebuilt is done; zero while nothing is being rebuilt.</summary>
    public long RebuildDone;
}

/// <summary>Fixed room for every bay a design may have.</summary>
[System.Runtime.CompilerServices.InlineArray(DroneBayRules.MaxBays)]
public struct BayEntries
{
    private BayEntry _first;
}

/// <summary>The flown hull's drone bays and the newest answer to the player's drone commands.</summary>
public struct BayView
{
    /// <summary>The hull the bays belong to; zero when nothing has been heard.</summary>
    public uint Hull;

    /// <summary>The server tick the view was taken at.</summary>
    public long Tick;

    /// <summary>Counts the server's answers: a new one is a new number, whatever it says.</summary>
    public uint ReplySequence;
    public DroneReply Reply;

    /// <summary>How many drones the answer is about.</summary>
    public byte ReplyCount;

    /// <summary>Drones of the hull out on a fetch, or waiting to launch for one.</summary>
    public byte Fetching;
    public int Count;
    public BayEntries Bays;

    /// <summary>Drones in every bay, out of every bay, waiting to launch, and the most the bays keep.</summary>
    public readonly void Totals(out int stocked, out int outside, out int pending, out int capacity)
    {
        stocked = outside = pending = capacity = 0;
        for (var i = 0; i < Count; i++)
        {
            var bay = Bays[i];
            if (!bay.Live) continue;
            stocked += bay.Stocked; outside += bay.Out; pending += bay.Pending; capacity += bay.Capacity;
        }
    }

    /// <summary>The soonest a rebuilt drone is done in any live bay; zero when none is being rebuilt.</summary>
    public readonly long NextRebuild()
    {
        var next = 0L;
        for (var i = 0; i < Count; i++)
            if (Bays[i].Live && Bays[i].RebuildDone > 0 && (next == 0 || Bays[i].RebuildDone < next)) next = Bays[i].RebuildDone;
        return next;
    }
}

/// <summary>The flown hull's bays, reliable on change, with the newest answer to a drone command.</summary>
public static class BayMessage
{
    public const int RecordBytes = 2 + 1 + 1 + 1 + 1 + 1 + 8;
    public const int HeaderBytes = 1 + 4 + 8 + 4 + 1 + 1 + 1 + 1;
    public const int MaxWireSize = HeaderBytes + DroneBayRules.MaxBays * RecordBytes;

    /// <summary>Writes the view whole.</summary>
    public static void Write(ref NetWriter writer, in BayView view)
    {
        writer.WriteByte((byte)MessageId.Bays);
        writer.WriteUInt32(view.Hull); writer.WriteInt64(view.Tick); writer.WriteUInt32(view.ReplySequence);
        writer.WriteByte((byte)view.Reply); writer.WriteByte(view.ReplyCount); writer.WriteByte(view.Fetching); writer.WriteByte((byte)view.Count);
        for (var i = 0; i < view.Count; i++)
        {
            var bay = view.Bays[i];
            writer.WriteUInt16(bay.Block); writer.WriteByte(bay.Capacity); writer.WriteByte(bay.Stocked);
            writer.WriteByte(bay.Out); writer.WriteByte(bay.Pending);
            writer.WriteByte((byte)((bay.Live ? 1 : 0) | (bay.HatchClear ? 2 : 0)));
            writer.WriteInt64(bay.RebuildDone);
        }
    }

    /// <summary>Refuses anything a server could not have sent.</summary>
    public static bool TryRead(ref NetReader reader, out BayView view)
    {
        view = default;
        view.Hull = reader.ReadUInt32(); view.Tick = reader.ReadInt64(); view.ReplySequence = reader.ReadUInt32();
        view.Reply = (DroneReply)reader.ReadByte(); view.ReplyCount = reader.ReadByte(); view.Fetching = reader.ReadByte(); view.Count = reader.ReadByte();
        if (!reader.IsValid || view.Tick < 0 || view.Reply > DroneReply.Delivered || view.Count > DroneBayRules.MaxBays
            || reader.Remaining != view.Count * RecordBytes) return false;
        for (var i = 0; i < view.Count; i++)
        {
            ref var bay = ref view.Bays[i];
            bay.Block = reader.ReadUInt16(); bay.Capacity = reader.ReadByte(); bay.Stocked = reader.ReadByte();
            bay.Out = reader.ReadByte(); bay.Pending = reader.ReadByte();
            var flags = reader.ReadByte();
            bay.Live = (flags & 1) != 0; bay.HatchClear = (flags & 2) != 0;
            bay.RebuildDone = reader.ReadInt64();
            if (flags > 3 || bay.Capacity is 0 or > DroneBayRules.MaxCapacity || bay.Stocked + bay.Out > bay.Capacity
                || bay.Pending > bay.Stocked || bay.RebuildDone < 0) return false;
        }
        return reader.IsValid;
    }
}
