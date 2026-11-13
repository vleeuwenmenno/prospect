using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// How the ghost of the part to place is drawn while it turns: a rotation about its middle and a shift of that
/// middle, both in the draft's grid, that glide to nothing so the outline ends exactly on the part's real size,
/// shape and place. Purely for the eye: the part placed is always the editor's whole quarter turn, never an angle
/// in between, and nothing here reaches the draft, the design or the network. Allocates nothing.
/// </summary>
internal sealed class GhostMotion
{
    // The glide runs from a start pose to a goal pose: the goal is no rotation, or while a ring is dragged the part
    // of the drag short of the next quarter turn. The shift only ever glides to nothing.
    private Quaternion _from = Quaternion.Identity;
    private Quaternion _to = Quaternion.Identity;
    private Vector3 _shiftFrom;
    private float _elapsed;
    private float _seconds;
    private bool _active;

    // The ghost the pose was made for: its place, size and shape. Any other ghost is drawn as it is.
    private GridSize _at;
    private GridSize _size;
    private BlockShape _shape;

    /// <summary>A quarter turn, or several, about grid axis <paramref name="axis"/>, as <see cref="BlockShapes.Rotate"/> turns a shape.</summary>
    public static Quaternion Quarter(int axis, int turns) => About(axis, turns * MathF.PI * 0.5f);

    /// <summary>A turn of <paramref name="radians"/> about grid axis <paramref name="axis"/>, right-handed.</summary>
    public static Quaternion About(int axis, float radians) =>
        Quaternion.CreateFromAxisAngle(axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }, radians);

    /// <summary>Forgets any glide: the ghost is drawn as it is.</summary>
    public void Reset()
    {
        _active = false;
        _from = _to = Quaternion.Identity;
        _shiftFrom = Vector3.Zero;
        _elapsed = 0f;
    }

    /// <summary>Moves the glide on by <paramref name="dt"/> seconds.</summary>
    public void Step(float dt)
    {
        if (_active)
            _elapsed += dt;
    }

    /// <summary>
    /// The pose to draw the ghost <paramref name="at"/>, <paramref name="size"/>, <paramref name="shape"/> with: a
    /// rotation of its outline about its middle, then a shift of that middle. Identity for any ghost other than the
    /// one the glide was made for, and once the glide is over.
    /// </summary>
    public void Pose(GridSize at, GridSize size, BlockShape shape, out Quaternion turn, out Vector3 shift)
    {
        if (!_active || at != _at || size != _size || shape != _shape)
        {
            turn = Quaternion.Identity;
            shift = Vector3.Zero;
            return;
        }

        var e = Eased();
        turn = e >= 1f ? _to : Quaternion.Normalize(Quaternion.Slerp(_from, _to, e));
        shift = _shiftFrom * (1f - e);
    }

    /// <summary>
    /// The ghost was turned <paramref name="turns"/> quarter turns about <paramref name="axis"/>, from the ghost
    /// <paramref name="oldAt"/>/<paramref name="oldSize"/>/<paramref name="oldShape"/> to the one now at
    /// <paramref name="at"/>/<paramref name="size"/>/<paramref name="shape"/>: the glide starts from what was on
    /// screen, so nothing jumps, and ends on the new ghost.
    /// </summary>
    public void Turned(int axis, int turns, GridSize oldAt, GridSize oldSize, BlockShape oldShape, GridSize at, GridSize size, BlockShape shape, float seconds)
    {
        Pose(oldAt, oldSize, oldShape, out var turn, out var shift);
        // The new ghost is the old one turned by the quarter turns, so drawing it turned back by them shows the old.
        _from = Quaternion.Normalize(turn * Quaternion.Conjugate(Quarter(axis, turns)));
        _to = Quaternion.Identity;
        _shiftFrom = Middle(oldAt, oldSize) + shift - Middle(at, size);
        Start(at, size, shape, seconds);
    }

    /// <summary>
    /// Glides the ghost now drawn to a goal of <paramref name="radians"/> about <paramref name="axis"/>: the part of
    /// a ring drag short of the next quarter turn, or zero once the ring is let go.
    /// </summary>
    public void Aim(int axis, float radians, GridSize at, GridSize size, BlockShape shape, float seconds)
    {
        Pose(at, size, shape, out var turn, out var shift);
        _from = turn;
        _to = About(axis, radians);
        _shiftFrom = shift;
        Start(at, size, shape, seconds);
    }

    /// <summary>The middle of a box in the grid.</summary>
    public static Vector3 Middle(GridSize at, GridSize size) =>
        new(at.X + (size.X - 1) * 0.5f, at.Y + (size.Y - 1) * 0.5f, at.Z + (size.Z - 1) * 0.5f);

    private void Start(GridSize at, GridSize size, BlockShape shape, float seconds)
    {
        (_at, _size, _shape) = (at, size, shape);
        _seconds = seconds;
        _elapsed = 0f;
        _active = true;
    }

    // Eased out, so a glide restarted every frame of a drag still moves at once rather than waiting.
    private float Eased()
    {
        if (_seconds <= 0f || _elapsed >= _seconds)
            return 1f;
        var t = 1f - _elapsed / _seconds;
        return 1f - t * t * t;
    }
}
