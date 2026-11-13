using System.Numerics;
using System.Security.Cryptography;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// The ship editor's side of the server. A player asks to edit the hull they fly; the server allows it once the
/// hull has gone long enough without damage, and ends the edit the moment the hull is damaged, left, destroyed or
/// sent into a jump. A committed design arrives in ordered chunks into one of a fixed number of buffers, is
/// checked off the tick, and is added to the catalogue, which only grows. The hull is replaced by a new hull of
/// the new design when its owner's client can show it. A repair is the same replacement by the design the hull
/// has: no bytes, no check, no new number. Everything here runs on the tick thread except the check.
/// </summary>
public sealed partial class ServerHost
{
    /// <summary>
    /// One design held while it arrives and is checked. The buffer is taken when the server starts, so receiving
    /// a design takes no memory, and a server holds as many as its rules give it buffers.
    /// </summary>
    private sealed class EditUpload(int bytes)
    {
        public readonly byte[] Buffer = new byte[bytes];
        public readonly byte[] Hash = new byte[SHA256.HashSizeInBytes];
        public int Total;
        public int Received;

        /// <summary>True from the commit that took the buffer until nothing reads it any more.</summary>
        public bool Held;

        /// <summary>The check, which reads the buffer until it completes.</summary>
        public Task<EditChecked?>? Worker;

        /// <summary>True when the edit ended while the check ran: the buffer is free once the check completes.</summary>
        public bool Orphaned;
    }

    /// <summary>A design that passed the check, with the bytes and hash the catalogue keeps. Nothing writes to them again.</summary>
    private sealed record EditChecked(HullDesign Design, byte[] Bytes, byte[] Hash);

    private EditUpload[] _uploads = [];
    private readonly CancellationTokenSource _editCancel = new();
    private uint _nextEditToken;
    private bool _editStopped;

    /// <summary>How many designs the catalogue had when the server started: what a client's own files can hold.</summary>
    private int _startupDesigns;

    private void ReceiveEditor(Connection connection, ref NetReader reader)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        if (!EditorRequestMessage.TryRead(ref reader, hash, out var request))
            return;

        switch (request.Kind)
        {
            case EditorRequestKind.Begin:
                BeginEdit(connection, request.Hull);
                break;
            case EditorRequestKind.Cancel:
                if (connection.EditPhase != EditorPhase.Closed && request.Token == connection.EditToken)
                    EndEdit(connection, EditorReason.Cancelled);
                else
                    SendEditorState(connection);
                break;
            case EditorRequestKind.Repair:
                RepairEdit(connection, request.Token);
                break;
            default:
                CommitEdit(connection, request, hash);
                break;
        }
    }

    /// <summary>
    /// Rebuilds the edited hull as the design it has: the blueprint, which no loss changes. It goes the way of a
    /// committed design the catalogue already holds, so the same things end or refuse it: a stale token, damage
    /// since the edit began, a jump, leaving the hull, and the time between a player's commits. A hull that has
    /// lost no block and no hull points is left alone; a shield that is down is not the hull's damage. Repair
    /// is free for now, as building is.
    /// </summary>
    private void RepairEdit(Connection connection, uint token)
    {
        if (connection.EditPhase != EditorPhase.Open || token != connection.EditToken)
        {
            SendEditorState(connection);
            return;
        }

        var invalid = EditInvalid(connection);
        if (invalid != EditorReason.None)
        {
            EndEdit(connection, invalid);
            return;
        }

        var design = _hulls.Info(connection.EditHull.Index).Design;
        if (!NeedsRepair(connection))
            ReplyEdit(connection, EditorReason.NoChange);
        else if (_tick < connection.EditNextCommit)
            ReplyEdit(connection, EditorReason.TooSoon, WaitSeconds(connection.EditNextCommit - _tick));
        else
        {
            connection.EditNextCommit = _tick + (long)_content.Editor.CommitSeconds * TickLoop.SimulationHz;
            Publish(connection, design);
        }
    }

    /// <summary>
    /// Allows a player to edit the hull they fly, or says why not. Other players nearby do not stand in the way
    /// and the hull is not protected while it is edited: damage ends the edit.
    /// </summary>
    private void BeginEdit(Connection connection, uint hull)
    {
        // A design that is on its way or being shown must be cancelled first; asking again says where it stands.
        if (connection.EditPhase is EditorPhase.Receiving or EditorPhase.Checking or EditorPhase.Publishing)
        {
            SendEditorState(connection);
            return;
        }

        connection.EditPhase = EditorPhase.Closed;
        if (!_hulls.IsAlive(connection.Hull) || connection.Hull.Packed != hull)
        {
            RefuseEdit(connection, hull, EditorReason.NotPiloting, 0);
            return;
        }

        ref var info = ref _hulls.Info(connection.Hull.Index);
        if (_hulls.State(connection.Hull.Index).Jump != JumpPhase.None || info.Order.Kind == OrderKind.Jump || (connection.Order.Kind == OrderKind.Jump && connection.OrderId != info.OrderId))
        {
            RefuseEdit(connection, hull, EditorReason.Jumping, 0);
            return;
        }

        var left = _content.Editor.QuietTicksLeft(_tick, info.DamageTick);
        if (left > 0)
        {
            RefuseEdit(connection, hull, EditorReason.Quiet, WaitSeconds(left));
            return;
        }

        // Zero never names an edit, so a client can tell "no edit" from the first one.
        if (++_nextEditToken == 0)
            _nextEditToken = 1;
        connection.EditPhase = EditorPhase.Open;
        connection.EditToken = _nextEditToken;
        connection.EditHull = connection.Hull;
        connection.EditStartTick = _tick;
        ReplyEdit(connection, EditorReason.None);
    }

    /// <summary>
    /// Takes the announcement of a design. Everything that can refuse it without the bytes is checked here, before
    /// a buffer is taken: the edit, damage to repair first, the size, the catalogue's room, and whether the catalogue
    /// has the design already.
    /// </summary>
    private void CommitEdit(Connection connection, in EditorRequestMessage request, ReadOnlySpan<byte> hash)
    {
        if (connection.EditPhase != EditorPhase.Open || request.Token != connection.EditToken)
        {
            SendEditorState(connection);
            return;
        }

        var invalid = EditInvalid(connection);
        if (invalid != EditorReason.None)
        {
            EndEdit(connection, invalid);
            return;
        }

        // A damaged hull is repaired before it is rebuilt as anything else, whatever design is named: before a
        // buffer, a check or the catalogue is touched. Damage after the edit began has ended it above.
        if (NeedsRepair(connection))
        {
            ReplyEdit(connection, EditorReason.RepairFirst);
            return;
        }

        if (request.Total < 1 || request.Total > _content.BlockTransfers.MaxBytes)
        {
            ReplyEdit(connection, EditorReason.TooLarge);
            return;
        }

        var current = _hulls.Info(connection.EditHull.Index).Design;
        var known = _catalogue.Find(hash, request.Total);
        if (known == current)
        {
            // The same design again is no edit; the hull keeps what it has. A repair is asked for by name.
            ReplyEdit(connection, EditorReason.NoChange);
            return;
        }

        if (known >= 0)
        {
            // The catalogue has these bytes already: nothing to send, nothing to check, no new number.
            if (!SameKind(_content.Designs[known], _content.Designs[current]))
                ReplyEdit(connection, EditorReason.WrongKind);
            else if (_tick < connection.EditNextCommit)
                ReplyEdit(connection, EditorReason.TooSoon, WaitSeconds(connection.EditNextCommit - _tick));
            else
            {
                connection.EditNextCommit = _tick + (long)_content.Editor.CommitSeconds * TickLoop.SimulationHz;
                Publish(connection, (byte)known);
            }
            return;
        }

        if (!_catalogue.Allows(request.Total))
        {
            ReplyEdit(connection, EditorReason.CatalogueFull);
            return;
        }

        if (_tick < connection.EditNextCommit)
        {
            ReplyEdit(connection, EditorReason.TooSoon, WaitSeconds(connection.EditNextCommit - _tick));
            return;
        }

        EditUpload? free = null;
        foreach (var upload in _uploads)
        {
            if (upload.Held)
                continue;
            free = upload;
            break;
        }

        if (free is null)
        {
            ReplyEdit(connection, EditorReason.Busy);
            return;
        }

        free.Held = true;
        free.Total = request.Total;
        free.Received = 0;
        hash.CopyTo(free.Hash);
        connection.EditUpload = free;
        connection.EditPhase = EditorPhase.Receiving;
        connection.EditDeadline = _tick + (long)_content.Editor.UploadSeconds * TickLoop.SimulationHz;
        connection.EditNextCommit = _tick + (long)_content.Editor.CommitSeconds * TickLoop.SimulationHz;
        ReplyEdit(connection, EditorReason.None);
    }

    /// <summary>
    /// Takes the next bytes of a design. A chunk is copied only when it is for the edit being received and starts
    /// exactly where the last one ended; anything else ends the upload, and the player may commit again.
    /// </summary>
    private void ReceiveEditorData(Connection connection, ref NetReader reader)
    {
        var ok = EditorDataMessage.TryReadHeader(ref reader, out var token, out var offset, out var count);

        // Chunks of an edit that has moved on are late, not wrong.
        if (connection.EditPhase != EditorPhase.Receiving || (ok && token != connection.EditToken))
            return;

        var upload = connection.EditUpload!;
        if (!ok || offset != upload.Received || count > upload.Total - upload.Received)
        {
            FailUpload(connection, EditorReason.UploadFailed);
            return;
        }

        reader.ReadBytes(count).CopyTo(upload.Buffer.AsSpan(offset));
        upload.Received += count;
        if (upload.Received < upload.Total)
            return;

        // The whole design is here. Hashing, decoding and every geometry check run off the tick, on a copy of
        // nothing: the worker reads the buffer, which nobody else touches until its result has been taken.
        var buffer = upload.Buffer;
        var total = upload.Total;
        var expected = upload.Hash;
        var content = _content;
        var cancel = _editCancel.Token;
        upload.Worker = Task.Run(() =>
        {
            if (cancel.IsCancellationRequested)
                return null;
            try
            {
                var bytes = buffer.AsSpan(0, total);
                var design = DesignBytes.Decode(bytes, expected, content);
                return new EditChecked(design, bytes.ToArray(), expected.AsSpan().ToArray());
            }
            catch (Exception)
            {
                // These are a stranger's bytes. Whatever they make the decoder throw, the design is refused.
                return null;
            }
        });
        connection.EditPhase = EditorPhase.Checking;
        ReplyEdit(connection, EditorReason.None);
    }

    /// <summary>
    /// Once a tick, before the hulls step: announces new designs, ends edits that no longer hold, and moves each
    /// edit on as far as it can go. Takes no memory while nothing is being edited.
    /// </summary>
    private void TickEditor(long tick)
    {
        foreach (var upload in _uploads)
        {
            if (!upload.Orphaned || !upload.Worker!.IsCompleted)
                continue;
            upload.Worker = null;
            upload.Orphaned = false;
            upload.Held = false;
        }

        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady)
                continue;

            // Designs are announced in order, each once. A send the transport refuses is tried again next tick.
            while (connection.NoticedDesigns < _catalogue.Count)
            {
                var id = connection.NoticedDesigns;
                var writer = new NetWriter(_sendBuffer);
                DesignNoticeMessage.Write(ref writer, (byte)id, _catalogue.Bytes[id].Length, _catalogue.Hashes[id]);
                if (!connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written))
                    break;
                connection.NoticedDesigns++;
            }

            if (connection.EditReplyDirty)
                SendEditorState(connection);
            if (connection.EditPhase == EditorPhase.Closed)
                continue;

            var invalid = EditInvalid(connection);
            if (invalid != EditorReason.None)
            {
                EndEdit(connection, invalid);
                continue;
            }

            if (connection.EditPhase == EditorPhase.Receiving && tick > connection.EditDeadline)
                FailUpload(connection, EditorReason.UploadTimedOut);
            if (connection.EditPhase == EditorPhase.Checking && connection.EditUpload!.Worker!.IsCompleted)
                Decide(connection);
            if (connection.EditPhase == EditorPhase.Publishing)
                TryReplace(connection, tick);
        }
    }

    /// <summary>
    /// Why an edit can no longer go on, or none. Asked every tick an edit is open, again when a design is
    /// committed, after it has been checked and before the hull is replaced.
    /// </summary>
    private EditorReason EditInvalid(Connection connection)
    {
        if (!_hulls.IsAlive(connection.EditHull) || connection.Hull != connection.EditHull)
            return EditorReason.HullChanged;
        ref var info = ref _hulls.Info(connection.EditHull.Index);

        // Compared, never subtracted: a hull that was never damaged carries the lowest tick there is.
        if (info.DamageTick >= connection.EditStartTick)
            return EditorReason.Damaged;
        if (_hulls.State(connection.EditHull.Index).Jump != JumpPhase.None || info.Order.Kind == OrderKind.Jump || (connection.Order.Kind == OrderKind.Jump && connection.OrderId != info.OrderId))
            return EditorReason.Jumping;
        return EditorReason.None;
    }

    /// <summary>
    /// Whether the edited hull has lost a block or hull points, as <see cref="HullDesign.NeedsRepair"/> says. A
    /// shield that is down is not damage to the hull.
    /// </summary>
    private bool NeedsRepair(Connection connection)
    {
        var index = connection.EditHull.Index;
        return _content.Designs[_hulls.Info(index).Design].NeedsRepair(_hulls.Revision(index), _hulls.State(index).Hull, _hulls.Info(index).Visual.Count);
    }

    /// <summary>Takes the result of a check and, for a design that passed, gives it its place in the catalogue.</summary>
    private void Decide(Connection connection)
    {
        var upload = connection.EditUpload!;
        var result = upload.Worker!.IsCompletedSuccessfully ? upload.Worker.Result : null;
        ReleaseUpload(connection);
        if (result is null)
        {
            connection.EditPhase = EditorPhase.Open;
            ReplyEdit(connection, EditorReason.Invalid);
            return;
        }

        // Refused at the commit already; asked again so that no path to the catalogue skips it.
        if (NeedsRepair(connection))
        {
            connection.EditPhase = EditorPhase.Open;
            ReplyEdit(connection, EditorReason.RepairFirst);
            return;
        }

        // The design must be the edited hull's kind: its class and block size decide what it is and how large
        // the grid is in space, and its name and colour are shown to everyone.
        var current = _hulls.Info(connection.EditHull.Index).Design;
        if (!SameKind(result.Design, _content.Designs[current]))
        {
            connection.EditPhase = EditorPhase.Open;
            ReplyEdit(connection, EditorReason.WrongKind);
            return;
        }

        // Asked again: another player's design may have gone in while this one was on its way.
        var id = _catalogue.Find(result.Hash, result.Bytes.Length);
        if (id == current)
        {
            connection.EditPhase = EditorPhase.Open;
            ReplyEdit(connection, EditorReason.NoChange);
            return;
        }

        if (id < 0)
        {
            if (!_catalogue.Allows(result.Bytes.Length))
            {
                connection.EditPhase = EditorPhase.Open;
                ReplyEdit(connection, EditorReason.CatalogueFull);
                return;
            }

            // The catalogue only grows: the designs it had keep their numbers and stay the same objects, so
            // every hull, record and transfer that names one stays right.
            id = _catalogue.Append(result.Bytes, result.Hash);
            _content = _content.WithDesign(result.Design);
            _field.SetDesigns(_content.Designs);
            Logs.Sim.Info($"{connection.Name} (player {connection.PlayerId}) added design {id}: {result.Design.BlockCount} blocks, {result.Bytes.Length} bytes");
        }

        Publish(connection, (byte)id);
    }

    /// <summary>The design has a number; the hull is replaced once the clients that must show it can.</summary>
    private void Publish(Connection connection, byte design)
    {
        connection.EditPhase = EditorPhase.Publishing;
        connection.EditDesign = design;
        connection.EditOwnerReady = false;
        connection.EditDeadline = _tick + (long)_content.Editor.ReadySeconds * TickLoop.SimulationHz;
        ReplyEdit(connection, EditorReason.None);
    }

    /// <summary>
    /// Replaces the hull when its owner's client is ready for the design, and the other players' clients are or
    /// have had their time. Nobody is held up meanwhile: every hull keeps flying, the edited one included.
    /// </summary>
    private void TryReplace(Connection connection, long tick)
    {
        var design = connection.EditDesign;
        if (BlockSet.Has(connection.FailedDesigns, design))
        {
            EndEdit(connection, EditorReason.ClientFailed);
            return;
        }

        if (!BlockSet.Has(connection.ReadyDesigns, design))
        {
            if (tick > connection.EditDeadline)
                EndEdit(connection, EditorReason.ClientNotReady);
            return;
        }

        if (!connection.EditOwnerReady)
        {
            connection.EditOwnerReady = true;
            connection.EditDeadline = tick + (long)_content.Editor.PeerWaitSeconds * TickLoop.SimulationHz;
        }

        // A client that is not ready is sent no hull of the design, so for it the ship would be gone until it
        // is. The others get a moment to catch up; one that never does holds nobody up for longer.
        if (tick < connection.EditDeadline)
        {
            foreach (var other in _connections)
            {
                if (other.State == ConnectionState.Active && other.CatalogueReady
                    && !BlockSet.Has(other.ReadyDesigns, design) && !BlockSet.Has(other.FailedDesigns, design))
                    return;
            }
        }

        ReplaceHull(connection, design);
    }

    /// <summary>
    /// Puts a new hull of the committed design where the edited one is and removes the old one. The new hull has
    /// a new id: a hull never changes design, and a repaired hull is a new hull of the same one. It is still
    /// the same ship, so it keeps the old hull's durable name (<see cref="HullInfo.Entity"/>). It keeps the
    /// owner, the pilot, the heading and the motion, and the grid stays where it was in space, so a block that
    /// was not touched does not move. Its blocks are new and whole, as building is free; its shield and battery
    /// keep the charge they had, up to what the new design holds. The old hull is removed only after the new
    /// one exists.
    /// </summary>
    private void ReplaceHull(Connection connection, byte design)
    {
        if (_hulls.Count >= HullStore.MaxHulls)
        {
            EndEdit(connection, EditorReason.HullLimit);
            return;
        }

        var old = connection.EditHull;

        // Copies: creating a hull can move the storage.
        var oldInfo = _hulls.Info(old.Index);
        var oldState = _hulls.State(old.Index);
        var from = _content.Designs[oldInfo.Design];
        var to = _content.Designs[design];

        // A saved world takes a larger design only while its save has room for the difference.
        if (to.BlockCount > from.BlockCount && !HasRoom(0, to.BlockCount - from.BlockCount))
        {
            EndEdit(connection, EditorReason.HullLimit);
            return;
        }

        // A hull's position is its design's centre. The two designs share a grid, so the new centre is the old
        // one moved by the difference of the centres, in the hull's own frame.
        var moved = (to.Pivot - from.Pivot) * to.BlockSize;
        var position = oldState.Position + Vector3.Transform(new Vector3((float)moved.X, (float)moved.Y, (float)moved.Z), oldState.Orientation);
        var next = CreateHull(oldInfo.Owner, design, position, oldInfo.Entity);

        ref var state = ref _hulls.State(next.Index);
        ref var flight = ref _hulls.Flight(next.Index);
        state.Orientation = oldState.Orientation;
        state.Velocity = oldState.Velocity;
        state.AngularVelocity = oldState.AngularVelocity;
        state.Heat = oldState.Heat;
        state.BoostLocked = oldState.BoostLocked;
        state.Energy = Math.Min(oldState.Energy, flight.EnergyStorage);
        state.Shield = Math.Min(oldState.Shield, flight.ShieldCapacity);
        state.ShieldCooldown = oldState.ShieldCooldown;
        state.ShieldOffline = oldState.ShieldOffline && flight.ShieldCapacity > 0f;

        ref var info = ref _hulls.Info(next.Index);
        info.Piloted = true;
        info.OrderId = connection.OrderId;
        info.DamageTick = oldInfo.DamageTick;
        info.EscapeDrone = oldInfo.EscapeDrone;
        _hulls.Destroy(old);

        // Inputs that were queued were for the old hull.
        SetHull(connection, next);
        ResetControl(connection);

        connection.EditPhase = EditorPhase.Closed;
        connection.EditReply = new EditorStateMessage
        {
            Phase = EditorPhase.Applied,
            Token = connection.EditToken,
            Hull = next.Packed,
            ReplacedHull = old.Packed,
        };
        SendEditorState(connection);
        Logs.Sim.Info($"{connection.Name} (player {connection.PlayerId}) rebuilt hull {old.Packed} as {next.Packed}, design {design}");
    }

    /// <summary>Whether a design may stand in for a hull's: the same name, class, block size and colour.</summary>
    private static bool SameKind(HullDesign design, HullDesign current) =>
        design.Class == current.Class && design.BlockSize == current.BlockSize && design.Color == current.Color && design.Name == current.Name;

    private static ushort WaitSeconds(long ticks) => (ushort)Math.Clamp((ticks + TickLoop.SimulationHz - 1) / TickLoop.SimulationHz, 1, ushort.MaxValue);

    /// <summary>The upload ends, the edit stays open: the player may commit again.</summary>
    private void FailUpload(Connection connection, EditorReason reason)
    {
        ReleaseUpload(connection);
        connection.EditPhase = EditorPhase.Open;
        ReplyEdit(connection, reason);
    }

    /// <summary>Ends an edit and tells the client why.</summary>
    private void EndEdit(Connection connection, EditorReason reason)
    {
        ReleaseUpload(connection);
        connection.EditPhase = EditorPhase.Closed;
        ReplyEdit(connection, reason);
    }

    /// <summary>Ends an edit without a word, for a player who is leaving.</summary>
    private void CloseEdit(Connection connection)
    {
        ReleaseUpload(connection);
        connection.EditPhase = EditorPhase.Closed;
    }

    /// <summary>
    /// Lets go of the buffer an edit holds. One a check is still reading stays taken until the check completes,
    /// which <see cref="TickEditor"/> sees: the server never holds more designs than it has buffers.
    /// </summary>
    private static void ReleaseUpload(Connection connection)
    {
        var upload = connection.EditUpload;
        if (upload is null)
            return;
        connection.EditUpload = null;
        if (upload.Worker is { IsCompleted: false })
        {
            upload.Orphaned = true;
            return;
        }

        upload.Worker = null;
        upload.Held = false;
    }

    /// <summary>Answers a request to begin with a refusal; the client has no edit.</summary>
    private void RefuseEdit(Connection connection, uint hull, EditorReason reason, ushort waitSeconds)
    {
        connection.EditReply = new EditorStateMessage { Phase = EditorPhase.Closed, Reason = reason, Hull = hull, WaitSeconds = waitSeconds };
        SendEditorState(connection);
    }

    /// <summary>Tells the client where its edit stands now, with the reason for what just happened.</summary>
    private void ReplyEdit(Connection connection, EditorReason reason, ushort waitSeconds = 0)
    {
        connection.EditReply = new EditorStateMessage
        {
            Phase = connection.EditPhase,
            Reason = reason,
            Token = connection.EditToken,
            Hull = connection.EditHull.Packed,
            WaitSeconds = waitSeconds,
        };
        SendEditorState(connection);
    }

    /// <summary>Sends the newest answer. One the transport refuses is kept and sent by <see cref="TickEditor"/>.</summary>
    private void SendEditorState(Connection connection)
    {
        var writer = new NetWriter(_sendBuffer);
        connection.EditReply.Write(ref writer);
        connection.EditReplyDirty = !connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
    }

    /// <summary>A client says it can show an announced design, or that it cannot.</summary>
    private static void ReceiveDesignReady(Connection connection, ref NetReader reader)
    {
        if (!DesignReadyMessage.TryRead(ref reader, out var design, out var ready) || design < connection.ManifestDesigns || design >= connection.NoticedDesigns)
            return;
        if (ready)
        {
            BlockSet.Add(connection.ReadyDesigns, design);
            BlockSet.Remove(connection.FailedDesigns, design);
        }
        else if (!BlockSet.Has(connection.ReadyDesigns, design))
        {
            BlockSet.Add(connection.FailedDesigns, design);
        }
    }

    /// <summary>Stops the checks. Each is finite and bounded by the design limits, so this waits for them.</summary>
    private void StopEditor()
    {
        if (_editStopped)
            return;
        _editStopped = true;
        _editCancel.Cancel();
        foreach (var upload in _uploads)
        {
            try { upload.Worker?.Wait(); }
            catch (AggregateException) { }
            upload.Worker = null;
            upload.Held = upload.Orphaned = false;
        }

        _editCancel.Dispose();
    }
}
