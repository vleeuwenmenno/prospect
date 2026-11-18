using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// The beams of carrying drones, in the same family as a ship's tractor beam: one from the middle of a carrier's belly
/// tractor array to the body it carries, ending where that body is drawn. Which drone pulls what comes from the server
/// (<see cref="DroneCarryMessage"/>); nothing here guesses from nearness, so a drone parked beside a loose container
/// shows no beam. Takes no memory.
/// </summary>
internal static class DroneCarryBeams
{
    /// <summary>
    /// Writes one beam a carry into <paramref name="output"/> for every carry whose drone and body are both drawn, and
    /// answers how many it wrote. A drone of another design, one drawn block by block as debris, and a body outside the
    /// client's set make no beam. The body is taken at the instant its set is drawn at: a carried body has no tractor
    /// hull, so that is the own hull's instant, as the containers and chunks themselves are drawn.
    /// </summary>
    public static int Fill(SessionSnapshot snapshot, ReadOnlySpan<HullVisuals.HullVisual> drawn, HullDesign[] designs, WorkDroneLook look,
        FreeContainerVisuals containers, OreChunkVisuals chunks, float alpha, Double3 camera, Span<TractorInstance> output)
    {
        var hulls = snapshot.Hulls.AsSpan(0, snapshot.HullCount);
        var carries = snapshot.DroneCarries.AsSpan(0, snapshot.DroneCarryCount);
        var tick = OreChunkVisuals.ShownTick(hulls, snapshot.OwnHull, snapshot.ServerTickPrecise, alpha);
        var count = 0;
        for (var i = 0; i < carries.Length && count < output.Length; i++)
        {
            ref readonly var carry = ref carries[i];
            if (!Drone(snapshot, hulls, drawn, designs, look, carry.Drone, camera, out var from)) continue;
            if (!Body(snapshot, containers, chunks, tick, carry, camera, out var to, out var quaternion, out var half)) continue;
            var length = (to - from).Length();
            if (length < 1e-4f) continue;
            var seed = ((carry.Drone * 0x9E3779B9u ^ carry.Body * 0x85EBCA6Bu) & 0xFFFFu) / 65536f;
            output[count++] = new TractorInstance
            {
                FromSeed = new float4(from.X, from.Y, from.Z, seed),
                ToLength = new float4(to.X, to.Y, to.Z, length),
                ContainerQuat = quaternion,
                ContainerHalf = half,
            };
        }
        return count;
    }

    /// <summary>Where a carrying drone's beam starts: the middle of its belly array, on the pose it is drawn at.</summary>
    private static bool Drone(SessionSnapshot snapshot, ReadOnlySpan<HullView> hulls, ReadOnlySpan<HullVisuals.HullVisual> drawn,
        HullDesign[] designs, WorkDroneLook look, uint id, Double3 camera, out Vector3 from)
    {
        from = default;
        for (var i = 0; i < hulls.Length && i < drawn.Length; i++)
        {
            ref readonly var hull = ref hulls[i];
            if (hull.Id != id) continue;
            if ((uint)hull.Design >= (uint)designs.Length) return false;
            var design = designs[hull.Design];
            if (!WorldView.IsModelledDrone(snapshot, hull, design)) return false;
            ref readonly var visual = ref drawn[i];
            from = (visual.Position + HullRotation.Rotate(PropulsionModels.WorkDroneArrayPoint(design, look), visual.Orientation)).RelativeTo(camera);
            return true;
        }
        return false;
    }

    /// <summary>The body a carry names, where it is drawn and how big it is: its centre, its turn and its half extents.</summary>
    private static bool Body(SessionSnapshot snapshot, FreeContainerVisuals containers, OreChunkVisuals chunks, double tick,
        in DroneCarry carry, Double3 camera, out Vector3 to, out float4 quaternion, out float4 half)
    {
        to = default;
        quaternion = default;
        half = default;
        if (carry.Kind == DroneCarryKind.Container)
        {
            var items = containers.Items;
            for (var i = 0; i < items.Length; i++)
            {
                ref readonly var item = ref items[i];
                if (item.Id != carry.Body) continue;
                var pose = containers.Pose(item, tick);
                var extents = FreeContainers.Half(snapshot.Content.Cargo, item.Size);
                to = pose.Position.RelativeTo(camera);
                quaternion = Turn(pose.Orientation);
                half = new float4(extents.X, extents.Y, extents.Z, extents.Length());
                return true;
            }
            return false;
        }

        var ores = chunks.Items;
        for (var i = 0; i < ores.Length; i++)
        {
            ref readonly var item = ref ores[i];
            if (item.Id != carry.Body) continue;
            var pose = chunks.Pose(i, tick);
            var extent = OreChunks.Half(snapshot.Content.Mining, item.Mass);
            to = pose.Position.RelativeTo(camera);
            quaternion = Turn(pose.Orientation);
            half = new float4(extent, extent, extent, extent * 1.7320508f);
            return true;
        }
        return false;
    }

    private static float4 Turn(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
