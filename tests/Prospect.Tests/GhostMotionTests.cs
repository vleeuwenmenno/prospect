using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The ghost's glide into a turn: it starts on what was drawn, stays finite and ends exactly on the part.</summary>
public sealed class GhostMotionTests
{
    private const float Seconds = 0.16f;

    // The corners a block of this size and shape has, about its middle.
    private static List<Vector3> Corners(GridSize size, BlockShape shape, Quaternion turn)
    {
        var half = new Vector3(size.X, size.Y, size.Z) * 0.5f;
        var corners = new List<Vector3>();
        for (var i = 0; i < 8; i++)
            if (BlockShapes.HasCorner(shape, i))
                corners.Add(Vector3.Transform(BlockShapes.Corner(i, half), turn));
        return corners;
    }

    private static void SameCorners(List<Vector3> expected, List<Vector3> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var corner in actual)
            Assert.Contains(expected, e => Vector3.Distance(e, corner) < 1e-4f);
    }

    private static GridSize Turned(GridSize size, int axis)
    {
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        return EditorGeometry.With(EditorGeometry.With(size, u, EditorGeometry.Of(size, v)), v, EditorGeometry.Of(size, u));
    }

    [Fact]
    public void AQuarterTurnIsTheTurnTheEditorGivesEveryShape()
    {
        var size = new GridSize(2, 3, 5);
        for (var shape = BlockShape.Box; BlockShapes.IsValid(shape); shape++)
        {
            for (var axis = 0; axis < 3; axis++)
                SameCorners(Corners(size, shape, GhostMotion.Quarter(axis, 1)), Corners(Turned(size, axis), BlockShapes.Rotate(shape, axis), Quaternion.Identity));
        }
    }

    [Fact]
    public void AGlideStartsOnTheOldGhostStaysFiniteAndEndsExactlyOnTheNewOne()
    {
        var motion = new GhostMotion();
        var (oldAt, oldSize, oldShape) = (new GridSize(4, 0, -2), new GridSize(2, 1, 3), BlockShape.WedgeXPlusPlus);
        var (at, size, shape) = (new GridSize(4, -1, -1), Turned(oldSize, 0), BlockShapes.Rotate(oldShape, 0));
        motion.Turned(0, 1, oldAt, oldSize, oldShape, at, size, shape, Seconds);

        // At the start the new ghost, drawn with the pose, is the old ghost where it was.
        motion.Pose(at, size, shape, out var turn, out var shift);
        Assert.Equal(GhostMotion.Middle(oldAt, oldSize), GhostMotion.Middle(at, size) + shift);
        SameCorners(Corners(oldSize, oldShape, Quaternion.Identity), Corners(size, shape, turn));

        // Half way: finite, part way round.
        motion.Step(Seconds * 0.5f);
        motion.Pose(at, size, shape, out turn, out shift);
        Assert.True(float.IsFinite(turn.X) && float.IsFinite(turn.Y) && float.IsFinite(turn.Z) && float.IsFinite(turn.W));
        Assert.True(float.IsFinite(shift.Length()));
        Assert.InRange(MathF.Abs(turn.W), 0.70711f, 0.99999f);

        // At the end exactly the new ghost, and so for any longer.
        motion.Step(Seconds * 0.5f);
        motion.Pose(at, size, shape, out turn, out shift);
        Assert.Equal(Quaternion.Identity, turn);
        Assert.Equal(Vector3.Zero, shift);
        motion.Step(1f);
        motion.Pose(at, size, shape, out turn, out shift);
        Assert.Equal(Quaternion.Identity, turn);
        Assert.Equal(Vector3.Zero, shift);

        // Any other ghost is drawn as it is.
        motion.Turned(0, 1, oldAt, oldSize, oldShape, at, size, shape, Seconds);
        motion.Pose(at, oldSize, shape, out turn, out shift);
        Assert.Equal(Quaternion.Identity, turn);
        Assert.Equal(Vector3.Zero, shift);
    }

    [Fact]
    public void ATurnInTheMiddleOfADragShowsTheSameGhostAndLettingGoGlidesToTheQuarterTurn()
    {
        var motion = new GhostMotion();
        var (oldAt, oldSize, oldShape) = (new GridSize(0, 0, 0), new GridSize(1, 2, 4), BlockShape.WedgeYPlusMinus);
        motion.Aim(1, 0.7f, oldAt, oldSize, oldShape, 0f);
        motion.Pose(oldAt, oldSize, oldShape, out var turn, out var shift);
        var drawn = Corners(oldSize, oldShape, turn);
        var drawnMiddle = GhostMotion.Middle(oldAt, oldSize) + shift;

        // Past half a quarter the part turns; the drag's angle is now short of it, and the ghost is where it was.
        var (at, size, shape) = (new GridSize(-1, 0, 1), Turned(oldSize, 1), BlockShapes.Rotate(oldShape, 1));
        motion.Turned(1, 1, oldAt, oldSize, oldShape, at, size, shape, Seconds);
        motion.Aim(1, 0.7f - MathF.PI * 0.5f, at, size, shape, Seconds);
        motion.Pose(at, size, shape, out turn, out shift);
        SameCorners(drawn, Corners(size, shape, turn));
        Assert.True(Vector3.Distance(drawnMiddle, GhostMotion.Middle(at, size) + shift) < 1e-4f);

        // Let go: it glides onto the quarter turn and ends exactly on it.
        motion.Aim(1, 0f, at, size, shape, Seconds);
        motion.Step(Seconds * 0.3f);
        motion.Pose(at, size, shape, out turn, out _);
        Assert.True(float.IsFinite(turn.W));
        motion.Step(Seconds);
        motion.Pose(at, size, shape, out turn, out shift);
        Assert.Equal(Quaternion.Identity, turn);
        Assert.Equal(Vector3.Zero, shift);
    }
}
