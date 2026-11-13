using System.Security.Cryptography;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>
/// A server's design catalogue as it travels: each design's canonical bytes and their hash, and the manifest that
/// lists them. It only grows. A design keeps its number and its bytes for as long as the server runs, and
/// <see cref="Append"/> replaces the arrays rather than writing into them, so a manifest or a design that is being
/// sent stays what it was when the sending began. Used from one thread.
/// </summary>
internal sealed class DesignCatalogue
{
    private readonly BlockTransferRules _rules;
    private readonly byte[] _head;

    /// <summary>The canonical bytes and the SHA-256 of each design, by design number. Never written to.</summary>
    internal byte[][] Bytes { get; private set; }
    internal byte[][] Hashes { get; private set; }

    /// <summary>The count, pod, drone and starting fleet, then each design's size and hash. Never written to.</summary>
    internal byte[] Manifest { get; private set; }

    /// <summary>The canonical bytes of every design together, which the shared budget bounds.</summary>
    internal long TotalBytes { get; private set; }

    internal int Count => Bytes.Length;

    internal DesignCatalogue(GameContent content)
    {
        _rules = content.BlockTransfers;
        Bytes = new byte[content.Designs.Length][];
        Hashes = new byte[Bytes.Length][];
        if (content.StartingFleet.Length > 255) throw new InvalidDataException("Fleet is too large.");
        _head = new byte[4 + content.StartingFleet.Length];
        var writer = new NetWriter(_head);
        writer.WriteByte((byte)Bytes.Length); writer.WriteByte((byte)content.PodDesign);
        writer.WriteByte((byte)content.DroneDesign); writer.WriteByte((byte)content.StartingFleet.Length);
        foreach (var id in content.StartingFleet) writer.WriteByte((byte)id);
        for (var i = 0; i < Bytes.Length; i++)
        {
            GeometryAdmission.Count(content.Designs[i].BlockCount, content.Blocks.Limits);
            if (content.Designs[i].IntactShell.IsEmpty) GeometryAdmission.Validate(content.Designs[i], content.Blocks.Limits);
            Bytes[i] = DesignBytes.Encode(content.Designs[i]);
            TotalBytes += Bytes[i].Length;
            if (!_rules.AllowsCatalogue(TotalBytes)) throw new InvalidDataException("Catalogue exceeds shared resource budgets.");
            if (Bytes[i].Length > _rules.MaxBytes) throw new InvalidDataException("Design exceeds bulk bounds.");
            Hashes[i] = SHA256.HashData(Bytes[i]);
        }
        Manifest = BuildManifest();
        if (Manifest.Length > _rules.MaxBytes) throw new InvalidDataException("Catalogue exceeds bulk bounds.");
    }

    /// <summary>The number of the design with these canonical bytes, by their size and hash; -1 if there is none.</summary>
    internal int Find(ReadOnlySpan<byte> hash, int size)
    {
        for (var i = 0; i < Bytes.Length; i++)
            if (Bytes[i].Length == size && hash.SequenceEqual(Hashes[i])) return i;
        return -1;
    }

    /// <summary>
    /// Whether a design of <paramref name="size"/> canonical bytes can still be added: a number is left, and the
    /// catalogue with it stays within the budgets every client checks a manifest against.
    /// </summary>
    internal bool Allows(int size) =>
        Bytes.Length < byte.MaxValue && size > 0 && size <= _rules.MaxBytes && _rules.AllowsCatalogue(TotalBytes + size)
        && Manifest.Length + 4 + SHA256.HashSizeInBytes <= _rules.MaxBytes;

    /// <summary>
    /// Adds a design at the end and returns its number. The caller hands over <paramref name="bytes"/> and
    /// <paramref name="hash"/> and never writes to them again. Check <see cref="Allows"/> first.
    /// </summary>
    internal int Append(byte[] bytes, byte[] hash)
    {
        if (!Allows(bytes.Length) || hash.Length != SHA256.HashSizeInBytes) throw new InvalidOperationException("The catalogue cannot take the design.");
        var id = Bytes.Length;
        var grownBytes = new byte[id + 1][];
        var grownHashes = new byte[id + 1][];
        Bytes.CopyTo(grownBytes, 0); Hashes.CopyTo(grownHashes, 0);
        grownBytes[id] = bytes; grownHashes[id] = hash;
        Bytes = grownBytes; Hashes = grownHashes;
        TotalBytes += bytes.Length;
        Manifest = BuildManifest();
        return id;
    }

    private byte[] BuildManifest()
    {
        var manifest = new byte[_head.Length + Bytes.Length * (4 + SHA256.HashSizeInBytes)];
        _head.CopyTo(manifest, 0);
        manifest[0] = (byte)Bytes.Length;
        var writer = new NetWriter(manifest.AsSpan(_head.Length));
        for (var i = 0; i < Bytes.Length; i++)
        {
            writer.WriteInt32(Bytes[i].Length); writer.WriteBytes(Hashes[i]);
        }
        return manifest;
    }
}
