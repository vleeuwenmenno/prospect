using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Prospect.Game.World;

/// <summary>Versioned canonical design data. Only catalogue names and box geometry cross this boundary.</summary>
internal static class DesignBytes
{
    /// <summary>
    /// Version 3 added each block's facing, one byte after its shape. Version 4 adds the parts mounted on block
    /// faces after the blocks: a count, then for each its type, parent block, face and place on the face, in the
    /// order of <see cref="FaceMounts.Compare"/>. Version 5 adds each mount's action group, one byte after its place.
    /// Version 6 adds each mount's size (<see cref="DesignMount.Size"/>), one byte after its group.
    /// </summary>
    internal const int Version = 6;

    /// <summary>The fewest bytes a mount takes: a one-byte name with its length, the parent, the face, two positions, the group and the size.</summary>
    private const int MinMountBytes = 2 + 4 + 1 + 2 + 2 + 1 + 1;

    /// <summary>The fewest bytes a block takes: a one-byte name with its length, six integers, a shape and a facing.</summary>
    private const int MinBlockBytes = 2 + 24 + 1 + 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(HullDesign design)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(Version);
        Text(writer, design.Name);
        writer.Write((byte)design.Class);
        writer.Write(design.BlockSize);
        writer.Write(design.Color.X); writer.Write(design.Color.Y); writer.Write(design.Color.Z);
        writer.Write(design.BlockCount);
        foreach (ref readonly var box in design.Boxes)
        {
            Text(writer, box.Type.Id);
            writer.Write(box.At.X); writer.Write(box.At.Y); writer.Write(box.At.Z);
            writer.Write(box.Size.X); writer.Write(box.Size.Y); writer.Write(box.Size.Z); writer.Write((byte)box.Shape); writer.Write((byte)box.Facing);
        }
        writer.Write(design.Mounts.Length);
        foreach (ref readonly var mount in design.Mounts)
        {
            Text(writer, mount.Type.Id);
            writer.Write(mount.Parent); writer.Write((byte)mount.Face); writer.Write(checked((ushort)mount.U)); writer.Write(checked((ushort)mount.V)); writer.Write(mount.Group); writer.Write(mount.Size);
        }
        return stream.ToArray();
    }

    internal static HullDesign Decode(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> hash, GameContent content)
    {
        if (bytes.Length > content.BlockTransfers.MaxBytes || hash.Length != SHA256.HashSizeInBytes)
            throw new InvalidDataException("Design exceeds transfer bounds.");
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, actual);
        if (!actual.SequenceEqual(hash)) throw new InvalidDataException("Design hash mismatch.");
        var reader = new Reader(bytes);
        if (reader.Int() != Version) throw new InvalidDataException("Unknown design encoding.");
        var name = reader.Text();
        var kind = (HullClass)reader.Byte();
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown hull class.");
        var size = reader.Float();
        var color = new System.Numerics.Vector3(reader.Float(), reader.Float(), reader.Float());
        var count = reader.Int();
        // Keep live geometry admission separate from the wire's representation.
        if (count < 1 || count > content.Blocks.Limits.MaxBlocks || count > reader.Left / MinBlockBytes)
            throw new InvalidDataException("Design block count exceeds live admission bounds.");
        GeometryAdmission.Count(count, content.Blocks.Limits);
        var boxes = new DesignBox[count];
        for (var i = 0; i < count; i++)
        {
            var type = content.Blocks.Get(reader.Text());
            var at = new GridSize(reader.Int(), reader.Int(), reader.Int());
            var extent = new GridSize(reader.Int(), reader.Int(), reader.Int());
            var shape = (BlockShape)reader.Byte();
            var facing = (BlockFacing)reader.Byte();
            if (!Facings.IsValid(facing)) throw new InvalidDataException("Unknown block facing.");
            boxes[i] = new DesignBox(at, extent, type, shape, facing);
        }
        var mountCount = reader.Int();
        if (mountCount < 0 || mountCount > content.Blocks.Limits.MaxMounts || mountCount > reader.Left / MinMountBytes)
            throw new InvalidDataException("Design mount count exceeds live admission bounds.");
        var mounts = new DesignMount[mountCount];
        for (var i = 0; i < mountCount; i++)
        {
            var type = content.Blocks.Get(reader.Text());
            var parent = reader.Int();
            var face = (BlockFacing)reader.Byte();
            if (!Facings.IsValid(face)) throw new InvalidDataException("Unknown mount face.");
            var u = reader.UShort();
            var v = reader.UShort();
            var group = reader.Byte();
            if (!ActionGroups.Allowed(type, group)) throw new InvalidDataException("A mount's action group is not one its part may have.");
            var units = reader.Byte();
            if (!FaceMounts.SizeAllowed(type, units)) throw new InvalidDataException("A mount's size is not one its part is made in.");
            mounts[i] = new DesignMount(parent, face, u, v, type, group, units);
        }
        if (reader.Left != 0) throw new InvalidDataException("Trailing design bytes.");
        // Checked before the design is evaluated or its shield built: it costs one pass over the boxes.
        BoxDesign.RequireOneDriveKind(boxes, "received design");
        // The design puts its mounts in order; bytes that list them in another are refused below as noncanonical.
        var design = HullDesign.Of(new BoxDesign(name, size, boxes, content.Blocks.Limits, "received design", kind, color, mounts), content.Rules);
        GeometryAdmission.Validate(design, content.Blocks.Limits);
        // Re-encoding prevents alternate encodings from naming the same accepted data differently.
        if (!Encode(design).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Noncanonical design bytes.");
        // What a hull can do is not a condition: one without thrust, torque or power is built, and lies still,
        // does not turn or has nothing to run on. Only figures that are no numbers are refused.
        ValidateFigures(design.Flight);
        ValidateFigures(design.Stats);
        foreach (var points in design.IntactHitPoints)
            if (!float.IsFinite(points)) throw new InvalidDataException("Nonfinite block hit points.");
        return design;
    }

    private static void ValidateFigures<T>(T value) where T : struct
    {
        // This runs only on the decode worker, never on the simulation or render thread.
        foreach (var field in typeof(T).GetFields())
        {
            var item = field.GetValue(value);
            if (item is float f && !float.IsFinite(f) || item is double d && !double.IsFinite(d)
                || item is System.Numerics.Vector3 v && (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
                || item is ThrustTable table && !table.IsFinite || item is FuelTanks tanks && !tanks.IsFinite
                || item is DirectionFigures figures && !figures.IsFinite)
                throw new InvalidDataException("Nonfinite design figures.");
        }
    }

    private static void Text(BinaryWriter writer, string text)
    {
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length is 0 or > 255) throw new InvalidDataException("Design text must contain 1..255 UTF-8 bytes.");
        writer.Write((byte)bytes.Length); writer.Write(bytes);
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;
        internal int Left => _bytes.Length;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _bytes.Length) throw new InvalidDataException("Truncated design.");
            var result = _bytes[..count]; _bytes = _bytes[count..]; return result;
        }
        internal byte Byte() => Take(1)[0];
        internal int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal ushort UShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        internal float Float()
        {
            var value = BitConverter.Int32BitsToSingle(Int());
            if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite design number.");
            return value;
        }
        internal string Text()
        {
            var count = Byte();
            if (count == 0) throw new InvalidDataException("Empty design text.");
            try { return Utf8.GetString(Take(count)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid design UTF-8.", error); }
        }
    }
}
