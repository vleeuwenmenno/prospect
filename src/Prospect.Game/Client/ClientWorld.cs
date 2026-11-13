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

    /// <summary>
    /// The blocks the hull still has: a <see cref="BlockSet"/> of its design, copied into the snapshot the view
    /// is published in. It is that snapshot's memory, so it is good for as long as the snapshot is.
    /// </summary>
    public ReadOnlyMemory<ulong> Blocks;

    /// <summary>
    /// How many blocks the hull has lost. It only grows, so with the hull's id it names the block set: a reader
    /// that kept something worked out from <see cref="Blocks"/> needs to work it out again only when this moves.
    /// </summary>
    public uint Revision;
    /// <summary>Value-owned dent profiles and latest eligible spark; valid for this snapshot lifetime.</summary>
    public VisualDamageState Visual;

    /// <summary>
    /// The hull's data sheet with the blocks it has left, in the world's rules: copied from the figures the
    /// session keeps for the hull, which are read off its running sums when it loses a block. Whoever draws or
    /// lists the hull reads its shield capacity, its drive and the rest here, and works nothing out from
    /// <see cref="Blocks"/>.
    /// </summary>
    public HullStats Stats;

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

    /// <summary>
    /// The moment this view shows, in simulation ticks on the hull's own timeline: the inputs predicted so far
    /// for the player's hull, the delayed server tick a remote hull is drawn at. Two views of one hull are this
    /// many ticks apart in what they show, which is not how long the frames between them took. It is never
    /// compared between hulls.
    /// </summary>
    public double ShownTick;
}

/// <summary>
/// The client's picture of the world. The player's own hull is predicted: inputs are applied immediately with the
/// same flight code the server runs, and each server snapshot corrects the result by replaying the inputs the
/// server has not seen yet. Other hulls are drawn slightly in the past, interpolated between snapshots. The
/// prediction runs into them all the same: it steps a copy of the hulls near the player's, brought up to the
/// present, with the code the server steps them with.
/// <para>
/// The world keeps the block state of every hull it knows, its own and the others, for as long as it knows the
/// hull. The hulls it predicts with are created over those states and only borrow them: clearing and filling that
/// store at every snapshot copies, rebuilds and releases nothing.
/// </para>
/// </summary>
internal sealed partial class ClientWorld : IDisposable
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
    private static readonly FlightInput Hold = InputTimeline.Fallback;

    /// <summary>
    /// What another player's hull is taken to be doing. Nothing is known of its pilot's hands, so it is given
    /// none: no thrust, no braking and no order, which leaves it the velocity it was last seen with.
    /// </summary>
    private static readonly FlightInput Coast = new();

    private struct Pending
    {
        public InputRecord Record;
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
        public bool HasHealth;
        public uint Id;
        public uint Owner;
        public byte Design;

        /// <summary>The hull's block state in the world's store, kept from when the hull is first heard of until it is forgotten.</summary>
        public BlockStateHandle Blocks;
        public long LastSeenTick;
        public int Newest;
        public int Count;

        /// <summary>The hull's flight figures with the blocks it has left: its mass, inertia and reach in a collision.</summary>
        public FlightParams Flight;

        /// <summary>The hull's data sheet with the blocks it has left, for whoever views it.</summary>
        public HullStats Stats;
    }

    private GameContent _content;
    private BlockReplica _replica;

    // The figures of the hull the player flies, read off its sums whenever it loses a block.
    private FlightParams _flight;
    private HullStats _stats;
    private byte _ownDesign;

    // Authoritative published blocks and health; speculative damage lives in the separate near store.
    private readonly BlockStateStore _blocks = new(MaxViews + 1);
    private BlockStateHandle _ownBlocks;
    private bool _ownHealth;
    private readonly BlockStateHandle[] _nearSources = new BlockStateHandle[MaxViews + 1];
    private uint _playerId;
    private readonly Pending[] _pending = new Pending[PendingCapacity];
    private readonly Remote[] _remotes = new Remote[MaxViews];
    private readonly Sample[] _samples = new Sample[MaxViews * SamplesPerHull];
    private readonly HullView[] _lastViews = new HullView[MaxViews];

    // The predicted world: the player's hull in slot 0 and the hulls near it after that, in the server's order.
    // Rebuilt from each snapshot and stepped once per world interval. Its hulls own disposable damage copies; _blocks remains authoritative.
    private readonly HullStore _near;
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
    private uint _controlEpoch;
    private long _predictionTick;
    private ushort _appliedOrderId;
    private readonly int[] _canonical = new int[MaxViews + 1];
    private int _canonicalCount;
    public long PhaseResets { get; private set; }
    public long FallbackIntervals { get; private set; }
    private long _snapshotTick = long.MinValue;
    private HullOrder _order;
    private HullOrder _orderDefinition;
    private ushort _orderId;
    private bool _dampeners = true;
    private RockField? _rocks;
    private int _lead;
    private int _assignmentPause;
    private long _lastSnapshotClientTick;
    private float _energyRate;

    public ClientWorld(GameContent content)
    {
        _content = content;
        _replica = new BlockReplica(content);
        _flight = content.Pod.Flight;
        _stats = content.Pod.Stats;
        _ownDesign = (byte)content.PodDesign;
        _near = new HullStore(MaxViews + 1);
        _field = new HullField(_near, content.Designs, authoritative: false);
    }

    public void Dispose()
    {
        _replica.Dispose();
        _near.Dispose();
        _blocks.Dispose();
    }

    /// <summary>Where the world keeps the block state of the hulls it knows.</summary>
    internal BlockStateStore BlockStates => _blocks;

    /// <summary>The store of hulls the prediction steps, filled again from every snapshot.</summary>
    internal HullStore Near => _near;

    /// <summary>The block state of a hull the world knows, the player's or another; false if it knows no such hull.</summary>
    internal bool TryGetBlockState(uint hull, out BlockStateHandle state)
    {
        state = default;
        if (hull == _ownHullId && _ownBlocks.IsValid)
        {
            state = _ownBlocks;
            return true;
        }

        for (var i = 0; i < _remotes.Length; i++)
        {
            if (!_remotes[i].Used || _remotes[i].Id != hull)
                continue;
            state = _remotes[i].Blocks;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gives the world the content as the server's settings make it. The prediction has to run on the server's
    /// figures, or every collision, shield and jump charge it shows would be corrected a moment later. Call it
    /// when the server has welcomed the client, before any snapshot is applied.
    /// </summary>
    public void SetContent(GameContent content)
    {
        if (!_hasOwnState)
        {
            // Admission replaces every consumer of design indices before any pose is accepted.
            _replica.Dispose();
            _replica = new BlockReplica(content);
            _ownDesign = (byte)content.PodDesign;
        }
        _content = content;
        _field = new HullField(_near, content.Designs, authoritative: false);
        if (_hasOwnState)
        {
            // The sums do not depend on a world's rules; the figures read off them do.
            OwnDesign.Figures(_blocks.Sums(_ownBlocks), out _flight, out _stats);
        }
        else
        {
            _flight = OwnDesign.Flight;
            _stats = OwnDesign.Stats;
        }
    }

    /// <summary>
    /// Gives the world a catalogue that has grown at its end during play. Every design it had keeps its number
    /// and is the same object, so the player's hull, the hulls it knows, their block states and whatever is being
    /// recovered stay exactly as they are; only hulls of the new designs become acceptable. Call it between ticks.
    /// </summary>
    public void AppendContent(GameContent content)
    {
        _content = content;
        _replica.Append(content);
        _field.SetDesigns(content.Designs);
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
    public void Tick(PlayerInputBridge bridge, ITransport transport, double serverTick, bool sendCommands = true, long clientTick = -1, float roundTripMs = 0)
    {

        _replica.SendAcks(transport, clientTick < 0 ? (long)serverTick : clientTick);
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
            // A hull that cannot turn is asked for no turn: its rate of zero divides into no number.
            if (_flight.TurnRate.Y > 0f)
                yaw = Math.Clamp(yawError * AlignGain / _flight.TurnRate.Y, -1f, 1f);
            if (_flight.TurnRate.X > 0f)
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

        var lead = Math.Clamp((int)Math.Ceiling(Math.Max(0, roundTripMs) / 2000 / HullSim.Dt) + 3, 3, 16);
        if (clientTick >= 0 && clientTick - _lastSnapshotClientTick >= PendingCapacity)
        {
            _hasOwnState = false;
            PhaseResets++;
            return;
        }
        if (_lead > lead) _assignmentPause += _lead - lead;
        var gap = Math.Max(0, lead - _lead - (_lead == 0 ? 1 : 0));
        _lead = lead;
        // Once authority catches prediction, restore delivery lead even inside the drift tolerance.
        var caughtUp = _predictionTick <= _snapshotTick;
        if (caughtUp) _assignmentPause = 0;
        if (_assignmentPause > 0) { _assignmentPause--; return; }
        if (_predictionTick - _snapshotTick >= InputTimeline.Capacity - 1) return;
        var expected = Math.Max(_snapshotTick, (long)Math.Floor(serverTick)) + lead;
        if (caughtUp || expected - _predictionTick > 8)
        {
            gap = (int)Math.Min(InputTimeline.Capacity - 1, expected - _predictionTick - 1);
            PhaseResets++;
        }
        else if (_predictionTick - expected > 8)
        {
            PhaseResets++;
            return;
        }
        var target = Math.Max(_predictionTick + 1 + gap, _snapshotTick + 1);
        target = Math.Min(target, _snapshotTick + InputTimeline.Capacity - 1);
        while (_predictionTick + 1 < target)
        {
            _predictionTick++;
            StepNear(ref _order, Hold);
            FallbackIntervals++;
        }
        _predictionTick = target;
        ref var slot = ref _pending[target % PendingCapacity];
        slot.Record = new InputRecord
        {
            TargetTick = target,
            Sequence = _nextSequence++,
            Input = input,
            OrderId = _orderId,
            Order = _orderDefinition,
        };
        if (InputTimeline.Newer(_orderId, _appliedOrderId))
        {
            HullSim.SetOrder(ref _near.State(0), ref _near.Info(0).Order, _orderDefinition);
            _appliedOrderId = _orderId;
        }

        // With the server silent the hulls nearby are guesses growing old: stop running into them.
        if (++_ticksSinceSnapshot > NearStaleTicks)
        {
            for (var i = 1; i < _near.Slots; i++)
                _near.Destroy(_near.HandleAt(i));
        }

        var energyBefore = _predicted.Energy;

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

            if (!_replica.Accept(record, clientTick))
            {
                HoldPose(record, header, clientTick);
                continue;
            }
            ClearHeld(record.Id);

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

        // Reconcile only after reading every remote, even though the own record is serialized first.
        if (!hasOwn)
            return;

        // Snapshots can arrive out of order, and it is the server's tick that says which is newer. The
        // acknowledged input cannot: a hull that is struck changes without the server simulating any input.
        if (header.ServerTick <= _snapshotTick)
            return;
        _snapshotTick = header.ServerTick;
        _lastSnapshotClientTick = clientTick;

        if (own.Id != _ownHullId || !_hasOwnState || header.ControlEpoch != _controlEpoch)
        {
            SwitchHull(own);
            _controlEpoch = header.ControlEpoch;
            _predictionTick = header.ServerTick;
            _lead = 0;
            _assignmentPause = 0;
            Array.Clear(_pending);
            _orderId = header.AppliedOrderId;
        }
        if (ApplyLosses(_ownBlocks, _replica.Losses(own.Id)))
        {
            // The hull has lost blocks: it flies on what is left.
            OwnDesign.Figures(_blocks.Sums(_ownBlocks), out _flight, out _stats);
        }

        _ownHealth = own.HasHealth;
        ReadHealth(_ownBlocks, own);
        Reconcile(own.State, own.Order, header.ServerTick, header.AppliedOrderId);
    }

    /// <summary>Receives a bounded block recovery chunk.</summary>
    public void OnBlockState(ref NetReader reader, long tick, bool visual = false)
    {
        _replica.Receive(ref reader, tick, visual);
        ReplayHeld(tick);
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

    /// <summary>
    /// Writes this tick's hull views. Returns how many were written and which one is the player's. Each view's
    /// blocks are copied into <paramref name="blockWords"/>, the publication's own memory for them, so the
    /// reader of the views never looks into the world's store. That memory is replaced by a larger one only
    /// when the hulls in view need more words than it has.
    /// </summary>
    public int BuildViews(HullView[] views, ref ulong[] blockWords, double serverTick, long clientTick, out int ownIndex)
    {
        var count = 0;
        ownIndex = -1;

        // The most words the views can take, known before any is written: a view keeps the memory it was given.
        var needed = _hasOwnState ? _blocks.Alive(_ownBlocks).Length : 0;
        for (var r = 0; r < _remotes.Length; r++)
        {
            if (_remotes[r].Used)
                needed += _blocks.Alive(_remotes[r].Blocks).Length;
        }

        if (blockWords.Length < needed)
            blockWords = new ulong[Math.Max(needed, blockWords.Length * 2)];
        var used = 0;

        if (_hasOwnState)
        {
            ownIndex = count;
            views[count++] = new HullView
            {
                Id = _ownHullId,
                Owner = _playerId,
                Design = _ownDesign,
                Blocks = Publish(_ownBlocks, blockWords, ref used),
                Revision = _blocks.Revision(_ownBlocks),
                Stats = _stats,
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
                ShownTick = _predictionTick,
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
                Forget(ref remote);
                continue;
            }

            views[count] = Interpolate(r, ref remote, renderTick);
            views[count].Blocks = Publish(remote.Blocks, blockWords, ref used);
            views[count].Stats = remote.Stats;
            views[count++].Revision = _blocks.Revision(remote.Blocks);
        }

        // Pair each view with where it was last tick so the render thread can interpolate between the two.
        for (var i = 0; i < count; i++)
        {
            ref var view = ref views[i];
            view.Visual = _replica.Visual(view.Id);
            if (serverTick < view.Visual.Spark.Tick || serverTick - view.Visual.Spark.Tick > 30)
                view.Visual.Spark = default;
            view.PreviousPosition = view.Position;
            view.PreviousOrientation = view.Orientation;
            for (var j = 0; j < _lastViewCount; j++)
            {
                if (_lastViews[j].Id != view.Id || _lastViews[j].Revision != view.Revision)
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

    /// <summary>Copies a hull's blocks into a publication's memory and returns where they are.</summary>
    private ReadOnlyMemory<ulong> Publish(BlockStateHandle state, ulong[] blockWords, ref int used)
    {
        var alive = _blocks.Alive(state);
        var published = new Memory<ulong>(blockWords, used, alive.Length);
        alive.CopyTo(published.Span);
        used += alive.Length;
        return published;
    }

    /// <summary>
    /// Creates the published state from the replica's validated loss history. Sums follow each loss.
    /// </summary>
    private BlockStateHandle TakeBlocks(HullDesign design, ReadOnlySpan<int> wire)
    {
        var state = _blocks.Create(design, hitPoints: true);
        ApplyLosses(state, wire);
        return state;
    }

    private void ReadHealth(BlockStateHandle state, in HullRecord record)
    {
        if (!record.HasHealth) return;
        var intact = _blocks.Design(state).IntactHitPoints;
        var next = 0;
        foreach (var block in BlockSet.Each(_blocks.Alive(state)))
        {
            var points = intact[block];
            if (next < record.HealthCount && record.DamagedBlocks[next] == block)
                points = record.RemainingHealth[next++];
            _blocks.SetHitPoints(state, block, points);
        }
    }

    /// <summary>
    /// Applies only losses not already published; unchanged snapshots never scan a block set.
    /// </summary>
    private bool ApplyLosses(BlockStateHandle state, ReadOnlySpan<int> wire)
    {
        var revision = (int)_blocks.Revision(state);
        for (var i = revision; i < wire.Length; i++) _blocks.Lose(state, wire[i]);
        return revision != wire.Length;
    }

    /// <summary>Forgets a hull that is no longer heard of, and gives its block state back.</summary>
    private void Forget(ref Remote remote)
    {
        ReleaseBlocks(remote.Blocks);
        remote = default;
    }

    /// <summary>
    /// Restores a nearby hull after a newly accepted block loss, then advances its known order to the horizon.
    /// The speculative copy may have lost different blocks; it is replaced, never merged into authority.
    /// </summary>
    private void RefitNear(BlockStateHandle state, in HullRecord record, long serverTick)
    {
        for (var i = 1; i < _near.Slots; i++)
        {
            if (!_near.IsAliveAt(i) || _nearSources[i] != state) continue;
            _near.BlockStates.CopyFrom(_near.Info(i).BlockState, _blocks, state);
            _near.Info(i).PredictDamage = record.HasHealth;
            _field.Refit(i);
            _near.State(i) = record.State;
            var mine = record.Owner == _playerId;
            _near.Info(i).Order = mine ? record.Order : default;
            var age = Math.Clamp(_predictionTick - serverTick, 0, NearStaleTicks);
            for (var step = 0; step < age; step++)
            {
                HullSim.AdvanceShieldClock(ref _near.State(i));
                HullSim.Step(ref _near.State(i), ref _near.Info(i).Order, mine ? Hold : Coast, _near.Flight(i), serverTick + step + 1);
            }
        }
    }

    /// <summary>
    /// Gives back the block state of a hull that is not the player's. A predicted hull still made over it goes
    /// first, so its copied state cannot outlive the authority it was associated with.
    /// </summary>
    private void ReleaseBlocks(BlockStateHandle state)
    {
        for (var i = 1; i < _near.Slots; i++)
        {
            if (_near.IsAliveAt(i) && _nearSources[i] == state)
                _near.Destroy(_near.HandleAt(i));
        }

        _blocks.Release(state);
    }

    /// <summary>
    /// The server has put the player in a different hull: start predicting that one. Inputs already sent were
    /// for the old hull, so they are not replayed onto the new one.
    /// </summary>
    private void SwitchHull(in HullRecord record)
    {
        // The replica accepted the record, so its design is one the world has: a record of a design it does
        // not know is held back before it gets here, never taken for another design.
        _ownHullId = record.Id;
        _ownDesign = record.Design;

        // Another hull, so another block state: the old hull's goes back, and it is heard of as a remote from here on.
        _blocks.Release(_ownBlocks);
        _ownBlocks = TakeBlocks(OwnDesign, _replica.Losses(record.Id));
        OwnDesign.Figures(_blocks.Sums(_ownBlocks), out _flight, out _stats);
        _hasOwnState = false;
        _correction = Vector3.Zero;
        SetOrder(default);

        // The hull was being drawn as someone else's until now.
        for (var i = 0; i < _remotes.Length; i++)
        {
            if (_remotes[i].Used && _remotes[i].Id == record.Id)
                Forget(ref _remotes[i]);
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
        var count = 0;
        for (var tick = _predictionTick; tick > _snapshotTick && _predictionTick - tick < PendingCapacity && count < InputMessage.MaxRecords; tick--)
        {
            var record = _pending[tick % PendingCapacity].Record;
            if (record.TargetTick == tick) records[count++] = record;
        }
        records[..count].Reverse();
        if (count == 0) return;
        var writer = new NetWriter(_sendBuffer);
        InputMessage.Write(ref writer, records[..count], _controlEpoch);
        transport.Send(0, NetChannel.Unreliable, writer.Written);
    }

    private void Reconcile(in HullState authoritative, in HullOrder authoritativeOrder, long snapshotTick, ushort appliedOrderId)
    {
        var hadState = _hasOwnState;
        _hasOwnState = true;
        var horizon = Math.Max(snapshotTick, _predictionTick);
        if (horizon - snapshotTick >= PendingCapacity)
        {
            horizon = snapshotTick;
            Array.Clear(_pending);
            PhaseResets++;
        }
        BuildNear(authoritative, snapshotTick);
        _near.Info(0).Order = authoritativeOrder;
        _appliedOrderId = appliedOrderId;
        for (var tick = snapshotTick + 1; tick <= horizon; tick++)
        {
            _predictionTick = tick;
            ref var record = ref _pending[tick % PendingCapacity].Record;
            var input = Hold;
            if (record.TargetTick == tick)
            {
                input = record.Input;
                if (InputTimeline.Newer(record.OrderId, _appliedOrderId))
                {
                    HullSim.SetOrder(ref _near.State(0), ref _near.Info(0).Order, record.Order);
                    _appliedOrderId = record.OrderId;
                }
            }
            var standing = _near.Info(0).Order;
            StepNear(ref standing, input);
        }
        _predictionTick = horizon;
        _order = _near.Info(0).Order;
        if (!hadState) _predicted = _near.State(0);

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

        // The replica accepted the record, so the design is one the world has.
        var design = record.Design;

        // What the hull weighs in a collision follows from the blocks it has left. A hull first heard of, or one
        // that turns out to be of another design, gets a block state of its own, kept until the hull is forgotten;
        // a known hull loses from the state it has whatever the record no longer shows.
        if (!known || remote.Design != design)
        {
            if (known)
                ReleaseBlocks(remote.Blocks);
            remote.Design = design;
            remote.Blocks = TakeBlocks(_content.Designs[design], _replica.Losses(record.Id));
            _content.Designs[design].Figures(_blocks.Sums(remote.Blocks), out remote.Flight, out remote.Stats);
        }
        else if (ApplyLosses(remote.Blocks, _replica.Losses(record.Id)))
        {
            _content.Designs[design].Figures(_blocks.Sums(remote.Blocks), out remote.Flight, out remote.Stats);
            ReadHealth(remote.Blocks, record);
            RefitNear(remote.Blocks, record, serverTick);
            remote.Count = 0;
        }

        remote.HasHealth = record.HasHealth;
        ReadHealth(remote.Blocks, record);
        remote.Owner = record.Owner;
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
        _nearSources[0] = _ownBlocks;
        _near.BlockStates.CopyFrom(_near.Info(0).BlockState, _blocks, _ownBlocks);
        _near.Info(0).PredictDamage = _ownHealth;
        _near.Info(0).FittedRevision = _blocks.Revision(_ownBlocks);
        _near.State(0) = own;
        _near.Flight(0) = _flight;
        _canonicalCount = 1;
        _canonical[0] = 0;

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
            _nearSources[index] = remote.Blocks;
            _near.BlockStates.CopyFrom(_near.Info(index).BlockState, _blocks, remote.Blocks);
            _near.Info(index).PredictDamage = remote.HasHealth;
            _near.Info(index).FittedRevision = _blocks.Revision(remote.Blocks);
            var at = _canonicalCount++;
            var key = remote.Id & 0xFFFF;
            // Remotes were sorted above; insert the own hull at its authoritative slot.
            for (; at > 0 && _canonical[at - 1] == 0 && (_ownHullId & 0xFFFF) > key; at--)
                _canonical[at] = _canonical[at - 1];
            _canonical[at] = index;
            ref var info = ref _near.Info(index);
            ref var state = ref _near.State(index);
            var mine = remote.Owner == _playerId;
            info.Order = mine ? sample.Order : default;
            state = sample.State;
            _near.Flight(index) = remote.Flight;

            // The player's other hulls are flown by the server, and how is known: fly them the same way up to
            // now. Somebody else's hull may have a pilot; all that can be said of it is that it keeps going.
            // Nothing is run into on the way: what the hull met before the snapshot's tick the server has
            // settled, and told of.
            for (var age = snapshotTick - sample.Tick; age > 0; age--)
            {
                HullSim.AdvanceShieldClock(ref state);
                HullSim.Step(ref state, ref info.Order, mine ? Hold : Coast, remote.Flight, snapshotTick - age + 1);
            }
        }
        _field.SetTraversal(_canonical.AsSpan(0, _canonicalCount));
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
        for (var at = 0; at < _canonicalCount; at++)
        {
            var i = _canonical[at];
            if (_near.IsAliveAt(i))
                _field.Step(i, i == 0 ? input : _near.Info(i).Owner == _playerId ? Hold : Coast, _rocks, _predictionTick);
        }
        order = _near.Info(0).Order;
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
            ShownTick = renderTick,
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
