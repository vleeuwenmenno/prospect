using System.Numerics;
using System.Runtime.CompilerServices;

namespace Prospect.Game.World;

/// <summary>Fixed-order running sums. Losing a block rebuilds only its ancestors; no subtraction drift accumulates.</summary>
public sealed class BoxHullSums
{
    [InlineArray(27)]
    private struct Numbers { private double _first; }
    private struct Sum
    {
        internal Numbers Values;
        internal Vector3 Min, Max;
        internal int Count;
        internal FtlKind Ftl;
    }
    private readonly BoxDesign _design;
    private readonly Sum[] _tree;
    private readonly int _leaves;

    /// <summary>Builds intact sums, or sums for an explicit survivor set in file order.</summary>
    public BoxHullSums(BoxDesign design, ReadOnlySpan<bool> alive = default)
    {
        _design = design;
        if (!alive.IsEmpty && alive.Length != design.Blocks.Length) throw new ArgumentException("Alive set must match design", nameof(alive));
        _leaves = 1;
        while (_leaves < design.Blocks.Length) _leaves *= 2;
        _tree = new Sum[_leaves * 2];
        for (var i = 0; i < design.Blocks.Length; i++)
            if (alive.IsEmpty || alive[i]) _tree[_leaves + i] = Contribution(design.Blocks[i]);
        for (var i = _leaves - 1; i > 0; i--) Merge(i);
    }

    /// <summary>Bytes retained by the sum array, excluding object headers and the shared design.</summary>
    public long StorageBytes => _tree.LongLength * Unsafe.SizeOf<Sum>();

    /// <summary>Removes a whole block idempotently in O(log n), without allocation or a full-design scan.</summary>
    public void Lose(int index)
    {
        if ((uint)index >= (uint)_design.Blocks.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var node = _leaves + index;
        _tree[node] = default;
        while ((node /= 2) > 0) Merge(node);
    }

    private void Merge(int node)
    {
        ref var a = ref _tree[node * 2]; ref var b = ref _tree[node * 2 + 1]; ref var sum = ref _tree[node];
        for (var j = 0; j < 27; j++) sum.Values[j] = a.Values[j] + b.Values[j];
        sum.Count = a.Count + b.Count;
        sum.Min = a.Count == 0 ? b.Min : b.Count == 0 ? a.Min : Vector3.Min(a.Min, b.Min);
        sum.Max = a.Count == 0 ? b.Max : b.Count == 0 ? a.Max : Vector3.Max(a.Max, b.Max);
        sum.Ftl = a.Ftl != FtlKind.None ? a.Ftl : b.Ftl;
    }

    private Sum Contribution(DesignBox b)
    {
        var t = b.Type;
        ReadOnlySpan<double> raw = [t.Mass, t.Hull, t.Thrust, t.Torque, t.PowerOutput, t.PowerDraw, t.EnergyStorage, t.Shield, t.ShieldRecharge, t.Cargo, t.Crew, t.FtlPower];
        var sum = new Sum { Count = 1, Min = BoxDesign.Low(b), Max = BoxDesign.High(b), Ftl = t.Ftl };
        ref var v = ref sum.Values;
        for (var j = 0; j < 12; j++) v[j] = BoxDesign.Scale(t, BoxDesign.StatNames[j], raw[j], BoxDesign.Volume(b));
        // Crew capacity is whole people, rounded down once per whole block.
        v[10] = Math.Floor(v[10]);
        v[t.ForwardOnly ? 12 : 13] = v[2];
        v[t.Thrust > 0 ? 15 : t.Shield > 0 ? 16 : 14] = v[5];
        v[17] = BoxDesign.Volume(b);
        var c = BoxDesign.Mid(b); var scale = (double)_design.BlockSize;
        v[18] = v[0] * c.X; v[19] = v[0] * c.Y; v[20] = v[0] * c.Z;
        var x = (c.X - _design.Center.X) * scale; var y = (c.Y - _design.Center.Y) * scale; var z = (c.Z - _design.Center.Z) * scale;
        var sx = b.Size.X * scale; var sy = b.Size.Y * scale; var sz = b.Size.Z * scale;
        v[21] = v[0] * (y * y + z * z + (sy * sy + sz * sz) / 12);
        v[22] = v[0] * (x * x + z * z + (sx * sx + sz * sz) / 12);
        v[23] = v[0] * (x * x + y * y + (sx * sx + sy * sy) / 12);
        if (t.Ftl != FtlKind.None) v[23 + (int)t.Ftl] = v[11];
        return sum;
    }

    /// <summary>Survivors' centre in grid coordinates; flight still turns about the design's original centre.</summary>
    public Double3 Center => _tree[1].Values[0] > 0 ? new Double3(_tree[1].Values[18] / _tree[1].Values[0], _tree[1].Values[19] / _tree[1].Values[0], _tree[1].Values[20] / _tree[1].Values[0]) : _design.Center;

    /// <summary>Returns all flight and data-sheet figures in constant time under the supplied world rules.</summary>
    public void Evaluate(FlightRules rules, out FlightParams flight, out HullStats stats)
    {
        ref var sum = ref _tree[1]; ref var v = ref sum.Values;
        var size = _design.BlockSize;
        var mass = Math.Max((float)v[0], 0.01f);
        var inertia = Vector3.Max(new Vector3((float)v[21], (float)v[22], (float)v[23]), new Vector3(0.001f));
        var radius = _design.Radius;
        var hull = (float)v[1] * rules.HullStrength; var shield = (float)v[7]; var recharge = (float)v[8] * rules.ShieldRechargeMultiplier;
        var torque = (float)v[3]; var generated = (float)v[4]; var storage = (float)v[6]; var cargo = (float)v[9];
        var crew = checked((int)v[10]); var blocks = sum.Count;
        var engineThrust = (float)v[12]; var thrusterThrust = (float)v[13];
        var idlePower = (float)v[14]; var thrustPower = (float)v[15]; var shieldPower = (float)v[16];
        var ftl = sum.Ftl; var ftlPower = ftl == FtlKind.None ? 0 : (float)v[23 + (int)ftl] * rules.FtlChargeMultiplier;
        var lateral = thrusterThrust / mass;
        var forward = (engineThrust + thrusterThrust) / mass;
        var turnAcceleration = new Vector3(torque) / inertia;
        var turnRate = new Vector3(MathF.Sqrt(turnAcceleration.X), MathF.Sqrt(turnAcceleration.Y), MathF.Sqrt(turnAcceleration.Z)) * rules.TurnRatePerRootAcceleration;
        var maxSpeed = rules.SpeedPerRootAcceleration * MathF.Sqrt(forward);

        flight = new FlightParams
        {
            Acceleration = new Vector3(lateral, lateral, forward),
            ReverseAcceleration = lateral,
            BoostMultiplier = rules.BoostThrustMultiplier,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            TurnAcceleration = turnAcceleration,
            HeatRate = 1f / rules.BoostHeatSeconds,
            CoolRate = 1f / rules.CoolSeconds,
            Mass = mass,
            Inertia = inertia,
            Radius = radius,
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            ShieldDelay = rules.ShieldRechargeDelaySeconds,
            PowerGenerated = generated,
            IdlePower = idlePower,
            ThrustPower = thrustPower,
            ShieldPower = shieldPower,
            BoostPowerMultiplier = rules.BoostPowerMultiplier,
            EnergyStorage = storage,
            CollisionSafeSpeed = rules.CollisionSafeSpeed,
            CollisionDamagePerTonneSpeedSquared = rules.CollisionDamagePerTonneSpeedSquared,
            CollisionBounce = rules.CollisionBounce,
            CollisionShieldCost = rules.CollisionShieldCost,
            Ftl = ftl,
            FtlPower = ftlPower,
            FtlEnergy = rules.FtlEnergyPerTonne * mass,
            FtlAlignCosine = MathF.Cos(rules.FtlAlignDegrees * MathF.PI / 180f),
            FtlCalculateSeconds = rules.FtlCalculateSeconds,
            FtlTunnelSeconds = rules.FtlTunnelSeconds,
            FtlSpeed = rules.FtlSpeed,
            FtlArriveSeconds = rules.FtlArriveSeconds,
        };

        stats = new HullStats
        {
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            Mass = mass,
            Volume = (float)v[17] * size * size * size,
            Size = blocks > 0 ? (sum.Max - sum.Min) * size : Vector3.Zero,
            Blocks = blocks,
            CargoHold = cargo,
            Crew = crew,
            Acceleration = forward,
            Deceleration = lateral,
            LateralAcceleration = lateral,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            PowerRequired = flight.PowerRequired,
            PowerGenerated = generated,
            EnergyStorage = storage,
            Ftl = ftl,
            FtlEnergy = rules.FtlEnergyPerTonne * mass,
            FtlPower = ftlPower,
        };
    }
}
