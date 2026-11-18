namespace Prospect.Client.Render;

/// <summary>
/// The model of one mounted module scanner, from the <c>scanners</c> entry of <c>propulsion.json</c> keyed by block type
/// id. A scanner is a small part on a block face, so its model is a pure function of the thin box it fills
/// (<see cref="PropulsionModels.BuildScanner"/>) and all of these figures are shares of that box: the radii of the
/// face's half width, the heights of its depth. The rim is painted in the colour of the scanner's tier.
/// </summary>
internal sealed class ScannerLook
{
    /// <summary>The kinds of head a scanner can have: a shallow dish, a spiked array or a domed sensor head.</summary>
    public const string Dish = "dish", Array = "array", Dome = "dome";

    /// <summary>What the head is: <see cref="Dish"/>, <see cref="Array"/> or <see cref="Dome"/>.</summary>
    public string Shape { get; init; } = Dome;

    /// <summary>Sides of every round part: 8 to 32.</summary>
    public int Sides { get; init; } = 16;

    /// <summary>The round base plate's radius as a share of the face's half width, and its height as a share of the depth.</summary>
    public float BaseRadius { get; init; } = 0.92f;
    public float BaseHeight { get; init; } = 0.22f;

    /// <summary>The head's radius (the dish's rim, the dome's shoulder, the array's ring) as a share of the half width.</summary>
    public float HeadRadius { get; init; } = 0.8f;

    /// <summary>How high the head reaches, as a share of the depth: the rim of a dish, the top of a dome, the tallest spike.</summary>
    public float HeadHeight { get; init; } = 1f;

    /// <summary>Where a dish's bowl bottoms out, or a dome's port sinks to, as a share of the depth.</summary>
    public float BowlHeight { get; init; } = 0.6f;

    /// <summary>The mast under a dish or the ring under an array's spikes, as a share of the half width.</summary>
    public float StemRadius { get; init; } = 0.2f;

    /// <summary>An array's spikes, 3 to 8, and their thickness as a share of the half width.</summary>
    public int Spikes { get; init; } = 3;
    public float SpikeRadius { get; init; } = 0.07f;

    /// <summary>
    /// How many concentric rings an array carries on its plate, 1 to 3. The basic locator is one ring of pickets; the
    /// advanced one adds rings inside it, so the two locators read apart on the same face.
    /// </summary>
    public int Rings { get; init; } = 1;

    /// <summary>How much of the base and head is paint (the tier's colour) rather than bare metal: the rim's share of the head's radius.</summary>
    public float RimShare { get; init; } = 0.9f;

    /// <summary>Shades, 0 dark to 1 bright, as the other models give them (<see cref="PropulsionModels.Tag"/>).</summary>
    public float BaseShade { get; init; } = 0.36f;
    public float HeadShade { get; init; } = 0.5f;
    public float DetailShade { get; init; } = 0.62f;
    public float PortShade { get; init; } = 0.05f;

    /// <summary>Refuses a look outside what the model's geometry can hold.</summary>
    public void Check(string path, string id)
    {
        static bool Share(float v, float low = 0.01f) => v >= low && v <= 1f;
        if (Shape is not (Dish or Array or Dome) || Sides is < 8 or > 32 || Spikes is < 3 or > 8 || Rings is < 1 or > 3
            || !Share(BaseRadius, 0.2f) || !Share(BaseHeight) || !Share(HeadRadius, 0.2f) || !Share(HeadHeight, 0.2f) || !Share(BowlHeight, 0.05f)
            || !Share(StemRadius, 0.02f) || !Share(SpikeRadius, 0.01f) || !Share(RimShare, 0.2f)
            || BaseHeight >= HeadHeight || BowlHeight >= HeadHeight || BowlHeight <= BaseHeight || StemRadius >= HeadRadius
            || !(BaseShade is >= 0 and <= 1) || !(HeadShade is >= 0 and <= 1) || !(DetailShade is >= 0 and <= 1) || !(PortShade is >= 0 and <= 1))
            throw new InvalidDataException($"{path}: scanner {id} is out of range: shape dish, array or dome; 8 to 32 sides; 1 to 3 rings; shares up to 1, with the base under the head and the bowl between them");
    }
}
