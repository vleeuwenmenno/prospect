using System.Security.Cryptography;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>
/// Receives the designs a server adds to its catalogue during play. The server announces each one in order with
/// its size and hash; this fetches the canonical bytes unless the client has them already, checks and decodes
/// them on one worker, and hands the design to the session, which adds it at the end of its catalogue. One
/// design at a time, in one buffer taken when the session is let in. A design that cannot be taken stops the
/// rest as well: designs are numbered by their place, so none can be skipped.
/// </summary>
internal sealed class DesignAppend : IDisposable
{
    private readonly BlockTransferRules _rules;

    // The session's content when it was let in. Only its blocks, rules and limits are read, which designs
    // added later do not change.
    private readonly GameContent _content;
    private readonly DesignCatalogue _cache;
    private readonly byte[] _buffer;
    private readonly int[] _sizes = new int[byte.MaxValue];
    private readonly byte[] _hashes = new byte[byte.MaxValue * SHA256.HashSizeInBytes];
    private readonly CancellationTokenSource _cancel = new();
    private Task<HullDesign?>? _worker;
    private long _catalogueBytes, _nextRequest, _progressDeadline;
    private int _offset;

    /// <param name="content">The session's content, for the rules a design's figures are worked out under.</param>
    /// <param name="cache">The designs of this machine's own files, which need not be fetched.</param>
    /// <param name="admitted">How many designs the catalogue had when the client was let in.</param>
    /// <param name="catalogueBytes">The canonical bytes of those designs together, which count against the catalogue's budget.</param>
    internal DesignAppend(GameContent content, DesignCatalogue cache, int admitted, long catalogueBytes)
    {
        _content = content;
        _rules = content.BlockTransfers;
        _cache = cache;
        _buffer = new byte[_rules.MaxBytes];
        Announced = admitted;
        _catalogueBytes = catalogueBytes;
    }

    /// <summary>One past the last design the server has announced.</summary>
    internal int Announced { get; private set; }

    /// <summary>True once a design could not be taken; that design and every later one stay unknown to this client.</summary>
    internal bool Failed { get; private set; }

    /// <summary>
    /// Takes an announcement. It must be for the next number and within the budgets the client was let in
    /// under; anything else is a server this client cannot follow, and no further design is taken.
    /// </summary>
    internal void OnNotice(ref NetReader reader)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        if (Failed || !DesignNoticeMessage.TryRead(ref reader, out var design, out var size, hash))
            return;
        if (design != Announced || Announced >= byte.MaxValue || size < 1 || size > _rules.MaxBytes || !_rules.AllowsCatalogue(_catalogueBytes + size))
        {
            Failed = true;
            return;
        }

        _sizes[design] = size;
        hash.CopyTo(_hashes.AsSpan(design * SHA256.HashSizeInBytes));
        _catalogueBytes += size;
        Announced++;
    }

    /// <summary>
    /// Takes a chunk of the design being fetched, which is the one after the last the session has. A chunk at
    /// offset zero starts over; any other must continue exactly where the last one ended, or it is left unread.
    /// </summary>
    internal void OnData(ref NetReader reader, int next, long tick)
    {
        var id = reader.ReadByte(); var total = reader.ReadInt32(); var offset = reader.ReadInt32();
        var count = reader.ReadUInt16(); var bytes = reader.ReadBytes(count);
        if (Failed || _worker is not null || !reader.IsValid || reader.Remaining != 0 || id != next || next >= Announced
            || total != _sizes[next] || count == 0 || offset < 0 || count > total - offset)
            return;
        if (offset == 0)
            _offset = 0;
        else if (offset != _offset)
            return;
        bytes.CopyTo(_buffer.AsSpan(offset));
        _offset += count;
        _progressDeadline = tick + _rules.AdmissionTicks;
        if (_offset == total)
            Decode(_buffer, next);
    }

    /// <summary>
    /// Moves the fetch on. Returns a design when the next one has been checked and decoded: the session adds it at
    /// number <paramref name="next"/>, which is how many designs it has.
    /// </summary>
    internal HullDesign? Tick(int next, EditorClient editor, ITransport transport, Span<byte> buffer, long tick)
    {
        if (Failed || next >= Announced)
            return null;
        if (_worker is not null)
        {
            if (!_worker.IsCompleted)
                return null;
            var decoded = _worker.IsCompletedSuccessfully ? _worker.Result : null;
            _worker = null;
            _offset = 0;
            Failed = decoded is null;
            return decoded;
        }

        // The sender releases expired transfers. Restart a stalled partial download so it can be queued again.
        if (_offset != 0 && tick > _progressDeadline)
            _offset = 0;

        // Bytes the client already has are not fetched: the design it committed itself, or one in its own files.
        // They go through the same check as any other.
        if (_offset == 0)
        {
            var hash = _hashes.AsSpan(next * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes);
            var local = editor.Committed(hash, _sizes[next]);
            if (local is null && _cache.Find(hash, _sizes[next]) is >= 0 and var cached)
                local = _cache.Bytes[cached];
            if (local is not null)
            {
                Decode(local, next);
                return null;
            }
        }

        // Requests remain rate limited, including after a partial transfer times out.
        if (_offset != 0 || tick < _nextRequest)
            return null;
        var writer = new NetWriter(buffer);
        writer.WriteByte((byte)MessageId.DesignRequest); writer.WriteByte((byte)next);
        transport.Send(0, NetChannel.Unreliable, writer.Written);
        _nextRequest = tick + _rules.RequestTicks;
        return null;
    }

    // The worker alone reads the bytes until its result has been taken: no chunk is accepted meanwhile.
    private void Decode(byte[] bytes, int design)
    {
        var length = _sizes[design];
        var hash = _hashes.AsSpan(design * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes).ToArray();
        var content = _content;
        var token = _cancel.Token;
        _worker = Task.Run(() =>
        {
            if (token.IsCancellationRequested) return null;
            try { return DesignBytes.Decode(bytes.AsSpan(0, length), hash, content); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException or OverflowException)
            { return null; }
        });
    }

    public void Dispose()
    {
        _cancel.Cancel();
        // Decode is finite and bounded by live geometry limits; join it before releasing its buffer.
        if (_worker is not null) { try { _worker.GetAwaiter().GetResult(); } catch (Exception) { } }
        _worker = null; _cancel.Dispose();
    }
}
