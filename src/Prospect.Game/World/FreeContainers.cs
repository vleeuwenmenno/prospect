using System.Numerics;

namespace Prospect.Game.World;

/// <summary>A server-owned loose container, independent of hull slots. Clocks count simulation ticks.</summary>
public struct FreeContainer
{
    public uint Id;
    /// <summary>Transient exclusive tractor claim; loading restarts acquisition.</summary>
    public uint TractorHull;
    public int TractorMount, TractorRack;
    /// <summary>0 while travelling to the staging point, 1 on the final glide into the slot.</summary>
    public byte TractorStage;
    public bool TractorPowered;
    /// <summary>Server-only, never sent or saved: the least path left so far (0 = not set) and the ticks without gaining on it.</summary>
    public float TractorBest;
    public int TractorIdle;
    /// <summary>
    /// Server-only, never sent or saved: the leg the beam last pulled the body along (0 none yet, 1 to the staging
    /// point, 2 by way of the emitter's exit, 3 the final glide). A change of leg sets <see cref="TractorBest"/> to the
    /// new leg's length; nothing else may raise it, so a body that only jitters on one leg still reaches the stall.
    /// </summary>
    public byte TractorRoute;
    /// <summary>
    /// Server-only, never sent or saved: the hull whose drones are fetching this body (packed handle, zero for none)
    /// and the tractor mount they hand it to. While set, no tractor acquires it and no other drones are sent for it.
    /// </summary>
    public uint FetchHull;
    public int FetchMount;
    public Double3 Position;
    public Vector3 Velocity, AngularVelocity;
    public Quaternion Orientation;
    public int Size, Resource;
    public double Amount;
    public float Hull;
    public long Born, Age;
    /// <summary>First authoritative tick on which tractors may acquire this body.</summary>
    public long PickupReadyTick;
    /// <summary>Durable hull owner identity; zero means anyone may pick it up.</summary>
    public uint ClaimOwner;
    /// <summary>Shared pickup gate for simulation and client previews.</summary>
    public readonly bool MayPickup(uint owner, long tick) => tick >= PickupReadyTick && (ClaimOwner == 0 || ClaimOwner == owner);
}

/// <summary>Fixed storage for loose cargo. Stable ids are never reused within a world.</summary>
public sealed class FreeContainers
{
    /// <summary>Storage and replication ceiling, independent of the 255 hull snapshot records.</summary>
    public const int Capacity = 256;
    private readonly FreeContainer[] _items = new FreeContainer[Capacity];
    private int _count;
    private uint _next = 1;
    /// <summary>The occupied prefix, in insertion order.</summary>
    public ReadOnlySpan<FreeContainer> Items => _items.AsSpan(0, _count);
    /// <summary>The next durable identity; zero means identity space is exhausted.</summary>
    public uint NextId => _next;
    /// <summary>Mutable access for the authoritative simulation only.</summary>
    public ref FreeContainer At(int index) => ref _items.AsSpan(0, _count)[index];

    /// <summary>Validates before evicting the oldest item in a full sector or full world.</summary>
    public bool Add(GameContent content, in FreeContainer item, out uint id)
    {
        id = 0;
        if (_next == 0 || !Valid(content, item) || item.Born < 0 || item.Age < 0) return false;
        var sector = content.Galaxy.SectorOf(item.Position);
        var count = 0;
        var oldest = -1;
        for (var i = 0; i < _count; i++)
            if (content.Galaxy.SectorOf(_items[i].Position) == sector)
            {
                count++;
                if (oldest < 0 || Older(_items[i], _items[oldest])) oldest = i;
            }
        if (count >= content.Cargo.SpacePerSector) Remove(oldest);
        if (_count == Capacity)
        {
            oldest = 0;
            for (var i = 1; i < _count; i++) if (Older(_items[i], _items[oldest])) oldest = i;
            Remove(oldest);
        }
        _items[_count] = item;
        id = _items[_count++].Id = _next++;
        return true;
    }

    /// <summary>Restores a validated snapshot while preserving identities and the exhausted counter state.</summary>
    public void Restore(ReadOnlySpan<FreeContainer> items, uint next)
    {
        if (items.Length > Capacity) throw new InvalidDataException("Too many free containers.");
        items.CopyTo(_items); _count = items.Length; _next = next;
        for (var i = 0; i < _count; i++) { ClearTractor(ref _items[i]); ClearFetch(ref _items[i]); }
    }

    /// <summary>Lets go of a body drones were fetching: it stays where it is, with the motion it has.</summary>
    public static void ClearFetch(ref FreeContainer item) { item.FetchHull = 0; item.FetchMount = 0; }

    /// <summary>Drops transient claim state on load or when the pulling hull is not visible to a recipient.</summary>
    public static void ClearTractor(ref FreeContainer item)
    { item.TractorHull = 0; item.TractorMount = 0; item.TractorRack = 0; item.TractorStage = 0; item.TractorPowered = false; item.TractorBest = 0; item.TractorIdle = 0; item.TractorRoute = 0; }

    private static bool Older(in FreeContainer a, in FreeContainer b) => a.Born < b.Born || a.Born == b.Born && a.Id < b.Id;

    /// <summary>Removes one item without changing any surviving identity.</summary>
    public void Remove(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count);
        _items.AsSpan(index + 1, _count - index - 1).CopyTo(_items.AsSpan(index));
        _items[--_count] = default;
    }

    /// <summary>Finds a stable identity, or -1 when it is absent.</summary>
    public int Find(uint id)
    {
        for (var i = 0; i < _count; i++) if (_items[i].Id == id) return i;
        return -1;
    }

    /// <summary>Checks physical state and payload before admission, decoding or restoration.</summary>
    public static bool Valid(GameContent content, in FreeContainer item) =>
        item.PickupReadyTick >= 0 && item.PickupReadyTick <= long.MaxValue / 2
        && double.IsFinite(item.Position.X) && double.IsFinite(item.Position.Y) && double.IsFinite(item.Position.Z)
        && Finite(item.Velocity) && Finite(item.AngularVelocity)
        && float.IsFinite(item.Orientation.LengthSquared()) && Math.Abs(item.Orientation.LengthSquared() - 1) < .001f
        && BlockStateStore.CargoPayloadValid(content, new(0, item.Size, item.Resource, item.Amount, item.Hull, default));

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.LengthSquared());

    /// <summary>Applies damage once; a destroyed shell and all its contents disappear together.</summary>
    public bool Damage(uint id, float amount)
    {
        var index = Find(id);
        if (index < 0 || !float.IsFinite(amount) || amount <= 0) return false;
        _items[index].Hull = Math.Max(0, _items[index].Hull - amount);
        if (_items[index].Hull == 0) Remove(index);
        return true;
    }

    /// <summary>Advances every body by one shared fixed interval without taking memory.</summary>
    public void Drift()
    {
        for (var i = 0; i < _count; i++)
        {
            ref var c = ref _items[i];
            c.Position += c.Velocity * HullSim.Dt;
            var speed = c.AngularVelocity.Length();
            if (speed > 0)
                c.Orientation = Quaternion.Normalize(c.Orientation * Quaternion.CreateFromAxisAngle(c.AngularVelocity / speed, speed * HullSim.Dt));
        }
    }

    /// <summary>Enforces sector limits after drift across a boundary, oldest first.</summary>
    public void EnforceCaps(GameContent content)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            var sector = content.Galaxy.SectorOf(_items[i].Position);
            var newer = 0;
            for (var j = 0; j < _count; j++)
                if (content.Galaxy.SectorOf(_items[j].Position) == sector && Older(_items[i], _items[j])) newer++;
            if (newer >= content.Cargo.SpacePerSector) Remove(i);
        }
    }

    /// <summary>Accumulates unattended ticks; proximity pauses lifetime but never the sector cap.</summary>
    public void Cleanup(long lifetime, ReadOnlySpan<Double3> players, double radius)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            ref var c = ref _items[i];
            var near = false;
            foreach (var player in players)
            {
                var d = c.Position - player;
                if (d.X * d.X + d.Y * d.Y + d.Z * d.Z <= (double)radius * radius) { near = true; break; }
            }
            if (!near) c.Age++;
            if (lifetime > 0 && c.Age >= lifetime) Remove(i);
        }
    }

    /// <summary>The collision box uses the same dimensions as the reusable container model.</summary>
    public static Vector3 Half(CargoCatalog cargo, int size)
    {
        var s = cargo.Sizes[size].Shape;
        return new Vector3(s.X, s.Y, s.Z) * (cargo.UnitSize * .5f);
    }

    /// <summary>Shared finest-facet rock contact, with no separate container rock approximation.</summary>
    public static bool RockContact(ref FreeContainer c, CargoCatalog cargo, in Asteroid rock, Double3 position,
        Vector3 velocity, float safeSpeed, float damagePerTonne, RockEditing.RockCut cut = default)
    {
        if (!BoxCollision.Rock(c.Position.RelativeTo(position), Half(cargo, c.Size), c.Orientation, rock,
            out var normal, out var depth, out _, BlockShape.Box, cut)) return false;
        c.Position += normal * depth;
        Resolve(ref c, cargo, normal, velocity, safeSpeed, damagePerTonne);
        return true;
    }

    /// <summary>Shared oriented box contact against one live hull block, including wedge geometry.</summary>
    public static bool BoxContact(ref FreeContainer c, CargoCatalog cargo, Double3 position, Quaternion orientation,
        Vector3 half, BlockShape shape, Vector3 velocity, float safeSpeed, float damagePerTonne, out Vector3 normal)
    {
        var inverse = Quaternion.Conjugate(orientation);
        var between = HullRotation.Rotate(c.Position.RelativeTo(position), inverse);
        if (!BoxCollision.Contact(between, Half(cargo, c.Size), inverse * c.Orientation, half,
            BlockShape.Box, shape, out var local, out var depth)) { normal = default; return false; }
        normal = HullRotation.Rotate(local, orientation);
        c.Position += normal * depth;
        Resolve(ref c, cargo, normal, velocity, safeSpeed, damagePerTonne);
        return true;
    }

    private static void Resolve(ref FreeContainer c, CargoCatalog cargo, Vector3 normal, Vector3 otherVelocity,
        float safeSpeed, float damagePerTonne)
    {
        var closing = -Vector3.Dot(c.Velocity - otherVelocity, normal);
        if (closing <= 0) return;
        c.Velocity += normal * (closing * (1 + cargo.SpaceRestitution));
        var excess = Math.Max(0, closing - safeSpeed);
        var tonnes = (float)(c.Amount / 1000) + cargo.SpaceShellKg * cargo.Sizes[c.Size].Units / 1000;
        c.Hull = Math.Max(0, c.Hull - excess * excess * tonnes * damagePerTonne);
    }
}
