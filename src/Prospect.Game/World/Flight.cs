using System.Numerics;
using System.Text.Json;
using Prospect.Engine.Net;

namespace Prospect.Game.World;

/// <summary>
/// One tick of pilot input, quantised to the bytes that travel on the wire so the client's prediction and the
/// server's simulation step from exactly the same numbers.
/// </summary>
public struct FlightInput
{
    public const int WireSize = 7;
    private const byte BoostBit = 1;
    private const byte DampenersBit = 2;

    public sbyte Strafe;
    public sbyte Vertical;
    public sbyte Forward;
    public sbyte Pitch;
    public sbyte Yaw;
    public sbyte Roll;
    public bool Boost;
    public bool Dampeners;

    /// <summary>Converts an axis value in -1..1 to its wire form.</summary>
    public static sbyte Quantize(float value) => (sbyte)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f);

    public readonly bool HasManualInput => Strafe != 0 || Vertical != 0 || Forward != 0 || Pitch != 0 || Yaw != 0 || Roll != 0;

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte((byte)Strafe);
        writer.WriteByte((byte)Vertical);
        writer.WriteByte((byte)Forward);
        writer.WriteByte((byte)Pitch);
        writer.WriteByte((byte)Yaw);
        writer.WriteByte((byte)Roll);
        writer.WriteByte((byte)((Boost ? BoostBit : 0) | (Dampeners ? DampenersBit : 0)));
    }

    public static FlightInput Read(ref NetReader reader)
    {
        FlightInput input;
        input.Strafe = Sanitize(reader.ReadByte());
        input.Vertical = Sanitize(reader.ReadByte());
        input.Forward = Sanitize(reader.ReadByte());
        input.Pitch = Sanitize(reader.ReadByte());
        input.Yaw = Sanitize(reader.ReadByte());
        input.Roll = Sanitize(reader.ReadByte());
        var flags = reader.ReadByte();
        input.Boost = (flags & BoostBit) != 0;
        input.Dampeners = (flags & DampenersBit) != 0;
        return input;
    }

    // -128 would be a hair over full deflection; a legitimate client never sends it.
    private static sbyte Sanitize(byte raw) => Math.Max((sbyte)raw, (sbyte)-127);
}

/// <summary>
/// Everything the simulation needs to know about a hull design, worked out from its blocks: how it accelerates and
/// turns, what it can take, and its power budget.
/// </summary>
public struct FlightParams
{
    /// <summary>
    /// Thrust acceleration in m/s² for each direction the hull can be pushed and each kind of part that pushes it (each
    /// fuel, then ion), at full throttle. A direction's parts are those facing the other way; a fuel that has run out
    /// adds nothing, and ion parts push only as far as there is power for them.
    /// </summary>
    public ThrustTable Thrust;

    /// <summary>Kilograms a second the parts of each push direction burn of each fuel at full throttle, without boost.</summary>
    public ThrustTable Burn;

    /// <summary>What the hull's tanks can hold of each fuel, in kilograms.</summary>
    public FuelTanks FuelCapacity;

    /// <summary>How many times its normal rate thrust burns fuel while boosted.</summary>
    public float BoostFuelMultiplier;

    /// <summary>How fast a hull above its rated speed sheds the excess, in m/s².</summary>
    public float ShedAcceleration;
    public float BoostMultiplier;
    public float MaxSpeed;
    public float BoostMaxSpeed;

    /// <summary>Maximum pitch, yaw and roll rates in rad/s.</summary>
    public Vector3 TurnRate;

    /// <summary>How quickly the hull reaches a commanded pitch, yaw and roll rate, in rad/s².</summary>
    public Vector3 TurnAcceleration;

    /// <summary>Heat gained per second of boost; the hull overheats at 1.</summary>
    public float HeatRate;
    public float CoolRate;

    /// <summary>Mass in tonnes.</summary>
    public float Mass;

    /// <summary>Moments of inertia about the hull's own axes, in tonne square metres.</summary>
    public Vector3 Inertia;

    /// <summary>Radius of the sphere around the centre of mass that contains the hull, in metres.</summary>
    public float Radius;
    public float HullPoints;
    public float ShieldCapacity;

    /// <summary>Shield points restored per second while recharging.</summary>
    public float ShieldRecharge;

    /// <summary>Seconds after taking damage before the shield starts to recharge.</summary>
    public float ShieldDelay;

    /// <summary>
    /// Share of <see cref="ShieldCapacity"/> a broken shield must charge back to before it is online again.
    /// </summary>
    public float ShieldRestartFraction;

    /// <summary>Megawatts a broken shield draws while it charges back to its restart threshold.</summary>
    public float ShieldRestartPower;

    /// <summary>Generator output in megawatts.</summary>
    public float PowerGenerated;

    /// <summary>
    /// Megawatts drawn all the time, by thrusters at full thrust, and by shields while recharging. The thrust figure
    /// counts fuelled parts' pumps all at once and ion parts along three axes at once (<see cref="IonPower"/>).
    /// </summary>
    public float IdlePower;
    public float ThrustPower;

    /// <summary>
    /// Megawatts the ion parts pushing each way draw at full thrust. Power is all they spend, so each direction pays
    /// for its own throttle, the dampeners' included.
    /// </summary>
    public DirectionFigures IonPower;

    /// <summary>
    /// <see cref="ThrustPower"/> split by the fuel the parts burn: megawatts for pumps, valves and ignition at full
    /// thrust. Parts whose fuel has run out draw nothing.
    /// </summary>
    public FuelTanks ThrustPowerByFuel;
    public float ShieldPower;
    public float BoostPowerMultiplier;

    /// <summary>Battery capacity in megajoules.</summary>
    public float EnergyStorage;

    /// <summary>
    /// Megajoules the battery must hold before boost will engage. Boost that is already running carries on below
    /// it until the battery is empty, so a hull does not flicker in and out of boost at the threshold.
    /// </summary>
    public float BoostEnergyFloor;

    /// <summary>Minimum closing speed for visual shield contact feedback.</summary>
    public float ShieldContactSpeed;

    /// <summary>Impacts slower than this do no damage, in m/s.</summary>
    public float CollisionSafeSpeed;
    public float CollisionDamagePerTonneSpeedSquared;
    public float CollisionBounce;

    /// <summary>
    /// Shield points a collision takes for each point of damage the shield absorbs; zero when collisions pass
    /// the shield and hit the blocks.
    /// </summary>
    public float CollisionShieldCost;

    /// <summary>The hull's faster-than-light drive, and how long each stage of a jump takes.</summary>
    public FtlKind Ftl;

    /// <summary>Megawatts the drive draws while charging, and megajoules it must take in before it can fire.</summary>
    public float FtlPower;
    public float FtlMaxDistance;
    public float FtlEnergy;

    /// <summary>Cosine of the largest angle between nose and destination at which the drive will charge.</summary>
    public float FtlAlignCosine;
    /// <summary>Seconds of calculation required alongside charging after alignment.</summary>
    public float FtlCalculateSeconds;
    public float FtlTunnelSeconds;
    /// <summary>Positive transit speed in metres per second, supplied by content.</summary>
    public float FtlSpeed;
    public float FtlArriveSeconds;

    /// <summary>Megawatts needed with everything running at once, without boost.</summary>
    public readonly float PowerRequired => IdlePower + ThrustPower + ShieldPower;

    /// <summary>Acceleration toward <paramref name="direction"/> in m/s² with fuel of every kind in the tanks.</summary>
    public readonly float Push(BlockFacing direction) => Thrust.Sum(direction);

    /// <summary>Acceleration toward <paramref name="direction"/> in m/s² from the parts whose fuel <paramref name="fuel"/> still holds.</summary>
    public readonly float Push(BlockFacing direction, in FuelTanks fuel) => Thrust.Sum(direction, fuel);

    /// <summary>
    /// Thrust acceleration with full tanks, in m/s²: sideways and vertical, each the weaker of its two directions,
    /// and forward. For readouts; flight uses each direction's own figure.
    /// </summary>
    public readonly Vector3 Acceleration => new(
        Math.Min(Push(BlockFacing.Right), Push(BlockFacing.Left)),
        Math.Min(Push(BlockFacing.Up), Push(BlockFacing.Down)),
        Push(BlockFacing.Front));

    /// <summary>Acceleration available for braking and reversing with full tanks, in m/s²: the parts that face the nose.</summary>
    public readonly float ReverseAcceleration => Push(BlockFacing.Back);

    /// <summary>The weakest of the six directions' accelerations with full tanks, in m/s²; zero when one has no thrust.</summary>
    public readonly float WeakestPush =>
        Math.Min(Math.Min(Acceleration.X, Acceleration.Y), Math.Min(Acceleration.Z, ReverseAcceleration));

    /// <summary>The strongest of the six directions' accelerations with full tanks, in m/s²; zero for a hull with no thrust.</summary>
    public readonly float StrongestPush =>
        Math.Max(Math.Max(Math.Max(Push(BlockFacing.Right), Push(BlockFacing.Left)), Math.Max(Push(BlockFacing.Up), Push(BlockFacing.Down))), Math.Max(Push(BlockFacing.Front), Push(BlockFacing.Back)));

    /// <summary>True when the hull has ion parts in any direction.</summary>
    public readonly bool HasIon
    {
        get
        {
            for (var direction = 0; direction < Facings.Count; direction++)
                if (Thrust[ThrustTable.IonIndex((BlockFacing)direction)] > 0f) return true;
            return false;
        }
    }

    /// <summary>True when the hull has parts burning <paramref name="fuel"/> in any direction.</summary>
    public readonly bool Burns(FuelKind fuel)
    {
        for (var direction = 0; direction < Facings.Count; direction++)
            if (Thrust[ThrustTable.Index((BlockFacing)direction, fuel)] > 0f) return true;
        return false;
    }
}

/// <summary>The stages of a faster-than-light jump.</summary>
public enum JumpPhase : byte
{
    None = 0,

    /// <summary>The hull is braking and turning its nose toward the destination.</summary>
    Align = 1,

    /// <summary>The drive is taking in power. Taking the controls cancels the jump and wastes the charge.</summary>
    Charge = 2,

    /// <summary>The hull has left; it is nowhere until it arrives.</summary>
    Tunnel = 3,

    /// <summary>The hull has just come out at its destination.</summary>
    Arrive = 4,
}

/// <summary>
/// The newest contact a hull's shield reported, for drawing its ripple. Simulation state like the rest of the hull:
/// the server and the predicting client write it with the same code, and snapshots carry it as it is stored, so
/// every viewer agrees on where and how long ago the shield was hit. Impacts in one tick are one event.
/// </summary>
public struct ShieldHit
{
    /// <summary>Ticks after which a hit is too old to show; the age stops counting there.</summary>
    public const byte MaxAgeTicks = 120;

    /// <summary>Counts contact ticks, wrapping, so a viewer can tell a new hit from one it has seen.</summary>
    public byte Serial;

    /// <summary>How hard the hit was, 1 for a light contact, up to 255 for absorbed damage; 0 means no hit.</summary>
    public byte Strength;

    /// <summary>Where it landed: the hull-local point, divided by the hull's radius, times 127.</summary>
    public sbyte X;
    public sbyte Y;
    public sbyte Z;

    /// <summary>Ticks since the hit, up to <see cref="MaxAgeTicks"/>.</summary>
    public byte AgeTicks;

    /// <summary>The hull-local point the hit landed at, in metres.</summary>
    public readonly Vector3 Point(float radius) => new Vector3(X, Y, Z) * (radius / 127f);
}

/// <summary>The moving state of one hull.</summary>
public struct HullState
{
    public Double3 Position;
    public Vector3 Velocity;
    public Quaternion Orientation;

    /// <summary>Angular velocity about the hull's own axes, in rad/s.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Boost heat from 0 to 1.</summary>
    public float Heat;

    /// <summary>Set when the hull overheated; boost stays off until it has cooled most of the way down.</summary>
    public bool BoostLocked;

    /// <summary>
    /// True while boost ran on the last tick. Boost that is running holds on until the battery is empty; boost
    /// that is not needs the battery above <see cref="FlightParams.BoostEnergyFloor"/> to start.
    /// </summary>
    public bool Boosting;

    /// <summary>Stored energy in megajoules.</summary>
    public float Energy;

    /// <summary>
    /// What the hull has of each fuel, in kilograms: its tanks of that fuel added up (<see cref="TankFuel.Total"/>).
    /// A part whose fuel is gone gives no thrust. The kilograms themselves are kept tank by tank with the hull's
    /// block state, in double precision; this is their sum in single, written by <see cref="FlightModel.Step"/>
    /// and by whoever else changes a tank, and never sent or saved. A hull stepped without its tanks is flown
    /// from this figure alone.
    /// </summary>
    public FuelTanks Fuel;

    /// <summary>What the thrust parts did in the last step; see <see cref="ThrustState"/>.</summary>
    public ThrustState Thrust;
    public float Shield;
    public float Hull;

    /// <summary>Seconds until the shield may start recharging.</summary>
    public float ShieldCooldown;

    /// <summary>
    /// Set when the shield broke. It takes no hits until it has charged back to its restart threshold, which
    /// draws more power than an online shield's top-up; any charge it holds meanwhile is not protection.
    /// </summary>
    public bool ShieldOffline;

    /// <summary>The newest contact reported by the shield.</summary>
    public ShieldHit ShieldHit;

    /// <summary>
    /// Ticks since the shield last changed between shown and gone: it broke, came back online, or lost its last
    /// emitter. Counts up to <see cref="ShieldHit.MaxAgeTicks"/> and stops, so viewers can time the change
    /// against the moment they draw, and never take an old change for a new one.
    /// </summary>
    public byte ShieldChangeTicks;

    /// <summary>
    /// Where the hull is in a faster-than-light jump. The timer counts seconds in the stage, except while
    /// charging, when it counts the megajoules the drive has taken in so far.
    /// </summary>
    public JumpPhase Jump;
    public float JumpTimer;

    /// <summary>Completed calculation seconds, saturated at the configured duration.</summary>
    public float JumpCalculation;

    /// <summary>Total transit seconds, latched at alignment and retained through arrival.</summary>
    public float JumpTunnelSeconds;

    /// <summary>Precise transit elapsed seconds; replicated so long trips and replay keep advancing.</summary>
    public double JumpTunnelElapsed;

    /// <summary>A hull at rest with full hull points, shield, battery and tanks.</summary>
    public static HullState Spawn(Double3 position, in FlightParams p) =>
        new()
        {
            Position = position,
            Orientation = Quaternion.Identity,
            Energy = p.EnergyStorage,
            Fuel = p.FuelCapacity,
            Shield = p.ShieldCapacity,
            Hull = p.HullPoints,
            ShieldChangeTicks = ShieldHit.MaxAgeTicks,
        };

    /// <summary>The hull's nose direction. Hulls face along their own -Z axis.</summary>
    public readonly Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);
}

/// <summary>
/// The rigid-body flight model. Space is frictionless: velocity persists until thrust changes it. Each thrust part
/// pushes the hull the opposite way to its facing and burns its own fuel, or, an ion part, only power; a direction
/// with no part facing away from it, or whose parts have run dry, cannot be pushed. Dampeners are the hull's computer firing those same parts
/// against any drift the pilot is not commanding, and burn fuel as they do. Thrust, boost and shield recharge all
/// draw power; the generator refills the battery with whatever is left over. This one function is the whole of
/// flight; the server runs it for authority and the client runs it to predict.
/// </summary>
public static class FlightModel
{
    /// <summary>Heat an overheated drive must cool to before boost unlocks.</summary>
    public const float BoostUnlockHeat = 0.35f;
    private const float AxisScale = 1f / 127f;

    public static void Step(ref HullState state, in FlightInput input, in FlightParams p, float dt) =>
        Step(ref state, input, p, dt, out _, out _);

    public static void Step(ref HullState state, in FlightInput input, in FlightParams p, float dt, out PowerFlow flow) =>
        Step(ref state, input, p, dt, out flow, out _);

    /// <summary>
    /// Advances the hull one tick and reports where its power went in <paramref name="flow"/> and what its tanks
    /// hold and burnt in <paramref name="fuel"/>, worked out by the same lines that move the battery and empty the
    /// tanks. The jump drive's draw is not here: it is taken before flight steps. This form flies the hull from
    /// <see cref="HullState.Fuel"/> alone; a hull with a block state steps over its tanks.
    /// </summary>
    public static void Step(ref HullState state, in FlightInput input, in FlightParams p, float dt, out PowerFlow flow, out FuelFlow fuel)
    {
        // A hull stepped without its tanks, such as one a client only knows the motion of, is flown as if each
        // fuel sat in one tank: the same arithmetic, on three figures.
        Span<double> levels = [state.Fuel.Nitrogen, state.Fuel.Hydrazine, state.Fuel.Bipropellant];
        ReadOnlySpan<DesignTank> tanks =
        [
            new(-1, FuelKind.Nitrogen, p.FuelCapacity.Nitrogen),
            new(-1, FuelKind.Hydrazine, p.FuelCapacity.Hydrazine),
            new(-1, FuelKind.Bipropellant, p.FuelCapacity.Bipropellant),
        ];
        Step(ref state, input, p, dt, levels, tanks, out flow, out fuel);
    }

    /// <summary>
    /// Advances the hull one tick over its own tanks: <paramref name="levels"/> holds the kilograms in each tank of
    /// <paramref name="tanks"/>, the hull's design's, and is what the step burns from. What the hull has of a fuel
    /// is read off them before the step and written to <see cref="HullState.Fuel"/> after it.
    /// </summary>
    public static void Step(ref HullState state, in FlightInput input, in FlightParams p, float dt, Span<double> levels, ReadOnlySpan<DesignTank> tanks, out PowerFlow flow, out FuelFlow fuel)
    {
        if (levels.Length != tanks.Length)
            throw new ArgumentException("A hull's tank levels are those of its design's tanks.", nameof(levels));

        // Rotation: the commanded rate is approached at a limited angular acceleration.
        var target = new Vector3(input.Pitch * p.TurnRate.X, -input.Yaw * p.TurnRate.Y, -input.Roll * p.TurnRate.Z) * AxisScale;
        var maxDelta = p.TurnAcceleration * dt;
        state.AngularVelocity += Vector3.Clamp(target - state.AngularVelocity, -maxDelta, maxDelta);

        var angle = state.AngularVelocity.Length() * dt;
        if (angle > 1e-7f)
        {
            var spin = Quaternion.CreateFromAxisAngle(Vector3.Normalize(state.AngularVelocity), angle);
            state.Orientation = Quaternion.Normalize(state.Orientation * spin);
        }

        // Boost and heat. Boost needs a cool drive and charge in the battery: to start, the battery must be above
        // its floor; once running, boost holds on until the battery is empty, then stays off until the battery is
        // back above the floor. A hull without a battery never boosts.
        var thrust = new Vector3(input.Strafe, input.Vertical, -input.Forward) * AxisScale;
        var boosting = input.Boost
            && !state.BoostLocked
            && thrust != Vector3.Zero
            && state.Energy > 0f
            && (state.Boosting || state.Energy >= p.BoostEnergyFloor);
        state.Heat = Math.Clamp(state.Heat + (boosting ? p.HeatRate : -p.CoolRate) * dt, 0f, 1f);
        if (state.Heat >= 1f)
            state.BoostLocked = true;
        else if (state.BoostLocked && state.Heat <= BoostUnlockHeat)
            state.BoostLocked = false;

        // Fuel: the tanks as they stand before the step decide which parts can fire in it. A tank that runs dry
        // during the step still feeds it to the end, so nothing here depends on the order parts are added up in.
        Span<double> sums = stackalloc double[Fuels.Count + 1];
        TankFuel.Sum(levels, tanks, sums);
        var held = TankFuel.Fuel(sums);
        var nitrogen = held.Nitrogen > 0f;
        var hydrazine = held.Hydrazine > 0f;
        var bipropellant = held.Bipropellant > 0f;

        // What each direction can push with: the parts of the fuels left, and the ion parts, which need none.
        var toLocal = Quaternion.Conjugate(state.Orientation);
        var velocity = Vector3.Transform(state.Velocity, toLocal);
        var pushRight = p.Push(BlockFacing.Right, held);
        var pushLeft = p.Push(BlockFacing.Left, held);
        var pushUp = p.Push(BlockFacing.Up, held);
        var pushDown = p.Push(BlockFacing.Down, held);
        var pushBack = p.Push(BlockFacing.Back, held);
        var pushFront = p.Push(BlockFacing.Front, held);

        // Power: what the hull wants this tick against what the generator makes. The battery covers a shortfall
        // and takes the surplus; with the battery flat, thrust is cut back to what the generator alone can feed.
        // Propulsion has priority over shield recovery; only the remaining energy can restore shield points.
        // Pumps and valves of parts with nothing left to pump draw nothing. Ion parts spend nothing but power, so
        // each direction pays for the throttle it is asked for, the dampeners' braking included.
        state.ShieldCooldown = Math.Max(0f, state.ShieldCooldown - dt);
        if (p.ShieldCapacity <= 0f)
            state.ShieldOffline = false;
        var thrustUse = Math.Max(MathF.Abs(thrust.X), Math.Max(MathF.Abs(thrust.Y), MathF.Abs(thrust.Z)));
        var thrustPower = (nitrogen ? p.ThrustPowerByFuel.Nitrogen : 0f) + (hydrazine ? p.ThrustPowerByFuel.Hydrazine : 0f) + (bipropellant ? p.ThrustPowerByFuel.Bipropellant : 0f);
        var ionDemand = IonAsked(velocity.X, thrust.X, pushRight, pushLeft, input.Dampeners, dt, p.IonPower[BlockFacing.Right], p.IonPower[BlockFacing.Left])
            + IonAsked(velocity.Y, thrust.Y, pushUp, pushDown, input.Dampeners, dt, p.IonPower[BlockFacing.Up], p.IonPower[BlockFacing.Down])
            + IonAsked(velocity.Z, thrust.Z, pushBack, pushFront, input.Dampeners, dt, p.IonPower[BlockFacing.Back], p.IonPower[BlockFacing.Front]);
        var thrustDemand = (thrustPower * thrustUse + ionDemand) * (boosting ? p.BoostPowerMultiplier : 1f);
        var demand = p.IdlePower + thrustDemand;
        var available = Math.Max(0f, state.Energy + (p.PowerGenerated - demand) * dt);
        state.Boosting = boosting && available > 0f;
        flow = new PowerFlow { Generated = p.PowerGenerated, Idle = p.IdlePower };

        var thrustScale = 1f;
        if (available <= 0f && demand > p.PowerGenerated)
            thrustScale = thrustDemand > 0f ? Math.Clamp((p.PowerGenerated - p.IdlePower) / thrustDemand, 0f, 1f) : 1f;
        flow.Thrust = thrustDemand * thrustScale;

        if (state.ShieldCooldown <= 0f && p.ShieldRecharge > 0f)
        {
            // Stop the expensive phase exactly at the existing restart threshold. No extra phase state is needed.
            var ceiling = state.ShieldOffline ? p.ShieldCapacity * p.ShieldRestartFraction : p.ShieldCapacity;
            var restored = Math.Min(Math.Max(0f, ceiling - state.Shield), p.ShieldRecharge * dt);
            var power = state.ShieldOffline ? p.ShieldRestartPower : p.ShieldPower;
            var energyPerPoint = power / p.ShieldRecharge;
            if (energyPerPoint > 0f)
            {
                restored = Math.Min(restored, available / energyPerPoint);
                available = Math.Max(0f, available - restored * energyPerPoint);
                flow.Shield = restored * energyPerPoint / dt;
            }
            state.Shield += restored;
        }
        state.Energy = Math.Min(available, p.EnergyStorage);

        // Outside the recharge so that a shield whose capacity shrank under its charge still comes back.
        if (state.ShieldOffline && state.ShieldCooldown <= 0f && state.Shield >= p.ShieldCapacity * p.ShieldRestartFraction)
        {
            state.ShieldOffline = false;
            state.ShieldChangeTicks = 0;
        }

        // Translation, in the hull's frame so each direction uses the parts that push that way. Forward is -Z.
        // Each axis reports the throttle it used toward its plus and its minus direction, which is what burns fuel.
        var gain = (boosting ? p.BoostMultiplier : 1f) * thrustScale;
        Drive(ref velocity.X, thrust.X, pushRight, pushLeft, input.Dampeners, gain, thrustScale, dt, out var right, out var left);
        Drive(ref velocity.Y, thrust.Y, pushUp, pushDown, input.Dampeners, gain, thrustScale, dt, out var up, out var down);
        Drive(ref velocity.Z, thrust.Z, pushBack, pushFront, input.Dampeners, gain, thrustScale, dt, out var back, out var front);

        // What the parts burnt: each direction's rate at the throttle it ran at. Boost burns more for the thrust
        // it adds; the dampeners never boost, but share the multiplier in a tick where the pilot does.
        var rate = boosting ? p.BoostFuelMultiplier : 1f;
        var burn = new FuelTanks
        {
            Nitrogen = nitrogen ? Burnt(p.Burn, FuelKind.Nitrogen, right, left, up, down, back, front) * rate : 0f,
            Hydrazine = hydrazine ? Burnt(p.Burn, FuelKind.Hydrazine, right, left, up, down, back, front) * rate : 0f,
            Bipropellant = bipropellant ? Burnt(p.Burn, FuelKind.Bipropellant, right, left, up, down, back, front) * rate : 0f,
        };

        // Every tank of a fuel gives its share of what was burnt, and what is left is added up again.
        var burnt = new FuelTanks { Nitrogen = burn.Nitrogen * dt, Hydrazine = burn.Hydrazine * dt, Bipropellant = burn.Bipropellant * dt };
        state.Fuel = TankFuel.Drain(levels, tanks, sums, burnt);
        var ion = p.Thrust[ThrustTable.IonIndex(BlockFacing.Right)] * right + p.Thrust[ThrustTable.IonIndex(BlockFacing.Left)] * left
            + p.Thrust[ThrustTable.IonIndex(BlockFacing.Up)] * up + p.Thrust[ThrustTable.IonIndex(BlockFacing.Down)] * down
            + p.Thrust[ThrustTable.IonIndex(BlockFacing.Back)] * back + p.Thrust[ThrustTable.IonIndex(BlockFacing.Front)] * front;
        state.Thrust = ThrustState.Of(right - left, up - down, back - front, burn.Nitrogen > 0f, burn.Hydrazine > 0f, burn.Bipropellant > 0f, ion > 0f, state.Boosting);
        fuel = new FuelFlow { Amount = state.Fuel, Capacity = p.FuelCapacity, Burn = burn };

        // Above the hull's rated speed the drive sheds the excess rather than cutting it instantly.
        var limit = boosting ? p.BoostMaxSpeed : p.MaxSpeed;
        var speed = velocity.Length();
        if (speed > limit)
            velocity *= Math.Max(limit, speed - p.ShedAcceleration * dt) / speed;

        state.Velocity = Vector3.Transform(velocity, state.Orientation);
        state.Position += state.Velocity * dt;
    }

    /// <summary>
    /// The megawatts the ion parts along one axis are asked for this tick, before any cut for power: the pilot's
    /// throttle toward the plus or minus direction, or, with no command, the share of one tick's thrust the dampeners
    /// need to stop the drift, as <see cref="Drive"/> works it out.
    /// </summary>
    private static float IonAsked(float speed, float command, float plus, float minus, bool dampeners, float dt, float ionPlus, float ionMinus)
    {
        if (command > 0f)
            return command * ionPlus;
        if (command < 0f)
            return -command * ionMinus;
        if (!dampeners)
            return 0f;
        if (speed > 0f && minus > 0f)
            return Math.Min(speed, minus * dt) / (minus * dt) * ionMinus;
        if (speed < 0f && plus > 0f)
            return Math.Min(-speed, plus * dt) / (plus * dt) * ionPlus;
        return 0f;
    }

    /// <summary>
    /// Moves the hull's speed along one of its own axes for a tick. A command pushes toward the axis's plus or
    /// minus direction with that direction's acceleration; with none, the dampeners brake with the direction that
    /// opposes the drift, as far as one tick's thrust reaches and no further. The throttle used toward each
    /// direction comes back from 0 to 1: a braking tick that needs only part of the thrust burns only that part.
    /// </summary>
    private static void Drive(ref float speed, float command, float plus, float minus, bool dampeners, float gain, float scale, float dt, out float usedPlus, out float usedMinus)
    {
        usedPlus = 0f;
        usedMinus = 0f;
        if (command > 0f)
        {
            speed += command * plus * gain * dt;
            usedPlus = plus > 0f ? command * scale : 0f;
        }
        else if (command < 0f)
        {
            speed += command * minus * gain * dt;
            usedMinus = minus > 0f ? -command * scale : 0f;
        }
        else if (dampeners)
        {
            // The dampeners are cut back with the rest of thrust when there is not the power for it, so a hull with
            // no power left brakes no more than it can push.
            if (speed > 0f && minus > 0f)
            {
                var reach = minus * scale * dt;
                var taken = Math.Min(speed, reach);
                speed -= taken;
                usedMinus = taken / (minus * dt);
            }
            else if (speed < 0f && plus > 0f)
            {
                var reach = plus * scale * dt;
                var taken = Math.Min(-speed, reach);
                speed += taken;
                usedPlus = taken / (plus * dt);
            }
        }
    }

    /// <summary>Kilograms a second of one fuel the six directions burn at the throttles they ran at.</summary>
    private static float Burnt(in ThrustTable burn, FuelKind fuel, float right, float left, float up, float down, float back, float front) =>
        burn[ThrustTable.Index(BlockFacing.Right, fuel)] * right + burn[ThrustTable.Index(BlockFacing.Left, fuel)] * left
        + burn[ThrustTable.Index(BlockFacing.Up, fuel)] * up + burn[ThrustTable.Index(BlockFacing.Down, fuel)] * down
        + burn[ThrustTable.Index(BlockFacing.Back, fuel)] * back + burn[ThrustTable.Index(BlockFacing.Front, fuel)] * front;
}

public enum OrderKind : byte
{
    None = 0,

    /// <summary>Fly to a point and hold there.</summary>
    MoveTo = 1,

    /// <summary>Jump faster than light to an eligible point and hold there.</summary>
    Jump = 2,
}

/// <summary>A standing order for a hull: what it should be doing without a pilot's hands on it.</summary>
public struct HullOrder
{
    public OrderKind Kind;
    public Double3 Target;
}

/// <summary>Flies a hull toward its order by producing the same inputs a pilot would.</summary>
public static class Autopilot
{
    private const float ArrivalDistance = 3f;
    private const float ArrivalSpeed = 1.5f;

    /// <summary>
    /// Returns the input for this tick. <paramref name="arrived"/> is true once the hull is at rest at the target,
    /// at which point the order is complete.
    /// </summary>
    public static FlightInput Steer(in HullState state, in HullOrder order, in FlightParams p, float dt, out bool arrived)
    {
        var input = new FlightInput { Dampeners = true };
        var toTarget = order.Target.RelativeTo(state.Position);
        var distance = toTarget.Length();
        var speed = state.Velocity.Length();
        arrived = distance < ArrivalDistance && speed < ArrivalSpeed;
        if (arrived)
            return input;

        // Aim for the fastest speed from which the weakest thrusters can still stop in the distance left. Only the
        // parts with fuel count: a hull that cannot brake some way is flown no faster than it can stop, which is
        // not at all.
        var right = p.Push(BlockFacing.Right, state.Fuel);
        var left = p.Push(BlockFacing.Left, state.Fuel);
        var up = p.Push(BlockFacing.Up, state.Fuel);
        var down = p.Push(BlockFacing.Down, state.Fuel);
        var back = p.Push(BlockFacing.Back, state.Fuel);
        var front = p.Push(BlockFacing.Front, state.Fuel);
        var weakest = Math.Min(Math.Min(right, left), Math.Min(Math.Min(up, down), back));
        var direction = toTarget / Math.Max(distance, 1e-3f);
        var desired = direction * Math.Min(p.MaxSpeed, MathF.Sqrt(2f * weakest * distance) * 0.8f);

        var toLocal = Quaternion.Conjugate(state.Orientation);
        var error = Vector3.Transform(desired - state.Velocity, toLocal);
        input.Strafe = Axis(error.X, (error.X > 0 ? right : left) * dt);
        input.Vertical = Axis(error.Y, (error.Y > 0 ? up : down) * dt);
        input.Forward = Axis(-error.Z, (error.Z < 0 ? front : back) * dt);

        // Point the nose along the route while there is still some way to go.
        if (distance > 25f)
        {
            var local = Vector3.Transform(direction, toLocal);
            var yaw = MathF.Atan2(local.X, -local.Z);
            var pitch = MathF.Atan2(local.Y, MathF.Sqrt(local.X * local.X + local.Z * local.Z));
            input.Yaw = FlightInput.Quantize(yaw * 2.5f);
            input.Pitch = FlightInput.Quantize(pitch * 2.5f);
        }

        return input;
    }

    /// <summary>
    /// The share of one tick's push that closes an error in speed. A hull that cannot push along the axis is
    /// asked for nothing: dividing by its zero would ask for a number that is none, and thrust it does not
    /// have would still be charged for.
    /// </summary>
    private static sbyte Axis(float error, float push) => push > 0f ? FlightInput.Quantize(error / push) : (sbyte)0;
}
