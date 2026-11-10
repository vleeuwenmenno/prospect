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

    /// <summary>
    /// Advances a hull one tick. With an order in force the autopilot flies and the pilot's input is ignored; the
    /// order clears itself on arrival. The server and the predicting client both call exactly this. Returns true
    /// if a collision destroyed the hull.
    /// </summary>
    public static bool Step(
        ref HullState state,
        ref HullOrder order,
        in FlightInput pilot,
        in FlightParams flight,
        ref HullBody body,
        RockField? rocks = null,
        double tick = 0,
        RockImpulses? impulses = null
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
        return rocks is not null && Collide(ref state, flight, ref body, rocks, tick, impulses);
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

                        // Closing speed at the point of contact, including the hull's spin.
                        var toHullFrame = Quaternion.Conjugate(state.Orientation);
                        var spin = Vector3.Transform(state.AngularVelocity, state.Orientation);
                        var closing = -Vector3.Dot(state.Velocity + Vector3.Cross(spin, contact) - rockVelocity, normal);
                        if (closing <= 0)
                            continue;

                        // One impulse along the contact normal, shared by inverse mass, with the hull free to turn.
                        var rockMass = rocks.Mass(rock.Radius);
                        var lever = Vector3.Cross(Vector3.Transform(contact, toHullFrame), Vector3.Transform(normal, toHullFrame));
                        var turning = Vector3.Dot(lever / flight.Inertia, lever);
                        var impulse = closing * (1f + flight.CollisionBounce) / (1f / flight.Mass + 1f / rockMass + turning);
                        state.Velocity += normal * (impulse / flight.Mass);
                        state.AngularVelocity += lever * impulse / flight.Inertia;
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

                        var excess = closing - flight.CollisionSafeSpeed;
                        if (excess > 0 && Damage(ref state, flight, ref body, touching, touchCount, excess * excess * flight.Mass * flight.CollisionDamagePerTonneSpeedSquared))
                            return true;
                    }
                }

        return false;
    }

    /// <summary>
    /// Applies impact damage: the shield takes what it can, the rest is split between the blocks that touched.
    /// A block at zero is gone. Returns true if the hull is destroyed: its core is gone or nothing is left.
    /// </summary>
    private static bool Damage(ref HullState state, in FlightParams flight, ref HullBody body, ulong touching, int touchCount, float amount)
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
