using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Prospect.Engine.Threading;

/// <summary>
/// A bounded single-producer single-consumer queue of byte messages in unmanaged memory. One thread writes, one
/// thread reads, neither blocks. Each message carries a one-byte tag chosen by the producer.
/// </summary>
public sealed unsafe class MessageRing : IDisposable
{
    private const int HeaderSize = 4;

    private readonly byte* _buffer;
    private readonly int _capacity;
    private readonly int _mask;

    // Monotonic byte positions; the producer owns the tail and the consumer owns the head.
    private long _head;
    private long _tail;

    public MessageRing(int capacityBytes)
    {
        if (capacityBytes < 1024 || (capacityBytes & (capacityBytes - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two of at least 1024 bytes", nameof(capacityBytes));
        _capacity = capacityBytes;
        _mask = capacityBytes - 1;
        _buffer = (byte*)NativeMemory.Alloc((nuint)capacityBytes);
    }

    public int MaxMessageSize => _capacity / 4;

    /// <summary>Appends a message. Returns false when there is not enough room; nothing is written in that case.</summary>
    public bool TryWrite(byte tag, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxMessageSize)
            throw new ArgumentException("Message exceeds the ring's maximum message size", nameof(payload));

        var tail = _tail;
        var used = tail - Volatile.Read(ref _head);
        if (used + HeaderSize + payload.Length > _capacity)
            return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length << 8 | tag);
        CopyIn(tail, header);
        CopyIn(tail + HeaderSize, payload);
        Volatile.Write(ref _tail, tail + HeaderSize + payload.Length);
        return true;
    }

    /// <summary>
    /// Removes the next message into <paramref name="destination"/>. Returns false when the ring is empty.
    /// </summary>
    public bool TryRead(Span<byte> destination, out byte tag, out int length)
    {
        var head = _head;
        if (head == Volatile.Read(ref _tail))
        {
            tag = 0;
            length = 0;
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        CopyOut(head, header);
        var word = BinaryPrimitives.ReadUInt32LittleEndian(header);
        tag = (byte)word;
        length = (int)(word >> 8);
        if (length > destination.Length)
            throw new ArgumentException("Destination is smaller than the queued message", nameof(destination));

        CopyOut(head + HeaderSize, destination[..length]);
        Volatile.Write(ref _head, head + HeaderSize + length);
        return true;
    }

    public void Dispose() => NativeMemory.Free(_buffer);

    private void CopyIn(long position, ReadOnlySpan<byte> source)
    {
        var offset = (int)(position & _mask);
        var first = Math.Min(source.Length, _capacity - offset);
        source[..first].CopyTo(new Span<byte>(_buffer + offset, first));
        source[first..].CopyTo(new Span<byte>(_buffer, source.Length - first));
    }

    private void CopyOut(long position, Span<byte> destination)
    {
        var offset = (int)(position & _mask);
        var first = Math.Min(destination.Length, _capacity - offset);
        new ReadOnlySpan<byte>(_buffer + offset, first).CopyTo(destination);
        new ReadOnlySpan<byte>(_buffer, destination.Length - first).CopyTo(destination[first..]);
    }
}
