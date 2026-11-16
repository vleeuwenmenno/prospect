using System.Runtime.CompilerServices;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>The cargo rules at capture time, retained to validate payloads before applying a geometry retune.</summary>
public struct SavedCargoRules
{
    public const int WireBytes = 4 + 8 * (12 + 4) + Galaxy.MaxResources * 8;
    [InlineArray(8)] public struct Shapes { private GridSize _first; }
    [InlineArray(8)] public struct Strengths { private float _first; }
    [InlineArray(Galaxy.MaxResources)] public struct Densities { private double _first; }
    public float Unit;
    public Shapes Size;
    public Strengths Hull;
    public Densities Density;

    public static SavedCargoRules Of(GameContent content)
    {
        var value = new SavedCargoRules { Unit = content.Cargo.UnitSize };
        for (var i = 0; i < 8; i++) { value.Size[i] = content.Cargo.Sizes[i].Shape; value.Hull[i] = content.Cargo.Sizes[i].Hull; }
        for (var i = 0; i < content.Galaxy.Resources.Length; i++) value.Density[i] = content.Cargo.Density(content.Galaxy.Resources[i].Id);
        return value;
    }

    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteSingle(Unit);
        for (var i = 0; i < 8; i++)
        { writer.WriteInt32(Size[i].X); writer.WriteInt32(Size[i].Y); writer.WriteInt32(Size[i].Z); writer.WriteSingle(Hull[i]); }
        for (var i = 0; i < Galaxy.MaxResources; i++) writer.WriteDouble(Density[i]);
    }

    public static SavedCargoRules Read(ref NetReader reader)
    {
        var value = new SavedCargoRules { Unit = reader.ReadSingle() };
        for (var i = 0; i < 8; i++)
        { value.Size[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()); value.Hull[i] = reader.ReadSingle(); }
        for (var i = 0; i < Galaxy.MaxResources; i++) value.Density[i] = reader.ReadDouble();
        return value;
    }

    public readonly bool Valid(int resources)
    {
        if (!float.IsFinite(Unit) || Unit < .0625f || Unit > 1) return false;
        for (var i = 0; i < 8; i++)
        {
            var shape = Size[i];
            if (shape.X < 1 || shape.Y < 1 || shape.Z < 1 || shape.X > 128 || shape.Y > 128 || shape.Z > 128
                || shape.X * shape.Y * shape.Z != (1 << i) || !float.IsFinite(Hull[i]) || Hull[i] <= 0) return false;
        }
        for (var i = 0; i < resources; i++)
            if (!double.IsFinite(Density[i]) || Density[i] <= 0 || Density[i] > 1e9) return false;
        return true;
    }

    /// <summary>Validates original bounds and overlaps before any container can be dropped for a retune.</summary>
    public readonly bool ValidHold(HullDesign design, ReadOnlySpan<ulong> alive, int resources, ReadOnlySpan<HoldContainer> hold)
    {
        if (hold.IsEmpty) return true;
        if (!Valid(resources) || hold.Length > BlockStateStore.MaxContainers) return false;
        for (var i = 0; i < hold.Length; i++)
        {
            var c = hold[i];
            if ((uint)c.Rack >= (uint)design.BlockCount || !BlockSet.Has(alive, c.Rack) || design.Types[c.Rack].Cargo <= 0
                || (uint)c.Size >= 8 || c.Resource < -1 || c.Resource >= resources) return false;
            var unit = (double)Unit;
            var capacity = c.Resource < 0 ? 0 : (1 << c.Size) * unit * unit * unit * Density[c.Resource];
            if (!(c.Amount >= 0 && c.Amount <= capacity) || !(c.Hull > 0 && c.Hull <= Hull[c.Size])) return false;
            var shape = Size[c.Size]; var box = design.Boxes[c.Rack];
            var x = Math.Floor(box.Size.X * (double)design.BlockSize / Unit);
            var y = Math.Floor(box.Size.Y * (double)design.BlockSize / Unit);
            var z = Math.Floor(box.Size.Z * (double)design.BlockSize / Unit);
            if (c.At.X < 0 || c.At.Y < 0 || c.At.Z < 0 || c.At.X > x - shape.X || c.At.Y > y - shape.Y || c.At.Z > z - shape.Z) return false;
            for (var j = 0; j < i; j++)
            {
                var b = hold[j]; var bs = Size[b.Size];
                if (b.Rack == c.Rack && c.At.X < b.At.X + bs.X && b.At.X < c.At.X + shape.X
                    && c.At.Y < b.At.Y + bs.Y && b.At.Y < c.At.Y + shape.Y
                    && c.At.Z < b.At.Z + bs.Z && b.At.Z < c.At.Z + shape.Z) return false;
            }
        }
        return true;
    }
}
