using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Render;

/// <summary>
/// Client-only figures for how a loose ore chunk is built and shaded, from <c>content/visuals/ore_chunk_models.json</c>.
/// The mesh figures shape the broken lump (<see cref="OreChunkModels"/>); the vein and glint figures travel with each
/// chunk's instance to <c>mesh.frag</c>. Lengths are fractions of the chunk's half size.
/// </summary>
internal sealed class OreChunkModelLook
{
    private static OreChunkModelLook? _current;

    /// <summary>How many different lumps there are; a chunk's id picks one.</summary>
    public int Variants { get; set; } = 6;

    /// <summary>Smallest and largest half extent of the box a lump is cut from, along each axis: how stretched it is.</summary>
    public float BoxHalfMin { get; set; } = 0.62f;

    /// <summary>See <see cref="BoxHalfMin"/>.</summary>
    public float BoxHalfMax { get; set; } = 0.95f;

    /// <summary>Fewest and most break planes that cut the box's corners and edges away.</summary>
    public int CutsMin { get; set; } = 6;

    /// <summary>See <see cref="CutsMin"/>.</summary>
    public int CutsMax { get; set; } = 9;

    /// <summary>A break plane lies this fraction (smallest and largest) of the way out to the box's far corner along its normal.</summary>
    public float CutDepthMin { get; set; } = 0.62f;

    /// <summary>See <see cref="CutDepthMin"/>.</summary>
    public float CutDepthMax { get; set; } = 0.88f;

    /// <summary>
    /// How far the middle of each break face is pushed in or out along its normal, as a fraction of the face's size: it
    /// splits the face into flat triangles that catch the light separately, like a knapped stone.
    /// </summary>
    public float FacetRelief { get; set; } = 0.08f;

    /// <summary>Grades at which a chunk takes the next entry of <see cref="CrystalCounts"/>; ascending.</summary>
    public float[] CrystalGrades { get; set; } = [0.3f, 0.65f];

    /// <summary>How many crystals a chunk of each grade band carries; one more entry than <see cref="CrystalGrades"/>, ascending.</summary>
    public int[] CrystalCounts { get; set; } = [0, 3, 6];

    /// <summary>Shortest and longest crystal.</summary>
    public float CrystalLengthMin { get; set; } = 0.3f;

    /// <summary>See <see cref="CrystalLengthMin"/>.</summary>
    public float CrystalLengthMax { get; set; } = 0.6f;

    /// <summary>Thinnest and widest crystal, as a radius.</summary>
    public float CrystalRadiusMin { get; set; } = 0.07f;

    /// <summary>See <see cref="CrystalRadiusMin"/>.</summary>
    public float CrystalRadiusMax { get; set; } = 0.13f;

    /// <summary>How far a crystal leans off its face's normal, as a tangent.</summary>
    public float CrystalTilt { get; set; } = 0.3f;

    /// <summary>Frequency of the vein noise on the lump, per half size.</summary>
    public float VeinScale { get; set; } = 5.5f;

    /// <summary>The share of a lump's surface that shows ore as veins at grade 0.</summary>
    public float VeinCoverMin { get; set; } = 0.05f;

    /// <summary>The same at grade 1.</summary>
    public float VeinCoverMax { get; set; } = 0.4f;

    /// <summary>How strongly ore shines and glows; 0 turns it off.</summary>
    public float Glint { get; set; } = 1f;

    /// <summary>The shipped file, read once.</summary>
    public static OreChunkModelLook Current => _current ??= Load(AppPaths.Content("visuals", "ore_chunk_models.json"));

    /// <summary>The grade band of <paramref name="grade"/>: how many of <see cref="CrystalGrades"/> it reaches.</summary>
    public int Band(float grade)
    {
        var band = 0;
        for (var i = 0; i < CrystalGrades.Length; i++)
            if (grade >= CrystalGrades[i]) band++;
        return band;
    }

    /// <summary>Reads and validates a look file; throws <see cref="InvalidDataException"/> when a figure is out of range.</summary>
    public static OreChunkModelLook Load(string path)
    {
        var value = JsonSerializer.Deserialize<OreChunkModelLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("ore_chunk_models.json is empty.");
        value.Validate();
        return value;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless every figure is sensible.</summary>
    public void Validate()
    {
        static bool Within(float v, float low, float high) => float.IsFinite(v) && v >= low && v <= high;
        if (Variants is < 1 or > 32)
            throw new InvalidDataException("ore_chunk_models.json: variants must be 1 to 32.");
        if (!(Within(BoxHalfMin, 0.3f, 1.5f) && Within(BoxHalfMax, BoxHalfMin, 1.5f) && CutsMin >= 0 && CutsMax >= CutsMin && CutsMax <= 24
            && Within(CutDepthMin, 0.3f, 1f) && Within(CutDepthMax, CutDepthMin, 1f) && Within(FacetRelief, 0f, 0.5f)))
            throw new InvalidDataException("ore_chunk_models.json: a box or cut figure is out of range or its minimum is above its maximum.");
        if (CrystalCounts.Length != CrystalGrades.Length + 1 || CrystalCounts.Length > 8 || CrystalCounts.Any(c => c is < 0 or > 12)
            || CrystalCounts.Zip(CrystalCounts.Skip(1)).Any(p => p.Second < p.First)
            || CrystalGrades.Any(g => !Within(g, 0f, 1f)) || CrystalGrades.Zip(CrystalGrades.Skip(1)).Any(p => p.Second <= p.First))
            throw new InvalidDataException("ore_chunk_models.json: crystalCounts needs one more entry than crystalGrades, both ascending (grades 0 to 1, counts 0 to 12).");
        if (!(Within(CrystalLengthMin, 0.05f, 1.2f) && Within(CrystalLengthMax, CrystalLengthMin, 1.2f) && Within(CrystalRadiusMin, 0.02f, 0.4f)
            && Within(CrystalRadiusMax, CrystalRadiusMin, 0.4f) && Within(CrystalTilt, 0f, 1f)))
            throw new InvalidDataException("ore_chunk_models.json: a crystal figure is out of range or its minimum is above its maximum.");
        if (!(Within(VeinScale, 1f, 40f) && Within(VeinCoverMin, 0f, 1f) && Within(VeinCoverMax, VeinCoverMin, 1f) && Within(Glint, 0f, 4f)))
            throw new InvalidDataException("ore_chunk_models.json: a vein or glint figure is out of range.");
    }
}
