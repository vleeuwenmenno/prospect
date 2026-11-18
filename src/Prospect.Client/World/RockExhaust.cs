using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal struct RockMarkData
{
    public float4 CenterRadius;
    public float4 NormalScorch;
    public float4 Glow;
    // A mining laser's hot mark: the fractures around it. x how dark they are now, y a line's width (m), z how far they
    // reach (m), w a seed that keeps one mark's pattern the same for every player. All zero for every other mark.
    public float4 Cut;
}

internal struct RockEffectInstance
{
    public float4 PositionSize;
    public float4 Right;
    public float4 Up;
    public float4 Color;
    public float4 Shape;
}

/// <summary>Fixed surface-mark and dust pools. Only the nearest candidate ray/rock pairs reach facet tests.</summary>
internal sealed class RockExhaust
{
    public const int Particles = 4096;
    public const int Marks = 64;
    public const int Rays = 16;
    public const int Capacity = Particles + Marks;
    private static readonly RockExhaustSurface Surface = new();
    private readonly Particle[] _particles = new Particle[Particles];
    private readonly Mark[] _marks = new Mark[Marks];
    private readonly Candidate[] _candidates = new Candidate[Rays];
    private int _candidateCount, _particleNext;
    private uint _random = 173;
    private double _time, _last;
    private float _dt;
    private Double3 _camera;
    private int _outputCount;
    public readonly RockEffectInstance[] Instances = new RockEffectInstance[Capacity];
    public readonly RockMarkData[] MarkData = new RockMarkData[Marks];
    public readonly int[] MarkDraws = new int[Marks];

    private struct Particle
    {
        public Double3 Origin;
        public Vector3 Velocity, Color;
        public double Born;
        public float Life, Size;
    }
    private struct Mark
    {
        public bool Used, Seen;
        // A mining laser's hot mark: its glow and fractures come from the laser's cut look, not from a plume's.
        public bool Cut;
        public float Seed;
        public SectorCoord Sector;
        public int Index, Kind, Draw;
        public Vector3 Local, Normal, Center, WorldNormal;
        public double Born, Heated;
        public float Radius, Strength;
    }
    private readonly record struct Candidate(int Plume, SectorCoord Sector, int Index, int Shape, Vector3 Center,
        Quaternion Rotation, float Radius, Vector3 Tint, float Score, int Draw);

    public void Clear()
    {
        Array.Clear(_particles);
        Array.Clear(_marks);
        _last = 0;
    }

    public void Begin(double time, Double3 camera)
    {
        _time = time;
        _dt = _last == 0 ? 0 : (float)Math.Clamp(time - _last, 0, 0.1);
        _last = time;
        _camera = camera;
        _candidateCount = _outputCount = 0;
        Array.Clear(MarkData);
        Array.Fill(MarkDraws, -1);
        for (var i = 0; i < Marks; i++) _marks[i].Seen = false;
    }

    public void Offer(SectorCoord sector, int index, int shape, Vector3 center, Quaternion rotation, float radius,
        Vector3 tint, ThrustEffects thrust, int draw = -1)
    {
        for (var i = 0; i < Marks; i++)
        {
            ref var mark = ref _marks[i];
            if (!mark.Used || mark.Sector != sector || mark.Index != index) continue;
            mark.Seen = true;
            mark.Draw = draw;
            mark.Center = center + Vector3.Transform(mark.Local, rotation);
            mark.WorldNormal = Vector3.Transform(mark.Normal, rotation);
        }
        for (var p = 0; p < thrust.Count; p++)
        {
            var source = thrust.Capsules[p];
            if (source.DistanceSquared(center) > radius * radius) continue;
            var score = source.Start.LengthSquared();
            var at = _candidateCount;
            while (at > 0 && score < _candidates[at - 1].Score) at--;
            if (at >= Rays) continue;
            for (var i = Math.Min(_candidateCount, Rays - 1); i > at; i--) _candidates[i] = _candidates[i - 1];
            _candidates[at] = new(p, sector, index, shape, center, rotation, radius, tint, score, draw);
            _candidateCount = Math.Min(Rays, _candidateCount + 1);
        }
    }

    public int End(ThrustEffects thrust, Vector3 right, Vector3 up)
    {
        Span<float> nearest = stackalloc float[PropulsionLook.PlumeCapacity];
        Span<int> selected = stackalloc int[PropulsionLook.PlumeCapacity];
        Span<Vector3> normals = stackalloc Vector3[PropulsionLook.PlumeCapacity];
        selected.Fill(-1);
        nearest.Fill(float.PositiveInfinity);
        for (var c = 0; c < _candidateCount; c++)
        {
            var candidate = _candidates[c];
            var source = thrust.Capsules[candidate.Plume];
            var inverse = Quaternion.Conjugate(candidate.Rotation);
            var origin = Vector3.Transform(source.Start - candidate.Center, inverse) / candidate.Radius;
            var direction = Vector3.Transform(source.Direction, inverse);
            if (!Surface.Hit(candidate.Shape, origin, direction, Math.Min(source.Length, nearest[candidate.Plume]) / candidate.Radius, out var t, out var n)) continue;
            t *= candidate.Radius;
            var point = source.Start + source.Direction * t;
            var clip = thrust.Instances[candidate.Plume].ShieldClip;
            if (Vector3.Dot(new Vector3(clip.X, clip.Y, clip.Z), point) + clip.W < -0.001f) continue;
            nearest[candidate.Plume] = t;
            normals[candidate.Plume] = n;
            selected[candidate.Plume] = c;
        }
        for (var p = 0; p < thrust.Count; p++)
        {
            if (selected[p] < 0) continue;
            var c = _candidates[selected[p]];
            var kind = thrust.Kinds[p];
            var look = PropulsionLook.Shared.PlumeOrder[kind].Rock;
            var source = thrust.Capsules[p];
            var point = source.Start + source.Direction * nearest[p];
            var normal = Vector3.Transform(normals[p], c.Rotation);
            var strength = thrust.Strengths[p] * ShieldExhaust.WeightAt(thrust.Instances[p], nearest[p]);
            if (!(strength > 0)) continue;
            var radius = ShieldExhaust.RadiusAt(thrust.Instances[p], nearest[p]) * look.Spread;
            if (look.Glow + look.Scorch + look.Shimmer > 0)
                Heat(c.Sector, c.Index, kind, Vector3.Transform(point - c.Center, Quaternion.Conjugate(c.Rotation)), normals[p], point, normal, radius, strength, c.Draw);
            var expected = look.DustRate * strength * _dt;
            var count = (int)expected + (Random() < expected % 1 ? 1 : 0);
            var tangent = Tangent(normal);
            var across = Vector3.Cross(normal, tangent);
            for (var i = 0; i < count; i++)
            {
                var angle = Random() * MathF.Tau;
                var radial = tangent * MathF.Cos(angle) + across * MathF.Sin(angle);
                _particles[_particleNext] = new Particle
                {
                    Origin = _camera + point + normal * look.Bias + radial * (radius * Random()),
                    Velocity = radial * (look.DustSpeed * (0.4f + Random())) + normal * (look.DustLift * (0.5f + Random())),
                    Born = _time,
                    Life = look.DustSeconds * (0.6f + Random() * 0.4f),
                    Size = look.DustSize * (0.5f + Random()),
                    Color = c.Tint,
                };
                _particleNext = (_particleNext + 1) % Particles;
            }
        }
        for (var i = 0; i < Marks; i++)
        {
            ref var m = ref _marks[i];
            if (!m.Used) continue;
            var age = (float)(_time - m.Heated);
            if (m.Cut)
            {
                var cut = PropulsionLook.Shared.Laser?.Cut;
                if (cut is null || age >= cut.CrackSeconds) { m.Used = false; continue; }
                if (!m.Seen) continue;
                var heat = cut.Heat(age);
                MarkData[i] = new RockMarkData
                {
                    CenterRadius = new(m.Center.X, m.Center.Y, m.Center.Z, m.Radius),
                    NormalScorch = new(m.WorldNormal.X, m.WorldNormal.Y, m.WorldNormal.Z, 0),
                    Glow = new(heat.X, heat.Y, heat.Z, -1),
                    Cut = new(cut.CrackStrength(age), cut.CrackWidth, m.Radius * cut.CrackReach, m.Seed),
                };
                MarkDraws[i] = m.Draw;
                continue;
            }

            var look = PropulsionLook.Shared.PlumeOrder[m.Kind].Rock;
            if (age >= look.FadeSeconds) { m.Used = false; continue; }
            if (!m.Seen) continue;
            var response = Response(look, age, m.Strength);
            MarkData[i] = new RockMarkData
            {
                CenterRadius = new(m.Center.X, m.Center.Y, m.Center.Z, m.Radius),
                NormalScorch = new(m.WorldNormal.X, m.WorldNormal.Y, m.WorldNormal.Z, response.W),
                Glow = new(response.X, response.Y, response.Z, look.Shimmer > 0 ? (float)(_time % 1000) : -1),
            };
            MarkDraws[i] = m.Draw;
        }
        for (var i = 0; i < Particles; i++)
        {
            ref var p = ref _particles[i];
            var age = (float)(_time - p.Born);
            if (p.Life <= 0 || age >= p.Life || age < 0) continue;
            var along = new Vector2(Vector3.Dot(p.Velocity, right), Vector3.Dot(p.Velocity, up));
            along = along.LengthSquared() > 1e-8f ? Vector2.Normalize(along) : Vector2.UnitX;
            var axis = right * along.X + up * along.Y;
            Write(p.Origin.RelativeTo(_camera) + p.Velocity * age, p.Size, axis * 3, -right * along.Y + up * along.X,
                p.Color * 2, 0.75f * (1 - age / p.Life), 0);
        }
        return _outputCount;
    }

    internal int Heat(SectorCoord sector, int index, int kind, Vector3 local, Vector3 normal, Vector3 center, Vector3 worldNormal, float radius, float strength, int draw = -1) =>
        HeatCore(sector, index, kind, local, normal, center, worldNormal, radius, strength, false, 0, draw);

    /// <summary>
    /// A mining laser's hot mark where its beam cut into a rock: the rock's own frame, the point on its surface the cut
    /// made, how far out that point faces, and how wide the mark reaches. Its glow, its cooling and its fractures are
    /// the laser cut look's; <paramref name="seed"/> (0 to 1) keeps one mark's pattern the same on every client.
    /// </summary>
    internal int HeatCut(SectorCoord sector, int index, Vector3 local, Vector3 normal, Vector3 center, Vector3 worldNormal, float radius, float seed) =>
        HeatCore(sector, index, -1, local, normal, center, worldNormal, radius, 1f, true, seed, -1);

    private int HeatCore(SectorCoord sector, int index, int kind, Vector3 local, Vector3 normal, Vector3 center, Vector3 worldNormal, float radius,
        float strength, bool cut, float seed, int draw)
    {
        var slot = -1;
        var oldest = 0;
        for (var i = 0; i < Marks; i++)
        {
            ref var m = ref _marks[i];
            if (m.Used && m.Cut == cut && m.Sector == sector && m.Index == index && m.Kind == kind && Vector3.DistanceSquared(m.Local, local) < radius * radius
                && Vector3.Dot(m.Normal, normal) > 0.8f)
            {
                m.Heated = _time; m.Strength = Math.Max(m.Strength, strength); m.Seen = true; m.Draw = draw;
                return i;
            }
            if (!m.Used && slot < 0) slot = i;
            if (m.Born < _marks[oldest].Born) oldest = i;
        }
        if (slot < 0) slot = oldest;
        _marks[slot] = new Mark
        {
            Used = true,
            Seen = true,
            Cut = cut,
            Seed = seed,
            Sector = sector,
            Index = index,
            Kind = kind,
            Draw = draw,
            Local = local,
            Normal = normal,
            Center = center,
            WorldNormal = worldNormal,
            Radius = radius,
            Strength = strength,
            Born = _time,
            Heated = _time
        };
        return slot;
    }

    /// <summary>
    /// The dust and debris a chunk throws up where it broke off: a short burst out of the fixed pool, thrown outward
    /// and drifting on the chunk's own velocity. <paramref name="half"/> is half the chunk's edge. Takes no memory.
    /// </summary>
    public void Burst(Double3 position, Vector3 velocity, float half, Vector3 tint)
    {
        if (PropulsionLook.Shared.Laser?.Cut is not { } cut) return;
        Spray(position, velocity, half, tint, cut.BurstParticles, cut.BurstSpeed, cut.BurstSeconds, cut.BurstSize);
        // Larger, slower motes after the dust read as debris rather than as light.
        Spray(position, velocity, half, tint * 0.55f, cut.DebrisParticles, cut.DebrisSpeed, cut.DebrisSeconds, cut.DebrisSize);
    }

    private void Spray(Double3 position, Vector3 velocity, float half, Vector3 tint, int count, float speed, float seconds, float size)
    {
        for (var i = 0; i < count; i++)
        {
            // Directions spread over the sphere, so a burst never clumps on one side.
            var up = Random() * 2f - 1f;
            var around = Random() * MathF.Tau;
            var across = MathF.Sqrt(MathF.Max(0f, 1f - up * up));
            var direction = new Vector3(across * MathF.Cos(around), up, across * MathF.Sin(around));
            _particles[_particleNext] = new Particle
            {
                Origin = position + direction * (half * 1.2f),
                Velocity = direction * (speed * (0.35f + Random())) + velocity * 0.5f,
                Born = _time,
                Life = seconds * (0.55f + Random() * 0.45f),
                Size = size * (0.4f + Random() * 0.8f),
                Color = tint,
            };
            _particleNext = (_particleNext + 1) % Particles;
        }
    }

    /// <summary>Retained heat and soot, independent of the last wisp's fading throttle.</summary>
    internal static float4 Response(RockExhaustLook look, float age, float strength)
    {
        var heat = Math.Max(0, 1 - age / look.HotSeconds);
        var scorch = look.Scorch * Math.Clamp((look.FadeSeconds - age) / (look.FadeSeconds - look.ScorchHoldSeconds), 0, 1);
        var shimmer = look.Shimmer * Math.Max(0, 1 - age / 0.15f);
        var glow = new Vector3(1, 0.19f, 0.025f) * (look.Glow * heat) + new Vector3(0.25f, 0.55f, 1) * shimmer;
        return new(glow.X * strength, glow.Y * strength, glow.Z * strength, scorch * strength);
    }

    /// <summary>Four exact 16-bit numeric masks avoid NaNs and denormal float bit patterns in vertex input.</summary>
    public static void Include(ref float4 mask, int mark)
    {
        var bit = 1 << (mark % 16);
        switch (mark / 16)
        {
            case 0: mask.X += bit; break;
            case 1: mask.Y += bit; break;
            case 2: mask.Z += bit; break;
            case 3: mask.W += bit; break;
        }
    }

    private void Write(Vector3 point, float radius, Vector3 right, Vector3 up, Vector3 color, float alpha, float kind)
    {
        Instances[_outputCount++] = new RockEffectInstance
        {
            PositionSize = new(point.X, point.Y, point.Z, radius),
            Right = new(right.X, right.Y, right.Z, 0),
            Up = new(up.X, up.Y, up.Z, 0),
            Color = new(color.X, color.Y, color.Z, alpha),
            Shape = new(kind, (float)(_time % 1000), 0, 0)
        };
    }
    private float Random() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return (_random & 0xffffff) / 16777216f; }
    private static Vector3 Tangent(Vector3 normal) => Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
}
