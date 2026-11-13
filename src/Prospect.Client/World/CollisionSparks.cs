using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Bounded visual bursts, consumed once even while culled and aged only by server time.</summary>
internal sealed class CollisionSparks
{
    private struct Seen { public uint Id, Sequence; }
    private struct Burst { public bool Live; public Double3 Origin; public Vector3 Velocity, Normal; public long Tick; public uint Seed; }
    private readonly Seen[] _seen = new Seen[64];
    private readonly Burst[] _bursts = new Burst[128];
    private readonly MeshVertex[] _vertices = new MeshVertex[128 * 12 * 6];

    public void Clear() { Array.Clear(_seen); Array.Clear(_bursts); }

    public void Begin(ReadOnlySpan<HullView> hulls)
    {
        for (var i = 0; i < _seen.Length; i++)
        {
            var found = false;
            foreach (ref readonly var hull in hulls) if (hull.Id == _seen[i].Id) { found = true; break; }
            if (!found) _seen[i] = default;
        }
    }

    /// <summary>Call even for far or hidden hulls: their events must never replay on returning nearby.</summary>
    public void Observe(in HullView hull, HullDesign design, Double3 position, Quaternion orientation, bool visible, double tick, CollisionDetail tuning)
    {
        var slot = -1;
        for (var i = 0; i < _seen.Length; i++) if (_seen[i].Id == hull.Id) { slot = i; break; }
        var spark = hull.Visual.Spark;
        if (slot < 0)
        {
            for (var i = 0; i < _seen.Length; i++) if (_seen[i].Id == 0) { _seen[i] = new Seen { Id = hull.Id, Sequence = spark.Sequence }; break; }
            return;
        }
        if (spark.Sequence <= _seen[slot].Sequence) return;
        _seen[slot].Sequence = spark.Sequence;
        var age = (float)((tick - spark.Tick) / TickLoop.SimulationHz);
        if (!visible || age < 0 || age >= tuning.Seconds || (uint)spark.Contact.Block >= design.BlockCount) return;
        var p = spark.Contact;
        var normal = Vector3.Transform(BlockShapes.FaceNormal(design.Shapes[p.Block], design.BlockHalfExtents[p.Block], p.Face), orientation);
        for (var i = 0; i < tuning.SparkCapacity; i++)
        {
            if (_bursts[i].Live && (tick - _bursts[i].Tick) / TickLoop.SimulationHz < tuning.Seconds) continue;
            _bursts[i] = new Burst
            {
                Live = true,
                Origin = position + Vector3.Transform(p.Point(design), orientation) - hull.Velocity * age,
                Velocity = hull.Velocity,
                Normal = normal,
                Tick = spark.Tick,
                Seed = spark.Sequence ^ hull.Id
            };
            return;
        }
    }

    public ReadOnlySpan<MeshVertex> Expand(Double3 camera, Vector3 right, Vector3 up, Vector3 forward, float aspect, float tanHalf, float height, double tick, CollisionDetail tuning)
    {
        var count = 0;
        for (var i = 0; i < tuning.SparkCapacity; i++)
        {
            ref var burst = ref _bursts[i];
            if (!burst.Live) continue;
            var age = (float)((tick - burst.Tick) / TickLoop.SimulationHz);
            if (age < 0 || age >= tuning.Seconds) { burst.Live = false; continue; }
            var center = burst.Origin.RelativeTo(camera) + burst.Velocity * age;
            var radius = tuning.Speed * tuning.Seconds + tuning.Length;
            var z = Vector3.Dot(center, forward);
            if (z + radius <= 0 || Math.Abs(Vector3.Dot(center, right)) > Math.Max(z, 0) * tanHalf * aspect + radius
                || Math.Abs(Vector3.Dot(center, up)) > Math.Max(z, 0) * tanHalf + radius) continue;
            var fade = tuning.Fade(center, radius, radius * height / (Math.Max(center.Length(), 1) * tanHalf));
            if (fade <= 0) continue;
            var normal = burst.Normal;
            var tangent = Vector3.Normalize(Vector3.Cross(normal, Math.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var bitangent = Vector3.Cross(normal, tangent);
            for (var s = 0; s < tuning.Streaks; s++)
            {
                var angle = (s + (burst.Seed & 255) / 255f) * 2.399963f;
                var direction = Vector3.Normalize(normal + tangent * MathF.Cos(angle) * 0.8f + bitangent * MathF.Sin(angle) * 0.8f);
                var head = center + direction * (age * tuning.Speed);
                var tail = head - direction * tuning.Length;
                var cross = Vector3.Cross(forward, direction);
                if (cross.LengthSquared() < 0.001f) cross = right;
                var width = Vector3.Normalize(cross) * (tuning.Width * fade * (1 - age / tuning.Seconds));
                Put(head + width); Put(head - width); Put(tail - width);
                Put(tail - width); Put(tail + width); Put(head + width);
            }
        }
        return _vertices.AsSpan(0, count);
        void Put(Vector3 p) => _vertices[count++] = new MeshVertex { Position = new float3(p.X, p.Y, p.Z), Normal = new float3(0, 1, 0) };
    }
}
