using System.Numerics;
using Prospect.Engine.IO;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>The data both the server and the client need to run the simulation, loaded from <c>content/</c>.</summary>
public sealed class GameContent
{
    private GameContent(Galaxy galaxy, BlockCatalog blocks, HullDesign[] designs, int pod, int drone, int[] startingFleet)
    {
        DroneDesign = drone;
        Galaxy = galaxy;
        Blocks = blocks;
        Designs = designs;
        PodDesign = pod;
        StartingFleet = startingFleet;
    }

    /// <summary>Every hull design, indexed by the design number hulls carry on the wire.</summary>
    public HullDesign[] Designs { get; }

    /// <summary>Index of the command pod's design.</summary>
    public int PodDesign { get; }

    /// <summary>Index of the design of the small craft a player flies after leaving a hull.</summary>
    public int DroneDesign { get; }

    /// <summary>Designs of the hulls a new player is given besides the pod.</summary>
    public int[] StartingFleet { get; }

    public BlockCatalog Blocks { get; }

    /// <summary>The galaxy's rules. Apply a server's seed with <see cref="Galaxy.WithSeed"/> to get its galaxy.</summary>
    public Galaxy Galaxy { get; }

    /// <summary>The command pod every player starts in.</summary>
    public HullDesign Pod => Designs[PodDesign];

    public static GameContent Load()
    {
        var blocks = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(AppPaths.Content("ships", "fleet.json")));
        var root = document.RootElement;
        var ids = new List<string>();
        var designs = new List<HullDesign>();
        foreach (var id in root.GetProperty("designs"u8).EnumerateArray())
        {
            ids.Add(id.GetString()!);
            designs.Add(HullDesign.Load(AppPaths.Content("ships", ids[^1] + ".json"), blocks));
        }

        int Index(string id) =>
            ids.IndexOf(id) is >= 0 and var index ? index : throw new InvalidDataException($"fleet.json: unknown design \"{id}\"");

        var fleet = new List<int>();
        foreach (var id in root.GetProperty("startingFleet"u8).EnumerateArray())
            fleet.Add(Index(id.GetString()!));
        if (designs.Count is 0 or > byte.MaxValue)
            throw new InvalidDataException("fleet.json: needs between 1 and 255 designs");

        return new GameContent(
            Galaxy.Load(AppPaths.Content("galaxy", "galaxy.json")),
            blocks,
            [.. designs],
            Index(root.GetProperty("pod"u8).GetString()!),
            Index(root.GetProperty("drone"u8).GetString()!),
            [.. fleet]
        );
    }
}

/// <summary>What a hull is made of right now: its design and which of the design's blocks it still has.</summary>
public ref struct HullBody
{
    public HullDesign Design;

    /// <summary>One bit per block of the design; a cleared bit is a destroyed block.</summary>
    public ulong Alive;

    /// <summary>
    /// Remaining hull points per block. Only the server has these; a predicting client passes none and leaves
    /// block damage to the server.
    /// </summary>
    public Span<float> BlockHp;
}

/// <summary>One simulation step of a hull under its pilot or its standing order.</summary>
public static class HullSim
{
    public const float Dt = (float)TickLoop.SimulationDt;

    /// <summary>The most places along one tick's flight at which a hull is tested against another.</summary>
    private const int MaxStrides = 32;

    /// <summary>
    /// Advances a hull one tick. With an order in force the autopilot flies and the pilot's input is ignored; the
    /// order clears itself on arrival. The server and the predicting client both call exactly this. Returns true
    /// if a collision destroyed the hull. With <paramref name="hulls"/> given the hull also runs into the hulls
    /// stored there.
    /// </summary>
    public static bool Step(
        ref HullState state,
        ref HullOrder order,
        in FlightInput pilot,
        in FlightParams flight,
        ref HullBody body,
        RockField? rocks = null,
        double tick = 0,
        RockImpulses? impulses = null,
        HullField? hulls = null
    )
    {
        var input = pilot;
        if (order.Kind == OrderKind.MoveTo)
        {
            input = Autopilot.Steer(state, order, flight, Dt, out var arrived);
            if (arrived)
                order.Kind = OrderKind.None;
        }

        if (Jump(ref state, ref order, ref input, flight))
            return false;

        FlightModel.Step(ref state, input, flight, Dt);

        // How far the hull came this tick, for finding what it would otherwise have flown straight through.
        var travel = state.Velocity * Dt;
        if (rocks is not null && Collide(ref state, flight, ref body, rocks, tick, impulses))
            return true;
        return hulls is not null && Collide(ref state, flight, ref body, hulls, travel);
    }

    /// <summary>
    /// Runs a faster-than-light jump. The hull brakes and turns its nose to the destination; the drive then
    /// charges from the hull's power, as fast as the drive can take it and the battery and generator can give
    /// it; once charged the hull is gone for the length of the tunnel, and then it is at its destination.
    /// Returns true while the hull is in the tunnel, when it neither flies nor collides. A hull without a drive
    /// drops the order.
    /// </summary>
    private static bool Jump(ref HullState state, ref HullOrder order, ref FlightInput input, in FlightParams flight)
    {
        if (order.Kind == OrderKind.Jump && state.Jump == JumpPhase.None)
        {
            if (flight.Ftl == FtlKind.None || flight.FtlPower <= 0)
            {
                order.Kind = OrderKind.None;
            }
            else
            {
                state.Jump = JumpPhase.Align;
                state.JumpTimer = 0;
            }
        }
        else if (order.Kind != OrderKind.Jump && state.Jump is JumpPhase.Align or JumpPhase.Charge)
        {
            // The order was withdrawn before the drive fired; whatever it had taken in is lost.
            state.Jump = JumpPhase.None;
            state.JumpTimer = 0;
        }

        switch (state.Jump)
        {
            case JumpPhase.Align:
            case JumpPhase.Charge:
                {
                    // Hold still and keep the nose on the destination for as long as the drive is working.
                    input = new FlightInput { Dampeners = true };
                    var toTarget = order.Target.RelativeTo(state.Position);
                    var distance = toTarget.Length();
                    var aligned = true;
                    if (distance > 1f)
                    {
                        var direction = toTarget / distance;
                        var local = System.Numerics.Vector3.Transform(direction, System.Numerics.Quaternion.Conjugate(state.Orientation));
                        input.Yaw = FlightInput.Quantize(MathF.Atan2(local.X, -local.Z) * 2.5f);
                        input.Pitch = FlightInput.Quantize(MathF.Atan2(local.Y, MathF.Sqrt(local.X * local.X + local.Z * local.Z)) * 2.5f);
                        aligned = -local.Z >= flight.FtlAlignCosine;
                    }

                    if (state.Jump == JumpPhase.Align)
                    {
                        state.JumpTimer += Dt;
                        if (!aligned)
                            return false;
                        state.Jump = JumpPhase.Charge;
                        state.JumpTimer = 0;
                    }

                    // The drive takes what the battery holds, up to its own limit. With the battery flat that is
                    // whatever the generator puts back each tick, so a starved hull charges slowly but still charges.
                    var taken = Math.Min(flight.FtlPower * Dt, state.Energy);
                    state.Energy -= taken;
                    state.JumpTimer += taken;
                    if (state.JumpTimer < flight.FtlEnergy)
                        return false;

                    state.Position = order.Target;
                    state.Velocity = System.Numerics.Vector3.Zero;
                    state.AngularVelocity = System.Numerics.Vector3.Zero;
                    state.Jump = JumpPhase.Tunnel;
                    state.JumpTimer = 0;
                    return true;
                }

            case JumpPhase.Tunnel:
                state.JumpTimer += Dt;
                if (state.JumpTimer >= flight.FtlTunnelSeconds)
                {
                    state.Jump = JumpPhase.Arrive;
                    state.JumpTimer = 0;
                    order.Kind = OrderKind.None;
                }

                return true;

            case JumpPhase.Arrive:
                state.JumpTimer += Dt;
                if (state.JumpTimer >= flight.FtlArriveSeconds)
                    state.Jump = JumpPhase.None;
                return false;

            default:
                return false;
        }
    }

    /// <summary>Steps a hull with nothing to run into.</summary>
    public static void Step(ref HullState state, ref HullOrder order, in FlightInput pilot, in FlightParams flight)
    {
        var body = default(HullBody);
        Step(ref state, ref order, pilot, flight, ref body);
    }

    /// <summary>
    /// Resolves the hull running into asteroids, block by block against each asteroid's real surface. The impact
    /// pushes hull and asteroid apart by their share of the momentum and turns the hull if it lands off-centre.
    /// Damage grows with the square of the closing speed and lands on the blocks that touched, after the shield.
    /// Returns true if the hull was destroyed. Changes to the asteroids' motion are written to
    /// <paramref name="impulses"/> when given; a predicting client passes none and waits to be told.
    /// </summary>
    private static bool Collide(ref HullState state, in FlightParams flight, ref HullBody body, RockField rocks, double tick, RockImpulses? impulses)
    {
        var galaxy = rocks.Galaxy;
        var design = body.Design;
        var half = design.BlockSize * 0.5f;
        var reach = new Double3(flight.Radius, flight.Radius, flight.Radius);
        var low = galaxy.SectorOf(state.Position - reach);
        var high = galaxy.SectorOf(state.Position + reach);

        // A hull spans at most two sectors along an axis. Anything else means its position is not a number,
        // and walking that range would never end.
        if (high.X - low.X is < 0 or > 1 || high.Y - low.Y is < 0 or > 1 || high.Z - low.Z is < 0 or > 1)
            return false;

        // Asteroids are generated wholly inside their sector, so only the sectors the hull itself touches matter.
        for (var x = low.X; x <= high.X; x++)
            for (var y = low.Y; y <= high.Y; y++)
                for (var z = low.Z; z <= high.Z; z++)
                {
                    var coord = new SectorCoord(x, y, z);
                    var content = rocks.Sector(coord);
                    var moved = rocks.MovedIn(coord);
                    var local = state.Position.RelativeTo(galaxy.CenterOf(coord));
                    for (var i = 0; i < content.AsteroidCount; i++)
                    {
                        ref var rock = ref content.Asteroids[i];
                        var rockOffset = Vector3.Zero;
                        var rockVelocity = Vector3.Zero;
                        if (moved is not null && moved[i] > 0)
                            rocks.DeltaAt(moved[i] - 1).Evaluate(tick, galaxy.DriftDecay, out rockOffset, out rockVelocity);

                        // Broad phase: the hull's sphere against the sphere the asteroid fits in.
                        var toHull = local - (rock.LocalPosition + rockOffset);
                        var bound = rock.Radius + flight.Radius;
                        if (toHull.LengthSquared() >= bound * bound)
                            continue;

                        // Narrow phase: every remaining block against the asteroid's surface in the block's direction.
                        var toRockFrame = Quaternion.Conjugate(rock.Rotation);
                        var deepest = 0f;
                        var normalSum = Vector3.Zero;
                        var armSum = Vector3.Zero;
                        var touching = 0UL;
                        var touchCount = 0;
                        for (var b = 0; b < design.BlockOffsets.Length; b++)
                        {
                            if ((body.Alive & 1UL << b) == 0)
                                continue;
                            var arm = Vector3.Transform(design.BlockOffsets[b], state.Orientation);
                            var fromRock = toHull + arm;
                            var distance = fromRock.Length();
                            if (distance >= rock.Radius + half || distance < 1e-3f)
                                continue;

                            var direction = fromRock / distance;
                            var surface = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(direction, toRockFrame));
                            var depth = surface + half - distance;
                            if (depth <= 0)
                                continue;

                            deepest = Math.Max(deepest, depth);
                            normalSum += direction;
                            armSum += arm;
                            touching |= 1UL << b;
                            touchCount++;
                        }

                        if (touchCount == 0)
                            continue;

                        var normal = normalSum.LengthSquared() > 1e-6f ? Vector3.Normalize(normalSum) : Vector3.UnitY;
                        var contact = armSum / touchCount;
                        state.Position += normal * deepest;
                        local += normal * deepest;

                        // One impulse along the contact normal; the asteroid takes its share without turning.
                        var rockMass = rocks.Mass(rock.Radius);
                        var impulse = Bounce(ref state, flight, contact, normal, rockVelocity, 1f / rockMass, 0f, out var closing);
                        if (impulse <= 0)
                            continue;

                        if (impulses is not null && impulses.Count < impulses.Items.Length)
                        {
                            impulses.Items[impulses.Count++] = new RockDelta
                            {
                                Rock = new RockRef(coord, i),
                                Offset = rockOffset,
                                Velocity = rockVelocity - normal * (impulse / rockMass),
                                StartTick = (long)tick,
                            };
                        }

                        var damage = ImpactDamage(closing, flight.Mass, flight);
                        if (damage > 0 && Damage(ref state, flight, ref body, touching, touchCount, damage))
                            return true;
                    }
                }

        return false;
    }

    /// <summary>
    /// Resolves the hull running into other hulls, block against block, each block the same ball the asteroid
    /// contact takes it for. The rules are those of an asteroid impact: one impulse shared by mass and inertia,
    /// the hull pushed back out, and damage on the blocks that touched. The other hull's share of the impulse and
    /// its damage are written to <paramref name="field"/> when it collects them; a predicting client's does not,
    /// and waits to be told. Returns true if the hull was destroyed.
    /// </summary>
    private static bool Collide(ref HullState state, in FlightParams flight, ref HullBody body, HullField field, Vector3 travel)
    {
        var store = field.Hulls;
        var design = body.Design;
        var half = design.BlockSize * 0.5f;
        Span<Vector3> arms = stackalloc Vector3[HullDesign.MaxBlocks];

        for (var j = 0; j < store.Slots; j++)
        {
            if (j == field.Self || !store.IsAliveAt(j))
                continue;

            // With nowhere left to write the other hull's share, the rest wait for the next step rather than
            // take an impulse nobody answers.
            if (field.IsFull)
                break;

            // A hull in the tunnel is not here, whatever its position says.
            ref var other = ref store.State(j);
            if (other.Jump == JumpPhase.Tunnel)
                continue;

            // Hulls step one after another, and two of them must be compared at the same moment whichever goes
            // first. This hull is at the end of its tick. One that has stepped this round is at the end of its
            // own, and how far it came is on record. One that has not is met where its tick will end, as near
            // as its velocity tells.
            var stepped = field.HasStepped(j);
            var otherTravel = stepped ? store.Info(j).Flown : other.Velocity * Dt;
            var apart = state.Position.RelativeTo(other.Position);
            if (!stepped)
                apart -= otherTravel;

            // Both flew during the tick, so the path to search is the one between them: two hulls that keep
            // station have none, however fast they go. In a further step of the same round the other has
            // had its tick and stands where it ended.
            var path = field.IsFirstStep ? travel - otherTravel : travel;
            var pathLength = path.Length();

            // Broad phase: the two spheres, with room for the distance flown this tick. Written so that a
            // position that is not a number fails it.
            ref var otherFlight = ref store.Flight(j);
            var bound = flight.Radius + otherFlight.Radius + pathLength;
            if (!(apart.LengthSquared() < bound * bound))
                continue;

            ref var otherInfo = ref store.Info(j);
            var otherDesign = field.Designs[otherInfo.Design];
            var theirs = otherInfo.Blocks & otherDesign.FullMask;
            var otherHalf = otherDesign.BlockSize * 0.5f;
            var mine = body.Alive & design.FullMask;

            // The narrow phase runs in the other hull's frame, where its blocks are where its design puts them.
            var toOtherFrame = Quaternion.Conjugate(other.Orientation);
            for (var bits = mine; bits != 0; bits &= bits - 1)
            {
                var b = BitOperations.TrailingZeroCount(bits);
                arms[b] = Vector3.Transform(Vector3.Transform(design.BlockOffsets[b], state.Orientation), toOtherFrame);
            }

            // A fast hull covers more than a block in a tick. Walk the path in strides too short to step over
            // a block, and stop at the first place it strikes.
            var stride = (half + otherHalf) * 0.5f;
            var strides = pathLength > stride ? Math.Min(MaxStrides, (int)MathF.Ceiling(pathLength / stride)) : 1;
            var pushed = Vector3.Zero;
            for (var k = 1; k <= strides; k++)
            {
                var remaining = 1f - (float)k / strides;
                var back = path * remaining;
                var centre = apart - back + pushed;
                var centreInFrame = Vector3.Transform(centre, toOtherFrame);
                if (!Touch(arms, mine, centreInFrame, half, otherDesign, theirs, otherHalf, otherFlight.Radius, out var touch))
                    continue;

                // Where the blocks' own directions mostly cancel the hulls are inside each other, and the way
                // out is along the line between their centres.
                var normal =
                    touch.NormalSum.LengthSquared() > 0.01f * touch.Pairs * touch.Pairs ? Vector3.Normalize(Vector3.Transform(touch.NormalSum, other.Orientation))
                    : centre.LengthSquared() > 1e-6f ? Vector3.Normalize(centre)
                    : Vector3.UnitY;
                var clearance = Clearance(arms, centreInFrame, Vector3.Transform(normal, toOtherFrame), otherDesign, half + otherHalf, touch);
                pushed += normal * clearance;
                centre += normal * clearance;

                // The point of contact as each hull sees it, and how freely the other gives way there.
                var otherArm = Vector3.Transform(touch.PointSum / touch.Pairs, other.Orientation);
                var otherSpin = Vector3.Transform(other.AngularVelocity, other.Orientation);
                var otherLever = Lever(other.Orientation, otherArm, normal);
                var otherInverseMass = 1f / otherFlight.Mass + Vector3.Dot(otherLever / otherFlight.Inertia, otherLever);

                // Hulls that only lean on each other do not bounce: at rest they would never settle.
                var impulse = Bounce(
                    ref state,
                    flight,
                    otherArm - centre,
                    normal,
                    other.Velocity + Vector3.Cross(otherSpin, otherArm),
                    otherInverseMass,
                    flight.CollisionSafeSpeed,
                    out var closing
                );
                if (impulse <= 0)
                    continue;

                // It struck here: the rest of this tick's flight is lost.
                var lost = travel * remaining;
                pushed -= lost;
                travel -= lost;

                // The mass that had to be stopped is the pair's reduced mass: all of a light hull that hits a
                // far heavier one, half of each when they match. Both take the same damage from it, so a drone
                // cannot wreck a freighter by flying into it. A hull just out of a jump does no harm and takes
                // none: nobody could have avoided where it came out.
                var harmless = state.Jump == JumpPhase.Arrive || other.Jump == JumpPhase.Arrive;
                var involved = flight.Mass * otherFlight.Mass / (flight.Mass + otherFlight.Mass);

                // The other hull is put where it was when they met. One that has flown its tick loses the
                // rest of it too, unless this is a further step and it stood still throughout. One that has
                // yet to fly will fly all of its tick at the velocity the impact leaves it with: up to the
                // impact it had the one it came with, and that difference is made up now, so it leaves from
                // where it was struck and not from a tick's flight short of there.
                var shift =
                    !stepped ? normal * (impulse / otherFlight.Mass * Dt * (field.IsFirstStep ? 1f - remaining : 1f))
                    : field.IsFirstStep ? -otherTravel * remaining
                    : Vector3.Zero;
                field.Add(
                    new HullImpulse
                    {
                        Hull = j,
                        Shift = shift,
                        Velocity = -normal * (impulse / otherFlight.Mass),
                        AngularVelocity = -otherLever * impulse / otherFlight.Inertia,
                        Touching = touch.Theirs,
                        Damage = harmless ? 0f : ImpactDamage(closing, involved, otherFlight),
                    }
                );

                var damage = harmless ? 0f : ImpactDamage(closing, involved, flight);
                if (damage > 0 && Damage(ref state, flight, ref body, touch.Mine, BitOperations.PopCount(touch.Mine), damage))
                {
                    state.Position += pushed;
                    return true;
                }

                break;
            }

            state.Position += pushed;
        }

        return false;
    }

    /// <summary>What the blocks of two hulls have to say about a contact between them, in the other hull's frame.</summary>
    private struct BlockTouch
    {
        /// <summary>The sum of the directions from the other hull's blocks to this hull's, one per touching pair.</summary>
        public Vector3 NormalSum;

        /// <summary>The sum of the points where the pairs meet.</summary>
        public Vector3 PointSum;

        /// <summary>The blocks of this hull and of the other that touch, one bit per block.</summary>
        public ulong Mine;
        public ulong Theirs;
        public int Pairs;
    }

    /// <summary>
    /// Finds the pairs of blocks of two hulls that are into each other. <paramref name="arms"/> are this hull's
    /// blocks from its centre and <paramref name="centre"/> is that centre, both in the other hull's frame.
    /// </summary>
    private static bool Touch(
        ReadOnlySpan<Vector3> arms,
        ulong mine,
        Vector3 centre,
        float half,
        HullDesign other,
        ulong theirs,
        float otherHalf,
        float otherRadius,
        out BlockTouch touch
    )
    {
        touch = default;
        var offsets = other.BlockOffsets;
        var reach = half + otherHalf;
        var bound = otherRadius + half;
        for (var bits = mine; bits != 0; bits &= bits - 1)
        {
            // A block outside the other hull's sphere touches none of its blocks.
            var a = BitOperations.TrailingZeroCount(bits);
            var block = centre + arms[a];
            if (block.LengthSquared() >= bound * bound)
                continue;

            for (var rest = theirs; rest != 0; rest &= rest - 1)
            {
                var b = BitOperations.TrailingZeroCount(rest);
                var between = block - offsets[b];
                var squared = between.LengthSquared();
                if (squared >= reach * reach)
                    continue;

                // Two blocks in the same place have no direction to part in; they still count as touching.
                var distance = MathF.Sqrt(squared);
                var depth = reach - distance;
                var point = offsets[b];
                if (distance > 1e-3f)
                {
                    var direction = between / distance;
                    touch.NormalSum += direction;
                    point += direction * (otherHalf - depth * 0.5f);
                }

                touch.PointSum += point;
                touch.Mine |= 1UL << a;
                touch.Theirs |= 1UL << b;
                touch.Pairs++;
            }
        }

        return touch.Pairs > 0;
    }

    /// <summary>
    /// How far the hull has to move along <paramref name="normal"/> for none of the touching pairs of blocks to
    /// be into each other any more. Everything is in the other hull's frame.
    /// </summary>
    private static float Clearance(ReadOnlySpan<Vector3> arms, Vector3 centre, Vector3 normal, HullDesign other, float reach, in BlockTouch touch)
    {
        var offsets = other.BlockOffsets;
        var clearance = 0f;
        for (var bits = touch.Mine; bits != 0; bits &= bits - 1)
        {
            var block = centre + arms[BitOperations.TrailingZeroCount(bits)];
            for (var rest = touch.Theirs; rest != 0; rest &= rest - 1)
            {
                var between = block - offsets[BitOperations.TrailingZeroCount(rest)];
                var inside = reach * reach - between.LengthSquared();
                if (inside <= 0)
                    continue;

                // The distance along the normal at which this pair is exactly their two radii apart.
                var along = Vector3.Dot(between, normal);
                clearance = Math.Max(clearance, MathF.Sqrt(along * along + inside) - along);
            }
        }

        return clearance;
    }

    /// <summary>
    /// One impulse along the contact normal between the hull and what it touched, shared by inverse mass with the
    /// hull free to turn. <paramref name="contact"/> is the point of contact from the hull's centre,
    /// <paramref name="otherVelocity"/> the other body's velocity there and <paramref name="otherInverseMass"/>
    /// how freely it gives way. A contact slower than <paramref name="restSpeed"/> does not bounce. Returns the
    /// impulse in tonne metres per second, or zero when the two are not closing.
    /// </summary>
    private static float Bounce(
        ref HullState state,
        in FlightParams flight,
        Vector3 contact,
        Vector3 normal,
        Vector3 otherVelocity,
        float otherInverseMass,
        float restSpeed,
        out float closing
    )
    {
        // Closing speed at the point of contact, including the hull's spin.
        var spin = Vector3.Transform(state.AngularVelocity, state.Orientation);
        closing = -Vector3.Dot(state.Velocity + Vector3.Cross(spin, contact) - otherVelocity, normal);
        if (closing <= 0)
            return 0f;

        var lever = Lever(state.Orientation, contact, normal);
        var turning = Vector3.Dot(lever / flight.Inertia, lever);
        var bounce = closing > restSpeed ? flight.CollisionBounce : 0f;
        var impulse = closing * (1f + bounce) / (1f / flight.Mass + otherInverseMass + turning);
        state.Velocity += normal * (impulse / flight.Mass);
        state.AngularVelocity += lever * impulse / flight.Inertia;
        return impulse;
    }

    /// <summary>
    /// The turning effect on a hull, about its own axes, of a unit push along <paramref name="normal"/> at
    /// <paramref name="arm"/> from its centre.
    /// </summary>
    private static Vector3 Lever(Quaternion orientation, Vector3 arm, Vector3 normal)
    {
        var toHullFrame = Quaternion.Conjugate(orientation);
        return Vector3.Cross(Vector3.Transform(arm, toHullFrame), Vector3.Transform(normal, toHullFrame));
    }

    /// <summary>
    /// The damage of an impact: none up to the safe speed, then growing with the square of the speed over it,
    /// for each tonne that had to be stopped.
    /// </summary>
    private static float ImpactDamage(float closing, float mass, in FlightParams flight)
    {
        var excess = closing - flight.CollisionSafeSpeed;
        return excess > 0 ? excess * excess * mass * flight.CollisionDamagePerTonneSpeedSquared : 0f;
    }

    /// <summary>
    /// Applies impact damage: the shield takes what it can, the rest is split between the blocks that touched.
    /// A block at zero is gone. Returns true if the hull is destroyed: its core is gone or nothing is left.
    /// </summary>
    internal static bool Damage(ref HullState state, in FlightParams flight, ref HullBody body, ulong touching, int touchCount, float amount)
    {
        state.ShieldCooldown = flight.ShieldDelay;
        var absorbed = Math.Min(state.Shield, amount);
        state.Shield -= absorbed;
        var remaining = amount - absorbed;
        if (remaining <= 0)
            return false;

        if (body.BlockHp.IsEmpty)
        {
            // Predicting: the server decides which blocks are lost; show the hull total falling meanwhile.
            state.Hull = Math.Max(0f, state.Hull - remaining);
            return false;
        }

        var share = remaining / touchCount;
        var total = 0f;
        for (var b = 0; b < body.Design.BlockOffsets.Length; b++)
        {
            if ((touching & 1UL << b) != 0)
            {
                body.BlockHp[b] = Math.Max(0f, body.BlockHp[b] - share);
                if (body.BlockHp[b] <= 0)
                    body.Alive &= ~(1UL << b);
            }

            total += body.BlockHp[b];
        }

        state.Hull = total;
        return total <= 0 || (body.Alive & body.Design.CoreMask) != body.Design.CoreMask;
    }
}
