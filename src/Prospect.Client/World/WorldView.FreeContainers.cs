using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    private readonly FreeContainerVisuals _freeVisuals = new();
    public int DrawnFreeContainers { get; private set; }
    public int FreeContainerCount => _freeVisuals.Items.Length;
    public uint TargetContainer { get; private set; }

    private void UpdateFreeContainers(SessionSnapshot snapshot)
    {
        if (!_freeVisuals.Update(snapshot.FreeContainers.AsSpan(0, snapshot.FreeContainerCount), snapshot.FreeContainerTick)) return;
        if (TargetContainer != 0 && !TryGetContainer(TargetContainer, out _)) TargetContainer = 0;
        foreach (var item in _freeVisuals.Items)
            if (_freeVisuals.Contact(item, out var normal)) _sparks.Container(item, normal, snapshot.FreeContainerTick, _collision);
    }
    public bool TryGetContainer(uint id, out FreeContainer container)
    {
        foreach (var item in _freeVisuals.Items)
            if (item.Id == id)
            {
                container = ContainerPose(item);
                return true;
            }
        container = default; return false;
    }
    /// <summary>The one pose a loose container is drawn, picked, hovered and beamed at (<see cref="FreeContainerVisuals.ShownTick"/>).</summary>
    private FreeContainer ContainerPose(in FreeContainer item) => _snapshot is { } snapshot
        ? _freeVisuals.Pose(item, FreeContainerVisuals.ShownTick(item, snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha))
        : item;

    private uint PickFreeContainer(SessionSnapshot snapshot, float2 pointer, out float distance)
    {
        var nx = pointer.X / _screen.X * 2 - 1;
        var ny = pointer.Y / _screen.Y * 2 - 1;
        var ray = Vector3.Normalize(_forward + _right * (nx * _tanHalfFov * _screen.X / _screen.Y) - _up * (ny * _tanHalfFov));
        distance = float.PositiveInfinity;
        uint id = 0;
        foreach (var item in _freeVisuals.Items)
        {
            if (!_partModels.TryGetContainer(item.Size, out _)) continue;
            var pose = ContainerPose(item);
            var hit = TargetPicking.Container(ray, CameraPosition, pose, snapshot.Content.Cargo, NearPlane);
            if (hit >= distance) continue;
            distance = hit; id = item.Id;
        }
        return id;
    }
    /// <summary>
    /// Capture only (<c>--container-sheet n</c>): every resource's container of size <c>n</c>, and an empty one, in two
    /// rows before the camera, turned to show a long side, an end and the top. Drawn through the one container mesh path.
    /// </summary>
    public int? ContainerSheetSize { get; set; }

    private void DrawContainerSheet(int frameIndex, SessionSnapshot snapshot)
    {
        if (ContainerSheetSize is not { } size || snapshot.Galaxy is not { } galaxy) return;
        var shape = snapshot.Content.Cargo.Sizes[size].Shape;
        var longest = MathF.Max(shape.X, MathF.Max(shape.Y, shape.Z)) * snapshot.Content.Cargo.UnitSize;
        var spacing = longest * 1.5f;
        var orientation = Quaternion.CreateFromYawPitchRoll(.6f, .35f, 0);
        var count = galaxy.Resources.Length + 1;
        var columns = (count + 1) / 2;
        for (var i = 0; i < count; i++)
        {
            if (!_partModels.TryGetContainer(size, i - 1, out var mesh)) continue;
            var column = i % columns - (columns - 1) * .5f;
            var row = i / columns == 0 ? .5f : -.5f;
            var position = _forward * (longest * 3.4f) + _right * (column * spacing) + _up * (row * spacing);
            _scene.AddTriangles(frameIndex, mesh, MeshTransform(position, orientation, Vector3.One, Vector3.One, Material(false, -1), 1));
        }
    }

    private void DrawFreeContainers(int frameIndex, SessionSnapshot snapshot, Double3 camera)
    {
        DrawContainerSheet(frameIndex, snapshot);
        DrawnFreeContainers = 0;
        foreach (var item in _freeVisuals.Items)
        {
            var pose = ContainerPose(item);
            var position = pose.Position.RelativeTo(camera);
            if (!Visible(position, FreeContainers.Half(snapshot.Content.Cargo, item.Size).Length(), _viewport.X / _viewport.Y, out _)
                || !_partModels.TryGetContainer(item.Size, item.Resource, out var mesh)) continue;
            if (_scene.AddTriangles(frameIndex, mesh, MeshTransform(position, pose.Orientation, Vector3.One, Vector3.One, Material(false, -1), 1))) DrawnFreeContainers++;
        }
    }
}
