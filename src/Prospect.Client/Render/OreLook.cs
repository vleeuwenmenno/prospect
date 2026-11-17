namespace Prospect.Client.Render;

/// <summary>
/// How an ore hopper is drawn, from the <c>hopper</c> entry of <c>content/visuals/propulsion.json</c>
/// (<see cref="PropulsionModels.BuildHopper"/>): a plated box body with edge beams, as a drone bay has, and on its one
/// intake face (the block's facing) a raised rim round a stepped pit with a dark floor, crossed by grate bars, and a
/// light on the rim. Every length is a share of the block's smallest half side, so one entry fits a block of any size.
/// </summary>
internal sealed class HopperLook
{
    /// <summary>How far the plating on the five closed faces lies behind the cell's faces.</summary>
    public float Inset { get; init; }

    /// <summary>An edge beam's thickness; the beams stay flush with the cell.</summary>
    public float Beam { get; init; }

    /// <summary>Brightness of the bare metal of the beams, the rim, the step inside it, the grate bars and the pit's floor.</summary>
    public float BeamShade { get; init; }
    public float RimShade { get; init; }
    public float StepShade { get; init; }
    public float BarShade { get; init; }
    public float PitShade { get; init; }

    /// <summary>The width of the rim round the intake, measured in from the edge beams, and of the step inside it.</summary>
    public float RimWidth { get; init; }
    public float StepWidth { get; init; }

    /// <summary>How far the pit's floor lies under the rim's top.</summary>
    public float PitDepth { get; init; }

    /// <summary>How many grate bars cross the intake, and how thick each is.</summary>
    public int Bars { get; init; }
    public float BarWidth { get; init; }

    /// <summary>How far the rim's top lies under the cell's face; the light fills that, so nothing leaves the cell.</summary>
    public float LightLift { get; init; }

    /// <summary>The light's length along the rim and its width across it.</summary>
    public float LightLength { get; init; }
    public float LightWidth { get; init; }

    /// <summary>Paint of the plating, RGB from 0 to 1.</summary>
    public float[] Body { get; init; } = [];

    /// <summary>The colour of the intake light, RGB from 0 to 1; it gives off light of its own.</summary>
    public float[] Light { get; init; } = [];

    /// <summary>Refuses figures that would draw outside the cell or leave no opening.</summary>
    internal void Check(string path)
    {
        static bool Shade(float v) => v is > 0 and < 1;
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 1 && c[1] is >= 0 and <= 1 && c[2] is >= 0 and <= 1;
        if (!(Beam is > 0 and <= 0.15f) || !(Inset > 0 && Inset < 2f * Beam)
            || !Shade(BeamShade) || !Shade(RimShade) || !Shade(StepShade) || !Shade(BarShade) || !Shade(PitShade)
            || !(RimWidth is >= 0.05f and <= 0.25f) || !(StepWidth is >= 0.02f and <= 0.2f)
            || !(PitDepth is >= 0.1f and <= 1.2f) || !(LightLift > 0 && LightLift < 0.5f * PitDepth)
            || Bars is < 0 or > 6 || !(BarWidth is > 0 and <= 0.12f)
            || !(LightLength is > 0 and <= 0.8f) || !(LightWidth > 0 && LightWidth <= 0.8f * RimWidth)
            || !Colour(Body) || !Colour(Light))
            throw new InvalidDataException($"{path}: the ore hopper look is out of range");
    }
}

/// <summary>
/// How an ore bin is drawn, from the <c>bin</c> entry of <c>content/visuals/propulsion.json</c>
/// (<see cref="PropulsionModels.BuildBin"/>): a plated box body with edge beams and a painted band round its middle,
/// across its longest side. Every length is a share of the block's smallest half side.
/// </summary>
internal sealed class BinLook
{
    /// <summary>How far the plating lies behind the cell's faces.</summary>
    public float Inset { get; init; }

    /// <summary>An edge beam's thickness; the beams stay flush with the cell.</summary>
    public float Beam { get; init; }

    /// <summary>Brightness of the bare metal of the beams.</summary>
    public float BeamShade { get; init; }

    /// <summary>The band's width along the bin's longest side, and how far it stands off the plating (less than the inset).</summary>
    public float BandWidth { get; init; }
    public float BandLift { get; init; }

    /// <summary>Paint of the plating and of the band, RGB from 0 to 1.</summary>
    public float[] Body { get; init; } = [];
    public float[] Band { get; init; } = [];

    /// <summary>Refuses figures that would draw outside the cell.</summary>
    internal void Check(string path)
    {
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 1 && c[1] is >= 0 and <= 1 && c[2] is >= 0 and <= 1;
        if (!(Beam is > 0 and <= 0.15f) || !(Inset > 0 && Inset < 2f * Beam) || !(BeamShade is > 0 and < 1)
            || !(BandWidth is > 0 and <= 0.8f) || !(BandLift > 0 && BandLift < Inset) || !Colour(Body) || !Colour(Band))
            throw new InvalidDataException($"{path}: the ore bin look is out of range");
    }
}
