using Prospect.Engine.Net;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Game.Persistence;

public static partial class WorldSnapshotCodec
{
    /// <summary>An edited asteroid's record without its edits: sector, index, how many edits follow.</summary>
    internal const int EditedRockBytes = 12 + 4 + 4;

    // The edits of every asteroid, each asteroid's as one chain: the same bytes the wire sends.
    private static long RockEditBytes(WorldSnapshot snapshot)
    {
        long size = (long)snapshot.EditedRockCount * EditedRockBytes;
        var at = 0;
        for (var rock = 0; rock < snapshot.EditedRockCount; rock++)
        {
            var count = snapshot.EditedRockEdits[rock];
            if (count < 1 || count > snapshot.RockEditCount - at)
                throw new InvalidDataException("The snapshot's rock edits do not add up.");
            for (var i = 0; i < count; i++, at++)
                size += RockEditCodec.Size(snapshot.RockEdits[at], i > 0 ? snapshot.RockEdits[at - 1] : default, i > 0);
        }

        if (at != snapshot.RockEditCount)
            throw new InvalidDataException("The snapshot's rock edits do not add up.");
        return size;
    }

    private static void WriteRockEdits(ref NetWriter writer, WorldSnapshot snapshot)
    {
        Span<byte> bytes = stackalloc byte[RockEditCodec.ScrapeBytes];
        var at = 0;
        for (var rock = 0; rock < snapshot.EditedRockCount; rock++)
        {
            Write(ref writer, snapshot.EditedRocks[rock].Sector);
            writer.WriteInt32(snapshot.EditedRocks[rock].Index);
            var count = snapshot.EditedRockEdits[rock];
            writer.WriteInt32(count);
            for (var i = 0; i < count; i++, at++)
            {
                var size = RockEditCodec.Write(snapshot.RockEdits[at], i > 0 ? snapshot.RockEdits[at - 1] : default, i > 0, bytes);
                writer.WriteBytes(bytes[..size]);
            }
        }
    }

    // Structure only: every asteroid named once, every count met, every edit one the codec reads. Whether an edit
    // is one the rules allow on that asteroid of this galaxy is the server's to judge when it restores the world.
    private static bool ReadRockEdits(ref NetReader reader, WorldSnapshot result, int rocks, int edits)
    {
        Span<byte> bytes = stackalloc byte[RockEditCodec.ScrapeBytes];
        var at = 0;
        for (var rock = 0; rock < rocks; rock++)
        {
            var sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var name = new RockRef(sector, reader.ReadInt32());
            var count = reader.ReadInt32();
            if (!reader.IsValid || name.Index < 0 || count < 1 || count > edits - at) return false;
            for (var earlier = 0; earlier < rock; earlier++)
                if (result.EditedRocks[earlier] == name) return false;
            result.EditedRocks[rock] = name;
            result.EditedRockEdits[rock] = count;
            for (var i = 0; i < count; i++, at++)
            {
                // The kind says how many bytes follow it.
                bytes[0] = reader.ReadByte();
                var rest = bytes[0] == 2 ? RockEditCodec.ScrapeOnBytes - 1 : RockEditCodec.ScrapeBytes - 1;
                var body = reader.ReadBytes(rest);
                if (!reader.IsValid || body.Length != rest) return false;
                body.CopyTo(bytes[1..]);
                if (RockEditCodec.Read(bytes[..(rest + 1)], i > 0 ? result.RockEdits[at - 1] : default, i > 0, out result.RockEdits[at]) == 0)
                    return false;
            }
        }

        result.EditedRockCount = rocks;
        result.RockEditCount = at;
        return at == edits;
    }
}
