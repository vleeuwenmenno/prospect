using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    // How the own containers are seen going in, moving inside and leaving: client look only (CargoMotion).
    private readonly CargoMotion _cargoMotion = new(PropulsionLook.Shared.Rack!.Motion!);

    /// <summary>The own hold's motion: stages and poses of containers going into and out of racks, for the interface.</summary>
    public CargoMotion CargoMotion => _cargoMotion;

    // Once a frame, after the hulls' drawn places are known: the loose bodies where they are drawn, in the own hull's
    // frame, then the own hold.
    private void UpdateCargoMotion(SessionSnapshot snapshot)
    {
        var own = snapshot.OwnHull;
        if ((uint)own >= (uint)snapshot.HullCount) return;
        ref readonly var hull = ref snapshot.Hulls[own];
        var visuals = _visuals.Hulls;
        var at = own < visuals.Length ? DrawnPosition(visuals[own]) : OwnPosition;
        var orientation = own < visuals.Length ? visuals[own].Orientation : OwnOrientation;
        var inverse = Quaternion.Conjugate(orientation);
        _cargoMotion.BeginFree(_time, hull.Id);
        foreach (var item in _freeVisuals.Items)
        {
            var pose = ContainerPose(item);
            _cargoMotion.SeeFree(item, Vector3.Transform(pose.Position.RelativeTo(at), inverse), Quaternion.Concatenate(pose.Orientation, inverse));
        }
        _cargoMotion.EndFree();
        _cargoMotionHull = hull.Id;
        _cargoMotion.Hold(hull.Id, snapshot.OwnCargoTick, hull.Design < _designs.Length ? _designs[hull.Design] : null, snapshot.Content.Cargo, hull.Cargo.Span);
    }

    private uint _cargoMotionHull;

    // Whether the own hold was drawn this frame: only then does a container stand in for a loose body, which is
    // otherwise drawn itself (an open editor draws the draft instead, and a hull out of view draws nothing).
    private bool _cargoHoldDrawn;

    // The containers a hull holds, each with its mag-lock jaws and their lights, where the motion has them; for the own
    // hull also the ones leaving, sliding out to the loose bodies they became.
    private void DrawCargo(int frameIndex, in HullView hull, HullDesign design, Double3 camera,
        Vector3 position, Quaternion orientation, float material, float stretch)
    {
        var catalog = PropulsionLook.Shared.Cargo;
        var cargo = hull.Cargo.Span;
        var moving = hull.Id == _cargoMotionHull && _cargoMotion.Records.Length == cargo.Length;
        for (var i = 0; i < cargo.Length; i++)
        {
            ref readonly var container = ref cargo[i];
            if (!CargoSlots.Valid(design, catalog, container) || !_partModels.TryGetContainer(container.Size, container.Resource, out var mesh)) continue;
            Vector3 offset, light;
            var turn = Quaternion.Identity;
            if (moving) _cargoMotion.Pose(i, design, catalog, out offset, out turn, out light);
            else
            {
                offset = CargoSlots.Centre(design, catalog, container);
                light = _cargoMotion.Light(CargoStage.Stored, double.PositiveInfinity);
            }
            offset.Z *= stretch;
            DrawHeld(frameIndex, mesh, container, design, position + Vector3.Transform(offset, orientation), Quaternion.Concatenate(turn, orientation),
                light, material, stretch, true);
        }
        if (hull.Id != _cargoMotionHull) return;
        _cargoHoldDrawn = true;
        for (var k = 0; k < _cargoMotion.LeavingCount; k++)
        {
            _cargoMotion.Leave(k, out var record, out var from, out var turn, out var free, out var slide, out var light);
            if (!CargoSlots.Valid(design, catalog, record) || !_partModels.TryGetContainer(record.Size, record.Resource, out var mesh)) continue;
            from.Z *= stretch;
            var at = position + Vector3.Transform(from, orientation);
            var rotation = Quaternion.Concatenate(turn, orientation);
            if (slide > 0 && TryGetContainer(free, out var pose))
            {
                // The hand-over: at the end of the slide the container is exactly where the loose body is drawn.
                at = Vector3.Lerp(at, pose.Position.RelativeTo(camera), slide);
                rotation = Quaternion.Slerp(rotation, pose.Orientation, slide);
            }
            DrawHeld(frameIndex, mesh, record, design, at, rotation, light, material, stretch, slide <= 0);
        }
    }

    private void DrawHeld(int frameIndex, ReadOnlySpan<MeshVertex> mesh, in HoldContainer container, HullDesign design, Vector3 at, Quaternion rotation,
        Vector3 light, float material, float stretch, bool clamped)
    {
        var transform = MeshTransform(at, rotation, Vector3.One, design.Color, material, stretch);
        _scene.AddTriangles(frameIndex, mesh, transform);
        if (!clamped || !_partModels.TryGetClamp(container.Size, Facings.Opposite(design.Boxes[container.Rack].Facing), out var clamp, out var lights)) return;
        _scene.AddTriangles(frameIndex, clamp, transform);
        _scene.AddTriangles(frameIndex, lights, MeshTransform(at, rotation, Vector3.One, light, MaterialLight, stretch));
    }
}
