using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>A hull as the render thread should draw it this tick, with last tick's pose for interpolation.</summary>
public struct HullView
{
    public uint Id;
    public uint Owner;

    /// <summary>Index of the hull's design.</summary>
    public byte Design;

    /// <summary>One bit per block of the design; a cleared bit is a destroyed block.</summary>
    public ulong Blocks;

    /// <summary>True for the hull the player is flying.</summary>
    public bool IsOwn;
    public Double3 Position;
    public Double3 PreviousPosition;
    public Quaternion Orientation;
    public Quaternion PreviousOrientation;
    public Vector3 Velocity;
    public float Heat;
    public bool BoostLocked;
    public HullOrder Order;

    /// <summary>Where the hull is in a faster-than-light jump, and how long it has been in that stage.</summary>
    public JumpPhase Jump;
    public float JumpTimer;

    /// <summary>Stored energy in megajoules, and how fast it is changing in megawatts.</summary>
    public float Energy;
    public float EnergyRate;
    public float Shield;
    public float Hull;
}

/// <summary>
/// The client's picture of the world. The player's own hull is predicted: inputs are applied immediately with the
/// same flight code the server runs, and each server snapshot corrects the result by replaying the inputs the
/// server has not seen yet. Other hulls are drawn slightly in the past, interpolated between snapshots.
/// </summary>
internal sealed class ClientWorld
{
    public const int MaxViews = 64;
    private const int PendingCapacity = 256;
    private const int SamplesPerHull = 8;
    private const int RemoteExpiryTicks = 120;

    /// <summary>How far in the past remote hulls are drawn, in ticks; covers the gap between their updates.</summary>
    private const double InterpolationDelayTicks = 6;
    private const float MaxExtrapolationSeconds = 0.25f;
    private const float JumpGap = 3000f;

    /// <summary>A correction larger than this is a teleport, not an error to smooth.</summary>
    private const float SnapDistance = 60f;

    /// <summary>How hard the hull turns toward where the player looks: turn rate in rad/s per radian of error.</summary>
    private const float AlignGain = 6f;

    private struct Pending
    {
        public InputRecord Record;

        /// <summary>The order in force when this tick was predicted.</summary>
        public HullOrder Order;
    }

    private struct Sample
    {
        public long Tick;
        public HullState State;
        public HullOrder Order;
    }

    private struct Remote
    {
        public bool Used;
        public uint Id;
        public uint Owner;
        public byte Design;
        public ulong Blocks;
        public long LastSeenTick;
        public int Newest;
        public int Count;
    }

    private readonly GameContent _content;
    private FlightParams _flight;
    private byte _ownDesign;
    private ulong _ownBlocks;
    private uint _playerId;
    private readonly Pending[] _pending = new Pending[PendingCapacity];
    private readonly Remote[] _remotes = new Remote[MaxViews];
    private readonly Sample[] _samples = new Sample[MaxViews * SamplesPerHull];
    private readonly HullView[] _lastViews = new HullView[MaxViews];
    private readonly byte[] _sendBuffer = new byte[256];
    private readonly byte[] _fleetBuffer = new byte[2 + FleetOrdersMessage.MaxOrders * 29];
    private int _lastViewCount;

    private uint _ownHullId;
    private bool _hasOwnState;
    private HullState _predicted;
    private Vector3 _correction;
    private uint _nextSequence = 1;
    private uint _ackedSequence;
    private HullOrder _order;
    private HullOrder _orderDefinition;
    private ushort _orderId;
    private bool _dampeners = true;
    private RockField? _rocks;
    private double _serverTick;
    private float _energyRate;

    public ClientWorld(GameContent content)
    {
        _content = content;
        _flight = content.Pod.Flight;
        _ownDesign = (byte)content.PodDesign;
    }

    public void SetPlayer(uint playerId) => _playerId = playerId;

    private HullDesign OwnDesign => _content.Designs[_ownDesign];

    public bool Dampeners => _dampeners;

    /// <summary>
    /// How far the server's answer has recently been from what the client predicted, in metres: the largest
    /// recent correction, fading over about a second. Zero when prediction and authority agree.
    /// </summary>
    public float PredictionError { get; private set; }

    /// <summary>The flight figures of the hull the player is flying.</summary>
    public FlightParams Flight => _flight;

    public void SetOwnHull(uint id) => _ownHullId = id;

    /// <summary>Gives the world the server's galaxy, whose asteroids the prediction collides with.</summary>
    public void SetGalaxy(Galaxy galaxy) => _rocks = new RockField(galaxy);

    /// <summary>Predicts one tick of the player's hull and sends the input to the server.</summary>
    public void Tick(PlayerInputBridge bridge, ITransport transport, double serverTick)
    {
        _serverTick = serverTick;
        var controls = bridge.Consume(out var hasOrder, out var order);
        _correction *= 0.86f;

        Span<FleetOrder> fleetOrders = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        var fleetOrderCount = bridge.ConsumeFleetOrders(fleetOrders);
        if (bridge.ConsumeControlRequest(out var controlHull))
        {
            var writer = new NetWriter(_fleetBuffer);
            writer.WriteByte((byte)MessageId.Control);
            writer.WriteUInt32(controlHull);
            transport.Send(0, NetChannel.Reliable, writer.Written);
        }

        if (fleetOrderCount > 0)
        {
            var writer = new NetWriter(_fleetBuffer);
            FleetOrdersMessage.Write(ref writer, fleetOrders[..fleetOrderCount]);
            transport.Send(0, NetChannel.Reliable, writer.Written);
        }

        if (!_hasOwnState)
            return;

        if (controls.ToggleDampeners)
            _dampeners = !_dampeners;

        if (hasOrder)
            SetOrder(order);

        // Turn the nose toward where the player is looking, if they asked for that.
        var pitch = 0f;
        var yaw = 0f;
        if (controls.Align && controls.Aim.LengthSquared() > 0.5f)
        {
            var local = Vector3.Transform(controls.Aim, Quaternion.Conjugate(_predicted.Orientation));
            var yawError = MathF.Atan2(local.X, -local.Z);
            var pitchError = MathF.Atan2(local.Y, MathF.Sqrt(local.X * local.X + local.Z * local.Z));
            yaw = Math.Clamp(yawError * AlignGain / _flight.TurnRate.Y, -1f, 1f);
            pitch = Math.Clamp(pitchError * AlignGain / _flight.TurnRate.X, -1f, 1f);
        }

        var input = new FlightInput
        {
            Strafe = FlightInput.Quantize(controls.Strafe),
            Vertical = FlightInput.Quantize(controls.Vertical),
            Forward = FlightInput.Quantize(controls.Forward),
            Pitch = FlightInput.Quantize(pitch),
            Yaw = FlightInput.Quantize(yaw),
            Roll = FlightInput.Quantize(controls.Roll),
            Boost = controls.Boost,
            Dampeners = _dampeners,
        };

        // Taking the controls ends a standing order.
        if (_order.Kind != OrderKind.None && (input.Strafe != 0 || input.Vertical != 0 || input.Forward != 0 || input.Roll != 0))
            SetOrder(default);

        ref var slot = ref _pending[_nextSequence % PendingCapacity];
        slot.Record = new InputRecord
        {
            Sequence = _nextSequence,
            Input = input,
            OrderId = _orderId,
        };
        slot.Order = _order;
        _nextSequence++;

        var energyBefore = _predicted.Energy;
        var body = new HullBody { Design = OwnDesign, Alive = _ownBlocks };
        HullSim.Step(ref _predicted, ref _order, input, _flight, ref body, _rocks, _serverTick);
        _energyRate += ((_predicted.Energy - energyBefore) / HullSim.Dt - _energyRate) * 0.2f;
        Send(transport);
    }

    /// <summary>Applies a world snapshot from the server.</summary>
    public void OnSnapshot(ref NetReader reader, long clientTick)
    {
        if (!SnapshotHeader.TryRead(ref reader, out var header))
            return;

        for (var i = 0; i < header.HullCount; i++)
        {
            if (!HullRecord.TryRead(ref reader, out var record))
                return;

            if (record.Id == header.OwnHull)
            {
                if (record.Id != _ownHullId || !_hasOwnState)
                    SwitchHull(record);
                if (record.Blocks != _ownBlocks)
                {
                    // The hull has lost blocks: it flies on what is left.
                    _ownBlocks = record.Blocks;
                    OwnDesign.Evaluate(_ownBlocks, out _flight, out _);
                }

                Reconcile(record.State, header.AckedInput);
            }
            else
            {
                StoreRemote(record, header.ServerTick, clientTick);
            }
        }
    }

    /// <summary>Applies asteroid motion records from the server.</summary>
    public void OnRockDeltas(ref NetReader reader)
    {
        Span<RockDelta> deltas = stackalloc RockDelta[RockDeltasMessage.MaxDeltas];
        if (_rocks is null || !RockDeltasMessage.TryRead(ref reader, deltas, out var count))
            return;
        for (var i = 0; i < count; i++)
            _rocks.Set(deltas[i]);
    }

    /// <summary>Copies the known asteroid motion records for the render thread. Returns how many were written.</summary>
    public int CopyRockDeltas(RockDelta[] destination)
    {
        if (_rocks is null)
            return 0;
        var deltas = _rocks.Deltas;
        var count = Math.Min(deltas.Length, destination.Length);
        deltas[..count].CopyTo(destination);
        return count;
    }

    /// <summary>Writes this tick's hull views. Returns how many were written and which one is the player's.</summary>
    public int BuildViews(HullView[] views, double serverTick, long clientTick, out int ownIndex)
    {
        var count = 0;
        ownIndex = -1;

        if (_hasOwnState)
        {
            ownIndex = count;
            views[count++] = new HullView
            {
                Id = _ownHullId,
                Owner = _playerId,
                Design = _ownDesign,
                Blocks = _ownBlocks,
                IsOwn = true,
                Position = _predicted.Position + _correction,
                Orientation = _predicted.Orientation,
                Velocity = _predicted.Velocity,
                Heat = _predicted.Heat,
                BoostLocked = _predicted.BoostLocked,
                Order = _order,
                Jump = _predicted.Jump,
                JumpTimer = _predicted.JumpTimer,
                Energy = _predicted.Energy,
                EnergyRate = _energyRate,
                Shield = _predicted.Shield,
                Hull = _predicted.Hull,
            };
        }

        var renderTick = serverTick - InterpolationDelayTicks;
        for (var r = 0; r < _remotes.Length && count < views.Length; r++)
        {
            ref var remote = ref _remotes[r];
            if (!remote.Used)
                continue;
            if (clientTick - remote.LastSeenTick > RemoteExpiryTicks)
            {
                remote.Used = false;
                continue;
            }

            views[count++] = Interpolate(r, ref remote, renderTick);
        }

        // Pair each view with where it was last tick so the render thread can interpolate between the two.
        for (var i = 0; i < count; i++)
        {
            ref var view = ref views[i];
            view.PreviousPosition = view.Position;
            view.PreviousOrientation = view.Orientation;
            for (var j = 0; j < _lastViewCount; j++)
            {
                if (_lastViews[j].Id != view.Id)
                    continue;
                view.PreviousPosition = _lastViews[j].Position;
                view.PreviousOrientation = _lastViews[j].Orientation;
                break;
            }
        }

        Array.Copy(views, _lastViews, count);
        _lastViewCount = count;
        return count;
    }

    /// <summary>
    /// The server has put the player in a different hull: start predicting that one. Inputs already sent were
    /// for the old hull, so they are not replayed onto the new one.
    /// </summary>
    private void SwitchHull(in HullRecord record)
    {
        _ownHullId = record.Id;
        _ownDesign = (byte)Math.Min(record.Design, _content.Designs.Length - 1);
        _ownBlocks = record.Blocks;
        OwnDesign.Evaluate(_ownBlocks, out _flight, out _);
        _hasOwnState = false;
        _correction = Vector3.Zero;
        _ackedSequence = _nextSequence - 1;
        SetOrder(default);

        // The hull was being drawn as someone else's until now.
        for (var i = 0; i < _remotes.Length; i++)
        {
            if (_remotes[i].Used && _remotes[i].Id == record.Id)
                _remotes[i].Used = false;
        }
    }

    private void SetOrder(in HullOrder order)
    {
        _order = order;
        _orderDefinition = order;
        _orderId++;
    }

    private void Send(ITransport transport)
    {
        Span<InputRecord> records = stackalloc InputRecord[InputMessage.MaxRecords];
        var available = (int)Math.Min(_nextSequence - 1 - _ackedSequence, InputMessage.MaxRecords);
        var count = Math.Max(available, 1);
        for (var i = 0; i < count; i++)
            records[i] = _pending[(_nextSequence - (uint)(count - i)) % PendingCapacity].Record;

        var writer = new NetWriter(_sendBuffer);
        InputMessage.Write(ref writer, records[..count], _orderId, _orderDefinition);
        transport.Send(0, NetChannel.Unreliable, writer.Written);
    }

    private void Reconcile(in HullState authoritative, uint acked)
    {
        if (!_hasOwnState)
        {
            _hasOwnState = true;
            _predicted = authoritative;
            _ackedSequence = Math.Max(acked, _ackedSequence);
            return;
        }

        // Snapshots can arrive out of order; only a newer acknowledgement carries newer truth.
        if (acked <= _ackedSequence && acked != 0)
            return;
        _ackedSequence = acked;

        // Start from what the server computed and re-apply every input it has not simulated yet.
        var state = authoritative;
        var first = Math.Max(acked + 1, _nextSequence > PendingCapacity ? _nextSequence - PendingCapacity + 1 : 1);
        for (var sequence = first; sequence < _nextSequence; sequence++)
        {
            ref var slot = ref _pending[sequence % PendingCapacity];
            var order = slot.Order;
            var body = new HullBody { Design = OwnDesign, Alive = _ownBlocks };
            HullSim.Step(ref state, ref order, slot.Record.Input, _flight, ref body, _rocks, _serverTick);
        }

        // Keep the drawn position continuous: whatever the correction moved is bled off over the next ticks.
        var error = _predicted.Position.RelativeTo(state.Position);
        PredictionError = Math.Max(PredictionError * 0.98f, error.Length());
        _correction = error.Length() > SnapDistance ? Vector3.Zero : _correction + error;
        _predicted = state;
    }

    private void StoreRemote(in HullRecord record, long serverTick, long clientTick)
    {
        var slot = -1;
        for (var i = 0; i < _remotes.Length; i++)
        {
            if (_remotes[i].Used && _remotes[i].Id == record.Id)
            {
                slot = i;
                break;
            }

            if (slot < 0 && !_remotes[i].Used)
                slot = i;
        }

        if (slot < 0)
            return;

        ref var remote = ref _remotes[slot];
        if (!remote.Used || remote.Id != record.Id)
            remote = new Remote { Used = true, Id = record.Id, Newest = -1 };

        remote.Owner = record.Owner;
        remote.Design = record.Design;
        remote.Blocks = record.Blocks;
        remote.LastSeenTick = clientTick;
        if (remote.Count > 0 && _samples[slot * SamplesPerHull + remote.Newest].Tick >= serverTick)
            return;

        remote.Newest = (remote.Newest + 1) % SamplesPerHull;
        remote.Count = Math.Min(remote.Count + 1, SamplesPerHull);
        _samples[slot * SamplesPerHull + remote.Newest] = new Sample
        {
            Tick = serverTick,
            State = record.State,
            Order = record.Order,
        };
    }

    private HullView Interpolate(int slot, ref Remote remote, double renderTick)
    {
        var samples = _samples.AsSpan(slot * SamplesPerHull, SamplesPerHull);
        ref var newest = ref samples[remote.Newest];
        var view = new HullView
        {
            Id = remote.Id,
            Owner = remote.Owner,
            Design = remote.Design,
            Blocks = remote.Blocks,
            Position = newest.State.Position,
            Orientation = newest.State.Orientation,
            Velocity = newest.State.Velocity,
            Heat = newest.State.Heat,
            BoostLocked = newest.State.BoostLocked,
            Order = newest.Order,
            Jump = newest.State.Jump,
            JumpTimer = newest.State.JumpTimer,
            Energy = newest.State.Energy,
            Shield = newest.State.Shield,
            Hull = newest.State.Hull,
        };

        if (renderTick >= newest.Tick)
        {
            // Ahead of the newest sample: coast on its velocity for a moment rather than freeze.
            var seconds = Math.Min((float)(renderTick - newest.Tick) * HullSim.Dt, MaxExtrapolationSeconds);
            view.Position = newest.State.Position + newest.State.Velocity * seconds;
            return view;
        }

        // Walk back from the newest sample to the pair that brackets the render time.
        var later = remote.Newest;
        for (var i = 1; i < remote.Count; i++)
        {
            var earlier = (remote.Newest - i + SamplesPerHull) % SamplesPerHull;
            ref var a = ref samples[earlier];
            ref var b = ref samples[later];
            if (renderTick >= a.Tick)
            {
                var t = (float)((renderTick - a.Tick) / Math.Max(1, b.Tick - a.Tick));

                // A jump moves a hull further in one step than it could ever fly: show it at one end or the
                // other, never somewhere along the line between.
                if (b.State.Position.RelativeTo(a.State.Position).LengthSquared() > JumpGap * JumpGap)
                    t = t < 0.5f ? 0f : 1f;
                view.Position = Double3.Lerp(a.State.Position, b.State.Position, t);
                view.Orientation = Quaternion.Slerp(a.State.Orientation, b.State.Orientation, t);
                view.Velocity = Vector3.Lerp(a.State.Velocity, b.State.Velocity, t);
                return view;
            }

            later = earlier;
        }

        // Older than everything buffered: show the oldest sample.
        ref var oldest = ref samples[later];
        view.Position = oldest.State.Position;
        view.Orientation = oldest.State.Orientation;
        return view;
    }
}
