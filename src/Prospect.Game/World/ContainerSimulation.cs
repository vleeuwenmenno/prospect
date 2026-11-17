using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Bounded, allocation-free loose cargo contacts, using shared hull and asteroid geometry.</summary>
public sealed class ContainerSimulation
{
    private readonly SectorContent _sector = new();
    private readonly ulong[] _touching = new ulong[BlockSet.WordCount(GeometryAdmission.MaxBlocks)];
    private bool _hasSector;
    private SectorCoord _coord;
    private int _first;

    /// <summary>Reserves generation buffers before the server starts ticking.</summary>
    public ContainerSimulation(Galaxy galaxy) => galaxy.Reserve(_sector);

    /// <summary>Advances cargo and processes a capped set of contacts in rotating container order.</summary>
    public void Step(FreeContainers containers, GameContent content, HullField field, RockField rocks, long tick)
    {
        if (containers.Items.IsEmpty) return;
        containers.Drift();
        var budget = content.Cargo.SpaceContactBudget;
        var count = containers.Items.Length;
        // At most one sector is generated per tick. Other sectors get their turn on the next tick.
        var first = _first % count;
        _first = (first + 1) % count;
        var sector = rocks.Galaxy.SectorOf(containers.Items[first].Position);
        if (!_hasSector || sector != _coord)
        {
            rocks.Galaxy.Generate(sector, _sector);
            _coord = sector; _hasSector = true;
        }
        for (var n = 0; n < count && budget > 0; n++)
        {
            var c = containers.At((first + n) % count);
            var half = FreeContainers.Half(content.Cargo, c.Size);
            for (var h = 0; h < field.Hulls.Slots && budget > 0; h++)
            {
                if (!field.Hulls.IsAliveAt(h)) continue;
                // A container the hull's own tractor is guiding into a rack passes the hull's blocks: the beam
                // steers it through the open face, and a jam against the ridge between it and the slot was the bug.
                // Rocks and other hulls still hit it.
                if (c.TractorPowered && c.TractorHull == field.Hulls.HandleAt(h).Packed) continue;
                ref var state = ref field.Hulls.State(h);
                if (state.Jump == JumpPhase.Tunnel) continue;
                var design = field.Designs[field.Hulls.Info(h).Design];
                var inverse = Quaternion.Conjugate(state.Orientation);
                var local = HullRotation.Rotate(c.Position.RelativeTo(state.Position), inverse);
                var extent = BoxCollision.Bounds(half, inverse * c.Orientation);
                var cursor = 0;
                while (design.Collision.Next(ref cursor, local - extent, local + extent, field.Hulls.Blocks(h), ref budget, out var b))
                {
                    var position = state.Position + HullRotation.Rotate(design.BlockOffsets[b], state.Orientation);
                    var before = c.Velocity;
                    var hp = c.Hull;
                    ref var flight = ref field.Hulls.Flight(h);
                    if (!FreeContainers.BoxContact(ref c, content.Cargo, position, state.Orientation, design.BlockHalfExtents[b],
                        design.Boxes[b].Shape, state.Velocity, flight.CollisionSafeSpeed, flight.CollisionDamagePerTonneSpeedSquared, out var normal)) continue;
                    var mass = (float)(c.Amount / 1000) + content.Cargo.SpaceShellKg * content.Cargo.Sizes[c.Size].Units / 1000;
                    var fraction = flight.Mass / (flight.Mass + mass);
                    var delta = (c.Velocity - before) * fraction;
                    c.Velocity = before + delta;
                    state.Velocity -= delta * (mass / flight.Mass);
                    _touching.AsSpan().Clear(); BlockSet.Add(_touching, b);
                    var body = field.Body(h);
                    HullSim.ShieldContact(ref state, flight, design, _touching, Math.Max(0, -Vector3.Dot(before - state.Velocity, normal)),
                        design.BlockOffsets[b], mass * fraction);
                    var closing = Math.Max(0, -Vector3.Dot(before - state.Velocity, normal));
                    var excess = Math.Max(0, closing - flight.CollisionSafeSpeed);
                    var damage = excess * excess * mass * fraction * flight.CollisionDamagePerTonneSpeedSquared;
                    c.Hull = Math.Max(0, hp - damage);
                    if (damage > 0) HullSim.Damage(ref state, flight, ref body, _touching, 1, damage, design.BlockOffsets[b]);
                    field.Refit(h);
                    break;
                }
            }
            var slot = containers.Find(c.Id);
            if (slot < 0) continue;
            containers.At(slot) = c;
            if (rocks.Galaxy.SectorOf(c.Position) != _coord) continue;
            var origin = rocks.Galaxy.CenterOf(_coord);
            for (var r = 0; r < _sector.AsteroidCount && budget > 0; r++)
            {
                ref var rock = ref _sector.Asteroids[r];
                rocks.Displacement(new(_coord, r), tick, out var offset, out var velocity);
                var position = origin + rock.LocalPosition + offset;
                var reach = rock.Radius + half.Length();
                if (c.Position.RelativeTo(position).LengthSquared() > reach * reach) continue;
                budget--;
                FreeContainers.RockContact(ref c, content.Cargo, rock, position, velocity,
                    content.Rules.CollisionSafeSpeed, content.Rules.CollisionDamagePerTonneSpeedSquared,
                    rocks.Edits is { } edits ? edits.CutOf(new RockRef(_coord, r)) : default);
            }
            slot = containers.Find(c.Id);
            if (slot >= 0) containers.At(slot) = c;
        }
        for (var i = containers.Items.Length - 1; i >= 0; i--)
            if (containers.Items[i].Hull <= 0) containers.Remove(i);
        containers.EnforceCaps(content);
    }
}
