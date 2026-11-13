using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Prospect.Game.World;

/// <summary>Versioned canonical design data. Only catalogue names and box geometry cross this boundary.</summary>
internal static class DesignBytes
{
    internal const int Version = 2;
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
            writer.Write(box.Size.X); writer.Write(box.Size.Y); writer.Write(box.Size.Z); writer.Write((byte)box.Shape);
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
        if (count < 1 || count > content.Blocks.Limits.MaxBlocks || count > reader.Left / 27)
            throw new InvalidDataException("Design block count exceeds live admission bounds.");
        GeometryAdmission.Count(count, content.Blocks.Limits);
        var boxes = new DesignBox[count];
        for (var i = 0; i < count; i++)
        {
            var type = content.Blocks.Get(reader.Text());
            var at = new GridSize(reader.Int(), reader.Int(), reader.Int());
            var extent = new GridSize(reader.Int(), reader.Int(), reader.Int());
            boxes[i] = new DesignBox(at, extent, type, (BlockShape)reader.Byte());
        }
        if (reader.Left != 0) throw new InvalidDataException("Trailing design bytes.");
        // Checked before the design is evaluated or its shield built: it costs one pass over the boxes.
        BoxDesign.RequireOneDriveKind(boxes, "received design");
        var design = HullDesign.Of(new BoxDesign(name, size, boxes, content.Blocks.Limits, "received design", kind, color), content.Rules);
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
                || item is System.Numerics.Vector3 v && (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)))
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
