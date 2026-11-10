namespace Prospect.Engine.Threading;

/// <summary>
/// A lock-free triple buffer that hands the latest value from one writer thread to one reader thread.
///
/// The simulation thread fills the write buffer and publishes it; the render thread acquires the most recent
/// published buffer whenever it wants. Neither side ever waits for the other and no buffer is shared while in use,
/// which is how the render thread reads world state without owning any of it.
/// </summary>
public sealed class SnapshotExchange<T>
    where T : class
{
    private const int IndexMask = 0b011;
    private const int FreshBit = 0b100;

    private readonly T[] _buffers;
    private int _write;
    private int _read = 1;

    // Index of the buffer waiting between the two threads, plus a bit saying whether it is newer than the read buffer.
    private int _middle = 2;

    public SnapshotExchange(Func<T> factory)
    {
        _buffers = [factory(), factory(), factory()];
    }

    /// <summary>The buffer the writer may fill. Only valid on the writer thread, until the next publish.</summary>
    public T WriteBuffer => _buffers[_write];

    /// <summary>Makes the write buffer the latest snapshot and returns a different buffer to fill next.</summary>
    public T Publish()
    {
        _write = Interlocked.Exchange(ref _middle, _write | FreshBit) & IndexMask;
        return _buffers[_write];
    }

    /// <summary>
    /// Returns the latest published snapshot. The result stays valid on the reader thread until the next call.
    /// <paramref name="isNew"/> is false when nothing has been published since the previous call.
    /// </summary>
    public T Acquire(out bool isNew)
    {
        isNew = (Volatile.Read(ref _middle) & FreshBit) != 0;
        if (isNew)
            _read = Interlocked.Exchange(ref _middle, _read) & IndexMask;
        return _buffers[_read];
    }
}
