using System.Buffers.Binary;
using System.Numerics;

namespace Prospect.Game.World.RockEditing;

/// <summary>
/// One scrape of a rock: the rock removed inside a capsule between two points, in the rock's own frame. Every figure
/// is a whole number (positions in <see cref="Units"/> of a metre, the radius in millimetres), fixed when the server
/// makes the edit, so the server and every client apply exactly the same values and arrive at the same bricks.
/// </summary>
public struct RockScrape : IEquatable<RockScrape>
{
    /// <summary>Position units in a metre: a position is a whole number of 1/64 m.</summary>
    public const int Units = 64;

    /// <summary>The largest coordinate a position can hold, in units: 24 bits signed, about 131 km.</summary>
    public const int MaxCoordinate = 8_388_607;

    /// <summary>Start of the stroke, in units.</summary>
    public int Ax, Ay, Az;

    /// <summary>End of the stroke, in units.</summary>
    public int Bx, By, Bz;

    /// <summary>Radius of the cut in millimetres; never zero in a valid scrape.</summary>
    public ushort RadiusMm;

    /// <summary>The scrape nearest to a stroke between two points in metres with a radius in metres.</summary>
    public static RockScrape From(Vector3 a, Vector3 b, float radius) => new()
    {
        Ax = Fixed(a.X),
        Ay = Fixed(a.Y),
        Az = Fixed(a.Z),
        Bx = Fixed(b.X),
        By = Fixed(b.Y),
        Bz = Fixed(b.Z),
        RadiusMm = (ushort)Math.Clamp(Math.Floor(radius * 1000.0 + 0.5), 1, ushort.MaxValue),
    };

    /// <summary>Start of the stroke in metres.</summary>
    public readonly Vector3 A => new Vector3(Ax, Ay, Az) / Units;

    /// <summary>End of the stroke in metres.</summary>
    public readonly Vector3 B => new Vector3(Bx, By, Bz) / Units;

    /// <summary>Radius of the cut in metres.</summary>
    public readonly float Radius => RadiusMm / 1000f;

    /// <summary>Whether this stroke starts exactly where another ended.</summary>
    public readonly bool StartsAtEndOf(in RockScrape previous) => Ax == previous.Bx && Ay == previous.By && Az == previous.Bz;

    /// <inheritdoc />
    public readonly bool Equals(RockScrape other) =>
        Ax == other.Ax && Ay == other.Ay && Az == other.Az && Bx == other.Bx && By == other.By && Bz == other.Bz && RadiusMm == other.RadiusMm;

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is RockScrape other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(Ax, Ay, Az, Bx, By, Bz, RadiusMm);

    private static int Fixed(float metres) => (int)Math.Clamp(Math.Floor(metres * (double)Units + 0.5), -MaxCoordinate, MaxCoordinate);
}

/// <summary>
/// Wire and save form of a rock's edit list. A scrape that starts anywhere is <see cref="ScrapeBytes"/>; one that
/// starts where the edit before it ended sends only its end point as three 16-bit steps, <see cref="ScrapeOnBytes"/>.
/// Kind 3 is kept for cracks. Reading never throws: bytes that are not an edit read as zero bytes taken.
/// </summary>
public static class RockEditCodec
{
    /// <summary>Bytes of a scrape that starts anywhere: kind, two points of three 24-bit numbers, radius.</summary>
    public const int ScrapeBytes = 1 + 9 + 9 + 2;

    /// <summary>Bytes of a scrape that continues the edit before it: kind, three 16-bit steps, radius.</summary>
    public const int ScrapeOnBytes = 1 + 6 + 2;

    private const byte Scrape = 1;
    private const byte ScrapeOn = 2;

    /// <summary>
    /// Writes one edit and returns the bytes written. With <paramref name="chain"/> the short form is used when the
    /// edit starts where <paramref name="previous"/> ended and its end is within 16 bits of that point.
    /// </summary>
    public static int Write(in RockScrape edit, in RockScrape previous, bool chain, Span<byte> output)
    {
        if (chain && edit.StartsAtEndOf(previous))
        {
            var dx = edit.Bx - previous.Bx;
            var dy = edit.By - previous.By;
            var dz = edit.Bz - previous.Bz;
            if (dx is >= short.MinValue and <= short.MaxValue && dy is >= short.MinValue and <= short.MaxValue && dz is >= short.MinValue and <= short.MaxValue)
            {
                output[0] = ScrapeOn;
                BinaryPrimitives.WriteInt16LittleEndian(output[1..], (short)dx);
                BinaryPrimitives.WriteInt16LittleEndian(output[3..], (short)dy);
                BinaryPrimitives.WriteInt16LittleEndian(output[5..], (short)dz);
                BinaryPrimitives.WriteUInt16LittleEndian(output[7..], edit.RadiusMm);
                return ScrapeOnBytes;
            }
        }

        output[0] = Scrape;
        WriteInt24(output[1..], edit.Ax);
        WriteInt24(output[4..], edit.Ay);
        WriteInt24(output[7..], edit.Az);
        WriteInt24(output[10..], edit.Bx);
        WriteInt24(output[13..], edit.By);
        WriteInt24(output[16..], edit.Bz);
        BinaryPrimitives.WriteUInt16LittleEndian(output[19..], edit.RadiusMm);
        return ScrapeBytes;
    }

    /// <summary>The bytes <see cref="Write"/> would take, without writing.</summary>
    public static int Size(in RockScrape edit, in RockScrape previous, bool chain)
    {
        if (!chain || !edit.StartsAtEndOf(previous)) return ScrapeBytes;
        var dx = edit.Bx - previous.Bx;
        var dy = edit.By - previous.By;
        var dz = edit.Bz - previous.Bz;
        return dx is >= short.MinValue and <= short.MaxValue && dy is >= short.MinValue and <= short.MaxValue && dz is >= short.MinValue and <= short.MaxValue
            ? ScrapeOnBytes
            : ScrapeBytes;
    }

    /// <summary>
    /// Reads one edit and returns the bytes taken, or zero for bytes that are not an edit: an unknown kind, too few
    /// bytes, a zero radius, a continuation with nothing before it (<paramref name="chain"/> false) or one that
    /// leaves the 24-bit range.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> input, in RockScrape previous, bool chain, out RockScrape edit)
    {
        edit = default;
        if (input.IsEmpty) return 0;
        switch (input[0])
        {
            case ScrapeOn when chain && input.Length >= ScrapeOnBytes:
                edit.Ax = previous.Bx;
                edit.Ay = previous.By;
                edit.Az = previous.Bz;
                edit.Bx = previous.Bx + BinaryPrimitives.ReadInt16LittleEndian(input[1..]);
                edit.By = previous.By + BinaryPrimitives.ReadInt16LittleEndian(input[3..]);
                edit.Bz = previous.Bz + BinaryPrimitives.ReadInt16LittleEndian(input[5..]);
                edit.RadiusMm = BinaryPrimitives.ReadUInt16LittleEndian(input[7..]);
                if (edit.RadiusMm == 0 || Math.Abs(edit.Bx) > RockScrape.MaxCoordinate || Math.Abs(edit.By) > RockScrape.MaxCoordinate || Math.Abs(edit.Bz) > RockScrape.MaxCoordinate)
                    return 0;
                return ScrapeOnBytes;
            case Scrape when input.Length >= ScrapeBytes:
                edit.Ax = ReadInt24(input[1..]);
                edit.Ay = ReadInt24(input[4..]);
                edit.Az = ReadInt24(input[7..]);
                edit.Bx = ReadInt24(input[10..]);
                edit.By = ReadInt24(input[13..]);
                edit.Bz = ReadInt24(input[16..]);
                edit.RadiusMm = BinaryPrimitives.ReadUInt16LittleEndian(input[19..]);
                // -8,388,608 fits 24 bits but no scrape is made there: keep the range symmetric.
                if (edit.RadiusMm == 0 || edit.Ax < -RockScrape.MaxCoordinate || edit.Ay < -RockScrape.MaxCoordinate || edit.Az < -RockScrape.MaxCoordinate
                    || edit.Bx < -RockScrape.MaxCoordinate || edit.By < -RockScrape.MaxCoordinate || edit.Bz < -RockScrape.MaxCoordinate)
                    return 0;
                return ScrapeBytes;
            default:
                return 0;
        }
    }

    private static void WriteInt24(Span<byte> output, int value)
    {
        output[0] = (byte)value;
        output[1] = (byte)(value >> 8);
        output[2] = (byte)(value >> 16);
    }

    private static int ReadInt24(ReadOnlySpan<byte> input) => (input[0] | input[1] << 8 | input[2] << 16) << 8 >> 8;
}
