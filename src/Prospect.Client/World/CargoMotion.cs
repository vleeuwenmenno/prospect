using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Where a container is in loading or unloading, for effects and the interface.</summary>
public enum CargoStage : byte
{
    /// <summary>In its slot with the mag-locks holding: nothing moves.</summary>
    Stored,

    /// <summary>Loose, held by one of the own hull's tractor beams on its way to the staging point outside a rack's opening.</summary>
    Approaching,

    /// <summary>Crossing a rack's opening: loose on the beam's final glide in, or, stowed, until it is wholly inside.</summary>
    Transferring,

    /// <summary>Stowed and sliding inside the rack to its slot: after it came in, or to another slot after a repack.</summary>
    Positioning,

    /// <summary>Just reached its slot: the mag-locks engage, with a flash of the lock lights.</summary>
    Locked,

    /// <summary>Released: the locks let go and it slides out of the opening to the loose body it becomes.</summary>
    Unlocking,

    /// <summary>A loose container no beam of the own hull holds.</summary>
    Loose,
}

/// <summary>
/// How the own hull's containers are seen moving into, inside and out of its racks. Client look only: the server stows
/// (<see cref="BlockStateStore.TryAddCargo"/>) and releases as it always did, and this works out from what changes in
/// the own hold (<see cref="Game.Client.SessionSnapshot.OwnCargo"/>, <see cref="Game.Client.SessionSnapshot.OwnCargoTick"/>)
/// and in the loose bodies around it how the change is shown. Font-free and allocation-free, with fixed buffers.
/// <list type="bullet">
/// <item>The first hold the client gets for a hull (joining, loading, taking control, an applied edit) is never animated.</item>
/// <item>A hold record is the same container across a change when rack, size, resource, kilograms and hull points
/// all agree; of several such, one in the same place first, then in hold order. Matched in another place, it slides
/// there (a repack).</item>
/// <item>A new record takes over from the loose body the own beam was leading in with the same size, resource,
/// kilograms and hull points, seen within <see cref="CargoMotionLook.MatchSeconds"/>, from where that body was drawn,
/// so it never pops; that body is no longer drawn (<see cref="Hides"/>). Without one it comes in through the opening.</item>
/// <item>A record that goes waits for the loose body that leaves with the same figures and slides out to it, handing
/// over to where that body is drawn, which is hidden until then. One that finds none is dropped.</item>
/// </list>
/// Feed it once a frame: <see cref="BeginFree"/>, <see cref="SeeFree"/> for every loose body, <see cref="EndFree"/>,
/// then <see cref="Hold"/>. Then read <see cref="Pose"/>, <see cref="Stage"/>, the leaving ones and <see cref="Hides"/>.
/// </summary>
internal sealed class CargoMotion(CargoMotionLook look)
{
    /// <summary>Released containers shown leaving at once; more are dropped at once.</summary>
    public const int MaxLeaving = 16;

    /// <summary>Loose bodies held by the own beams remembered at once.</summary>
    public const int MaxBeamed = 32;

    /// <summary>Loose bodies first seen lately remembered at once.</summary>
    public const int MaxFresh = 16;

    private const int Max = BlockStateStore.MaxContainers;
    private const int SeenSlots = 1024;

    private struct Motion
    {
        public double Start;
        public float Duration, Inside;
        public Vector3 From;
        public Quaternion Turn;
        public bool Moves;
    }

    private struct Beamed
    {
        public uint Id;
        public int Size, Resource;
        public double Amount;
        public float Hull;
        public Vector3 Offset;
        public Quaternion Turn;
        public double Seen;
        public bool Hidden;
    }

    private struct Fresh
    {
        public uint Id;
        public int Size, Resource;
        public double Amount;
        public float Hull;
        public double Seen;
        public bool Hidden;
    }

    private struct Leaving
    {
        public HoldContainer Record;
        public Vector3 From;
        public Quaternion Turn;
        public double Removed, Start;
        public uint Free;
    }

    private readonly HoldContainer[] _hold = new HoldContainer[Max];
    private readonly HoldContainer[] _old = new HoldContainer[Max];
    private readonly Motion[] _motion = new Motion[Max];
    private readonly Motion[] _oldMotion = new Motion[Max];
    private readonly bool[] _taken = new bool[Max];
    private readonly bool[] _matched = new bool[Max];
    private readonly Beamed[] _beamed = new Beamed[MaxBeamed];
    private readonly Fresh[] _fresh = new Fresh[MaxFresh];
    private readonly Leaving[] _leaving = new Leaving[MaxLeaving];
    private uint[] _seen = new uint[SeenSlots], _seenBefore = new uint[SeenSlots];
    private int _count, _beamedCount, _freshCount, _leavingCount;
    private uint _hull, _ownHull;
    private long _tick;
    private bool _primed, _tracking, _freeKnown, _freeSeenOnce;
    private double _now;

    /// <summary>The look this motion is timed and lit by.</summary>
    public CargoMotionLook Look => look;

    /// <summary>The records of the hold as last taken, in hold order: index <c>i</c> is <see cref="Pose"/>'s.</summary>
    public ReadOnlySpan<HoldContainer> Records => _hold.AsSpan(0, _count);

    /// <summary>Released containers being shown leaving.</summary>
    public int LeavingCount => _leavingCount;

    /// <summary>Forgets everything: a new session.</summary>
    public void Clear()
    {
        _count = _beamedCount = _freshCount = _leavingCount = 0;
        _hull = _ownHull = 0;
        _tick = 0;
        _primed = _tracking = _freeKnown = _freeSeenOnce = false;
        Array.Clear(_seen);
        Array.Clear(_seenBefore);
    }

    /// <summary>Starts a frame's look at the loose bodies, at <paramref name="now"/> seconds, for the hull flown.</summary>
    public void BeginFree(double now, uint ownHull)
    {
        _now = now;
        _ownHull = ownHull;
        (_seen, _seenBefore) = (_seenBefore, _seen);
        Array.Clear(_seen);
        _freeKnown = _freeSeenOnce;
    }

    /// <summary>
    /// One loose body this frame, where it is drawn in the own hull's frame (<paramref name="offset"/> metres from the
    /// hull's pivot, turned by <paramref name="turn"/> from the hull's axes).
    /// </summary>
    public void SeeFree(in FreeContainer item, Vector3 offset, Quaternion turn)
    {
        if (item.Id == 0) return;
        Insert(_seen, item.Id);
        if (item.TractorHull != 0 && item.TractorHull == _ownHull)
        {
            var slot = -1;
            for (var b = 0; b < _beamedCount; b++) if (_beamed[b].Id == item.Id) { slot = b; break; }
            if (slot < 0) slot = _beamedCount < MaxBeamed ? _beamedCount++ : Oldest(_beamed.AsSpan(0, _beamedCount));
            if (_beamed[slot].Id != item.Id) _beamed[slot] = default;
            ref var b2 = ref _beamed[slot];
            b2.Id = item.Id; b2.Size = item.Size; b2.Resource = item.Resource; b2.Amount = item.Amount; b2.Hull = item.Hull;
            b2.Offset = offset; b2.Turn = turn; b2.Seen = _now;
        }
        // A body new this frame may be one the hold is letting go; until the hold says so it is not drawn twice.
        if (!_freeKnown || Contains(_seenBefore, item.Id)) return;
        for (var k = 0; k < _leavingCount; k++)
            if (_leaving[k].Free == 0 && Same(_leaving[k].Record, item))
            {
                _leaving[k].Free = item.Id;
                _leaving[k].Start = _now;
                return;
            }
        var at = _freshCount < MaxFresh ? _freshCount++ : OldestFresh();
        _fresh[at] = new Fresh
        {
            Id = item.Id,
            Size = item.Size,
            Resource = item.Resource,
            Amount = item.Amount,
            Hull = item.Hull,
            Seen = _now,
            Hidden = HoldHas(item),
        };
    }

    /// <summary>Ends the frame's look at the loose bodies and forgets what is too old to match.</summary>
    public void EndFree()
    {
        _freeSeenOnce = true;
        var limit = look.MatchSeconds;
        for (var b = _beamedCount - 1; b >= 0; b--)
            if (_now - _beamed[b].Seen > limit) _beamed[b] = _beamed[--_beamedCount];
        for (var f = _freshCount - 1; f >= 0; f--)
            if (_now - _fresh[f].Seen > limit) _fresh[f] = _fresh[--_freshCount];
        for (var k = _leavingCount - 1; k >= 0; k--)
        {
            ref var leaving = ref _leaving[k];
            var over = leaving.Free == 0 ? _now - leaving.Removed > limit : _now - leaving.Start >= look.UnlockSeconds + look.LeaveSeconds
                || !Contains(_seen, leaving.Free);
            if (over) _leaving[k] = _leaving[--_leavingCount];
        }
    }

    /// <summary>
    /// Takes the own hold as the client has it: the hull flown, the tick of the hold, the hull's design and the records.
    /// Nothing happens unless the tick or the hull changed.
    /// </summary>
    public void Hold(uint hull, long tick, HullDesign? design, CargoCatalog catalog, ReadOnlySpan<HoldContainer> hold)
    {
        if (_tracking && hull == _hull && tick == _tick) return;
        var count = Math.Min(hold.Length, Max);
        if (!_tracking || hull != _hull || design is null)
        {
            // Another hull (or the first): what it holds is shown as it is. Its first hold, if not here yet, will be too.
            _primed = _tracking && hull != _hull && tick != _tick && design is not null;
            _tracking = true;
            _hull = hull; _tick = tick;
            hold[..count].CopyTo(_hold);
            _count = count;
            Array.Clear(_motion, 0, Max);
            _leavingCount = 0;
            for (var b = 0; b < _beamedCount; b++) _beamed[b].Hidden = false;
            for (var f = 0; f < _freshCount; f++) _fresh[f].Hidden = false;
            return;
        }

        _tick = tick;
        if (!_primed)
        {
            // The first hold the server sends for this hull: taken as it is, never animated.
            _primed = true;
            hold[..count].CopyTo(_hold);
            _count = count;
            Array.Clear(_motion, 0, Max);
            return;
        }

        var oldCount = _count;
        _hold.AsSpan(0, oldCount).CopyTo(_old);
        _motion.AsSpan(0, oldCount).CopyTo(_oldMotion);
        hold[..count].CopyTo(_hold);
        _count = count;
        Array.Clear(_taken, 0, oldCount);
        Array.Clear(_motion, 0, count);
        var matched = _matched.AsSpan(0, count);
        matched.Clear();
        // The same container in the same place keeps whatever it was doing.
        for (var i = 0; i < count; i++)
            for (var o = 0; o < oldCount; o++)
                if (!_taken[o] && Same(_old[o], _hold[i]) && _old[o].At == _hold[i].At)
                {
                    _taken[o] = matched[i] = true;
                    _motion[i] = _oldMotion[o];
                    break;
                }
        // The same container elsewhere in its rack slides there from where it is drawn.
        for (var i = 0; i < count; i++)
        {
            if (matched[i]) continue;
            for (var o = 0; o < oldCount; o++)
            {
                if (_taken[o] || !Same(_old[o], _hold[i])) continue;
                _taken[o] = matched[i] = true;
                Drawn(o, design, catalog, out var from, out var turn);
                Start(i, design, catalog, from, turn);
                break;
            }
        }
        // A new container comes from the body the own beam led in, else through its rack's opening.
        for (var i = 0; i < count; i++)
        {
            if (matched[i] || !CargoSlots.Valid(design, catalog, _hold[i])) continue;
            var beam = -1;
            for (var b = 0; b < _beamedCount; b++)
                if (!_beamed[b].Hidden && SameBody(_hold[i], _beamed[b].Size, _beamed[b].Resource, _beamed[b].Amount, _beamed[b].Hull)
                    && (beam < 0 || _beamed[b].Seen > _beamed[beam].Seen))
                    beam = b;
            if (beam >= 0)
            {
                _beamed[beam].Hidden = true;
                Start(i, design, catalog, _beamed[beam].Offset, _beamed[beam].Turn);
                continue;
            }
            var from = CargoSlots.Centre(design, catalog, _hold[i]);
            var axis = (int)design.Boxes[_hold[i].Rack].Facing >> 1;
            from[axis] = CargoEjection.Exit(design, _hold[i].Rack, CargoSlots.Half(catalog, _hold[i]), catalog);
            Start(i, design, catalog, from, Quaternion.Identity);
        }
        // A container gone waits for the body it became.
        for (var o = 0; o < oldCount; o++)
        {
            if (_taken[o] || !CargoSlots.Valid(design, catalog, _old[o])) continue;
            Drawn(o, design, catalog, out var from, out var turn);
            var at = _leavingCount < MaxLeaving ? _leavingCount++ : -1;
            if (at < 0) continue;
            _leaving[at] = new Leaving { Record = _old[o], From = from, Turn = turn, Removed = _now };
            for (var f = 0; f < _freshCount; f++)
                if (SameBody(_old[o], _fresh[f].Size, _fresh[f].Resource, _fresh[f].Amount, _fresh[f].Hull) && Contains(_seen, _fresh[f].Id)
                    && !Claimed(_fresh[f].Id))
                {
                    _leaving[at].Free = _fresh[f].Id;
                    _leaving[at].Start = _now;
                    _fresh[f].Hidden = false;
                    break;
                }
        }
        // Bodies held back for a record that is still in the hold stay hidden only while it is.
        for (var f = 0; f < _freshCount; f++)
            if (_fresh[f].Hidden && !HoldHasBody(_fresh[f].Size, _fresh[f].Resource, _fresh[f].Amount, _fresh[f].Hull)) _fresh[f].Hidden = false;
    }

    /// <summary>
    /// Record <paramref name="i"/> as drawn now: its centre in the hull's frame, its turn from the hull's axes and the
    /// colour of its lock lights. Returns its stage.
    /// </summary>
    public CargoStage Pose(int i, HullDesign design, CargoCatalog catalog, out Vector3 offset, out Quaternion turn, out Vector3 light)
    {
        offset = CargoSlots.Centre(design, catalog, _hold[i]);
        turn = Quaternion.Identity;
        var stage = Stage(i);
        ref readonly var m = ref _motion[i];
        if (m.Moves && stage is CargoStage.Transferring or CargoStage.Positioning)
        {
            var e = Ease(Progress(m));
            offset = Vector3.Lerp(m.From, offset, e);
            turn = Quaternion.Slerp(m.Turn, Quaternion.Identity, e);
        }
        light = Light(stage, m.Moves ? _now - (m.Start + m.Duration) : double.PositiveInfinity);
        return stage;
    }

    /// <summary>The stage of record <paramref name="i"/> (hold order) now.</summary>
    public CargoStage Stage(int i)
    {
        if ((uint)i >= (uint)_count) return CargoStage.Stored;
        ref readonly var m = ref _motion[i];
        if (!m.Moves) return CargoStage.Stored;
        var t = Progress(m);
        if (t < 1) return Ease(t) < m.Inside ? CargoStage.Transferring : CargoStage.Positioning;
        return _now - (m.Start + m.Duration) < look.LockSeconds ? CargoStage.Locked : CargoStage.Stored;
    }

    /// <summary>
    /// Leaving container <paramref name="k"/>: its record, where it left its slot from (hull frame), the loose body it
    /// hands over to (0 while that is not there yet) and how far along the slide out it is (0 in the slot, 1 at the body).
    /// </summary>
    public CargoStage Leave(int k, out HoldContainer record, out Vector3 from, out Quaternion turn, out uint free, out float slide, out Vector3 light)
    {
        ref readonly var leaving = ref _leaving[k];
        record = leaving.Record; from = leaving.From; turn = leaving.Turn; free = leaving.Free;
        slide = 0;
        if (free != 0 && look.LeaveSeconds > 0)
            slide = Ease((float)Math.Clamp((_now - leaving.Start - look.UnlockSeconds) / look.LeaveSeconds, 0, 1));
        light = Light(CargoStage.Unlocking, 0);
        return slide > 0 ? CargoStage.Transferring : CargoStage.Unlocking;
    }

    /// <summary>True when the loose body <paramref name="id"/> is not to be drawn: a container shows it.</summary>
    public bool Hides(uint id)
    {
        if (id == 0) return false;
        for (var b = 0; b < _beamedCount; b++) if (_beamed[b].Hidden && _beamed[b].Id == id) return true;
        for (var f = 0; f < _freshCount; f++) if (_fresh[f].Hidden && _fresh[f].Id == id) return true;
        return Claimed(id);
    }

    /// <summary>The stage of a loose body for the hull flown: on its beam to the staging point, on the glide in, or loose.</summary>
    public static CargoStage StageOf(in FreeContainer item, uint ownHull) =>
        item.TractorHull == 0 || item.TractorHull != ownHull ? CargoStage.Loose
        : item.TractorStage == 0 ? CargoStage.Approaching : CargoStage.Transferring;

    /// <summary>The colour of a container's lock lights in a stage, <paramref name="sinceLock"/> seconds after it reached its slot.</summary>
    public Vector3 Light(CargoStage stage, double sinceLock)
    {
        switch (stage)
        {
            case CargoStage.Approaching or CargoStage.Transferring or CargoStage.Positioning:
                var pulse = look.PulseHz <= 0 ? 1f : .55f + .45f * MathF.Cos((float)(_now * look.PulseHz * Math.Tau));
                return CargoMotionLook.Light(look.MovingLight, look.MovingLevel * pulse);
            case CargoStage.Locked:
                var k = (float)Math.Clamp(sinceLock / look.LockSeconds, 0, 1);
                return Vector3.Lerp(CargoMotionLook.Light(look.LockLight, look.LockLevel), CargoMotionLook.Light(look.StoredLight, look.StoredLevel), k * k);
            case CargoStage.Unlocking:
                return CargoMotionLook.Light(look.UnlockLight, look.UnlockLevel);
            default:
                return CargoMotionLook.Light(look.StoredLight, look.StoredLevel);
        }
    }

    private void Start(int i, HullDesign design, CargoCatalog catalog, Vector3 from, Quaternion turn)
    {
        var to = CargoSlots.Centre(design, catalog, _hold[i]);
        var distance = Vector3.Distance(from, to);
        var duration = Math.Clamp(distance / look.SlideSpeed, look.MinSeconds, look.MaxSeconds);
        // The share of the path after which the container is wholly inside its rack: before it, it is crossing the opening.
        var rack = _hold[i].Rack;
        var face = (int)design.Boxes[rack].Facing;
        var axis = face >> 1;
        var sign = (face & 1) == 0 ? 1f : -1f;
        var plane = design.BlockOffsets[rack][axis] + sign * design.BlockHalfExtents[rack][axis];
        var half = CargoSlots.Half(catalog, _hold[i])[axis];
        var outside = sign * (from[axis] - plane) + half;
        var inside = sign * (to[axis] - plane) + half;
        _motion[i] = new Motion
        {
            Start = _now,
            Duration = duration,
            From = from,
            Turn = turn,
            Moves = true,
            Inside = outside <= 0 || outside - inside <= 1e-6f ? 0 : Math.Clamp(outside / (outside - inside), 0, 1),
        };
    }

    // Where old record o is drawn now, in the hull's frame.
    private void Drawn(int o, HullDesign design, CargoCatalog catalog, out Vector3 offset, out Quaternion turn)
    {
        offset = CargoSlots.Valid(design, catalog, _old[o]) ? CargoSlots.Centre(design, catalog, _old[o]) : Vector3.Zero;
        turn = Quaternion.Identity;
        ref readonly var m = ref _oldMotion[o];
        if (!m.Moves) return;
        var t = Progress(m);
        if (t >= 1) return;
        var e = Ease(t);
        offset = Vector3.Lerp(m.From, offset, e);
        turn = Quaternion.Slerp(m.Turn, Quaternion.Identity, e);
    }

    private float Progress(in Motion m) => m.Duration <= 0 ? 1 : (float)Math.Clamp((_now - m.Start) / m.Duration, 0, 1);
    private static float Ease(float t) => t * t * (3 - 2 * t);

    private bool HoldHas(in FreeContainer item) => HoldHasBody(item.Size, item.Resource, item.Amount, item.Hull);

    private bool HoldHasBody(int size, int resource, double amount, float hull)
    {
        for (var i = 0; i < _count; i++) if (SameBody(_hold[i], size, resource, amount, hull)) return true;
        return false;
    }

    private bool Claimed(uint id)
    {
        for (var k = 0; k < _leavingCount; k++) if (_leaving[k].Free == id) return true;
        return false;
    }

    private static bool Same(in HoldContainer a, in HoldContainer b) =>
        a.Rack == b.Rack && a.Size == b.Size && a.Resource == b.Resource && a.Amount == b.Amount && a.Hull == b.Hull;

    private static bool Same(in HoldContainer a, in FreeContainer b) => SameBody(a, b.Size, b.Resource, b.Amount, b.Hull);

    private static bool SameBody(in HoldContainer a, int size, int resource, double amount, float hull) =>
        a.Size == size && a.Resource == resource && a.Amount == amount && a.Hull == hull;

    private static int Oldest(ReadOnlySpan<Beamed> items)
    {
        var at = 0;
        for (var i = 1; i < items.Length; i++) if (items[i].Seen < items[at].Seen) at = i;
        return at;
    }

    private int OldestFresh()
    {
        var at = 0;
        for (var i = 1; i < _freshCount; i++) if (_fresh[i].Seen < _fresh[at].Seen) at = i;
        return at;
    }

    private static void Insert(uint[] set, uint id)
    {
        var mask = set.Length - 1;
        var probe = (int)(id * 2654435761u) & mask;
        for (var n = 0; n < set.Length; n++, probe = (probe + 1) & mask)
        {
            if (set[probe] == id) return;
            if (set[probe] == 0) { set[probe] = id; return; }
        }
    }

    private static bool Contains(uint[] set, uint id)
    {
        var mask = set.Length - 1;
        var probe = (int)(id * 2654435761u) & mask;
        for (var n = 0; n < set.Length; n++, probe = (probe + 1) & mask)
        {
            if (set[probe] == id) return true;
            if (set[probe] == 0) return false;
        }
        return false;
    }
}
