using System.Buffers.Binary;
using System.Text;

namespace Prospect.Engine.Net;

/// <summary>Writes a network message into a caller-provided span. Little-endian, byte-aligned.</summary>
public ref struct NetWriter(Span<byte> buffer)
{
    private readonly Span<byte> _buffer = buffer;
    private int _position;

    public readonly int Length => _position;
    public readonly ReadOnlySpan<byte> Written => _buffer[.._position];

    public void WriteByte(byte value) => _buffer[_position++] = value;

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_position..], value);
        _position += 2;
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_position..], value);
        _position += 4;
    }

    public void WriteInt32(int value) => WriteUInt32((uint)value);

    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer[_position..], value);
        _position += 8;
    }

    public void WriteInt64(long value) => WriteUInt64((ulong)value);

    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>Writes an unsigned integer in 1 to 5 bytes, smaller values taking fewer bytes.</summary>
    public void WriteVarUInt(uint value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        WriteByte((byte)value);
    }

    public void WriteBytes(scoped ReadOnlySpan<byte> value)
    {
        value.CopyTo(_buffer[_position..]);
        _position += value.Length;
    }

    /// <summary>Writes a length-prefixed UTF-8 string.</summary>
    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt((uint)byteCount);
        Encoding.UTF8.GetBytes(value, _buffer[_position..]);
        _position += byteCount;
    }
}

/// <summary>
/// Reads a network message. Input comes from other machines and is never trusted: reading past the end does not
/// throw, it returns zeroes and clears <see cref="IsValid"/>, which the caller checks once after reading.
/// </summary>
public ref struct NetReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position;
    private bool _overrun;

    /// <summary>False once any read has run past the end of the message.</summary>
    public readonly bool IsValid => !_overrun;
    public readonly int Remaining => _buffer.Length - _position;

    public byte ReadByte() => Take(1, out var span) ? span[0] : (byte)0;

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUInt16() => Take(2, out var span) ? BinaryPrimitives.ReadUInt16LittleEndian(span) : (ushort)0;

    public uint ReadUInt32() => Take(4, out var span) ? BinaryPrimitives.ReadUInt32LittleEndian(span) : 0;

    public int ReadInt32() => (int)ReadUInt32();

    public ulong ReadUInt64() => Take(8, out var span) ? BinaryPrimitives.ReadUInt64LittleEndian(span) : 0;

    public long ReadInt64() => (long)ReadUInt64();

    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());

    public uint ReadVarUInt()
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = ReadByte();
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }

        _overrun = true;
        return 0;
    }

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count, out var span) ? span : default;

    /// <summary>
    /// Reads a length-prefixed UTF-8 string of at most <paramref name="maxBytes"/> bytes into
    /// <paramref name="destination"/> and returns the number of characters written.
    /// </summary>
    public int ReadString(scoped Span<char> destination, int maxBytes)
    {
        var byteCount = ReadVarUInt();
        if (byteCount > maxBytes || !Take((int)byteCount, out var span))
        {
            _overrun = true;
            return 0;
        }

        if (Encoding.UTF8.GetMaxCharCount(span.Length) > destination.Length && Encoding.UTF8.GetCharCount(span) > destination.Length)
        {
            _overrun = true;
            return 0;
        }

        return Encoding.UTF8.GetChars(span, destination);
    }

    private bool Take(int count, out ReadOnlySpan<byte> span)
    {
        if (count < 0 || count > Remaining)
        {
            _overrun = true;
            _position = _buffer.Length;
            span = default;
            return false;
        }

        span = _buffer.Slice(_position, count);
        _position += count;
        return true;
    }
}
