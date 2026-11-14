using System.Numerics;
using Prospect.Engine.IO;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>The data both the server and the client need to run the simulation, loaded from <c>content/</c>.</summary>
public sealed class GameContent
{
    private GameContent(
        Galaxy galaxy,
        BlockCatalog blocks,
        WorldSettingCatalog settingCatalog,
        in WorldSettings settings,
        FlightRules rules,
        HullDesign[] designs,
        int pod,
        int drone,
        int[] startingFleet,
        SocialRules social,
        ContentIdentity identity,
        BlockTransferRules blockTransfers,
        EditorRules editor,
        ExplorationRules exploration
    )
    {
        Exploration = exploration;
        Editor = editor;
        BlockTransfers = blockTransfers;
        Identity = identity;
        Social = social;
        DroneDesign = drone;
        Galaxy = galaxy;
        Blocks = blocks;
        SettingCatalog = settingCatalog;
        Settings = settings;
        Rules = rules;
        Designs = designs;
        PodDesign = pod;
        StartingFleet = startingFleet;
    }

    /// <summary>
    /// Every hull design, indexed by the design number hulls carry on the wire, with its figures worked out
    /// under <see cref="Settings"/>.
    /// </summary>
    public HullDesign[] Designs { get; }

    /// <summary>Shared block recovery budgets.</summary>
    public BlockTransferRules BlockTransfers { get; }

    /// <summary>
    /// The ship editor's limits as this machine's files have them. A server enforces its own and tells a client
    /// what it refused and how long to wait, so they are not part of <see cref="Identity"/>.
    /// </summary>
    public EditorRules Editor { get; }
    public ExplorationRules Exploration { get; }

    /// <summary>
    /// Social limits as this machine's files have them. A server enforces its own; a client's only shape what
    /// its interface offers, so they are not part of <see cref="Identity"/>.
    /// </summary>
    public SocialRules Social { get; }

    /// <summary>
    /// What the shared content comes to: the block catalogue with its rules and limits, the world setting
    /// catalogue, galaxy rules, bulk transfer limits and the evaluator's version. Designs arrive separately. A
    /// server lets a client in only when theirs are the same. A world's settings are not part of it: they travel
    /// in the welcome, so <see cref="WithSettings"/> keeps it.
    /// </summary>
    public ContentIdentity Identity { get; }

    /// <summary>Every setting a world can have: what it is called, its range and its default.</summary>
    public WorldSettingCatalog SettingCatalog { get; }

    /// <summary>
    /// The world settings this content is evaluated under. Loaded content has the defaults; a server and each
    /// client that joins it get their own with <see cref="WithSettings"/>.
    /// </summary>
    public WorldSettings Settings { get; }

    /// <summary>The flight rules with <see cref="Settings"/> applied.</summary>
    public FlightRules Rules { get; }

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

    /// <summary>
    /// This content as a world with the given settings plays by it: the same blocks, designs and galaxy rules,
    /// with every figure that a setting touches worked out again. Nothing here is changed, so one process can
    /// hold several worlds: a server and its client in a local game, or one server after another. The settings
    /// are brought into range first, wherever they came from.
    /// </summary>
    public GameContent WithSettings(in WorldSettings settings)
    {
        var clean = SettingCatalog.Sanitize(settings);
        if (clean == Settings)
            return this;

        // Always from the rules as the files give them, so settings never stack on earlier settings.
        var rules = Blocks.Rules.Under(SettingCatalog, clean);
        var designs = new HullDesign[Designs.Length];
        for (var i = 0; i < designs.Length; i++)
            designs[i] = Designs[i].WithRules(rules);
        return new GameContent(Galaxy.Under(SettingCatalog, clean), Blocks, SettingCatalog, clean, rules, designs, PodDesign, DroneDesign, StartingFleet, Social, Identity, BlockTransfers, Editor, Exploration);
    }

    internal GameContent WithCatalogue(HullDesign[] designs, int pod, int drone, int[] fleet) =>
        new(Galaxy, Blocks, SettingCatalog, Settings, Rules, (HullDesign[])designs.Clone(), pod, drone,
            (int[])fleet.Clone(), Social, Identity, BlockTransfers, Editor, Exploration);

    /// <summary>
    /// This content with one more design at the end of the catalogue. Every design it had keeps its number and is
    /// the same object, so whatever holds a design or its number stays right. Nothing here is changed.
    /// </summary>
    internal GameContent WithDesign(HullDesign design)
    {
        if (Designs.Length >= byte.MaxValue) throw new InvalidOperationException("The design catalogue is full.");
        var designs = new HullDesign[Designs.Length + 1];
        Designs.CopyTo(designs, 0);
        designs[^1] = design;
        return new GameContent(Galaxy, Blocks, SettingCatalog, Settings, Rules, designs, PodDesign, DroneDesign, StartingFleet, Social, Identity, BlockTransfers, Editor, Exploration);
    }

    /// <summary>Loads the content shipped beside the executable.</summary>
    public static GameContent Load() => Load(AppPaths.Content());

    /// <summary>
    /// Loads the content in a directory laid out as <c>content/</c> is, and works out its
    /// <see cref="Identity"/> from the same files.
    /// </summary>
    public static GameContent Load(string directory)
    {
        // Every file the server and the client must agree on is loaded through this, which is what puts it in
        // the identity: a file the shared simulation reads cannot be added here and left out there. The order
        // of the calls is the order the files are hashed in.
        var identity = new ContentIdentityBuilder();
        string At(string folder, string file) => Path.Combine(directory, folder, file);
        byte[] Shared(string folder, string file) => identity.Add(folder + "/" + file, At(folder, file));

        var blocks = BlockCatalog.Parse(Shared("blocks", "blocks.json"), At("blocks", "blocks.json"));
        var settingCatalog = WorldSettingCatalog.Parse(Shared("rules", "settings.json"), At("rules", "settings.json"));
        var exploration = ExplorationRules.Parse(Shared("rules", "exploration.json"));
        var settings = settingCatalog.Defaults;
        var rules = blocks.Rules.Under(settingCatalog, settings);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(At("ships", "fleet.json")));
        var root = document.RootElement;
        var transfers = BlockTransferRules.Parse(Shared("net", "blocks.json"));
        long catalogueBytes = 0;
        var ids = new List<string>();
        var designs = new List<HullDesign>();
        foreach (var id in root.GetProperty("designs"u8).EnumerateArray())
        {
            if (designs.Count == 255) throw new InvalidDataException("Too many designs.");
            ids.Add(id.GetString()!);
            designs.Add(HullDesign.Parse(File.ReadAllBytes(At("ships", ids[^1] + ".json")), At("ships", ids[^1] + ".json"), blocks, rules));
            var size = DesignBytes.Encode(designs[^1]).Length;
            catalogueBytes += size;
            if (size > transfers.MaxBytes || !transfers.AllowsCatalogue(catalogueBytes))
                throw new InvalidDataException("Catalogue exceeds shared resource budgets.");
        }

        int Index(string id) =>
            ids.IndexOf(id) is >= 0 and var index ? index : throw new InvalidDataException($"fleet.json: unknown design \"{id}\"");

        var fleet = new List<int>();
        foreach (var id in root.GetProperty("startingFleet"u8).EnumerateArray())
            fleet.Add(Index(id.GetString()!));
        if (designs.Count is 0 or > byte.MaxValue)
            throw new InvalidDataException("fleet.json: needs between 1 and 255 designs");

        var galaxy = Galaxy.Parse(Shared("galaxy", "galaxy.json"), At("galaxy", "galaxy.json")).Under(settingCatalog, settings);

        // Not shared: the server enforces its own social limits and the wire's bounds are fixed in code, so a
        // client with other numbers shows another party size or chat counter and nothing in the simulation moves.
        var social = SocialRules.Load(Path.Combine(directory, "net", "social.json"));

        // Not shared either: only the server applies the editor's limits, and it tells a client what it decided.
        var editor = EditorRules.Load(Path.Combine(directory, "net", "editor.json"), transfers);

        foreach (var design in designs)
            if ((long)BlockSet.WordCount(design.BlockCount) * sizeof(ulong) > transfers.MaxBytes)
                throw new InvalidDataException("A design's block set exceeds the bulk transfer limit.");
        return new GameContent(
            galaxy,
            blocks,
            settingCatalog,
            settings,
            rules,
            [.. designs],
            Index(root.GetProperty("pod"u8).GetString()!),
            Index(root.GetProperty("drone"u8).GetString()!),
            [.. fleet],
            social,
            identity.Build(),
            transfers,
            editor,
            exploration
        );
    }
}

/// <summary>What a hull is made of right now: its design and which of the design's blocks it still has.</summary>
public ref struct HullBody
{
    public HullDesign Design;
    internal Span<VisualDamageState> Visual;
    internal ReadOnlySpan<VisualContact> Contacts;
    internal long ContactTick;

    /// <summary>
    /// The blocks the hull still has: a <see cref="BlockSet"/>. For a body that takes damage it is the set of
    /// <see cref="State"/> itself, so a block that is destroyed is gone from it at once.
    /// </summary>
    public ReadOnlySpan<ulong> Alive;

    /// <summary>
    /// Where the hull's block state is kept, for a hull that is decided here: its blocks have hit points there,
    /// and a block that runs out is taken through the store, which counts the loss and keeps the hull's sums and
    /// hit total in step. A predicting client passes none and leaves block damage to the server.
    /// </summary>
    public BlockStateStore? States;

    /// <summary>The hull's block state in <see cref="States"/>.</summary>
    public BlockStateHandle State;

    /// <summary>
    /// Working memory for contacts, large enough for the design. Needed by a step that can run into anything;
    /// a hull stepped through a <see cref="HullField"/> gets the field's.
    /// </summary>
    public ContactScratch? Scratch;

    /// <summary>Live branch counts borrowed from the block state, including on prediction.</summary>
    internal ReadOnlySpan<int> CollisionCounts;

    /// <summary>
    /// Set by <see cref="HullSim.Damage"/> when the body took damage, absorbed by its shield or not. A contact
    /// that only shows on the shield leaves it clear. Whoever steps the body reads it afterwards.
    /// </summary>
    public bool Damaged;
}

/// <summary>One simulation step of a hull under its pilot or its standing order.</summary>
public static class HullSim
{
    public const float Dt = (float)TickLoop.SimulationDt;


    /// <summary>
    /// How much further than the hull's own radius the search for nearby asteroids reaches, in metres: more than
    /// any contact pushes a hull back out in one tick.
    /// </summary>
    private const float ContactPad = 32f;

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
    /// Advances cosmetic event ages once at the start of a world tick, before any hull can be struck.
    /// Input replay owns its predicted ticks; physical steps may run zero or several times within a server tick.
    /// </summary>
    public static void AdvanceShieldClock(ref HullState state)
    {
        if (state.ShieldHit.AgeTicks < ShieldHit.MaxAgeTicks)
            state.ShieldHit.AgeTicks++;
        if (state.ShieldChangeTicks < ShieldHit.MaxAgeTicks)
            state.ShieldChangeTicks++;
    }

    /// <summary>Numerical duration ceiling: double elapsed time retains sub-tick precision up to this value.</summary>
    public const float MaxJumpTravelSeconds = 1099511627776f;

    /// <summary>Base transit plus Euclidean world distance over speed; NaN means numerically unsupported.</summary>
    public static float JumpTravelSeconds(Double3 departure, Double3 target, in FlightParams flight)
    {
        var speed = flight.FtlSpeed;
        var delta = target - departure;
        var scale = Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
        if (!double.IsFinite(scale) || !float.IsFinite(speed) || speed <= 0 ||
            !float.IsFinite(flight.FtlTunnelSeconds) || flight.FtlTunnelSeconds < 0)
            return float.NaN;
        var unit = scale > 0 ? new Double3(delta.X / scale, delta.Y / scale, delta.Z / scale) : Double3.Zero;
        var seconds = flight.FtlTunnelSeconds + scale / speed * unit.Length;
        return double.IsFinite(seconds) && seconds <= MaxJumpTravelSeconds ? (float)seconds : float.NaN;
    }

    /// <summary>Replaces an order, discarding unfinished jump work when its destination or kind changes.</summary>
    public static void SetOrder(ref HullState state, ref HullOrder current, in HullOrder next)
    {
        if ((current.Kind != next.Kind || !current.Target.Equals(next.Target)) && state.Jump is JumpPhase.Align or JumpPhase.Charge)
        {
            state.Jump = JumpPhase.None;
            state.JumpTimer = 0;
            state.JumpCalculation = 0;
            state.JumpTunnelSeconds = 0;
            state.JumpTunnelElapsed = 0;
        }
        current = next;
    }

    /// <summary>
    /// Runs a faster-than-light jump. The hull brakes and turns its nose to the destination; the drive then
    /// charges from the hull's power, as fast as the drive can take it and the battery and generator can give
    /// it. Calculation runs concurrently; once both finish the hull is gone for the length of the tunnel,
    /// and then it is at its destination.
    /// Returns true while the hull is in the tunnel, when it neither flies nor collides. A hull without a drive
    /// drops the order.
    /// </summary>
    private static bool Jump(ref HullState state, ref HullOrder order, ref FlightInput input, in FlightParams flight)
    {
        if (state.Jump is JumpPhase.Align or JumpPhase.Charge && (flight.Ftl == FtlKind.None || flight.FtlPower <= 0))
            order.Kind = OrderKind.None;
        if (order.Kind == OrderKind.Jump && state.Jump == JumpPhase.None)
        {
            if (flight.Ftl == FtlKind.None || flight.FtlPower <= 0)
            {
                order.Kind = OrderKind.None;
            }
            else
            {
                var duration = JumpTravelSeconds(state.Position, order.Target, flight);
                if (!float.IsFinite(duration))
                {
                    order = default;
                    return false;
                }
                state.Jump = JumpPhase.Align;
                state.JumpTimer = 0;
                state.JumpCalculation = 0;
                state.JumpTunnelElapsed = 0;
                state.JumpTunnelSeconds = duration;
            }
        }
        else if (order.Kind != OrderKind.Jump && state.Jump is JumpPhase.Align or JumpPhase.Charge)
        {
            // The order was withdrawn before the drive fired; whatever it had taken in is lost.
            state.Jump = JumpPhase.None;
            state.JumpTimer = 0;
            state.JumpCalculation = 0;
            state.JumpTunnelSeconds = 0;
            state.JumpTunnelElapsed = 0;
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
                    state.JumpCalculation = Math.Min(flight.FtlCalculateSeconds, state.JumpCalculation + Dt);
                    var taken = Math.Min(Math.Min(flight.FtlPower * Dt, state.Energy), Math.Max(0f, flight.FtlEnergy - state.JumpTimer));
                    state.Energy -= taken;
                    state.JumpTimer += taken;
                    if (state.JumpTimer < flight.FtlEnergy || state.JumpCalculation < flight.FtlCalculateSeconds)
                        return false;

                    state.Position = order.Target;
                    state.Velocity = System.Numerics.Vector3.Zero;
                    state.AngularVelocity = System.Numerics.Vector3.Zero;
                    state.Jump = JumpPhase.Tunnel;
                    state.JumpTimer = 0;
                    return true;
                }

            case JumpPhase.Tunnel:
                state.JumpTunnelElapsed += Dt;
                state.JumpTimer = (float)state.JumpTunnelElapsed;
                if (state.JumpTunnelElapsed >= state.JumpTunnelSeconds)
                {
                    state.Jump = JumpPhase.Arrive;
                    state.JumpTimer = 0;
                    order.Kind = OrderKind.None;
                }

                return true;

            case JumpPhase.Arrive:
                state.JumpTimer += Dt;
                if (state.JumpTimer >= flight.FtlArriveSeconds)
                {
                    state.Jump = JumpPhase.None;
                    state.JumpTunnelSeconds = 0;
                    state.JumpTunnelElapsed = 0;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>Steps a hull with nothing to run into.</summary>
    public static void Step(ref HullState state, ref HullOrder order, in FlightInput pilot, in FlightParams flight, long tick = 0)
    {
        var body = default(HullBody);
        Step(ref state, ref order, pilot, flight, ref body, tick: tick);
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
                    if (content.AsteroidCount == 0)
                        continue;

                    var moved = rocks.MovedIn(coord);
                    var local = state.Position.RelativeTo(galaxy.CenterOf(coord));

                    // The asteroids still where the seed put them, found through the sector's index rather than
                    // by looking at every rock of a field of thousands. A contact pushes the hull back out, so
                    // the search reaches a little further than the hull does now.
                    var near = content.Rocks.Near(local, flight.Radius + ContactPad);
                    while (near.Next(out var i))
                    {
                        if (moved is not null && moved.Entries[i] > 0)
                            continue;
                        if (Strike(ref state, flight, ref body, rocks, new RockRef(coord, i), content.Asteroids[i], default, default, tick, impulses, ref local))
                            return true;
                    }

                    if (moved is null)
                        continue;

                    // The asteroids that have been pushed are not where the index has them. There are few, and
                    // most are ruled out by how far each could ever have drifted, without working out where it is.
                    for (var m = 0; m < moved.Count; m++)
                    {
                        var i = moved.Indices[m];
                        if (i >= content.AsteroidCount)
                            continue;
                        var entry = moved.Entries[i] - 1;
                        ref readonly var rock = ref content.Asteroids[i];
                        var bound = rock.Radius + flight.Radius + ContactPad + rocks.ReachAt(entry);
                        if ((local - rock.LocalPosition).LengthSquared() >= bound * bound)
                            continue;
                        rocks.DeltaAt(entry).Evaluate(tick, galaxy.DriftDecay, out var rockOffset, out var rockVelocity);
                        if (Strike(ref state, flight, ref body, rocks, new RockRef(coord, i), rock, rockOffset, rockVelocity, tick, impulses, ref local))
                            return true;
                    }
                }

        return false;
    }

    /// <summary>
    /// Resolves the hull against one asteroid. <paramref name="local"/> is the hull's position relative to the
    /// asteroid's sector and moves with the hull when the contact pushes it out. Returns true if the hull was
    /// destroyed.
    /// </summary>
    private static bool Strike(
        ref HullState state,
        in FlightParams flight,
        ref HullBody body,
        RockField rocks,
        RockRef name,
        in Asteroid rock,
        Vector3 rockOffset,
        Vector3 rockVelocity,
        double tick,
        RockImpulses? impulses,
        ref Vector3 local
    )
    {
        // Broad phase: the hull's sphere against the sphere the asteroid fits in.
        var toHull = local - (rock.LocalPosition + rockOffset);
        var bound = rock.Radius + flight.Radius;
        if (toHull.LengthSquared() >= bound * bound)
            return false;

        // Narrow phase: every remaining block against the asteroid's surface in the block's direction.
        var design = body.Design;
        var deepest = 0f;
        var normalSum = Vector3.Zero;
        var armSum = Vector3.Zero;
        var shieldPointSum = Vector3.Zero;
        var touching = Scratch(body).Mine.AsSpan(0, body.Alive.Length);
        touching.Clear();
        var touchCount = 0;
        var rules = design.CollisionRules;
        var visits = rules.CollisionTreeVisits;
        var tests = rules.CollisionBoxTests;
        var cursor = 0;
        var rockInHull = Vector3.Transform(-toHull, Quaternion.Conjugate(state.Orientation));
        var reach = new Vector3(rock.Radius);
        while (tests > 0 && touchCount < rules.CollisionContacts && design.Collision.Next(ref cursor, rockInHull - reach, rockInHull + reach, body.Alive, ref visits, out var b, body.CollisionCounts))
        {
            tests--;
            var arm = Vector3.Transform(design.BlockOffsets[b], state.Orientation);
            if (!BoxCollision.Rock(toHull + arm, design.BlockHalfExtents[b], state.Orientation, rock, out var direction, out var depth, out var point, design.Boxes[b].Shape))
                continue;
            deepest = Math.Max(deepest, depth);
            normalSum += direction;
            armSum += arm;
            shieldPointSum += arm + point;
            Scratch(body).OwnContacts[touchCount] = new VisualContact
            {
                Block = b,
                Point = Vector3.Transform(arm + point, Quaternion.Conjugate(state.Orientation)),
                Normal = Vector3.Transform(-direction, Quaternion.Conjugate(state.Orientation))
            };
            BlockSet.Add(touching, b);
            touchCount++;
        }

        if (touchCount == 0)
            return false;

        var normal = normalSum.LengthSquared() > 1e-6f ? Vector3.Normalize(normalSum) : Vector3.UnitY;
        var contact = armSum / touchCount;
        state.Position += normal * deepest;
        local += normal * deepest;

        // Whether the asteroid can take its share is settled before anything is exchanged. Where its motion
        // is decided, it must be possible to write that motion down: there is a place left for it among this
        // tick's impulses, and the field can still take it, the motions already promised in this tick counted.
        // One that cannot move is struck as a body that does not give way, at the velocity it has, so the hull
        // gets the whole of an impact whose other half nobody would otherwise carry. A predicting client has
        // no say in it and assumes the asteroid moves; the server's snapshot corrects it if not.
        var listed = impulses is null || impulses.Count < impulses.Items.Length;
        var movable = listed && (impulses is null || rocks.CanMove(name, impulses.Items.AsSpan(0, impulses.Count)));

        // One impulse along the contact normal; the asteroid takes its share without turning.
        var rockMass = rocks.Mass(rock.Radius);
        var impulse = Bounce(ref state, flight, contact, normal, rockVelocity, movable ? 1f / rockMass : 0f, 0f, out var closing);
        if (impulse <= 0)
            return false;

        if (!movable)
        {
            // Counted only when it was the field that had no room, which is what a full world looks like.
            if (listed)
                impulses!.Refused++;
        }
        else if (impulses is not null)
        {
            impulses.Items[impulses.Count++] = new RockDelta
            {
                Rock = name,
                Offset = rockOffset,
                Velocity = rockVelocity - normal * (impulse / rockMass),
                StartTick = (long)tick,
            };
        }

        body.Contacts = Scratch(body).OwnContacts.AsSpan(0, touchCount);
        var shieldPoint = Vector3.Transform(shieldPointSum / touchCount, Quaternion.Conjugate(state.Orientation));
        ShieldContact(ref state, flight, body.Design, touching, closing, shieldPoint);
        var damage = ImpactDamage(closing, flight.Mass, flight);
        return damage > 0 && Damage(ref state, flight, ref body, touching, touchCount, damage, shieldPoint);
    }

    /// <summary>
    /// Resolves the hull running into other hulls, oriented box against oriented box. The rules are those of an asteroid impact: one impulse shared by mass and inertia,
    /// the hull pushed back out, and damage on the blocks that touched. The other hull's share of the impulse and
    /// its damage are written to <paramref name="field"/> when it collects them; a predicting client's does not,
    /// and waits to be told. Returns true if the hull was destroyed.
    /// </summary>
    private static bool Collide(ref HullState state, in FlightParams flight, ref HullBody body, HullField field, Vector3 travel)
    {
        var store = field.Hulls;
        var design = body.Design;
        var half = design.BlockSize * 0.5f;
        var scratch = Scratch(body);
        var touchMine = scratch.Mine.AsSpan(0, body.Alive.Length);

        for (var candidate = 0; candidate < field.CandidateCount; candidate++)
        {
            var j = field.Candidate(candidate);
            if (j < 0 || j == field.Self || !store.IsAliveAt(j))
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
            ReadOnlySpan<ulong> theirs = store.Blocks(j);
            var touchTheirs = scratch.Theirs.AsSpan(0, theirs.Length);
            var otherHalf = otherDesign.BlockSize * 0.5f;
            ReadOnlySpan<ulong> mine = body.Alive;

            // The narrow phase runs in the other hull's frame, where its blocks are where its design puts them.
            var toOtherFrame = Quaternion.Conjugate(other.Orientation);
            var relativeRotation = Quaternion.Normalize(toOtherFrame * state.Orientation);

            // A fast hull covers more than a block in a tick. Walk the path in strides too short to step over
            // a block, and stop at the first place it strikes.
            var stride = (half + otherHalf) * 0.5f;
            var strides = pathLength > stride ? Math.Min(design.CollisionRules.CollisionStrides, (int)MathF.Ceiling(pathLength / stride)) : 1;
            var pushed = Vector3.Zero;
            for (var k = 1; k <= strides; k++)
            {
                var remaining = 1f - (float)k / strides;
                var back = path * remaining;
                var centre = apart - back + pushed;
                var centreInFrame = Vector3.Transform(centre, toOtherFrame);
                if (!Touch(design, relativeRotation, mine, body.CollisionCounts, centreInFrame, otherDesign, theirs, store.BlockStates.CollisionCounts(otherInfo.BlockState), touchMine, touchTheirs, scratch, out var touch))
                    continue;

                // Where the blocks' own directions mostly cancel the hulls are inside each other, and the way
                // out is along the line between their centres.
                var normal =
                    touch.NormalSum.LengthSquared() > 0.01f * touch.Weight * touch.Weight ? Vector3.Normalize(Vector3.Transform(touch.NormalSum, other.Orientation))
                    : centre.LengthSquared() > 1e-6f ? Vector3.Normalize(centre)
                    : Vector3.UnitY;
                var clearance = 0f;
                var localNormal = Vector3.Transform(normal, toOtherFrame);
                var ownNormal = Vector3.Transform(localNormal, Quaternion.Conjugate(relativeRotation));
                for (var pair = 0; pair < touch.Pairs; pair++)
                {
                    var a = scratch.PairA[pair]; var b = scratch.PairB[pair];
                    var between = centreInFrame + Vector3.Transform(design.BlockOffsets[a], relativeRotation) - otherDesign.BlockOffsets[b];
                    var reachAlong = BlockShapes.Support(otherDesign.Boxes[b].Shape, otherDesign.BlockHalfExtents[b], localNormal) + BlockShapes.Support(design.Boxes[a].Shape, design.BlockHalfExtents[a], -ownNormal);
                    clearance = Math.Max(clearance, reachAlong - Vector3.Dot(between, localNormal));
                }
                pushed += normal * clearance;
                centre += normal * clearance;

                // The point of contact as each hull sees it, and how freely the other gives way there.
                var otherArm = Vector3.Transform(touch.PointSum / touch.Weight, other.Orientation);
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
                        ContactSpeed = closing,
                        ShieldPoint = touch.ShieldTheirsSum / touch.Weight,
                        Damage = harmless ? 0f : ImpactDamage(closing, involved, otherFlight),
                    },
                    touchTheirs
                );

                body.Contacts = Scratch(body).OwnContacts.AsSpan(0, Scratch(body).ContactCount);
                var shieldPoint = touch.ShieldMineSum / touch.Weight;
                ShieldContact(ref state, flight, body.Design, touchMine, closing, shieldPoint);
                var damage = harmless ? 0f : ImpactDamage(closing, involved, flight);
                if (damage > 0 && Damage(ref state, flight, ref body, touchMine, BlockSet.Count(touchMine), damage, shieldPoint))
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

    /// <summary>
    /// The working memory a body brings for its contacts. A hull that can run into something must bring some
    /// that holds its design; a hull stepped through a field always does.
    /// </summary>
    private static ContactScratch Scratch(in HullBody body)
    {
        var scratch = body.Scratch;
        if (scratch is null || scratch.Blocks < body.Design.BlockCount)
            throw new InvalidOperationException("A hull that can run into something needs working memory for its design; see HullBody.Scratch.");
        return scratch;
    }

    /// <summary>What the blocks of two hulls have to say about a contact between them, in the other hull's frame.</summary>
    private struct BlockTouch
    {
        /// <summary>The sum of the directions from the other hull's blocks to this hull's, one per touching pair.</summary>
        public Vector3 NormalSum;

        /// <summary>The penetration-weighted sum of the pairs' contact points.</summary>
        public Vector3 PointSum;
        /// <summary>Penetration-weighted visual surface points in each hull's own frame.</summary>
        public Vector3 ShieldMineSum, ShieldTheirsSum;
        public int Pairs;
        public float Weight;
    }

    /// <summary>
    /// Finds overlapping box pairs. The relative rotation and <paramref name="centre"/> place this hull
    /// in the other hull's frame. The
    /// blocks of this hull and of the other that touch are written to <paramref name="touchMine"/> and
    /// <paramref name="touchTheirs"/>, each a <see cref="BlockSet"/> of its hull's design.
    /// </summary>
    private static bool Touch(
        HullDesign design, Quaternion rotation, ReadOnlySpan<ulong> mine, ReadOnlySpan<int> mineCounts, Vector3 centre,
        HullDesign other, ReadOnlySpan<ulong> theirs, ReadOnlySpan<int> theirCounts, Span<ulong> touchMine, Span<ulong> touchTheirs, ContactScratch scratch,
        out BlockTouch touch)
    {
        touch = default;
        touchMine.Clear(); touchTheirs.Clear();
        scratch.ContactCount = 0;
        var rules = design.CollisionRules;
        var visits = rules.CollisionTreeVisits;
        var tests = rules.CollisionBoxTests;
        var cursor = 0;
        var inverse = Quaternion.Conjugate(rotation);
        var query = Vector3.Transform(-centre, inverse);
        var reach = new Vector3(other.Flight.Radius);
        while (tests > 0 && touch.Pairs < rules.CollisionContacts && design.Collision.Next(ref cursor, query - reach, query + reach, mine, ref visits, out var a, mineCounts))
        {
            var block = centre + Vector3.Transform(design.BlockOffsets[a], rotation);
            var bounds = BoxCollision.Bounds(design.BlockHalfExtents[a], rotation);
            var otherCursor = 0;
            while (tests > 0 && touch.Pairs < rules.CollisionContacts && other.Collision.Next(ref otherCursor, block - bounds, block + bounds, theirs, ref visits, out var b, theirCounts))
            {
                var cost = design.Boxes[a].Shape == BlockShape.Box && other.Boxes[b].Shape == BlockShape.Box ? 1 : 4;
                if (tests < cost) break;
                tests -= cost;
                var between = block - other.BlockOffsets[b];
                if (!BoxCollision.Contact(between, design.BlockHalfExtents[a], rotation, other.BlockHalfExtents[b], design.Boxes[a].Shape, other.Boxes[b].Shape, out var normal, out var depth)) continue;
                // Weight the manifold by penetration so grazing edges do not dominate a broad face contact.
                touch.NormalSum += normal * depth;
                touch.Weight += depth;
                touch.PointSum += (block + other.BlockOffsets[b]) * (0.5f * depth);
                // The visual event uses each box's surface; the impulse keeps its existing manifold point.
                var middle = (block + other.BlockOffsets[b]) * 0.5f;
                var minePoint = Vector3.Transform(middle - centre, inverse) - design.BlockOffsets[a];
                var ownSurface = design.BlockOffsets[a] + BoxSurface(minePoint, design.BlockHalfExtents[a], Vector3.Transform(-normal, inverse), design.Boxes[a].Shape);
                var otherSurface = other.BlockOffsets[b] + BoxSurface(middle - other.BlockOffsets[b], other.BlockHalfExtents[b], normal, other.Boxes[b].Shape);
                touch.ShieldMineSum += ownSurface * depth;
                touch.ShieldTheirsSum += otherSurface * depth;
                scratch.OwnContacts[touch.Pairs] = new VisualContact
                {
                    Block = a,
                    Point = ownSurface,
                    Normal = Vector3.Transform(-normal, inverse)
                };
                scratch.OtherContacts[touch.Pairs] = new VisualContact
                {
                    Block = b,
                    Point = otherSurface,
                    Normal = normal
                };
                scratch.ContactCount = touch.Pairs + 1;
                scratch.PairA[touch.Pairs] = a; scratch.PairB[touch.Pairs] = b;
                BlockSet.Add(touchMine, a); BlockSet.Add(touchTheirs, b); touch.Pairs++;
            }
        }
        return touch.Pairs > 0;
    }

    // A bounded visual contact estimate on the box, without changing the physical contact manifold.
    private static Vector3 BoxSurface(Vector3 point, Vector3 half, Vector3 outward, BlockShape shape)
    {
        point = BlockShapes.Closest(shape, half, point);
        var distance = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
            if (MathF.Abs(outward[axis]) > 1e-6f)
                distance = Math.Min(distance, (half[axis] - MathF.Sign(outward[axis]) * point[axis]) / MathF.Abs(outward[axis]));
        if (shape != BlockShape.Box)
        {
            var n = BlockShapes.Slant(shape, half);
            var rate = Vector3.Dot(n, outward);
            if (rate > 1e-6f) distance = Math.Min(distance, Math.Max(0, -Vector3.Dot(n, point) / rate));
        }
        return point + outward * (float.IsFinite(distance) ? distance : 0f);
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
    /// What the shield pays for each point it takes is the world's choice, and so is whether it stands in the
    /// way of a collision at all. A shield that pays out everything it had breaks and goes offline; an offline
    /// shield takes nothing, whatever charge it is building back. Every hit, absorbed or not, restarts the
    /// recharge delay. A block at zero is gone, and its loss is counted in the body's block state. Only the blocks
    /// that touched are looked at: the hull's total comes from the running total its block state keeps.
    /// <paramref name="touching"/> is the <see cref="BlockSet"/> of the blocks that touched. Returns true if the
    /// hull is destroyed: its core is gone or nothing is left.
    /// </summary>
    internal static bool Damage(ref HullState state, in FlightParams flight, ref HullBody body, scoped ReadOnlySpan<ulong> touching, int touchCount, float amount, Vector3? shieldPoint = null)
    {
        body.Damaged = true;
        state.ShieldCooldown = flight.ShieldDelay;
        var remaining = amount;
        var cost = flight.CollisionShieldCost;
        if (cost > 0 && flight.ShieldCapacity > 0 && !state.ShieldOffline)
        {
            // What the shield can still take at that price. A shield that cannot take it all goes to exactly
            // zero, not to whatever the division left over.
            var holds = state.Shield / cost;
            float absorbed;
            if (holds > amount)
            {
                absorbed = amount * cost;
                state.Shield -= absorbed;
                remaining = 0;
            }
            else
            {
                absorbed = state.Shield;
                state.Shield = 0;
                state.ShieldOffline = true;
                state.ShieldChangeTicks = 0;
                remaining = amount - holds;
            }

            if (absorbed > 0)
                RecordShieldHit(ref state, flight, body.Design, touching, absorbed, shieldPoint);
        }

        if (remaining <= 0)
            return false;

        if (body.States is not { } states)
        {
            // Predicting: the server decides which blocks are lost; show the hull total falling meanwhile.
            state.Hull = Math.Max(0f, state.Hull - remaining);
            return false;
        }

        var share = remaining / touchCount;
        var blocks = body.Design.BlockCount;
        foreach (var b in BlockSet.Each(touching))
        {
            // A set handed in from outside may name blocks the design does not have; they touched nothing.
            if (b >= blocks)
                break;
            states.Hit(body.State, b, share);
        }

        if (!body.Visual.IsEmpty)
        {
            ref var visual = ref body.Visual[0];
            visual.Prune(states.Alive(body.State));
            foreach (var contact in body.Contacts)
                if ((uint)contact.Block < blocks)
                    visual.Hit(body.Design, contact.Block, share, contact.Point, contact.Normal, body.ContactTick, BlockSet.Has(states.Alive(body.State), contact.Block));
        }
        state.Hull = (float)states.HitTotal(body.State);

        // Whether anything is left is counted, not read off the total: the total is kept by subtraction, and
        // beside one enormous block the others can round out of it while they are still there.
        return states.Revision(body.State) == (uint)states.Blocks(body.State) || states.CoresLost(body.State) > 0;
    }

    /// <summary>Reports a light contact without changing charge, damage or the recharge delay.</summary>
    internal static void ShieldContact(ref HullState state, in FlightParams flight, HullDesign design, ReadOnlySpan<ulong> touching, float closing, Vector3? shieldPoint = null)
    {
        if (closing < flight.ShieldContactSpeed || closing <= 0 || flight.CollisionShieldCost <= 0 || flight.ShieldCapacity <= 0 || state.ShieldOffline || state.Shield <= 0)
            return;
        RecordShieldHit(ref state, flight, design, touching, 0f, shieldPoint);
    }

    /// <summary>
    /// Records a contact or absorbed impact at its surface point, or the middle of touched blocks for callers
    /// without one. Impacts in the same tick are one event: the strongest individual impact supplies both strength and position.
    /// </summary>
    private static void RecordShieldHit(ref HullState state, in FlightParams flight, HullDesign? design, ReadOnlySpan<ulong> touching, float absorbed, Vector3? surfacePoint)
    {
        var point = Vector3.Zero;
        var count = 0;
        if (surfacePoint is null && design is not null)
        {
            var offsets = design.BlockOffsets;
            foreach (var b in BlockSet.Each(touching))
            {
                // A set handed in from outside may name blocks the design does not have; they touched nothing.
                if (b >= offsets.Length)
                    break;
                point += offsets[b];
                count++;
            }
        }

        if (count > 0)
            point /= count;
        point = surfacePoint ?? point;
        var scale = flight.Radius > 0 ? 127f / flight.Radius : 0f;
        point = Vector3.Clamp(point * scale, new Vector3(-127f), new Vector3(127f));

        // Four hits of a quarter of the shield each show at full strength; a contact with no absorbed damage gets the minimum strength.
        var strength = (int)MathF.Ceiling(Math.Min(1f, absorbed / flight.ShieldCapacity * 4f) * 255f);
        strength = Math.Clamp(strength, 1, 255);
        ref var hit = ref state.ShieldHit;
        var sameTick = hit.Strength > 0 && hit.AgeTicks == 0;
        if (sameTick && strength <= hit.Strength)
            return;

        if (!sameTick)
            hit.Serial++;
        hit.Strength = (byte)strength;
        hit.X = (sbyte)MathF.Round(point.X);
        hit.Y = (sbyte)MathF.Round(point.Y);
        hit.Z = (sbyte)MathF.Round(point.Z);
        hit.AgeTicks = 0;
    }
}
