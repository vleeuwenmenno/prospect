using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// Branching lightning bolts in lightning clouds, worked out on the CPU every frame and handed to the gas shader as
/// line segments (<see cref="FrameConstants.GasBoltHeads"/>, <see cref="FrameConstants.GasBoltSegments"/>).
///
/// Every bolt is seeded by the cloud, the burst cycle (<see cref="GasSensors.LightningCycle"/>, on the server's clock)
/// and its own number or place, so every client sees the same bolt at the same moment, and no two strokes are alike.
/// A burst has a few bolts spread through the cloud, which show from afar, and bolts in a lattice of cells around the
/// camera, so a viewer inside a large cloud sees strikes near them: a client looking at a cell sees what every other
/// client looking at it sees. A bolt grows along its path, holds and flickers, then fades with a short afterglow.
///
/// Takes no memory after construction: candidates, the fork queue and the segments are fixed arrays.
/// </summary>
internal sealed class LightningBolts
{
    /// <summary>The most bolts a frame draws; the nearest are kept.</summary>
    public const int MaxBolts = 6;

    /// <summary>The most segments one bolt has, branches included.</summary>
    public const int MaxSegments = 128;

    private const int MaxCandidates = 64;
    private const int MaxQueue = 32;

    private struct Candidate
    {
        public ulong Seed;
        public Vector3 Origin;     // Cloud-local metres.
        public Vector3 Direction;
        public Vector3 CloudCenter; // Camera-relative metres.
        public float Length;
        public float Age;
        public float Distance;
        public int Slot;
        public Vector3 HalfSize;
    }

    private struct Branch
    {
        public Vector3 Position;
        public Vector3 Heading;
        public float Length;
        public float Arrival;
        public int Depth;
    }

    private readonly Candidate[] _candidates = new Candidate[MaxCandidates];
    private readonly Branch[] _queue = new Branch[MaxQueue];
    private int _count;

    /// <summary>How many bolts the last <see cref="Write"/> drew.</summary>
    public int Drawn { get; private set; }

    /// <summary>Forgets the last frame's candidates.</summary>
    public void Begin() => _count = 0;

    /// <summary>
    /// The seconds into the burst a cloud is at, at fractional server tick <paramref name="tick"/>, and the burst's
    /// cycle; <paramref name="pin"/> (muted captures only) holds every cloud at that moment of cycle 1.
    /// </summary>
    public static float Clock(in GasCloud cloud, ExplorationRules rules, double tick, float? pin, out long cycle)
    {
        var whole = (long)Math.Floor(tick);
        cycle = GasSensors.LightningCycle(cloud, rules, whole, out var into);
        if (pin is { } pinned)
        {
            cycle = 1;
            return pinned;
        }
        return (float)((into + (tick - whole)) / Prospect.Engine.Time.TickLoop.SimulationHz);
    }

    /// <summary>How long a bolt shows, from its first growth to the end of its afterglow.</summary>
    public static float Lifetime(GasLightningLook look) => look.BoltGrow + look.BoltHold + 4 * look.BoltFade;

    /// <summary>
    /// Adds the bolts of one lightning cloud that are lit at this moment, and returns how many. <paramref name="center"/>
    /// is the cloud's centre relative to the camera; <paramref name="slot"/> the cloud's flash slot, which the shader
    /// matches. <paramref name="fill"/> is the envelope's share of the box (<c>GasCloudModule.Shape.X</c>): a strike
    /// starts inside it.
    /// </summary>
    public int Collect(GasLightningLook look, in GasCloud cloud, Vector3 center, float seconds, long cycle, float burst, int slot, float fill)
    {
        var added = 0;
        var life = Lifetime(look);
        var seed = Mix((ulong)BitConverter.SingleToInt32Bits(cloud.Seed), (ulong)cycle);
        var half = cloud.HalfSize;
        var window = burst * 0.8f;

        // Bolts in the cloud's outer shell, in every direction: what a viewer far off sees, some of them on the side
        // facing the viewer rather than behind the dense core.
        var envelope = half * fill;
        for (var j = 0; j < look.Bolts; j++)
        {
            var random = new Rng(Mix(seed, 0x100UL + (ulong)j));
            var age = seconds - random.Unit() * window;
            if (age < 0 || age > life) continue;
            var direction = Vector3.Normalize(new Vector3(random.Signed(), random.Signed(), random.Signed()) + new Vector3(0, 0, 0.001f));
            var origin = direction * (0.45f + 0.45f * random.Unit()) * envelope;
            added += Offer(random, origin, age, look, center, slot, half);
        }

        // Strikes in a lattice of cells around the camera, or, from outside, around the part of the envelope nearest
        // it: a viewer in or near a large cloud sees lightning close by. If none of those cells strikes this burst, the
        // one that came nearest does, so a burst never passes unseen. Every client near the same cells sees the same.
        var camera = -center;
        var cell = look.BoltCell;
        var reach = (camera / envelope).Length();
        var anchor = reach > 0.9f ? camera * (0.9f / reach) : camera;
        if ((camera - anchor).Length() > look.BoltNear)
            return added;
        var home = new Vector3(MathF.Floor(anchor.X / cell + 0.5f), MathF.Floor(anchor.Y / cell + 0.5f), MathF.Floor(anchor.Z / cell + 0.5f));
        var struck = false;
        var best = 2f;
        var bestCell = Vector3.Zero;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var at = home + new Vector3(dx, dy, dz);
                        if (pass == 1 && at != bestCell) continue;
                        var random = new Rng(Mix(seed, CellKey(at)));
                        var roll = random.Unit();
                        var origin = (at + new Vector3(random.Signed(), random.Signed(), random.Signed()) * 0.45f) * cell;
                        // Lumps swell the body past the smooth envelope, so strikes may start a little outside it.
                        var e = origin / (envelope * 1.15f);
                        if (e.LengthSquared() > 1) continue;
                        if (pass == 0)
                        {
                            if (roll < best) { best = roll; bestCell = at; }
                            if (roll >= look.BoltCellChance) continue;
                            struck = true;
                        }
                        var age = seconds - random.Unit() * window;
                        if (age < 0 || age > life) continue;
                        added += Offer(random, origin, age, look, center, slot, half);
                    }
            if (struck || best > 1) break;
        }
        return added;
    }

    private int Offer(Rng random, Vector3 origin, float age, GasLightningLook look, Vector3 center, int slot, Vector3 half)
    {
        if (_count == MaxCandidates) return 0;
        var direction = Vector3.Normalize(new Vector3(random.Signed(), random.Signed(), random.Signed()) + new Vector3(0, 0.001f, 0));
        var length = look.BoltLengthMin + (look.BoltLengthMax - look.BoltLengthMin) * random.Unit();
        _candidates[_count++] = new Candidate
        {
            Seed = random.Next(),
            Origin = origin,
            Direction = direction,
            CloudCenter = center,
            Length = length,
            Age = age,
            Distance = (center + origin + direction * (length * 0.5f)).Length(),
            Slot = slot,
            HalfSize = half,
        };
        return 1;
    }

    /// <summary>
    /// Draws the nearest candidates, at most <see cref="MaxBolts"/>: their segments, camera-relative, and per bolt its
    /// bounds, growth front, light and cloud slot.
    /// </summary>
    public void Write(GasLightningLook look, ref FrameConstants constants)
    {
        Drawn = 0;
        var segments = 0;
        while (Drawn < MaxBolts)
        {
            var pick = -1;
            for (var i = 0; i < _count; i++)
                if (_candidates[i].Length > 0 && (pick < 0 || _candidates[i].Distance < _candidates[pick].Distance)) pick = i;
            if (pick < 0) break;
            ref var bolt = ref _candidates[pick];
            var first = segments;
            var reach = Generate(bolt, look, ref constants, ref segments, out var low, out var high);
            bolt.Length = 0;
            if (segments == first) continue;

            var age = bolt.Age;
            var growth = look.BoltGrow;
            // The leader runs the main channel's length in the growth time and keeps going, so branches finish too.
            var front = age / growth * reach;
            float main, branch;
            if (age < growth)
            {
                main = branch = 0.3f;
            }
            else
            {
                var since = age - growth;
                var flicker = 1f - look.BoltFlicker * 0.5f * (1f - MathF.Cos(since * (60f + 40f * (bolt.Seed % 7) / 7f)));
                var stroke = since < look.BoltHold ? flicker : MathF.Exp(-1.0986f * (since - look.BoltHold) / look.BoltFade) * flicker;
                main = MathF.Max(stroke, look.BoltAfterglow * MathF.Exp(-since / (3 * look.BoltFade)));
                branch = stroke * MathF.Exp(-1.0986f * since / (look.BoltHold + look.BoltFade));
            }
            var middle = (low + high) * 0.5f;
            constants.GasBoltHeads[3 * Drawn] = new float4(middle.X, middle.Y, middle.Z, (high - low).Length() * 0.5f);
            constants.GasBoltHeads[3 * Drawn + 1] = new float4(first, segments - first, front, bolt.Slot);
            constants.GasBoltHeads[3 * Drawn + 2] = new float4(main * look.BoltBrightness, branch * look.BoltBrightness, main * look.BoltLight, 0);
            Drawn++;
        }
    }

    /// <summary>
    /// Writes one bolt's segments, camera-relative: a jagged main channel and forks off it and off forks, each segment
    /// with the path length from the root to its start (when the leader reaches it) and its fork depth (0 the main
    /// channel). Returns the main channel's length.
    /// </summary>
    private float Generate(in Candidate bolt, GasLightningLook look, ref FrameConstants constants, ref int segments, out Vector3 low, out Vector3 high)
    {
        var random = new Rng(bolt.Seed);
        var limit = Math.Min(segments + MaxSegments, MaxBolts * MaxSegments);
        var start = bolt.CloudCenter + bolt.Origin;
        low = high = start;
        // Forks are drawn breadth first, in the order they leave their parent: every fork off the main channel comes
        // before any fork off a fork, so the segment budget never goes to one deep fork while the others are left out.
        var head = 0;
        var tail = 0;
        _queue[tail++] = new Branch { Position = start, Heading = bolt.Direction, Length = bolt.Length, Arrival = 0, Depth = 0 };
        var mainLength = 0f;
        // The main channel takes at most two fifths of the segments, so there is room for forks however long it is.
        var step = MathF.Max(look.BoltSegment, bolt.Length / (MaxSegments * 2 / 5));
        var bounds = new Vector3(0.97f) * bolt.HalfSize;
        while (head < tail && segments < limit)
        {
            var branch = _queue[head++];
            var position = branch.Position;
            var current = branch.Heading;
            var travelled = 0f;
            var arrival = branch.Arrival;
            var length = step * (branch.Depth == 0 ? 1f : 1.2f);
            while (travelled < branch.Length && segments < limit)
            {
                // Each step turns at random and is pulled back toward the branch's heading: jagged, not a random walk.
                var kink = new Vector3(random.Signed(), random.Signed(), random.Signed());
                current = Vector3.Normalize(current + kink * look.BoltJagged + (branch.Heading - current) * 0.4f);
                var reach = length * (0.6f + 0.8f * random.Unit());
                var next = position + current * reach;
                // Keep a bolt inside its cloud's box.
                var local = Vector3.Clamp(next - bolt.CloudCenter, -bounds, bounds);
                next = bolt.CloudCenter + local;
                constants.GasBoltSegments[2 * segments] = new float4(position.X, position.Y, position.Z, arrival);
                constants.GasBoltSegments[2 * segments + 1] = new float4(next.X, next.Y, next.Z, branch.Depth);
                segments++;
                low = Vector3.Min(low, next);
                high = Vector3.Max(high, next);
                arrival += reach;
                travelled += reach;
                position = next;
                // Forks, and forks off forks, to a few levels; a fork takes part of what is left of its parent.
                var chance = look.BoltBranch * MathF.Pow(0.7f, branch.Depth);
                if (branch.Depth < look.BoltDepth && tail < MaxQueue && random.Unit() < chance)
                {
                    var side = new Vector3(random.Signed(), random.Signed(), random.Signed());
                    var heading = Vector3.Normalize(current + side * 1.2f + new Vector3(0, 0.0001f, 0));
                    var left = MathF.Max(branch.Length - travelled, branch.Length * 0.3f);
                    _queue[tail++] = new Branch { Position = position, Heading = heading, Length = left * (0.3f + 0.4f * random.Unit()), Arrival = arrival, Depth = branch.Depth + 1 };
                }
            }
            if (branch.Depth == 0) mainLength = arrival;
        }
        return MathF.Max(mainLength, 1f);
    }

    private static ulong CellKey(Vector3 cell) =>
        Mix(Mix(0x200UL + (ulong)(long)cell.X, (ulong)(long)cell.Y), (ulong)(long)cell.Z);

    private static ulong Mix(ulong a, ulong b)
    {
        var h = a * 0x9E3779B97F4A7C15UL ^ (b + 0x632BE59BD9B4E019UL) * 0xBF58476D1CE4E5B9UL;
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27; h *= 0x94D049BB133111EBUL; h ^= h >> 31;
        return h;
    }

    /// <summary>A small seeded generator: the same seed gives the same bolt on every client.</summary>
    private struct Rng(ulong state)
    {
        private ulong _state = state | 1;

        public ulong Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return _state;
        }

        public float Unit() => (Next() >> 40) / (float)(1UL << 24);

        public float Signed() => Unit() * 2f - 1f;
    }
}
