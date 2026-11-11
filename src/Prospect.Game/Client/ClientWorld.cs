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

    /// <summary>True while the hull's boost is running.</summary>
    public bool Boosting;
    public HullOrder Order;

    /// <summary>Where the hull is in a faster-than-light jump, and how long it has been in that stage.</summary>
    public JumpPhase Jump;
    public float JumpTimer;

    /// <summary>Completed calculation seconds, saturated at the configured duration.</summary>
    public float JumpCalculation;

    /// <summary>Total transit seconds, latched at alignment and retained through arrival.</summary>
    public float JumpTunnelSeconds;

    /// <summary>
    /// Seconds in the transit so far, exactly as the simulation keeps them: a long transit's last seconds stay
    /// distinct in double where single precision would round them away.
    /// </summary>
    public double JumpTunnelElapsed;

    /// <summary>Stored energy in megajoules, and how fast it is changing in megawatts.</summary>
    public float Energy;
    public float EnergyRate;
    public float Shield;
    public float Hull;

    /// <summary>True while the shield is broken and charging back: it is not drawn and takes no hits.</summary>
    public bool ShieldOffline;

    /// <summary>The newest hit the shield absorbed, as the simulation stores it.</summary>
    public ShieldHit ShieldHit;

    /// <summary>
    /// Seconds from that hit to the moment this view shows. A remote hull is drawn in the past, so a hit the
    /// server has already reported can still be ahead of it, and this is negative until the drawn hull gets there.
    /// </summary>
    public float ShieldHitAge;

    /// <summary>
    /// Seconds from the shield's last change (break, restart, last emitter lost) to the moment this view shows;
    /// negative for a remote hull whose drawn pose has not reached it yet.
    /// </summary>
    public float ShieldChangeAge;
}

/// <summary>
/// The client's picture of the world. The player's own hull is predicted: inputs are applied immediately with the
/// same flight code the server runs, and each server snapshot corrects the result by replaying the inputs the
/// server has not seen yet. Other hulls are drawn slightly in the past, interpolated between snapshots. The
/// prediction runs into them all the same: it steps a copy of the hulls near the player's, brought up to the
/// present, with the code the server steps them with.
/// </summary>
internal sealed class ClientWorld : IDisposable
{
    public const int MaxViews = 64;
    private const int PendingCapacity = 256;

    /// <summary>
    /// Samples kept per remote hull. A hull in contact with the player's is sent every tick, so this has to
    /// reach back past the interpolation delay at one sample a tick, with room for late packets.
    /// </summary>
    private const int SamplesPerHull = 16;
    private const int RemoteExpiryTicks = 120;

    /// <summary>How far in the past remote hulls are drawn, in ticks; covers the gap between their updates.</summary>
    private const double InterpolationDelayTicks = 6;
    private const float MaxExtrapolationSeconds = 0.25f;

    /// <summary>
    /// A hull not heard of for this many ticks, four of the server's updates, is no longer something to run into:
    /// it has most likely been destroyed, and the server decides either way.
    /// </summary>
    private const int NearStaleTicks = 12;

    /// <summary>
    /// A hull is near enough to predict against if the two could meet within this many seconds at the speeds
    /// they have, with <see cref="NearMargin"/> metres to spare. It covers the inputs the server has yet to answer.
    /// </summary>
    private const float NearSeconds = 0.5f;
    private const float NearMargin = 20f;
    private const float JumpGap = 3000f;

    /// <summary>A correction larger than this is a teleport, not an error to smooth.</summary>
    private const float SnapDistance = 60f;

    /// <summary>How hard the hull turns toward where the player looks: turn rate in rad/s per radian of error.</summary>
    private const float AlignGain = 6f;

    /// <summary>What the server flies a hull with when nobody does: it brakes, and follows its order if it has one.</summary>
    private static readonly FlightInput Hold = new() { Dampeners = true };

    /// <summary>
    /// What another player's hull is taken to be doing. Nothing is known of its pilot's hands, so it is given
    /// none: no thrust, no braking and no order, which leaves it the velocity it was last seen with.
    /// </summary>
    private static readonly FlightInput Coast = new();

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

        /// <summary>The hull's flight figures with the blocks it has left: its mass, inertia and reach in a collision.</summary>
        public FlightParams Flight;
    }

    private GameContent _content;
    private FlightParams _flight;
    private byte _ownDesign;
    private ulong _ownBlocks;
    private uint _playerId;
    private readonly Pending[] _pending = new Pending[PendingCapacity];
    private readonly Remote[] _remotes = new Remote[MaxViews];
    private readonly Sample[] _samples = new Sample[MaxViews * SamplesPerHull];
    private readonly HullView[] _lastViews = new HullView[MaxViews];

    // The predicted world: the player's hull in slot 0 and the hulls near it after that, in the server's order.
    // Rebuilt from each snapshot and stepped once per input.
    private readonly HullStore _near = new(MaxViews + 1);
    private HullField _field;
    private int _ticksSinceSnapshot;
    private readonly byte[] _sendBuffer = new byte[256];
    private readonly byte[] _fleetBuffer = new byte[2 + FleetOrdersMessage.MaxOrders * 29];
    private int _lastViewCount;

    private uint _ownHullId;
    private bool _hasOwnState;
    private HullState _predicted;
    private Vector3 _correction;
    private uint _nextSequence = 1;
    private uint _ackedSequence;
    private long _snapshotTick = long.MinValue;
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
        _field = new HullField(_near, content.Designs, authoritative: false);
    }

    public void Dispose() => _near.Dispose();

    /// <summary>
    /// Gives the world the content as the server's settings make it. The prediction has to run on the server's
    /// figures, or every collision, shield and jump charge it shows would be corrected a moment later. Call it
    /// when the server has welcomed the client, before any snapshot is applied.
    /// </summary>
    public void SetContent(GameContent content)
    {
        _content = content;
        _field = new HullField(_near, content.Designs, authoritative: false);
        OwnDesign.Evaluate(_hasOwnState ? _ownBlocks : OwnDesign.FullMask, out _flight, out _);
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

    /// <summary>
    /// Predicts one tick and sends input. When sendCommands is false, reliable control/fleet intents stay queued
    /// until the session receives a newer server snapshot, so catch-up ticks cannot spend the allowance again.
    /// </summary>
    public void Tick(PlayerInputBridge bridge, ITransport transport, double serverTick, bool sendCommands = true)
    {
        _serverTick = serverTick;
        var controls = bridge.Consume(out var hasOrder, out var order);
        _correction *= 0.86f;

        Span<FleetOrder> fleetOrders = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        var fleetOrderCount = sendCommands ? bridge.ConsumeFleetOrders(fleetOrders) : 0;
        if (sendCommands && bridge.ConsumeControlRequest(out var controlHull))
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

        // With the server silent the hulls nearby are guesses growing old: stop running into them.
        if (++_ticksSinceSnapshot > NearStaleTicks)
        {
            for (var i = 1; i < _near.Slots; i++)
                _near.Destroy(_near.HandleAt(i));
        }

        var energyBefore = _predicted.Energy;
        _near.State(0) = _predicted;
        StepNear(ref _order, input);
        _predicted = _near.State(0);
        _energyRate += ((_predicted.Energy - energyBefore) / HullSim.Dt - _energyRate) * 0.2f;
        Send(transport);
    }

    /// <summary>Applies a world snapshot from the server.</summary>
    public void OnSnapshot(ref NetReader reader, long clientTick)
    {
        if (!SnapshotHeader.TryRead(ref reader, out var header))
            return;

        var own = default(HullRecord);
        var hasOwn = false;
        for (var i = 0; i < header.HullCount; i++)
        {
            if (!HullRecord.TryRead(ref reader, out var record))
                break;

            if (record.Id == header.OwnHull)
            {
                own = record;
                hasOwn = true;
            }
            else
            {
                StoreRemote(record, header.ServerTick, clientTick);
            }
        }

        // The player's hull goes last, so its inputs are replayed against the other hulls as this snapshot has them.
        if (!hasOwn)
            return;

        // Snapshots can arrive out of order, and it is the server's tick that says which is newer. The
        // acknowledged input cannot: a hull that is struck changes without the server simulating any input.
        if (header.ServerTick <= _snapshotTick)
            return;
        _snapshotTick = header.ServerTick;

        if (own.Id != _ownHullId || !_hasOwnState)
            SwitchHull(own);
        if (own.Blocks != _ownBlocks)
        {
            // The hull has lost blocks: it flies on what is left.
            _ownBlocks = own.Blocks;
            OwnDesign.Evaluate(_ownBlocks, out _flight, out _);
        }

        Reconcile(own.State, own.Order, header.AckedInput, header.ServerTick);
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
                Boosting = _predicted.Boosting,
                Order = _order,
                Jump = _predicted.Jump,
                JumpTimer = _predicted.JumpTimer,
                JumpCalculation = _predicted.JumpCalculation,
                JumpTunnelSeconds = _predicted.JumpTunnelSeconds,
                JumpTunnelElapsed = _predicted.JumpTunnelElapsed,
                Energy = _predicted.Energy,
                EnergyRate = _energyRate,
                Shield = _predicted.Shield,
                Hull = _predicted.Hull,
                ShieldOffline = _predicted.ShieldOffline,
                ShieldHit = _predicted.ShieldHit,
                ShieldHitAge = _predicted.ShieldHit.AgeTicks * HullSim.Dt,
                ShieldChangeAge = _predicted.ShieldChangeTicks * HullSim.Dt,
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

    private void Reconcile(in HullState authoritative, in HullOrder authoritativeOrder, uint acked, long snapshotTick)
    {
        if (!_hasOwnState)
        {
            _hasOwnState = true;
            _predicted = authoritative;
            _ackedSequence = Math.Max(acked, _ackedSequence);
            BuildNear(authoritative, snapshotTick);
            _near.Info(0).Order = authoritativeOrder;
            return;
        }

        // The acknowledgement only says which inputs are in this state already. It never goes back: after a
        // change of hull it stands ahead of the server's, at the last input that was meant for the old hull.
        _ackedSequence = Math.Max(acked, _ackedSequence);

        // Start from what the server computed, for the player's hull and for the hulls near it, and re-apply
        // every input it has not simulated yet.
        BuildNear(authoritative, snapshotTick);
        _near.Info(0).Order = authoritativeOrder;
        var first = Math.Max(_ackedSequence + 1, _nextSequence > PendingCapacity ? _nextSequence - PendingCapacity + 1 : 1);
        for (var sequence = first; sequence < _nextSequence; sequence++)
        {
            ref var slot = ref _pending[sequence % PendingCapacity];
            var order = slot.Order;
            StepNear(ref order, slot.Record.Input);
        }

        var state = _near.State(0);

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
        var known = remote.Used && remote.Id == record.Id;
        if (!known)
            remote = new Remote { Used = true, Id = record.Id, Newest = -1 };

        // A record that arrives late says nothing new, about where the hull is or about what it has left.
        remote.LastSeenTick = clientTick;
        if (remote.Count > 0 && _samples[slot * SamplesPerHull + remote.Newest].Tick >= serverTick)
            return;

        var design = (byte)Math.Min(record.Design, _content.Designs.Length - 1);

        // What the hull weighs in a collision follows from the blocks it has left.
        if (!known || remote.Design != design || remote.Blocks != record.Blocks)
            _content.Designs[design].Evaluate(record.Blocks, out remote.Flight, out _);

        remote.Owner = record.Owner;
        remote.Design = design;
        remote.Blocks = record.Blocks;
        remote.Newest = (remote.Newest + 1) % SamplesPerHull;
        remote.Count = Math.Min(remote.Count + 1, SamplesPerHull);
        _samples[slot * SamplesPerHull + remote.Newest] = new Sample
        {
            Tick = serverTick,
            State = record.State,
            Order = record.Order,
        };
    }

    /// <summary>
    /// Rebuilds the predicted world from a snapshot: the player's hull as the server has it, and each hull near
    /// enough to meet it soon, brought forward from when it was last heard of to the snapshot's tick.
    /// </summary>
    private void BuildNear(in HullState own, long snapshotTick)
    {
        _ticksSinceSnapshot = 0;
        _near.Clear();
        _near.Create(_playerId, _ownDesign, OwnDesign, own.Position);
        _near.State(0) = own;
        _near.Info(0).Blocks = _ownBlocks;
        _near.Flight(0) = _flight;

        // The server steps the hulls nobody flies in slot order, and the order decides who gives way when two
        // of them touch: keep it.
        Span<int> order = stackalloc int[MaxViews];
        var count = 0;
        for (var r = 0; r < _remotes.Length; r++)
        {
            ref var remote = ref _remotes[r];
            if (!remote.Used || remote.Count == 0)
                continue;

            ref var sample = ref _samples[r * SamplesPerHull + remote.Newest];
            var reach = _flight.Radius + remote.Flight.Radius + (own.Velocity.Length() + sample.State.Velocity.Length()) * NearSeconds + NearMargin;
            if (snapshotTick - sample.Tick > NearStaleTicks || !(sample.State.Position.RelativeTo(own.Position).LengthSquared() < reach * reach))
                continue;

            var at = count++;
            for (; at > 0 && (_remotes[order[at - 1]].Id & 0xFFFF) > (remote.Id & 0xFFFF); at--)
                order[at] = order[at - 1];
            order[at] = r;
        }

        for (var i = 0; i < count; i++)
        {
            ref var remote = ref _remotes[order[i]];
            ref var sample = ref _samples[order[i] * SamplesPerHull + remote.Newest];
            var index = _near.Create(remote.Owner, remote.Design, _content.Designs[remote.Design], sample.State.Position).Index;
            ref var info = ref _near.Info(index);
            ref var state = ref _near.State(index);
            var mine = remote.Owner == _playerId;
            info.Blocks = remote.Blocks;
            info.Order = mine ? sample.Order : default;
            state = sample.State;
            _near.Flight(index) = remote.Flight;

            // The player's other hulls are flown by the server, and how is known: fly them the same way up to
            // now. Somebody else's hull may have a pilot; all that can be said of it is that it keeps going.
            // Nothing is run into on the way: what the hull met before the snapshot's tick the server has
            // settled, and told of.
            for (var age = snapshotTick - sample.Tick; age > 0; age--)
                HullSim.Step(ref state, ref info.Order, mine ? Hold : Coast, remote.Flight);
        }
    }

    /// <summary>
    /// Advances the predicted world one tick the way the server advances the real one: the player's hull under
    /// the given input, then the player's other hulls under their orders, and other players' hulls as they
    /// were going. All of them step through the field, so a hull that crosses another within the tick strikes
    /// it. Hulls that are struck recoil here as they will on the server, so the next input meets them where
    /// they will be.
    /// </summary>
    private void StepNear(ref HullOrder order, in FlightInput input)
    {
        _field.BeginRound();
        HullSim.SetOrder(ref _near.State(0), ref _near.Info(0).Order, order);
        _field.Step(0, input, _rocks, _serverTick);
        order = _near.Info(0).Order;

        for (var i = 1; i < _near.Slots; i++)
        {
            if (_near.IsAliveAt(i))
                _field.Step(i, _near.Info(i).Owner == _playerId ? Hold : Coast, _rocks, _serverTick);
        }
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
            Boosting = newest.State.Boosting,
            Order = newest.Order,
            Jump = newest.State.Jump,
            JumpTimer = newest.State.JumpTimer,
            JumpCalculation = newest.State.JumpCalculation,
            JumpTunnelSeconds = newest.State.JumpTunnelSeconds,
            JumpTunnelElapsed = newest.State.JumpTunnelElapsed,
            Energy = newest.State.Energy,
            Shield = newest.State.Shield,
            Hull = newest.State.Hull,
            ShieldOffline = newest.State.ShieldOffline,
            ShieldHit = newest.State.ShieldHit,
            ShieldHitAge = (float)(newest.State.ShieldHit.AgeTicks + (renderTick - newest.Tick)) * HullSim.Dt,
            ShieldChangeAge = (float)(newest.State.ShieldChangeTicks + (renderTick - newest.Tick)) * HullSim.Dt,
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
