namespace Prospect.Client.Render;

/// <summary>Immutable, periodic value-noise lattice. RGB packs warp offsets; alpha is the base scalar field.</summary>
internal static class GasNoiseLattice
{
    /// <summary>Noise cells per axis; matches the period in gas.glsl.</summary>
    public const int Side = 64;
    /// <summary>Four unsigned normalized 8-bit channels per texel.</summary>
    public const int ValueCount = Side * Side * Side * 4;
    /// <summary>Exact image payload and temporary coherent staging size, excluding Vulkan allocation alignment.</summary>
    public const int ByteCount = ValueCount * sizeof(byte);

    /// <summary>Fills caller-owned storage once at renderer creation; no per-cloud or per-frame work.</summary>
    public static void Fill(Span<byte> values)
    {
        if (values.Length != ValueCount)
            throw new ArgumentException("Gas noise storage must match the fixed lattice size.", nameof(values));
        var at = 0;
        for (var z = 0; z < Side; z++)
            for (var y = 0; y < Side; y++)
                for (var x = 0; x < Side; x++)
                {
                    values[at++] = Value(x + 13, y + 13, z + 13);
                    values[at++] = Value(x + 31, y + 31, z + 31);
                    values[at++] = Value(x + 57, y + 57, z + 57);
                    values[at++] = Value(x, y, z);
                }
    }

    private static byte Value(int x, int y, int z)
    {
        // The former shader's lattice hash, made periodic before hashing. This is decorative, not generation.
        var px = (x & (Side - 1)) * 0.1031f;
        var py = (y & (Side - 1)) * 0.1031f;
        var pz = (z & (Side - 1)) * 0.1031f;
        px -= MathF.Floor(px); py -= MathF.Floor(py); pz -= MathF.Floor(pz);
        var d = px * (py + 33.33f) + py * (pz + 33.33f) + pz * (px + 33.33f);
        px += d; py += d; pz += d;
        var hash = (px + py) * pz;
        hash -= MathF.Floor(hash);
        // Eight bits are plenty for random lattice values: filtering interpolates between them at full precision,
        // and the smaller texels halve the bandwidth and filter at full rate, which is most of the gas cost.
        return (byte)(hash * byte.MaxValue + 0.5f);
    }
}
