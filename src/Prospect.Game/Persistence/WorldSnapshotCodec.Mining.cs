using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>One hull's change of one groupable part's action group, kept without a rebuild.</summary>
/// <param name="Hull">The hull's index in <see cref="WorldSnapshot.Hulls"/>.</param>
/// <param name="Part">The part's place in the design's <see cref="HullDesign.Lasers"/> list.</param>
/// <param name="Group">The group, 0 to 10.</param>
public readonly record struct SavedActionGroup(int Hull, byte Part, byte Group);

public static partial class WorldSnapshotCodec
{
    /// <summary>A loose ore chunk: the wire's record and its two clocks.</summary>
    internal const int OreChunkBytes = OreChunkMessage.RecordBytes + 8 + 8;

    /// <summary>One hull's change of one part's group: hull, part and group.</summary>
    internal const int ActionGroupBytes = 4 + 1 + 1;

    /// <summary>The mining section's counts: chunks, the next chunk id and group changes.</summary>
    internal const int MiningHeaderBytes = 4 + 4 + 4;

    private static long MiningBytes(WorldSnapshot snapshot) =>
        MiningHeaderBytes + (long)snapshot.OreChunkCount * OreChunkBytes + (long)snapshot.ActionGroupCount * ActionGroupBytes;

    private static void WriteMining(ref NetWriter writer, WorldSnapshot snapshot)
    {
        writer.WriteInt32(snapshot.OreChunkCount);
        writer.WriteUInt32(snapshot.NextOreChunkId);
        writer.WriteInt32(snapshot.ActionGroupCount);
        foreach (ref readonly var chunk in snapshot.OreChunks.AsSpan(0, snapshot.OreChunkCount))
        {
            OreChunkMessage.WriteRecord(ref writer, chunk);
            writer.WriteInt64(chunk.Born);
            writer.WriteInt64(chunk.Age);
        }

        foreach (ref readonly var group in snapshot.ActionGroups.AsSpan(0, snapshot.ActionGroupCount))
        {
            writer.WriteInt32(group.Hull);
            writer.WriteByte(group.Part);
            writer.WriteByte(group.Group);
        }
    }

    // Structure only: counts within limits, chunks physically sound with identities below the counter and clocks
    // within the world's, groups naming a saved hull once per part. Whether a chunk's resource is one of this
    // galaxy's and a part is groupable is the server's to judge when it restores the world.
    private static bool ReadMining(ref NetReader reader, WorldSnapshot result, long nextTick, out string error)
    {
        var chunks = reader.ReadInt32();
        var next = reader.ReadUInt32();
        var groups = reader.ReadInt32();
        if (!reader.IsValid || (uint)chunks > OreChunks.Capacity || (uint)groups > (uint)result.ActionGroups.Length
            || (long)chunks * OreChunkBytes + (long)groups * ActionGroupBytes > reader.Remaining)
        {
            error = "the save's ore chunks or action groups are more than a world may hold";
            return false;
        }

        for (var i = 0; i < chunks; i++)
        {
            var c = OreChunkMessage.ReadRecord(ref reader);
            c.Born = reader.ReadInt64();
            c.Age = reader.ReadInt64();
            if (!reader.IsValid || c.Id == 0 || next != 0 && c.Id >= next || c.Born < 0 || c.Born > nextTick || c.Age < 0 || c.Age > nextTick - c.Born
                || !double.IsFinite(c.Position.X) || !double.IsFinite(c.Position.Y) || !double.IsFinite(c.Position.Z)
                || !float.IsFinite(c.Velocity.LengthSquared()) || !float.IsFinite(c.AngularVelocity.LengthSquared())
                || !float.IsFinite(c.Orientation.LengthSquared()) || Math.Abs(c.Orientation.LengthSquared() - 1) >= .001f
                || c.Resource >= Galaxy.MaxResources || !(c.Grade > 0 && c.Grade <= 1) || !(c.Mass > 0 && c.Mass <= 1e7f))
            {
                error = "invalid saved ore chunk";
                return false;
            }

            for (var j = 0; j < i; j++)
                if (result.OreChunks[j].Id == c.Id)
                {
                    error = "duplicate ore chunk identity";
                    return false;
                }
            result.OreChunks[i] = c;
        }

        for (var i = 0; i < groups; i++)
        {
            var group = new SavedActionGroup(reader.ReadInt32(), reader.ReadByte(), reader.ReadByte());
            if (!reader.IsValid || (uint)group.Hull >= (uint)result.HullCount || group.Part >= ActionGroupsMessage.MaxParts || !ActionGroups.Valid(group.Group))
            {
                error = "invalid saved action group";
                return false;
            }

            for (var j = 0; j < i; j++)
                if (result.ActionGroups[j].Hull == group.Hull && result.ActionGroups[j].Part == group.Part)
                {
                    error = "an action group saved twice";
                    return false;
                }
            result.ActionGroups[i] = group;
        }

        result.OreChunkCount = chunks;
        result.NextOreChunkId = next;
        result.ActionGroupCount = groups;
        error = "";
        return true;
    }
}
