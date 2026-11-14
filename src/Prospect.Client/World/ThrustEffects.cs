using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// Turns what each hull's thrust parts fire (<see cref="HullView.Thrust"/>) into this frame's plumes, and remembers
/// when each group of parts ignited, so an engine that starts throws its sparks and grows its plume once. A group is
/// the parts of one fuel and one facing: they fire together (<see cref="ThrustState.Firing"/>). It also remembers when
/// a group stopped and how hard it fired, so its last gas can fly off and clear (<see cref="PlumeLook.FadeSeconds"/>).
/// Fixed tables, no memory taken in a frame; past the plume budget the largest on screen are kept.
/// </summary>
internal sealed class ThrustEffects
{
    /// <summary>Hulls whose ignitions are remembered at once: every hull a snapshot can hold.</summary>
    public const int Slots = 256;

    private const int Groups = 3 * Facings.Count;
    private readonly uint[] _ids = new uint[Slots];
    private readonly bool[] _seen = new bool[Slots];
    private readonly double[] _ignited = new double[Slots * Groups];
    private readonly bool[] _firing = new bool[Slots * Groups];
    private readonly double[] _stopped = new double[Slots * Groups];
    private readonly float[] _throttle = new float[Slots * Groups];
    private readonly float[] _scores = new float[PropulsionLook.PlumeCapacity];
    private readonly PropulsionLook _look;
    private int _count;
    private int _weakest = -1;
    private double _seconds;
    private Vector3 _right, _up, _forward;
    private float _tanHalf, _aspect, _height;

    public ThrustEffects(PropulsionLook look)
    {
        _look = look;
        Array.Fill(_ignited, double.NegativeInfinity);
        Array.Fill(_stopped, double.NegativeInfinity);
    }

    /// <summary>Plumes written this frame.</summary>
    public int Count => _count;

    /// <summary>Forgets every hull, as when a session ends.</summary>
    public void Clear()
    {
        Array.Clear(_ids);
        Array.Clear(_seen);
        Array.Clear(_firing);
        Array.Clear(_throttle);
        Array.Fill(_ignited, double.NegativeInfinity);
        Array.Fill(_stopped, double.NegativeInfinity);
        _count = 0;
    }

    /// <summary>Starts a frame at <paramref name="seconds"/> of the snapshot clock, seen by the given camera.</summary>
    public void Begin(double seconds, Vector3 right, Vector3 up, Vector3 forward, float tanHalf, float aspect, float height)
    {
        _seconds = seconds;
        _right = right;
        _up = up;
        _forward = forward;
        _tanHalf = tanHalf;
        _aspect = aspect;
        _height = height;
        _count = 0;
        _weakest = -1;
        Array.Clear(_seen);
    }

    /// <summary>
    /// Notes what a hull fires now; call it for every hull every frame, drawn or not, so an engine lit while out of
    /// view does not ignite again when it comes into view. Returns the hull's slot, or -1 when every slot is taken.
    /// </summary>
    public int Observe(in HullView hull)
    {
        var slot = -1;
        for (var i = 0; i < Slots; i++)
        {
            if (_ids[i] == hull.Id)
            {
                slot = i;
                break;
            }
        }
        var fresh = false;
        if (slot < 0)
        {
            for (var i = 0; i < Slots; i++)
            {
                if (_ids[i] == 0)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0 || hull.Id == 0)
                return -1;
            _ids[slot] = hull.Id;
            fresh = true;
        }
        _seen[slot] = true;
        var thrust = hull.Thrust;
        for (var g = 0; g < Groups; g++)
        {
            var throttle = thrust.Firing((FuelKind)(g / Facings.Count + 1), (BlockFacing)(g % Facings.Count));
            var on = throttle > 0f;
            ref var was = ref _firing[slot * Groups + g];
            // A hull first seen while it fires is not shown igniting: it lit before it came into view.
            if (on && !was)
                _ignited[slot * Groups + g] = fresh ? double.NegativeInfinity : _seconds;
            // A group that stops keeps how hard it fired, for the gas that clears after it.
            if (!on && was)
                _stopped[slot * Groups + g] = _seconds;
            if (on)
            {
                _throttle[slot * Groups + g] = throttle;
                _stopped[slot * Groups + g] = double.NegativeInfinity;
            }
            was = on;
        }
        return slot;
    }

    /// <summary>Ends the frame: forgets hulls no longer in the snapshot. Returns the plumes written.</summary>
    public int End()
    {
        for (var i = 0; i < Slots; i++)
        {
            if (_ids[i] != 0 && !_seen[i])
            {
                _ids[i] = 0;
                for (var g = 0; g < Groups; g++)
                {
                    _firing[i * Groups + g] = false;
                    _throttle[i * Groups + g] = 0f;
                    _ignited[i * Groups + g] = double.NegativeInfinity;
                    _stopped[i * Groups + g] = double.NegativeInfinity;
                }
            }
        }
        return _count;
    }

    /// <summary>
    /// Writes the plumes of a drawn hull's firing nozzles into <paramref name="output"/>. <paramref name="position"/>
    /// is camera-relative; <paramref name="stretch"/> draws the hull out along its length, as its mesh is.
    /// </summary>
    public void Add(int slot, in HullView hull, ReadOnlySpan<ThrustNozzle> nozzles, Vector3 position, Quaternion orientation, float stretch, Span<PlumeInstance> output)
    {
        if (slot < 0 || nozzles.IsEmpty)
            return;
        var thrust = hull.Thrust;
        var alive = hull.Blocks.Span;
        var time = (float)(_seconds % 1000.0);
        foreach (ref readonly var nozzle in nozzles)
        {
            if (nozzle.Plume < 0 || !BlockSet.Has(alive, nozzle.Block))
                continue;
            var look = _look.PlumeOrder[nozzle.Plume];
            var group = (((int)nozzle.Fuel - 1) * Facings.Count) + (int)nozzle.Facing;
            if ((uint)group >= Groups)
                continue;
            var throttle = thrust.Firing(nozzle.Fuel, nozzle.Facing);
            // A part that has just stopped still shows its last gas, flying off down the plume, for the fade seconds.
            var released = -1f;
            if (throttle <= 0f)
            {
                var since = (float)(_seconds - _stopped[slot * Groups + group]);
                if (!(since >= 0f && since < look.FadeSeconds))
                    continue;
                throttle = _throttle[slot * Groups + group];
                released = since;
                if (throttle <= 0f)
                    continue;
            }
            var age = (float)Math.Min(_seconds - _ignited[slot * Groups + group], 1000.0);

            var radius = nozzle.ExitRadius;
            var exit = nozzle.Exit;
            exit.Z *= stretch;
            var at = position + Vector3.Transform(exit, orientation);
            var axis = Vector3.Transform(Facings.Vector(nozzle.Facing), orientation);

            var puff = look.IsPuff;
            var boost = thrust.Boosted && !puff;
            // A flame grows out of its sparks; a puff opens almost at once.
            var grow = puff ? Smooth(0f, look.GrowSeconds, age) : Smooth(look.SparkSeconds * 0.4f, look.SparkSeconds + look.GrowSeconds, age);
            var length = radius * look.Length * (look.MinLength + (1f - look.MinLength) * throttle) * MathF.Max(grow, 0.02f) * (boost ? _look.BoostLength : 1f);
            // The cap on brightness: boost raises it a little, never past what the compression in the shader holds.
            var brightness = look.Brightness * (0.55f + 0.45f * throttle) * (boost ? _look.BoostBrightness : 1f);
            var spread = look.Spread;
            if (puff)
            {
                // A valve opening throws a denser, wider burst that settles to a steady jet.
                var burst = look.Burst * MathF.Exp(-age / look.BurstSeconds);
                brightness *= 1f + burst;
                spread *= 1f + 0.25f * burst;
            }
            else
            {
                brightness *= 0.3f + 0.7f * grow;
            }
            if (released >= 0f)
                brightness *= 1f - released / MathF.Max(look.FadeSeconds, 1e-3f);
            var sparking = look.Sparks > 0 && age < look.SparkSeconds && released < 0f;
            var sparkSpeed = look.SparkSpeed * radius;
            var width = radius * (1f + spread) * 1.15f;
            if (sparking)
                width = MathF.Max(width, sparkSpeed * look.SparkSeconds * 0.5f + radius);
            var reach = MathF.Max(length * 1.1f, sparking ? sparkSpeed * look.SparkSeconds : 0f) + width;

            // Left out when off screen or too small to see; past the budget, the largest on screen stay.
            var middle = at + axis * (0.5f * reach);
            var depth = Vector3.Dot(middle, _forward);
            var bound = 0.5f * reach + width;
            if (depth < -bound)
                continue;
            var limit = MathF.Max(depth, 0f) * _tanHalf + bound * 1.5f;
            if (MathF.Abs(Vector3.Dot(middle, _up)) > limit || MathF.Abs(Vector3.Dot(middle, _right)) > limit * _aspect + bound * 1.5f)
                continue;
            var pixels = radius * _height / (MathF.Max(at.Length(), radius) * _tanHalf);
            if (pixels < _look.MinPixels)
                continue;
            var target = Place(pixels);
            if (target < 0)
                continue;

            var seed = (float)(((hull.Id * 2654435761u) ^ ((uint)nozzle.Index * 40503u)) & 1023u) / 37f;
            output[target] = new PlumeInstance
            {
                ExitRadius = new float4(at.X, at.Y, at.Z, radius),
                AxisLength = new float4(axis.X, axis.Y, axis.Z, length),
                CoreBright = new float4(look.Core[0], look.Core[1], look.Core[2], brightness),
                EdgeKind = new float4(look.Edge[0], look.Edge[1], look.Edge[2], puff ? 1f : 0f),
                SparkAge = new float4(look.Spark[0], look.Spark[1], look.Spark[2], age),
                Shape = new float4(spread, boost ? look.Diamonds * 1.2f : look.Diamonds, look.DiamondSpacing * radius, seed),
                Motion = new float4(time, look.Flicker, look.SparkSeconds, sparkSpeed),
                Extent = new float4(puff ? 0.02f * radius : nozzle.Depth * 0.6f, look.Turbulence, sparking ? look.Sparks : 0f, width),
                Flow = new float4(released, look.FlowSpeed * radius, look.CoreLength * radius * (boost ? 1.3f : 1f), look.Opening * radius),
            };
        }
    }

    // The index to write a plume of this size to, or -1 when the budget is full of larger ones.
    private int Place(float score)
    {
        var max = _look.MaxPlumes;
        if (_count < max)
        {
            _scores[_count] = score;
            _weakest = -1;
            return _count++;
        }
        if (_weakest < 0)
        {
            _weakest = 0;
            for (var i = 1; i < max; i++)
                if (_scores[i] < _scores[_weakest]) _weakest = i;
        }
        if (score <= _scores[_weakest])
            return -1;
        var slot = _weakest;
        _scores[slot] = score;
        _weakest = -1;
        return slot;
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / MathF.Max(to - from, 1e-4f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>Flight keys a scripted run holds (<c>--thrust</c>), so a capture can show plumes without a person at the keys.</summary>
internal readonly record struct ScriptedThrust(float Strafe, float Vertical, float Forward, bool Boost)
{
    /// <summary>Reads a comma list of forward, back, left, right, up, down and boost; false for anything else.</summary>
    public static bool TryParse(string text, out ScriptedThrust thrust)
    {
        thrust = default;
        float strafe = 0, vertical = 0, forward = 0;
        var boost = false;
        foreach (var word in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (word)
            {
                case "forward": forward = 1; break;
                case "back": forward = -1; break;
                case "right": strafe = 1; break;
                case "left": strafe = -1; break;
                case "up": vertical = 1; break;
                case "down": vertical = -1; break;
                case "boost": boost = true; break;
                default: return false;
            }
        }
        thrust = new ScriptedThrust(strafe, vertical, forward, boost);
        return true;
    }
}
