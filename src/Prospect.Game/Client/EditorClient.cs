using System.Security.Cryptography;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;

namespace Prospect.Game.Client;

/// <summary>
/// The player's edit as the session publishes it each tick. The server decides everything in it; the session only
/// adds whether a request of its own is still waiting for an answer, and how much of a design it has sent.
/// </summary>
public readonly record struct EditorView
{
    /// <summary>Where the edit stands. <see cref="EditorPhase.Open"/> is when the draft may be committed.</summary>
    public EditorPhase Phase { get; init; }

    /// <summary>Why the last request was refused or the edit ended; none when all is well.</summary>
    public EditorReason Reason { get; init; }

    /// <summary>The edit's name, which a commit carries. Zero when there is none.</summary>
    public uint Token { get; init; }

    /// <summary>The hull being edited; with <see cref="EditorPhase.Applied"/> the new hull, which the player now flies.</summary>
    public uint Hull { get; init; }

    /// <summary>With <see cref="EditorPhase.Applied"/>: the hull that was replaced, to carry a selection or target over.</summary>
    public uint ReplacedHull { get; init; }

    /// <summary>With <see cref="EditorReason.Quiet"/> and <see cref="EditorReason.TooSoon"/>: seconds the server asks to wait.</summary>
    public int WaitSeconds { get; init; }

    /// <summary>Rises with every answer, so the same refusal twice is seen as two.</summary>
    public uint Serial { get; init; }

    /// <summary>True while a request has gone out, or is about to, and its answer has not arrived.</summary>
    public bool Pending { get; init; }

    /// <summary>While a design is being sent: the bytes sent so far, and the design's size.</summary>
    public int SentBytes { get; init; }
    public int TotalBytes { get; init; }
}

/// <summary>
/// The session's side of the editor: sends what the interface asked through <see cref="PlayerInputBridge"/>,
/// keeps the server's newest answer, and sends a committed design in ordered chunks once the server has said it
/// will take it. A repair is a request alone. Runs on the session thread.
/// </summary>
internal sealed class EditorClient
{
    private readonly byte[] _buffer = new byte[EditorDataMessage.HeaderBytes + EditorDataMessage.MaxChunk];
    private readonly byte[] _hash = new byte[SHA256.HashSizeInBytes];
    private EditorStateMessage _state;
    private uint _serial;
    internal NewShipView NewShip { get; private set; }
    private bool _pending, _wantCancel, _uploading;

    // The committed design. Never written to: the bridge copied it from the caller, and it is only read here.
    private byte[]? _bytes;
    private uint _commitToken;
    private int _offset;

    internal EditorView View => new()
    {
        Phase = _state.Phase,
        Reason = _state.Reason,
        Token = _state.Token,
        Hull = _state.Hull,
        ReplacedHull = _state.ReplacedHull,
        WaitSeconds = _state.WaitSeconds,
        Serial = _serial,
        Pending = _pending || _wantCancel,
        SentBytes = _uploading || _state.Phase is EditorPhase.Checking or EditorPhase.Publishing ? _offset : 0,
        TotalBytes = _bytes?.Length ?? 0,
    };

    /// <summary>
    /// The bytes of the design this client committed, if they are the ones a server's notice names: the design
    /// need not be fetched back from the server it was just sent to.
    /// </summary>
    internal byte[]? Committed(ReadOnlySpan<byte> hash, int size) =>
        _bytes is not null && _bytes.Length == size && hash.SequenceEqual(_hash) ? _bytes : null;

    /// <summary>Takes the server's answer.</summary>
    internal void OnState(ref NetReader reader)
    {
        if (!EditorStateMessage.TryRead(ref reader, out var state))
            return;
        _state = state;
        if (state.NewShip != NewShipStatus.None)
            NewShip = new(state.NewShip, state.Reason, state.NewShip == NewShipStatus.Created ? state.Hull : 0, NewShip.Serial + 1);
        _serial++;
        _pending = false;

        // The server takes chunks only after it has said so, for the edit it named; that is when sending starts.
        _uploading = state.Phase == EditorPhase.Receiving && state.Token == _commitToken && _bytes is not null;
        if (_uploading)
            _offset = 0;
        if (state.Phase is not (EditorPhase.Receiving or EditorPhase.Checking or EditorPhase.Publishing))
            _bytes = null;
        if (state.Phase is EditorPhase.Closed or EditorPhase.Applied)
            _wantCancel = false;
    }

    /// <summary>
    /// Sends at most one request and one chunk. <paramref name="send"/> is false in a catch-up tick that has no
    /// new server snapshot behind it, so a burst of ticks cannot send more than the server allows a player.
    /// </summary>
    internal void Tick(PlayerInputBridge bridge, ITransport transport, bool send, int maxBytes)
    {
        if (!send)
            return;

        if (bridge.ConsumeNewShip(out var name))
        {
            if (_pending || _state.Phase is not (EditorPhase.Closed or EditorPhase.Applied))
            { NewShip = new(NewShipStatus.Refused, EditorReason.Busy, 0, NewShip.Serial + 1); return; }
            Request(transport, new EditorRequestMessage { Kind = EditorRequestKind.NewShip, Name = name });
            NewShip = new(_pending ? NewShipStatus.Pending : NewShipStatus.Refused, _pending ? EditorReason.None : EditorReason.Unavailable, 0, NewShip.Serial + 1);
            return;
        }
        switch (bridge.ConsumeEdit(out var value, out var bytes))
        {
            case PlayerInputBridge.EditIntent.Begin:
                Request(transport, new EditorRequestMessage { Kind = EditorRequestKind.Begin, Hull = value });
                return;
            case PlayerInputBridge.EditIntent.Cancel:
                _wantCancel = true;
                break;
            case PlayerInputBridge.EditIntent.Commit:
                if (_state.Phase != EditorPhase.Open || value != _state.Token || _pending)
                {
                    Refuse(EditorReason.Unavailable);
                    return;
                }

                if (bytes!.Length > maxBytes)
                {
                    Refuse(EditorReason.TooLarge);
                    return;
                }

                SHA256.HashData(bytes, _hash);
                _bytes = bytes;
                _commitToken = value;
                _offset = 0;
                Request(transport, new EditorRequestMessage { Kind = EditorRequestKind.Commit, Token = value, Total = bytes.Length });
                return;
            case PlayerInputBridge.EditIntent.Repair:
                if (_state.Phase != EditorPhase.Open || value != _state.Token || _pending)
                {
                    Refuse(EditorReason.Unavailable);
                    return;
                }

                // No design goes out with a repair, so none is shown as sent.
                _bytes = null;
                _offset = 0;
                Request(transport, new EditorRequestMessage { Kind = EditorRequestKind.Repair, Token = value });
                return;
        }

        if (_wantCancel)
        {
            // A cancel names an edit. One asked for while the request to begin is still out waits for its answer.
            if (_pending && _state.Phase is EditorPhase.Closed or EditorPhase.Applied)
                return;
            _wantCancel = false;
            if (_state.Phase is not (EditorPhase.Closed or EditorPhase.Applied))
                Request(transport, new EditorRequestMessage { Kind = EditorRequestKind.Cancel, Token = _state.Token });
            return;
        }

        if (!_uploading)
            return;
        var count = Math.Min(EditorDataMessage.MaxChunk, _bytes!.Length - _offset);
        var writer = new NetWriter(_buffer);
        EditorDataMessage.Write(ref writer, _commitToken, _offset, _bytes.AsSpan(_offset, count));

        // A chunk the transport refuses is sent again next tick: the server takes them only in order.
        if (!transport.Send(0, NetChannel.Bulk, writer.Written))
            return;
        _offset += count;
        _uploading = _offset < _bytes.Length;
    }

    /// <summary>Forgets the edit, for a session that has ended.</summary>
    internal void Clear()
    {
        _state = default;
        NewShip = default;
        _bytes = null;
        _pending = _wantCancel = _uploading = false;
    }

    private void Request(ITransport transport, in EditorRequestMessage request)
    {
        var writer = new NetWriter(_buffer);
        request.Write(ref writer, _hash);
        if (transport.Send(0, NetChannel.Reliable, writer.Written))
            _pending = true;
        else
            Refuse(EditorReason.Unavailable);
    }

    /// <summary>An answer the session gives itself, for a request it could not send.</summary>
    private void Refuse(EditorReason reason)
    {
        _state.Reason = reason;
        _state.WaitSeconds = 0;
        _serial++;
    }
}
