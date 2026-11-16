using System.Numerics;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    private readonly FreeContainerVisuals _freeVisuals = new();
    public uint TargetContainer { get; private set; }

    private void UpdateFreeContainers(SessionSnapshot snapshot)
    {
        if (!_freeVisuals.Update(snapshot.FreeContainers.AsSpan(0, snapshot.FreeContainerCount), snapshot.FreeContainerTick)) return;
        foreach (var item in _freeVisuals.Items)
            if (_freeVisuals.Contact(item, out var normal)) _sparks.Container(item, normal, snapshot.FreeContainerTick, _collision);
    }
    public bool TryGetContainer(uint id, out FreeContainer container)
    {
        foreach (var item in _freeVisuals.Items)
            if (item.Id == id)
            {
                container = _freeVisuals.Pose(item, (_snapshot?.ServerTickPrecise ?? 0) + _frameAlpha);
                return true;
            }
        container = default; return false;
    }
    private uint PickFreeContainer(SessionSnapshot snapshot, out float distance)
    {
        distance = float.PositiveInfinity;
        uint id = 0;
        foreach (var item in _freeVisuals.Items)
        {
            if (!_partModels.TryGetContainer(item.Size, out _)) continue;
            var pose = _freeVisuals.Pose(item, snapshot.ServerTickPrecise + _frameAlpha);
            var hit = TargetPicking.Container(_forward, CameraPosition, pose, snapshot.Content.Cargo, NearPlane);
            if (hit >= distance) continue;
            distance = hit; id = item.Id;
        }
        return id;
    }
    private void DrawFreeContainers(int frameIndex, SessionSnapshot snapshot, Double3 camera)
    {
        foreach (var item in _freeVisuals.Items)
        {
            var pose = _freeVisuals.Pose(item, snapshot.ServerTickPrecise + _frameAlpha);
            var position = pose.Position.RelativeTo(camera);
            if (!Visible(position, FreeContainers.Half(snapshot.Content.Cargo, item.Size).Length(), _viewport.X / _viewport.Y, out _)
                || !_partModels.TryGetContainer(item.Size, out var mesh)) continue;
            _scene.AddTriangles(frameIndex, mesh, MeshTransform(position, pose.Orientation, Vector3.One, Vector3.One, Material(false, -1), 1));
        }
    }
}
