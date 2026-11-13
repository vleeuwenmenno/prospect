namespace Prospect.Game.World;

/// <summary>
/// What a design's shield shell takes against what there is room for, as <see cref="GeometryAdmission.Measure"/>
/// works it out: the smooth shell of the intact hull, which every damaged hull falls back to when its own shell would
/// not fit. <paramref name="WorstBlock"/> and <paramref name="WorstSide"/> name a face only for the square measure
/// of an earlier measure and stay for its callers; the smooth measure names none (-1).
/// </summary>
/// <param name="Vertices">The vertices the shell takes.</param>
/// <param name="Limit">The vertices there is room for.</param>
/// <param name="WorstBlock">The block whose face can take most, or -1.</param>
/// <param name="WorstSide">That face, numbered as <see cref="HullDesign.ExteriorFaces"/> numbers faces, or -1.</param>
/// <param name="WorstVertices">What that face can take.</param>
public readonly record struct SurfaceUsage(long Vertices, int Limit, int WorstBlock, int WorstSide, long WorstVertices)
{
    /// <summary>The indices the shell takes, three a triangle.</summary>
    public long Indices { get; init; }

    /// <summary>The indices there is room for.</summary>
    public int IndexLimit { get; init; } = GeometryAdmission.MaxIndices;

    /// <summary>Why the builder stopped short of a shell, or null when it built one.</summary>
    public string? Overflow { get; init; }

    /// <summary>Whether the design is admitted.</summary>
    public bool Fits => Overflow is null && Vertices <= Limit && Indices <= IndexLimit;
}

/// <summary>
/// Shared admission of a design's shield: the smooth shell of the intact hull (<see cref="ShieldSurface"/>) must fit
/// the client's storage, and is kept with the design as the shell every damaged hull of it can fall back to.
/// </summary>
public static class GeometryAdmission
{
    /// <summary>Compiled storage capacity in distinct vertices; content may reduce it. Indices fit 16 bits.</summary>
    public const int MaxVertices = 32768;

    /// <summary>Compiled storage capacity in indices, three a triangle; content may reduce it.</summary>
    public const int MaxIndices = 196608;
    /// <summary>
    /// The most blocks a design admitted for its shield may have: thirty-six vertices a block, as the square shell
    /// had, kept as the bound on per-block runtime caches and on admission's work.
    /// </summary>
    public const int MaxBlocks = MaxVertices / 36;
    /// <summary>How far the shell stands off the blocks, in block sizes, shared with the shell builder.</summary>
    public const float Margin = 0.3f;

    /// <summary>Rejects before constructing per-block runtime caches.</summary>
    public static void Count(int count, DesignLimits limits)
    {
        if (count < 1 || (long)count * 36 > limits.SurfaceVertices || 6L * count * count > limits.GeometryPairTests)
            throw new InvalidDataException("Design exceeds surface storage or geometry work budget.");
    }

    /// <summary>
    /// Admits a design's shield: builds the smooth shell of the intact hull, refuses the design when it does not fit
    /// or the builder runs out of room, and keeps the shell with the design. A damaged hull's own shell is built
    /// later, off the frame; when that one would not fit, the hull keeps this one, which encloses whatever is left
    /// of it. The message says how much it needs; <see cref="Measure"/> gives the same figures without throwing.
    /// Takes milliseconds: never call it from a frame or a tick.
    /// </summary>
    public static void Validate(HullDesign design, DesignLimits limits) => Validate(design, limits.SurfaceVertices, limits.Shield, limits.SurfaceIndices);

    /// <summary>As <see cref="Validate(HullDesign, DesignLimits)"/>, with the room and shape given.</summary>
    public static void Validate(HullDesign design, int maxVertices = MaxVertices, ShellShape? shape = null, int maxIndices = MaxIndices)
    {
        if (design.BlockCount > MaxBlocks)
            throw new InvalidDataException("Design has too many blocks for its shield.");
        var usage = Measure(design, maxVertices, shape, maxIndices);
        if (usage.Fits)
            return;
        throw new InvalidDataException(usage.Overflow switch
        {
            null when usage.Vertices > usage.Limit => $"Design exceeds the shield surface budget: it needs {usage.Vertices} of {usage.Limit} vertices.",
            null => $"Design exceeds the shield surface budget: it needs {usage.Indices} of {usage.IndexLimit} indices.",
            "vertices" => $"Design exceeds the shield surface budget: it needs more than {usage.Limit} vertices.",
            "indices" => $"Design exceeds the shield surface budget: it needs more than {usage.IndexLimit} indices.",
            var overflow => $"Design's shield needs too much work to build: it ran out of {overflow}.",
        });
    }

    /// <summary>
    /// The vertices and indices of the intact hull's smooth shell against <paramref name="maxVertices"/> and
    /// <paramref name="maxIndices"/>. When both fit, the shell is kept with the design, vertices and indices together
    /// (<see cref="HullDesign.IntactShell"/>), so nothing builds it again. Past the compiled room only that it is over
    /// is known.
    /// </summary>
    public static SurfaceUsage Measure(HullDesign design, int maxVertices = MaxVertices, ShellShape? shape = null, int maxIndices = MaxIndices)
    {
        if (design.BlockCount > MaxBlocks)
            return new SurfaceUsage(0, maxVertices, -1, -1, 0) { IndexLimit = maxIndices, Overflow = "blocks" };
        var form = shape ?? ShellShape.Default;
        return ShieldSurface.WithShared((design, maxVertices, maxIndices, form), static (builder, state) =>
        {
            var (hull, limit, indexLimit, form) = state;
            var built = builder.Build(hull, hull.FullSet, form, Scratch, ScratchIndices, out var indices);
            var work = builder.Work;
            if (built < 0)
            {
                var overflow = work.Overflow ?? "indices";
                return new SurfaceUsage(overflow == "vertices" ? MaxVertices + 1 : work.Vertices, limit, -1, -1, 0)
                {
                    Indices = overflow == "indices" ? MaxIndices + 1 : work.Indices,
                    IndexLimit = indexLimit,
                    Overflow = overflow,
                };
            }

            var usage = new SurfaceUsage(built, limit, -1, -1, 0) { Indices = indices, IndexLimit = indexLimit };
            if (usage.Fits)
                hull.Admit(Scratch.AsSpan(0, built).ToArray(), ScratchIndices.AsSpan(0, indices).ToArray(), form);
            return usage;
        });
    }

    // Written only under the shared builder's lock.
    private static readonly ShellVertex[] Scratch = new ShellVertex[MaxVertices];
    private static readonly ushort[] ScratchIndices = new ushort[MaxIndices];
}
