using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>Admission-only receiver: one bounded buffer and one decode worker, never concurrent with block recovery.</summary>
internal sealed class DesignDownload : IDisposable
{
    private readonly GameContent _content;
    private readonly DesignCatalogue _cache;
    private readonly byte[] _buffer;
    private readonly CancellationTokenSource _cancel = new();
    private Task<HullDesign?>? _worker;
    private HullDesign[]? _designs;
    private byte[][]? _hashes;
    private int[]? _sizes, _fleet;
    private int _pod, _drone, _index, _offset, _total;
    private long _nextRequest;
    internal bool Failed { get; private set; }
    internal GameContent? Result { get; private set; }

    /// <summary>The canonical bytes of every design the manifest lists, together.</summary>
    internal long TotalBytes { get; private set; }

    internal DesignDownload(GameContent content, DesignCatalogue cache)
    {
        _content = content; _cache = cache;
        _buffer = new byte[content.BlockTransfers.MaxBytes];
    }

    internal void Receive(ref NetReader reader)
    {
        if (Failed) return;
        var id = reader.ReadByte(); var total = reader.ReadInt32(); var offset = reader.ReadInt32();
        var count = reader.ReadUInt16(); var bytes = reader.ReadBytes(count);
        var expected = _designs is null ? 255 : _index;
        if (!reader.IsValid || reader.Remaining != 0 || id != expected || _worker is not null || Result is not null
            || total <= 0 || total > _buffer.Length || count == 0 || offset != _offset || count > total - offset
            || (_offset != 0 && total != _total) || (_sizes is not null && total != _sizes[_index]))
        { Failed = true; return; }
        _total = total; bytes.CopyTo(_buffer.AsSpan(offset)); _offset += count;
        if (_offset != total) return;
        _offset = 0;
        if (_designs is null) { ReadManifest(_buffer.AsSpan(0, total)); return; }
        var length = total; var hash = _hashes![_index]; var token = _cancel.Token;
        // Only this worker owns the receive buffer until its result has been consumed.
        _worker = Task.Run(() =>
        {
            if (token.IsCancellationRequested) return null;
            try { return DesignBytes.Decode(_buffer.AsSpan(0, length), hash, _content); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException or OverflowException)
            { return null; }
        });
    }

    private void ReadManifest(ReadOnlySpan<byte> bytes)
    {
        var reader = new NetReader(bytes);
        var count = reader.ReadByte(); _pod = reader.ReadByte(); _drone = reader.ReadByte();
        var fleetCount = reader.ReadByte();
        if (count == 0 || _pod >= count || _drone >= count || reader.Remaining != fleetCount + count * 36)
        { Failed = true; return; }
        // Scan totals before allocating decoded catalogue arrays, including entries already cached.
        var scan = reader;
        scan.ReadBytes(fleetCount);
        long encoded = 0;
        for (var i = 0; i < count; i++)
        {
            var size = scan.ReadInt32(); scan.ReadBytes(32);
            encoded += size;
            if (size <= 0 || size > _buffer.Length || !_content.BlockTransfers.AllowsCatalogue(encoded))
            { Failed = true; return; }
        }
        TotalBytes = encoded;
        _fleet = new int[fleetCount];
        for (var i = 0; i < fleetCount; i++)
        {
            _fleet[i] = reader.ReadByte();
            if (_fleet[i] >= count) { Failed = true; return; }
        }
        _designs = new HullDesign[count]; _hashes = new byte[count][]; _sizes = new int[count];
        for (var i = 0; i < count; i++)
        {
            _sizes[i] = reader.ReadInt32(); _hashes[i] = reader.ReadBytes(32).ToArray();
            if (_sizes[i] <= 0 || _sizes[i] > _buffer.Length) { Failed = true; return; }
            for (var j = 0; j < i; j++)
                if (_hashes[i].AsSpan().SequenceEqual(_hashes[j]) && _sizes[i] != _sizes[j]) { Failed = true; return; }
            for (var j = 0; j < _cache.Hashes.Length; j++)
                if (_sizes[i] == _cache.Bytes[j].Length && _hashes[i].AsSpan().SequenceEqual(_cache.Hashes[j]))
                { _designs[i] = _content.Designs[j]; break; }
        }
    }

    internal void Tick(ITransport transport, Span<byte> buffer, long tick)
    {
        if (Failed || Result is not null) return;
        if (_designs is null) { Request(255, transport, buffer, tick); return; }
        if (_worker is not null)
        {
            if (!_worker.IsCompleted) return;
            if (!_worker.IsCompletedSuccessfully || _worker.Result is null) { Failed = true; return; }
            var decoded = _worker.Result;
            for (var i = _index; i < _designs.Length; i++)
                if (_hashes![i].AsSpan().SequenceEqual(_hashes[_index])) _designs[i] = decoded;
            _worker = null;
        }
        while (_index < _designs.Length && _designs[_index] is not null) _index++;
        if (_index == _designs.Length)
        {
            Result = _content.WithCatalogue(_designs, _pod, _drone, _fleet!);
            return;
        }
        Request((byte)_index, transport, buffer, tick);
    }

    private void Request(byte id, ITransport transport, Span<byte> buffer, long tick)
    {
        if (_offset != 0 || tick < _nextRequest) return;
        var writer = new NetWriter(buffer);
        writer.WriteByte((byte)MessageId.DesignRequest); writer.WriteByte(id);
        transport.Send(0, NetChannel.Unreliable, writer.Written);
        _nextRequest = tick + _content.BlockTransfers.RequestTicks;
    }

    public void Dispose()
    {
        _cancel.Cancel();
        // Decode is finite and bounded by live geometry limits; join it before releasing its buffer.
        if (_worker is not null) { try { _worker.GetAwaiter().GetResult(); } catch (Exception) { } }
        _worker = null; _cancel.Dispose();
    }
}
