using System.Numerics;
using System.Runtime.CompilerServices;
using Prospect.Engine.Net;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Game.Protocol;

/// <summary>
/// Server to client, reliable: a run of one rock's edits, in the order they were made. <c>First</c> is the place of
/// the run's first edit in the rock's list, so a client that already holds some of them takes only the rest and one
/// that misses earlier ones takes none. The first edit of a message is always in its full form: a message reads
/// without any other.
/// </summary>
public static class RockEditsMessage
{
    /// <summary>Most edits in one message.</summary>
    public const int MaxEdits = 64;

    /// <summary>Id, sector, rock index, place of the first edit, count, and every edit in its full form.</summary>
    public const int MaxWireSize = 1 + 12 + 2 + 2 + 1 + MaxEdits * RockEditCodec.ScrapeBytes;

    /// <summary>Writes a run of at most <see cref="MaxEdits"/> edits.</summary>
    public static void Write(ref NetWriter writer, RockRef rock, int first, scoped ReadOnlySpan<RockScrape> edits)
    {
        writer.WriteByte((byte)MessageId.RockEdits);
        writer.WriteInt32(rock.Sector.X);
        writer.WriteInt32(rock.Sector.Y);
        writer.WriteInt32(rock.Sector.Z);
        writer.WriteUInt16((ushort)rock.Index);
        writer.WriteUInt16((ushort)first);
        writer.WriteByte((byte)edits.Length);
        Span<byte> bytes = stackalloc byte[RockEditCodec.ScrapeBytes];
        for (var i = 0; i < edits.Length; i++)
        {
            var size = RockEditCodec.Write(edits[i], i > 0 ? edits[i - 1] : default, i > 0, bytes);
            writer.WriteBytes(bytes[..size]);
        }
    }

    /// <summary>
    /// Reads a run. False for anything that is not one: no edits or too many, bytes that are no edit, bytes left
    /// over. Whether the edits are ones the rules allow on that rock is the caller's to check.
    /// </summary>
    public static bool TryRead(ref NetReader reader, out RockRef rock, out int first, scoped Span<RockScrape> edits, out int count)
    {
        var sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        rock = new RockRef(sector, reader.ReadUInt16());
        first = reader.ReadUInt16();
        count = reader.ReadByte();
        if (!reader.IsValid || count is < 1 or > MaxEdits || count > edits.Length)
            return false;
        var bytes = reader.ReadBytes(reader.Remaining);
        for (var i = 0; i < count; i++)
        {
            var size = RockEditCodec.Read(bytes, i > 0 ? edits[i - 1] : default, i > 0, out edits[i]);
            if (size == 0) return false;
            bytes = bytes[size..];
        }

        return reader.IsValid && bytes.IsEmpty;
    }
}

/// <summary>
/// Client to server, reliable: the temporary debug scrape. Rays in the rock's own frame, in metres, along which the
/// server looks for the rock's surface; it scrapes from each ray's hit to the next one's, or a pit for a single ray.
/// The server decides where the surface is and whether anything is scraped at all.
/// </summary>
public struct RockScrapeRequest
{
    /// <summary>Most rays in one request, so at most one fewer scrapes.</summary>
    public const int MaxRays = 17;

    /// <summary>Id, sector, rock index, radius, ray count, and an origin and a direction a ray.</summary>
    public const int MaxWireSize = 1 + 12 + 2 + 2 + 1 + MaxRays * 24;

    /// <summary>The rock to scrape.</summary>
    public RockRef Rock;

    /// <summary>Radius of the cut in millimetres.</summary>
    public ushort RadiusMm;

    /// <summary>How many rays follow.</summary>
    public int Rays;

    /// <summary>Where each ray starts.</summary>
    public RayPoints Origins;

    /// <summary>Which way each ray points; need not be unit length.</summary>
    public RayPoints Directions;

    /// <summary>Room for the rays of one request.</summary>
    [InlineArray(MaxRays)]
    public struct RayPoints
    {
        private Vector3 _first;
    }

    /// <summary>Writes the request.</summary>
    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)MessageId.RockScrape);
        writer.WriteInt32(Rock.Sector.X);
        writer.WriteInt32(Rock.Sector.Y);
        writer.WriteInt32(Rock.Sector.Z);
        writer.WriteUInt16((ushort)Rock.Index);
        writer.WriteUInt16(RadiusMm);
        writer.WriteByte((byte)Rays);
        for (var i = 0; i < Rays; i++)
        {
            WorldWire.WriteVector(ref writer, Origins[i]);
            WorldWire.WriteVector(ref writer, Directions[i]);
        }
    }

    /// <summary>Reads a request. False for a malformed one: a ray count out of range, a figure that is not a number, bytes left over.</summary>
    public static bool TryRead(ref NetReader reader, out RockScrapeRequest request)
    {
        request = default;
        var sector = new SectorCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        request.Rock = new RockRef(sector, reader.ReadUInt16());
        request.RadiusMm = reader.ReadUInt16();
        request.Rays = reader.ReadByte();
        if (!reader.IsValid || request.Rays is < 1 or > MaxRays || reader.Remaining != request.Rays * 24)
            return false;
        for (var i = 0; i < request.Rays; i++)
        {
            if (!WorldWire.TryReadVector(ref reader, out var origin) || !WorldWire.TryReadVector(ref reader, out var direction))
                return false;
            request.Origins[i] = origin;
            request.Directions[i] = direction;
        }

        return reader.IsValid && reader.Remaining == 0;
    }
}
