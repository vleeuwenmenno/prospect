namespace Prospect.Client.Render;

/// <summary>
/// How a drone bay is drawn, from the <c>bay</c> entry of <c>content/visuals/propulsion.json</c>
/// (<see cref="PropulsionModels.BuildBay"/>): a plated box body with edge beams, and on its one hatch face (the
/// block's facing) a raised rim round an opening that holds two door leaves with a seam between them and a light on
/// the rim. Every length is a share of the block's smallest half side, so one entry fits a block of any size, except
/// <see cref="Beam"/>, which is a share of the smallest whole side as for tanks.
/// </summary>
internal sealed class BayLook
{
    /// <summary>How far the plating on the five closed faces lies behind the cell's faces, so the edge beams stand proud of it.</summary>
    public float Inset { get; init; }

    /// <summary>An edge beam's thickness as a share of the block's smallest side; the beams stay flush with the cell.</summary>
    public float Beam { get; init; }

    /// <summary>Brightness of the bare metal of the beams.</summary>
    public float BeamShade { get; init; }

    /// <summary>Brightness of the bare metal of the rim.</summary>
    public float RimShade { get; init; }

    /// <summary>Brightness of the bare metal of the recess behind the doors; dark, so the seam reads.</summary>
    public float PlateShade { get; init; }

    /// <summary>The width of the rim round the opening, measured in from the edge beams.</summary>
    public float RimWidth { get; init; }

    /// <summary>How far the rim stands above the recess behind the doors.</summary>
    public float RimDepth { get; init; }

    /// <summary>How far under the rim's top the doors lie.</summary>
    public float DoorRecess { get; init; }

    /// <summary>The gap between the two door leaves.</summary>
    public float Seam { get; init; }

    /// <summary>
    /// How far the light stands above the rim. The rim lies this far under the cell's face, so the light never leaves
    /// the cell.
    /// </summary>
    public float LightLift { get; init; }

    /// <summary>The light's length along the rim.</summary>
    public float LightLength { get; init; }

    /// <summary>The light's width across the rim; it must fit on it.</summary>
    public float LightWidth { get; init; }

    /// <summary>Paint of the plating, RGB from 0 to 1.</summary>
    public float[] Body { get; init; } = [];

    /// <summary>Paint of the door leaves, RGB from 0 to 1.</summary>
    public float[] Door { get; init; } = [];

    /// <summary>The colour of the hatch light, RGB from 0 to 1; it gives off light of its own.</summary>
    public float[] Light { get; init; } = [];

    /// <summary>Refuses figures that would draw outside the cell or leave no opening for the doors.</summary>
    internal void Check(string path)
    {
        static bool Shade(float v) => v is > 0 and < 1;
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 1 && c[1] is >= 0 and <= 1 && c[2] is >= 0 and <= 1;
        // The beams are at most 0.3 of the smallest half side thick and the rim at most as wide, which leaves the opening
        // at least 0.4 of it, enough for the seam and the light.
        if (!(Beam is > 0 and <= 0.15f) || !(Inset > 0 && Inset < 2f * Beam)
            || !Shade(BeamShade) || !Shade(RimShade) || !Shade(PlateShade)
            || !(RimWidth is >= 0.05f and <= 0.3f) || !(RimDepth is > 0 and <= 0.4f)
            || !(LightLift > 0 && LightLift < DoorRecess) || !(DoorRecess < RimDepth)
            || !(Seam is > 0 and <= 0.2f)
            || !(LightLength is > 0 and <= 0.8f) || !(LightWidth > 0 && LightWidth <= 0.8f * RimWidth)
            || !Colour(Body) || !Colour(Door) || !Colour(Light))
            throw new InvalidDataException($"{path}: the drone bay look is out of range");
    }
}
