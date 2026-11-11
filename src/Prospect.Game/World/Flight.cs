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
    /// <summary>Thrust acceleration along the hull's right, up and forward axes, in m/s².</summary>
    public Vector3 Acceleration;

    /// <summary>Acceleration available for braking and reversing, in m/s²; main engines only push forward.</summary>
    public float ReverseAcceleration;
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

    /// <summary>Generator output in megawatts.</summary>
    public float PowerGenerated;

    /// <summary>Megawatts drawn all the time, by thrusters at full thrust, and by shields while recharging.</summary>
    public float IdlePower;
    public float ThrustPower;
    public float ShieldPower;
    public float BoostPowerMultiplier;

    /// <summary>Battery capacity in megajoules.</summary>
    public float EnergyStorage;

    /// <summary>
    /// Megajoules the battery must hold before boost will engage. Boost that is already running carries on below
    /// it until the battery is empty, so a hull does not flicker in and out of boost at the threshold.
    /// </summary>
    public float BoostEnergyFloor;

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
    public float FtlEnergy;

    /// <summary>Cosine of the largest angle between nose and destination at which the drive will charge.</summary>
    public float FtlAlignCosine;
    /// <summary>Seconds of calculation required alongside charging after alignment.</summary>
    public float FtlCalculateSeconds;
    public float FtlTunnelSeconds;
    /// <summary>Transit metres per second; zero in legacy constructed parameters uses 40000.</summary>
    public float FtlSpeed;
    public float FtlArriveSeconds;

    /// <summary>Megawatts needed with everything running at once, without boost.</summary>
    public readonly float PowerRequired => IdlePower + ThrustPower + ShieldPower;
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
    public float Shield;
    public float Hull;

    /// <summary>Seconds until the shield may start recharging.</summary>
    public float ShieldCooldown;

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

    /// <summary>A hull at rest with full hull points, shield and battery.</summary>
    public static HullState Spawn(Double3 position, in FlightParams p) =>
        new()
        {
            Position = position,
            Orientation = Quaternion.Identity,
            Energy = p.EnergyStorage,
            Shield = p.ShieldCapacity,
            Hull = p.HullPoints,
        };

    /// <summary>The hull's nose direction. Hulls face along their own -Z axis.</summary>
    public readonly Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);
}

/// <summary>
/// The rigid-body flight model. Space is frictionless: velocity persists until thrust changes it. Dampeners are
/// the hull's computer firing thrusters against any drift the pilot is not commanding. Thrust, boost and shield
/// recharge all draw power; the generator refills the battery with whatever is left over. This one function is
/// the whole of flight; the server runs it for authority and the client runs it to predict.
/// </summary>
public static class FlightModel
{
    /// <summary>Heat an overheated drive must cool to before boost unlocks.</summary>
    public const float BoostUnlockHeat = 0.35f;
    private const float AxisScale = 1f / 127f;

    public static void Step(ref HullState state, in FlightInput input, in FlightParams p, float dt)
    {
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

        // Power: what the hull wants this tick against what the generator makes. The battery covers a shortfall
        // and takes the surplus; with the battery flat, thrust is cut back to what the generator alone can feed.
        state.ShieldCooldown = Math.Max(0f, state.ShieldCooldown - dt);
        var thrustUse = Math.Max(MathF.Abs(thrust.X), Math.Max(MathF.Abs(thrust.Y), MathF.Abs(thrust.Z)));
        var thrustDemand = p.ThrustPower * thrustUse * (boosting ? p.BoostPowerMultiplier : 1f);
        var recharging = state.Shield < p.ShieldCapacity && state.ShieldCooldown <= 0f && state.Energy > 0f;
        var demand = p.IdlePower + thrustDemand + (recharging ? p.ShieldPower : 0f);
        state.Energy = Math.Clamp(state.Energy + (p.PowerGenerated - demand) * dt, 0f, p.EnergyStorage);
        state.Boosting = boosting && state.Energy > 0f;

        var thrustScale = 1f;
        if (state.Energy <= 0f && demand > p.PowerGenerated)
        {
            recharging = false;
            thrustScale = thrustDemand > 0f ? Math.Clamp((p.PowerGenerated - p.IdlePower) / thrustDemand, 0f, 1f) : 1f;
        }

        if (recharging)
            state.Shield = Math.Min(p.ShieldCapacity, state.Shield + p.ShieldRecharge * dt);

        // Translation, in the hull's frame so each axis uses its own thrusters. Forward is -Z; only the main
        // engines push that way, so braking and reversing are weaker than accelerating.
        var toLocal = Quaternion.Conjugate(state.Orientation);
        var velocity = Vector3.Transform(state.Velocity, toLocal);

        if (input.Dampeners)
        {
            if (thrust.X == 0)
                velocity.X -= Math.Clamp(velocity.X, -p.Acceleration.X * dt, p.Acceleration.X * dt);
            if (thrust.Y == 0)
                velocity.Y -= Math.Clamp(velocity.Y, -p.Acceleration.Y * dt, p.Acceleration.Y * dt);
            if (thrust.Z == 0)
                velocity.Z -= Math.Clamp(velocity.Z, -p.Acceleration.Z * dt, p.ReverseAcceleration * dt);
        }

        var acceleration = new Vector3(p.Acceleration.X, p.Acceleration.Y, thrust.Z < 0 ? p.Acceleration.Z : p.ReverseAcceleration);
        velocity += thrust * acceleration * ((boosting ? p.BoostMultiplier : 1f) * thrustScale * dt);

        // Above the hull's rated speed the drive sheds the excess rather than cutting it instantly.
        var limit = boosting ? p.BoostMaxSpeed : p.MaxSpeed;
        var speed = velocity.Length();
        if (speed > limit)
            velocity *= Math.Max(limit, speed - p.ReverseAcceleration * 2f * dt) / speed;

        state.Velocity = Vector3.Transform(velocity, state.Orientation);
        state.Position += state.Velocity * dt;
    }
}

public enum OrderKind : byte
{
    None = 0,

    /// <summary>Fly to a point and hold there.</summary>
    MoveTo = 1,

    /// <summary>Jump faster than light to a point, however far, and hold there.</summary>
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

        // Aim for the fastest speed from which the weakest thrusters can still stop in the distance left.
        var weakest = Math.Min(p.Acceleration.X, Math.Min(p.Acceleration.Y, p.ReverseAcceleration));
        var direction = toTarget / Math.Max(distance, 1e-3f);
        var desired = direction * Math.Min(p.MaxSpeed, MathF.Sqrt(2f * weakest * distance) * 0.8f);

        var toLocal = Quaternion.Conjugate(state.Orientation);
        var error = Vector3.Transform(desired - state.Velocity, toLocal);
        input.Strafe = FlightInput.Quantize(error.X / (p.Acceleration.X * dt));
        input.Vertical = FlightInput.Quantize(error.Y / (p.Acceleration.Y * dt));
        input.Forward = FlightInput.Quantize(-error.Z / ((error.Z < 0 ? p.Acceleration.Z : p.ReverseAcceleration) * dt));

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
}
