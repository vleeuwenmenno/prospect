using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// Which face of a cargo rack carries its loading deck. One rule for the hull's meshes, the editor's draft and its
/// ghosts, so a rack looks the same before and after Apply. Font-free and allocation-free apart from the cache's
/// buffer, which only grows.
/// </summary>
internal sealed class RackDeck
{
    /// <summary>
    /// The deck face for a rack whose neighbours touch the faces in <paramref name="touched"/> (bit n is face n, as
    /// <see cref="HullDesign.TouchedFaces"/>): the one touched face; with several, the -Y face when it is one of them,
    /// else the lowest; with none, -Y. A face and a <see cref="BlockFacing"/> share their number.
    /// </summary>
    public static BlockFacing Choose(int touched)
    {
        touched &= 0x3F;
        if (touched == 0 || (touched & (1 << (int)BlockFacing.Down)) != 0 && (touched & (touched - 1)) != 0)
            return BlockFacing.Down;
        return (BlockFacing)System.Numerics.BitOperations.TrailingZeroCount(touched);
    }

    /// <summary>
    /// The hatch face of a drone bay placed where its neighbours touch the faces in <paramref name="touched"/>: the face
    /// opposite the one a rack would take as its deck, so the hatch looks away from what the bay is set against. It is
    /// chosen once, when the bay is placed, and kept as the block's facing.
    /// </summary>
    public static BlockFacing Hatch(int touched) => Facings.Opposite(Choose(touched));

    /// <summary>The deck face of block <paramref name="block"/> of a built design.</summary>
    public static BlockFacing Of(HullDesign design, int block) => Choose(design.TouchedFaces[block]);

    /// <summary>
    /// The faces of the box <paramref name="at"/> + <paramref name="size"/> that another box of <paramref name="boxes"/>
    /// touches over some area, skipping index <paramref name="self"/>; the same test as the design's own.
    /// </summary>
    public static int Touched(ReadOnlySpan<DesignBox> boxes, int self, GridSize at, GridSize size)
    {
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
                    overlap *= Math.Max(0, Math.Min(Get(at, w) + Get(size, w), Get(o.At, w) + Get(o.Size, w)) - Math.Max(Get(at, w), Get(o.At, w)));
                }
                if (overlap == 0) continue;
                if (Get(at, axis) + Get(size, axis) == Get(o.At, axis)) touched |= 1 << (2 * axis);
                if (Get(at, axis) == Get(o.At, axis) + Get(o.Size, axis)) touched |= 1 << (2 * axis + 1);
            }
        }
        return touched;
    }

    private static int Get(GridSize g, int axis) => axis == 0 ? g.X : axis == 1 ? g.Y : g.Z;

    private byte[] _faces = new byte[64];
    private BuildDraft? _draft;
    private int _version = -1;

    /// <summary>The deck face of draft block <paramref name="block"/>, worked out again only when the draft changes.</summary>
    public BlockFacing Of(BuildDraft draft, int block)
    {
        if (!ReferenceEquals(draft, _draft) || draft.Version != _version)
        {
            var boxes = draft.Boxes;
            if (_faces.Length < boxes.Length) _faces = new byte[Math.Max(boxes.Length, _faces.Length * 2)];
            for (var i = 0; i < boxes.Length; i++)
                _faces[i] = boxes[i].Type.Cargo > 0 ? (byte)Choose(Touched(boxes, i, boxes[i].At, boxes[i].Size)) : (byte)0;
            _draft = draft;
            _version = draft.Version;
        }
        return (uint)block < (uint)draft.Boxes.Length ? (BlockFacing)_faces[block] : BlockFacing.Down;
    }
}
