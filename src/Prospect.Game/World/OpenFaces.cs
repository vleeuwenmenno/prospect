namespace Prospect.Game.World;

/// <summary>
/// The one rule for the open face of a block that has one (<see cref="BlockType.FacesOut"/>: a cargo rack's opening, a
/// drone bay's hatch, an ore hopper's intake): it looks away from what the block is set against. The editor asks it once,
/// when the block is placed, and keeps the answer as the block's facing; an old design file whose racks named none asks
/// it on load (<see cref="BoxDesign"/>). Allocates nothing.
/// </summary>
public static class OpenFaces
{
    /// <summary>
    /// The open face of a block whose neighbours touch the faces in <paramref name="touched"/> (bit n is face n, as
    /// <see cref="HullDesign.TouchedFaces"/>): opposite the face it is set against, which is the one touched face; with
    /// several, the -Y face when it is one of them, else the lowest; with none, -Y. So a block set on nothing, or on
    /// something below it, opens upwards.
    /// </summary>
    public static BlockFacing Choose(int touched)
    {
        touched &= 0x3F;
        BlockFacing against;
        if (touched == 0 || (touched & (1 << (int)BlockFacing.Down)) != 0 && (touched & (touched - 1)) != 0)
            against = BlockFacing.Down;
        else
            against = (BlockFacing)System.Numerics.BitOperations.TrailingZeroCount(touched);
        return Facings.Opposite(against);
    }

    /// <summary>
    /// The faces of the box <paramref name="at"/> + <paramref name="size"/> that another box of <paramref name="boxes"/>
    /// touches over some area, skipping index <paramref name="self"/>; the same test as the design's own, on the grid's
    /// edges in quarter cells, so a box set between cells touches what it sits against.
    /// </summary>
    public static int Touched(ReadOnlySpan<DesignBox> boxes, int self, GridPoint at, GridSize size)
    {
        var own = new DesignBox(at, size, null!);
        var touched = 0;
        for (var j = 0; j < boxes.Length; j++)
        {
            if (j == self || boxes[j].Shape != BlockShape.Box) continue;
            var o = boxes[j];
            for (var axis = 0; axis < 3; axis++)
            {
                var overlap = 1L;
                for (var k = 1; k <= 2; k++)
                {
                    var w = (axis + k) % 3;
                    overlap *= Math.Max(0, Math.Min(own.High4(w), o.High4(w)) - Math.Max(own.Low4(w), o.Low4(w)));
                }
                if (overlap == 0) continue;
                if (own.High4(axis) == o.Low4(axis)) touched |= 1 << (2 * axis);
                if (own.Low4(axis) == o.High4(axis)) touched |= 1 << (2 * axis + 1);
            }
        }
        return touched;
    }
}
