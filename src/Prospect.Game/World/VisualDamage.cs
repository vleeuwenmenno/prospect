using System.Numerics;
using System.Runtime.CompilerServices;
using Prospect.Engine.Net;

namespace Prospect.Game.World;

/// <summary>A surface point in a canonical face basis, and a bounded inward depth fraction.</summary>
public struct DentProfile
{
    /// <summary>Immutable blueprint block index.</summary>
    public int Block;
    /// <summary>Canonical face number and quantized damage depth/strength.</summary>
    public byte Face, Depth;
    /// <summary>Face edge coefficients in units of 1/65535.</summary>
    public ushort U, V;

    /// <summary>Decodes the point relative to the immutable design pivot. Depth is Depth/255 of the configured maximum.</summary>
    public readonly Vector3 Point(HullDesign design)
    {
        Span<int> corners = stackalloc int[4];
        var n = BlockShapes.Face(design.Boxes[Block].Shape, Face, corners);
        var half = design.BlockHalfExtents[Block];
        var a = BlockShapes.Corner(corners[0], half);
        return design.BlockOffsets[Block] + a + (BlockShapes.Corner(corners[1], half) - a) * (U / 65535f)
            + (BlockShapes.Corner(corners[n - 1], half) - a) * (V / 65535f);
    }

    internal readonly bool Valid(HullDesign design)
    {
        if ((uint)Block >= design.BlockCount || Face > 5 || Depth == 0) return false;
        Span<int> corners = stackalloc int[4];
        var n = BlockShapes.Face(design.Boxes[Block].Shape, Face, corners);
        return n >= 3 && (n != 3 || (uint)U + V <= 65535);
    }

    internal static DentProfile At(HullDesign design, int block, Vector3 point, Vector3 normal, byte depth)
    {
        var shape = design.Boxes[block].Shape;
        var half = design.BlockHalfExtents[block];
        Span<int> corners = stackalloc int[4];
        var face = 0; var best = float.NegativeInfinity;
        for (var f = 0; f < 6; f++)
        {
            if (BlockShapes.Face(shape, f, corners) < 3) continue;
            var dot = Vector3.Dot(BlockShapes.FaceNormal(shape, half, f), normal);
            if (dot > best) { face = f; best = dot; }
        }
        var n = BlockShapes.Face(shape, face, corners);
        var a = BlockShapes.Corner(corners[0], half);
        var u = BlockShapes.Corner(corners[1], half) - a;
        var v = BlockShapes.Corner(corners[n - 1], half) - a;
        var p = point - design.BlockOffsets[block] - a;
        var uu = Vector3.Dot(u, u); var vv = Vector3.Dot(v, v); var uv = Vector3.Dot(u, v);
        var determinant = uu * vv - uv * uv;
        var x = Math.Clamp((Vector3.Dot(p, u) * vv - Vector3.Dot(p, v) * uv) / determinant, 0, 1);
        var y = Math.Clamp((Vector3.Dot(p, v) * uu - Vector3.Dot(p, u) * uv) / determinant, 0, 1);
        if (n == 3 && x + y > 1) { var sum = x + y; x /= sum; y /= sum; }
        return new DentProfile { Block = block, Face = (byte)face, Depth = depth, U = (ushort)(x * 65535), V = (ushort)(y * 65535) };
    }

    internal readonly void Write(ref NetWriter w) { w.WriteInt32(Block); w.WriteByte(Face); w.WriteUInt16(U); w.WriteUInt16(V); w.WriteByte(Depth); }
    internal static DentProfile Read(ref NetReader r) => new() { Block = r.ReadInt32(), Face = r.ReadByte(), U = r.ReadUInt16(), V = r.ReadUInt16(), Depth = r.ReadByte() };
}

/// <summary>Latest authoritative contact; sequence changes once per tick, strongest contact wins within a tick.</summary>
public struct CollisionSpark
{
    /// <summary>Monotone sequence scoped to this hull identity.</summary>
    public uint Sequence;
    /// <summary>Authoritative world tick of the contact.</summary>
    public long Tick;
    /// <summary>Surface location, outward face and quantized strength.</summary>
    public DentProfile Contact;
    internal readonly void Write(ref NetWriter w) { w.WriteUInt32(Sequence); w.WriteInt64(Tick); Contact.Write(ref w); }
    internal static CollisionSpark Read(ref NetReader r) => new() { Sequence = r.ReadUInt32(), Tick = r.ReadInt64(), Contact = DentProfile.Read(ref r) };
}

/// <summary>Fixed value-owned visual state. Full capacity retains existing blocks; physical damage is never capped.</summary>
public struct VisualDamageState
{
    /// <summary>Hard cap on retained block profiles.</summary>
    public const int Capacity = 16;
    /// <summary>Maximum canonical bytes before transfer-word padding.</summary>
    public const int WireBytes = 1 + Capacity * 10;
    /// <summary>Independent monotone profile revision; zero means pristine.</summary>
    public uint Version;
    /// <summary>Number of valid profiles.</summary>
    public byte Count;
    /// <summary>Inline value storage, copied with snapshots.</summary>
    [InlineArray(Capacity)]
    public struct Profiles { private DentProfile _element0; }
    /// <summary>Profiles in stable insertion order; read only the counted prefix.</summary>
    public Profiles Dents;
    /// <summary>Independent event metadata, not part of full-state recovery.</summary>
    public CollisionSpark Spark;

    internal void Hit(HullDesign design, int block, float damage, Vector3 point, Vector3 normal, long tick, bool retain = true)
    {
        var ratio = damage / design.BlockHitPoints(block);
        if (!float.IsFinite(ratio) || ratio < design.CollisionRules.DentThreshold || !float.IsFinite(point.LengthSquared()) || !float.IsFinite(normal.LengthSquared())) return;
        var depth = (byte)Math.Clamp((int)(Math.Min(1f, ratio / design.CollisionRules.DentFullDamage) * 255), 1, 255);
        var profile = DentProfile.At(design, block, point, normal, depth);
        if (Spark.Sequence == 0 || Spark.Tick != tick) Spark = new CollisionSpark { Sequence = Spark.Sequence == uint.MaxValue ? uint.MaxValue : Spark.Sequence + 1, Tick = tick, Contact = profile };
        else if (depth > Spark.Contact.Depth) Spark.Contact = profile;
        if (!retain || Version == uint.MaxValue) return;
        for (var i = 0; i < Count; i++)
        {
            if (Dents[i].Block != block) continue;
            if (depth > Dents[i].Depth) { Dents[i] = profile; Version++; }
            return;
        }
        if (Count == Capacity) return;
        Dents[Count++] = profile; Version++;
    }

    internal void Prune(ReadOnlySpan<ulong> alive)
    {
        if (Version == uint.MaxValue) return;
        for (var i = Count - 1; i >= 0; i--)
        {
            if (BlockSet.Has(alive, Dents[i].Block)) continue;
            for (var j = i + 1; j < Count; j++) Dents[j - 1] = Dents[j];
            Count--; if (Version != uint.MaxValue) Version++;
        }
    }

    internal readonly void Write(ref NetWriter w)
    {
        w.WriteByte(Count);
        for (var i = 0; i < Count; i++) Dents[i].Write(ref w);
    }

    internal static bool Read(ref NetReader r, HullDesign design, uint version, out VisualDamageState state)
    {
        state = new VisualDamageState { Version = version, Count = r.ReadByte() };
        if (state.Count > Capacity) return false;
        for (var i = 0; i < state.Count; i++)
        {
            var p = DentProfile.Read(ref r);
            if (!p.Valid(design)) return false;
            for (var j = 0; j < i; j++) if (state.Dents[j].Block == p.Block) return false;
            state.Dents[i] = p;
        }
        return r.IsValid;
    }
}

internal struct VisualContact
{
    internal int Block;
    internal Vector3 Point, Normal;
}
