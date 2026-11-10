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

/// <summary>How a hull flies. Derived from the hull's design.</summary>
public struct FlightParams
{
    /// <summary>Thrust acceleration along the hull's right, up and forward axes, in m/s².</summary>
    public Vector3 Acceleration;
    public float BoostMultiplier;
    public float MaxSpeed;
    public float BoostMaxSpeed;

    /// <summary>Maximum pitch, yaw and roll rates in rad/s.</summary>
    public Vector3 TurnRate;

    /// <summary>How quickly the hull reaches a commanded turn rate, in rad/s².</summary>
    public float TurnAcceleration;

    /// <summary>Heat gained per second of boost; the hull overheats at 1.</summary>
    public float HeatRate;
    public float CoolRate;
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

    public static HullState At(Double3 position) => new() { Position = position, Orientation = Quaternion.Identity };

    /// <summary>The hull's nose direction. Hulls face along their own -Z axis.</summary>
    public readonly Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);
}

/// <summary>
/// The rigid-body flight model. Space is frictionless: velocity persists until thrust changes it. Dampeners are
/// the hull's computer firing thrusters against any drift the pilot is not commanding. This one function is the
/// whole of flight; the server runs it for authority and the client runs it to predict.
/// </summary>
public static class FlightModel
{
    private const float BoostUnlockHeat = 0.35f;
    private const float AxisScale = 1f / 127f;

    public static void Step(ref HullState state, in FlightInput input, in FlightParams p, float dt)
    {
        // Rotation: the commanded rate is approached at a limited angular acceleration.
        var target = new Vector3(input.Pitch * p.TurnRate.X, -input.Yaw * p.TurnRate.Y, -input.Roll * p.TurnRate.Z) * AxisScale;
        var maxDelta = p.TurnAcceleration * dt;
        var delta = target - state.AngularVelocity;
        state.AngularVelocity += Vector3.Clamp(delta, new Vector3(-maxDelta), new Vector3(maxDelta));

        var angle = state.AngularVelocity.Length() * dt;
        if (angle > 1e-7f)
        {
            var spin = Quaternion.CreateFromAxisAngle(Vector3.Normalize(state.AngularVelocity), angle);
            state.Orientation = Quaternion.Normalize(state.Orientation * spin);
        }

        // Boost and heat.
        var thrust = new Vector3(input.Strafe, input.Vertical, -input.Forward) * AxisScale;
        var boosting = input.Boost && !state.BoostLocked && thrust != Vector3.Zero;
        state.Heat = Math.Clamp(state.Heat + (boosting ? p.HeatRate : -p.CoolRate) * dt, 0f, 1f);
        if (state.Heat >= 1f)
            state.BoostLocked = true;
        else if (state.BoostLocked && state.Heat <= BoostUnlockHeat)
            state.BoostLocked = false;

        // Translation, in the hull's frame so each axis uses its own thrusters.
        var toLocal = Quaternion.Conjugate(state.Orientation);
        var velocity = Vector3.Transform(state.Velocity, toLocal);
        var acceleration = p.Acceleration * (boosting ? p.BoostMultiplier : 1f);

        if (input.Dampeners)
        {
            var brake = p.Acceleration * dt;
            if (thrust.X == 0)
                velocity.X -= Math.Clamp(velocity.X, -brake.X, brake.X);
            if (thrust.Y == 0)
                velocity.Y -= Math.Clamp(velocity.Y, -brake.Y, brake.Y);
            if (thrust.Z == 0)
                velocity.Z -= Math.Clamp(velocity.Z, -brake.Z, brake.Z);
        }

        velocity += thrust * acceleration * dt;

        // Above the hull's rated speed the drive sheds the excess rather than cutting it instantly.
        var limit = boosting ? p.BoostMaxSpeed : p.MaxSpeed;
        var speed = velocity.Length();
        if (speed > limit)
            velocity *= Math.Max(limit, speed - p.Acceleration.Z * 2f * dt) / speed;

        state.Velocity = Vector3.Transform(velocity, state.Orientation);
        state.Position += state.Velocity * dt;
    }
}

public enum OrderKind : byte
{
    None = 0,

    /// <summary>Fly to a point and hold there.</summary>
    MoveTo = 1,
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
        var weakest = Math.Min(p.Acceleration.X, Math.Min(p.Acceleration.Y, p.Acceleration.Z));
        var direction = toTarget / Math.Max(distance, 1e-3f);
        var desired = direction * Math.Min(p.MaxSpeed, MathF.Sqrt(2f * weakest * distance) * 0.8f);

        var toLocal = Quaternion.Conjugate(state.Orientation);
        var error = Vector3.Transform(desired - state.Velocity, toLocal);
        var perTick = p.Acceleration * dt;
        input.Strafe = FlightInput.Quantize(error.X / perTick.X);
        input.Vertical = FlightInput.Quantize(error.Y / perTick.Y);
        input.Forward = FlightInput.Quantize(-error.Z / perTick.Z);

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

/// <summary>A hull design loaded from <c>content/ships</c>: its blocks and the flight figures that follow.</summary>
public sealed class HullDesign
{
    public required string Name { get; init; }
    public required float BlockSize { get; init; }

    /// <summary>Grid cells occupied by a block.</summary>
    public required (int X, int Y, int Z)[] Blocks { get; init; }
    public required Vector3 Color { get; init; }
    public required FlightParams Flight { get; init; }

    public static HullDesign Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        var blocks = new List<(int, int, int)>();
        foreach (var block in root.GetProperty("blocks"u8).EnumerateArray())
            blocks.Add((block[0].GetInt32(), block[1].GetInt32(), block[2].GetInt32()));
        if (blocks.Count == 0)
            throw new InvalidDataException($"{path}: a hull needs at least one block");

        var flight = root.GetProperty("flight"u8);
        var acceleration = flight.GetProperty("acceleration"u8);
        var turn = flight.GetProperty("turnRateDegrees"u8);
        var color = root.GetProperty("color"u8);
        const float radians = MathF.PI / 180f;

        return new HullDesign
        {
            Name = root.GetProperty("name"u8).GetString()!,
            BlockSize = root.GetProperty("blockSize"u8).GetSingle(),
            Blocks = [.. blocks],
            Color = new Vector3(color[0].GetSingle(), color[1].GetSingle(), color[2].GetSingle()),
            Flight = new FlightParams
            {
                Acceleration = new Vector3(
                    acceleration.GetProperty("strafe"u8).GetSingle(),
                    acceleration.GetProperty("vertical"u8).GetSingle(),
                    acceleration.GetProperty("forward"u8).GetSingle()
                ),
                BoostMultiplier = flight.GetProperty("boostMultiplier"u8).GetSingle(),
                MaxSpeed = flight.GetProperty("maxSpeed"u8).GetSingle(),
                BoostMaxSpeed = flight.GetProperty("boostMaxSpeed"u8).GetSingle(),
                TurnRate = new Vector3(
                    turn.GetProperty("pitch"u8).GetSingle(),
                    turn.GetProperty("yaw"u8).GetSingle(),
                    turn.GetProperty("roll"u8).GetSingle()
                ) * radians,
                TurnAcceleration = flight.GetProperty("turnAccelerationDegrees"u8).GetSingle() * radians,
                HeatRate = 1f / flight.GetProperty("boostHeatSeconds"u8).GetSingle(),
                CoolRate = 1f / flight.GetProperty("coolSeconds"u8).GetSingle(),
            },
        };
    }
}
