using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>A container's immutable size, in cargo-grid units, and empty shell strength.</summary>
public readonly record struct ContainerSize(string Name, int Units, GridSize Shape, float Hull);

/// <summary>Shared container geometry. One ship cell is one metre; drone cells do not redefine cargo units.</summary>
public sealed class CargoCatalog
{
    private readonly ContainerSize[] _sizes;
    /// <summary>Metres per unit edge, relative to the one-metre ship cell.</summary>
    public float UnitSize { get; }
    /// <summary>Sizes in ascending volume order.</summary>
    public ReadOnlySpan<ContainerSize> Sizes => _sizes;
    private CargoCatalog(float unitSize, ContainerSize[] sizes) { UnitSize = unitSize; _sizes = sizes; }

    /// <summary>Reads and validates doubling sizes, positive strength and exact integer box volumes.</summary>
    public static CargoCatalog Parse(ReadOnlyMemory<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var unit = root.GetProperty("unitSize"u8).GetSingle();
        var entries = root.GetProperty("sizes"u8);
        if (!float.IsFinite(unit) || unit < 0.0625f || unit > 1 || entries.GetArrayLength() != 8)
            throw new InvalidDataException("Invalid cargo unit size or size count.");
        var sizes = new ContainerSize[8];
        for (var i = 0; i < sizes.Length; i++)
        {
            var e = entries[i];
            var shape = e.GetProperty("shape"u8);
            if (shape.GetArrayLength() != 3) throw new InvalidDataException("Cargo shape needs three sides.");
            var s = new GridSize(shape[0].GetInt32(), shape[1].GetInt32(), shape[2].GetInt32());
            var units = e.GetProperty("units"u8).GetInt32();
            var hull = e.GetProperty("hull"u8).GetSingle();
            var name = e.GetProperty("name"u8).GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 32 || units != (1 << i)
                || s.X < 1 || s.Y < 1 || s.Z < 1 || s.X > units || s.Y > units || s.Z > units
                || (long)s.X * s.Y * s.Z != units || !float.IsFinite(hull) || hull <= 0
                || (i > 0 && hull < sizes[i - 1].Hull))
                throw new InvalidDataException("Invalid cargo size, shape or hull points.");
            for (var j = 0; j < i; j++)
                if (sizes[j].Name == name) throw new InvalidDataException("Duplicate cargo size name.");
            sizes[i] = new ContainerSize(name, units, s, hull);
        }
        return new CargoCatalog(unit, sizes);
    }
}

/// <summary>A packed container, indexed by size, at its minimum unit-grid corner.</summary>
public readonly record struct CargoPlacement(int Size, GridSize At);

/// <summary>Largest first, fixed orientation, first fit with X fastest, then Z, then Y. No allocation or rotation.</summary>
public static class CargoPacking
{
    /// <summary>Usable whole unit-grid dimensions; any fractional strip remains unused.</summary>
    public static GridSize Grid(CargoCatalog cargo, GridSize cells, float blockSize)
    {
        if (!float.IsFinite(blockSize) || blockSize <= 0 || cells.X <= 0 || cells.Y <= 0 || cells.Z <= 0)
            return default;
        var x = Math.Floor(cells.X * (double)blockSize / cargo.UnitSize);
        var y = Math.Floor(cells.Y * (double)blockSize / cargo.UnitSize);
        var z = Math.Floor(cells.Z * (double)blockSize / cargo.UnitSize);
        if (x > int.MaxValue || y > int.MaxValue || z > int.MaxValue || x * y * z > int.MaxValue) return default;
        return new((int)x, (int)y, (int)z);
    }

    /// <summary>Capacity in u, including only whole unit slots.</summary>
    public static int Capacity(CargoCatalog cargo, GridSize cells, float blockSize)
    {
        var g = Grid(cargo, cells, blockSize);
        return g.X * g.Y * g.Z;
    }

    /// <summary>Largest size index fitting the fixed orientation, or -1.</summary>
    public static int Largest(CargoCatalog cargo, GridSize cells, float blockSize)
    {
        var g = Grid(cargo, cells, blockSize);
        for (var i = cargo.Sizes.Length - 1; i >= 0; i--)
        {
            var s = cargo.Sizes[i].Shape;
            if (s.X <= g.X && s.Y <= g.Y && s.Z <= g.Z) return i;
        }
        return -1;
    }

    /// <summary>
    /// Packs size indices into caller storage, in canonical descending size order. Occupancy needs Capacity bytes.
    /// False means invalid input, insufficient buffers or no fit; output then has no usable placements.
    /// </summary>
    public static bool TryPack(CargoCatalog cargo, GridSize cells, float blockSize, ReadOnlySpan<int> sizes,
        Span<byte> occupied, Span<CargoPlacement> output)
    {
        var g = Grid(cargo, cells, blockSize);
        var capacity = Capacity(cargo, cells, blockSize);
        if (capacity == 0 || occupied.Length < capacity || output.Length < sizes.Length) return false;
        long total = 0;
        foreach (var i in sizes)
        {
            if ((uint)i >= (uint)cargo.Sizes.Length) return false;
            total += cargo.Sizes[i].Units;
        }
        if (total > capacity) return false;
        occupied[..capacity].Clear();
        var count = 0;
        for (var size = cargo.Sizes.Length - 1; size >= 0; size--)
        {
            var s = cargo.Sizes[size].Shape;
            foreach (var requested in sizes)
            {
                if (requested != size) continue;
                var found = false;
                for (var y = 0; y <= g.Y - s.Y && !found; y++)
                    for (var z = 0; z <= g.Z - s.Z && !found; z++)
                        for (var x = 0; x <= g.X - s.X && !found; x++)
                        {
                            var free = true;
                            for (var dy = 0; dy < s.Y && free; dy++)
                                for (var dz = 0; dz < s.Z && free; dz++)
                                    for (var dx = 0; dx < s.X; dx++)
                                        if (occupied[x + dx + g.X * (z + dz + g.Z * (y + dy))] != 0) { free = false; break; }
                            if (!free) continue;
                            for (var dy = 0; dy < s.Y; dy++)
                                for (var dz = 0; dz < s.Z; dz++)
                                    occupied.Slice(x + g.X * (z + dz + g.Z * (y + dy)), s.X).Fill(1);
                            output[count++] = new CargoPlacement(size, new GridSize(x, y, z));
                            found = true;
                        }
                if (!found) { output[..count].Clear(); return false; }
            }
        }
        return true;
    }
}
