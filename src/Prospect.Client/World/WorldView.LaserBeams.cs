using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.World;

// The mining laser's beams, drawn from what the snapshot says: each starts at the lens of its laser's turret as that
// is drawn this frame (for a turret not drawn, at LaserAim's emitter on the drawn hull, the point the server fires
// from) and ends where the server found the end, relative to the hull. All the light is made in laser.vert and laser.frag from one instance a beam, so a frame takes
// no memory; the cut's glow and sparks are in the same draw and only a cutting beam has them.
internal sealed partial class WorldView
{
    /// <summary>Beams written for the scene this frame.</summary>
    public int DrawnLaserBeams { get; private set; }

    private void DrawLaserBeams(int frameIndex, SessionSnapshot snapshot, Double3 camera)
    {
        DrawnLaserBeams = 0;
        if (snapshot.LaserBeamCount == 0 || PropulsionLook.Shared.Laser is not { } look)
            return;
        var output = _scene.Lasers(frameIndex);
        var count = 0;
        var visuals = _visuals.Hulls;
        for (var b = 0; b < snapshot.LaserBeamCount && count < output.Length; b++)
        {
            ref readonly var beam = ref snapshot.LaserBeams[b];
            for (var i = 0; i < snapshot.HullCount && i < visuals.Length; i++)
            {
                ref readonly var hull = ref snapshot.Hulls[i];
                if (hull.Id != beam.Hull)
                    continue;
                if (hull.Design >= InstalledDesigns || hull.Design >= _designs.Length)
                    break;
                var design = _designs[hull.Design];
                if (beam.Part >= design.Lasers.Length)
                    break;
                var mount = design.Lasers[beam.Part];
                // A laser lost with its block shows no beam, whatever the server last said.
                if (!BlockSet.Has(hull.Blocks.Span, design.Mounts[mount].Parent))
                    break;
                ref readonly var visual = ref visuals[i];
                var place = DrawnPosition(visual);
                LaserAim.World(design, mount, place, visual.Orientation, out var emitter, out _);
                var from = TurretLens(beam.Hull, mount, out var lens) ? lens : emitter.RelativeTo(camera);
                var to = place.RelativeTo(camera) + beam.End;
                if ((to - from).LengthSquared() < 1e-6f)
                    break;
                var seed = ((beam.Hull * 0x9E3779B9u + beam.Part * 0x85EBCA6Bu) & 0xFFFFu) / 65536f;
                output[count++] = look.Beam.Instance(new float3(from.X, from.Y, from.Z), new float3(to.X, to.Y, to.Z), beam.State == LaserBeamState.Cutting, seed);
                break;
            }
        }
        DrawnLaserBeams = count;
        _scene.QueueLasers(count);
    }
}
