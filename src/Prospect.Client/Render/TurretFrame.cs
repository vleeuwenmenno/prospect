namespace Prospect.Client.Render;

/// <summary>
/// The figures every posed turret shares, whatever stands on it: a base ring flush on the mount's face (in the hull
/// mesh), and a yoke that turns about the face normal and holds a head on a trunnion (parts posed each frame). The
/// tractor beam and the mining laser each give one from their own look (<see cref="TractorLook.Turret"/>,
/// <see cref="LaserLook.Turret"/>); the base and the yoke are then one model for both
/// (<see cref="PropulsionModels.BuildTurretBase"/>, <see cref="PropulsionModels.BuildTurretYoke"/>) and only the head
/// is the part's own. Every length is a share of the base ring's radius, itself a share of the mount's half width.
/// </summary>
/// <param name="BaseRadius">The base ring's radius (to its corners) as a share of the mount's half width.</param>
/// <param name="BaseHeight">The base ring's height as a share of its radius.</param>
/// <param name="BaseSides">Sides of the base ring: even, 8 to 16. Every other side carries a glow slot or an accent panel.</param>
/// <param name="Slots">Glow slots in the ring's wall: 0 to half its sides.</param>
/// <param name="YokeHeight">From the top of the bearing collar to the trunnion the head pitches about.</param>
/// <param name="YokeSpread">From the turret's axis to the middle of a yoke arm at the trunnion.</param>
/// <param name="Arm">Half the thickness of a yoke arm.</param>
/// <param name="Hub">The radius of the hub cap outside each arm.</param>
/// <param name="BodyColor">The paint of the plating, as 0xRRGGBB.</param>
/// <param name="DarkColor">The paint of the dark frames.</param>
/// <param name="AccentColor">The paint of the accent panels.</param>
/// <param name="SlotColor">The light of the glow slots.</param>
internal readonly record struct TurretFrame(
    float BaseRadius,
    float BaseHeight,
    int BaseSides,
    int Slots,
    float YokeHeight,
    float YokeSpread,
    float Arm,
    float Hub,
    uint BodyColor,
    uint DarkColor,
    uint AccentColor,
    uint SlotColor)
{
    /// <summary>The top of the bearing collar the yoke turns on, in base heights.</summary>
    public const float CollarTop = 1.45f;

    /// <summary>The top of the yoke's turntable above the collar, and the brace between its arms, in base radii.</summary>
    public const float TableHeight = 0.1f;
    public const float BraceBack = 0.29f;
    public const float BraceLift = 0.17f;
    public const float BraceHalfDepth = 0.06f;
    public const float BraceHalfHeight = 0.09f;

    /// <summary>The base ring's radius for a mount of half width <paramref name="small"/>.</summary>
    public float Radius(float small) => small * BaseRadius;

    /// <summary>Height from the block face to the top of the bearing collar, where the yoke sits.</summary>
    public float CollarOffset(float small) => Radius(small) * BaseHeight * CollarTop;

    /// <summary>Height from the block face to the trunnion.</summary>
    public float PivotOffset(float small) => CollarOffset(small) + Radius(small) * YokeHeight;

    /// <summary>
    /// Whether a head whose rear reaches <paramref name="swing"/> base radii from the trunnion's axis turns clear of
    /// the yoke at every pitch: above the turntable under the trunnion and short of the brace between the arms.
    /// </summary>
    public bool Clears(float swing)
    {
        var braceY = BraceBack - BraceHalfDepth;
        var braceZ = YokeHeight - (BraceLift + BraceHalfHeight);
        return YokeHeight >= TableHeight + 0.05f + swing && braceZ > 0f && swing * swing <= braceY * braceY + braceZ * braceZ;
    }

    /// <summary>True when the frame's own figures are in range.</summary>
    public bool Valid =>
        BaseRadius is > 0.3f and <= 1f && BaseHeight is > 0.05f and <= 0.6f
        && BaseSides is >= 8 and <= 16 && BaseSides % 2 == 0 && Slots >= 0 && Slots <= BaseSides / 2
        && YokeSpread is > 0.2f and <= 1f && YokeHeight is > 0.3f and <= 2f
        && Arm is >= 0.04f and <= 0.2f && Hub >= Arm && Hub <= 0.4f
        && BodyColor <= 0xFFFFFF && DarkColor <= 0xFFFFFF && AccentColor <= 0xFFFFFF && SlotColor <= 0xFFFFFF;
}

/// <summary>Which posed turret a worker-built set of parts is for (<see cref="PartModels.TryGetTurret"/>).</summary>
internal enum TurretKind : byte
{
    None,
    Tractor,
    Laser,
}
