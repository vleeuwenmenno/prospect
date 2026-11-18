using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    /// <summary>
    /// True when <paramref name="hull"/> is drawn as the work drone's own model and not from its blocks: a hull of the
    /// work drone design that still has half its blocks or more (less is debris, drawn block by block like any wreck),
    /// when the look has a work drone. Dents and lost blocks do not change the model.
    /// </summary>
    internal static bool IsModelledDrone(SessionSnapshot snapshot, in HullView hull, HullDesign design) =>
        hull.Design == snapshot.Content.WorkDroneDesign && PropulsionLook.Shared.WorkDrone is not null
        && 2 * BlockSet.Count(hull.Blocks.Span) >= design.BlockCount;

    /// <summary>
    /// Draws the light of a work drone over its model: the tractor array under the nose, brighter and pulsing while the
    /// server says the drone carries a body, and the strips on its pods, brighter with thrust. A drone no player owns
    /// (a wreck) and one too small on screen stay dark. Takes no memory.
    /// </summary>
    private void DrawWorkDroneGlow(int frameIndex, SessionSnapshot snapshot, in HullView hull, HullDesign design, Vector3 position, Quaternion orientation, float pixels, float time)
    {
        var look = PropulsionLook.Shared.WorkDrone!;
        if (hull.Owner == 0 || pixels < look.GlowPixels || !_partModels.TryGetDroneGlow(design, out var array, out var strips))
            return;
        var thrust = hull.Thrust;
        var throttle = Math.Max(Math.Abs((int)thrust.X), Math.Max(Math.Abs((int)thrust.Y), Math.Abs((int)thrust.Z))) / 127f;
        _scene.AddTriangles(frameIndex, array, MeshTransform(position, orientation, Vector3.One, look.GlowLight(look.ArrayLevel(snapshot.IsCarrying(hull.Id), time)), MaterialLight, 1f));
        _scene.AddTriangles(frameIndex, strips, MeshTransform(position, orientation, Vector3.One, look.GlowLight(look.StripLevel(throttle)), MaterialLight, 1f));
    }

    /// <summary>
    /// Muted capture harness hook (<c>--drone-camera</c>): holds the command camera on the player's first work drone in
    /// space, where it is drawn, <paramref name="distance"/> metres off (closer than the command view lets a player
    /// come) and from any pitch, below included; angles are radians. False, changing nothing, while no drone is out. Never called from ordinary input.
    /// </summary>
    internal bool SetDroneCapturePose(float distance, float yaw, float pitch)
    {
        if (!_devices.Muted || _snapshot is not { } snapshot)
            return false;
        var visuals = _visuals.Hulls;
        for (var i = 0; i < Math.Min(snapshot.HullCount, visuals.Length); i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId || hull.Design != snapshot.Content.WorkDroneDesign || !visuals[i].Visible)
                continue;
            // The command plane goes through the camera's focus and is marked on what it cuts: held under the drone
            // (less so from very near, to keep the drone in view), the mark stays off the model.
            SetCommandCapturePose(DrawnPosition(visuals[i]) - new Double3(0, Math.Min(1.1f * _designs[hull.Design].Flight.Radius, 0.2f * distance), 0), distance, yaw, pitch);
            // Past what the command view allows a player: nearer, and from below, where the tractor array is.
            _commandDistance = distance;
            _commandPitch = pitch;
            return true;
        }
        return false;
    }
}
