using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Client.World;

// What the frame adds to the rock marks and the dust pool beyond the plumes: the hot mark a mining laser's cut leaves,
// glowing and cracking as it cools, and the puff of dust a loose ore chunk throws up when it breaks off. Both go through
// RockExhaust's fixed pools, so neither adds a particle system, and neither takes memory in a frame.
internal sealed partial class WorldView
{
    /// <summary>Marks a frame takes in at most, so a burst of edits on a slow frame is spent over a few frames.</summary>
    private const int CutMarksPerFrame = 8;

    /// <summary>Edits behind at once that are still worth marking; more than this and they are old cuts, not a burst.</summary>
    private const int CutMarksBehind = 128;

    /// <summary>Bursts a frame throws at most, so a whole set of chunks arriving at once cannot fill the dust pool.</summary>
    private const int ChunkBurstsPerFrame = 8;

    // The edit log read so far and how many of its edits are already marked. A log this is not the one held is a
    // world's edits arriving at once (joining, or loading): those cuts are old and none of them glows.
    private RockEditLog? _cutLog;
    private int _cutSeen;

    /// <summary>
    /// Lights and cracks the rock under each cut the session's edit log has gained since the last frame, at the depth
    /// and width the scrape was made at. It reads only what every client holds (the edits, newest last) and the rock's
    /// own pose, so each client draws the same cut; the marks cool by the laser cut look's clock.
    /// </summary>
    private void FeedCutMarks(RockEditLog? log)
    {
        if (log is null)
        {
            _cutLog = null;
            _cutSeen = 0;
            return;
        }

        var count = log.Count;
        if (!ReferenceEquals(log, _cutLog))
        {
            _cutLog = log;
            _cutSeen = count;
            return;
        }

        if (count <= _cutSeen) return;
        // A run of edits arrives whole (a debug groove, or a client catching up): marks are laid for the first of them
        // at a time, oldest first, so every cut is marked and none of the work lands in one frame.
        if (count - _cutSeen > CutMarksBehind)
        {
            _cutSeen = count;
            return;
        }

        var cut = PropulsionLook.Shared.Laser?.Cut;
        var first = _cutSeen;
        var fresh = Math.Min(count - _cutSeen, CutMarksPerFrame);
        if (cut is not null)
            for (var index = first; index < first + fresh; index++)
            {
                var rock = log.RockAt(log.RockOf(index));
                if (!TryGetRockPose(rock, out var position, out var rotation, out _)) continue;
                ref readonly var edit = ref log.EditAt(index);
                // The scrape's own point is the middle of the cut: the mark is centred there, so the rock round the rim
                // of the hole, a radius or so out, is what glows and cracks.
                var local = edit.B;
                var distance = local.Length();
                if (!(distance > 1e-3f)) continue;
                var normal = local / distance;
                var radius = edit.Radius * cut.MarkRadius;
                _rockExhaust.HeatCut(rock.Sector, rock.Index, local, normal, position.RelativeTo(CameraPosition),
                    Vector3.Transform(normal, rotation), radius, Seed(local));
            }

        _cutSeen = first + fresh;
    }

    /// <summary>
    /// Throws the dust of every loose chunk the newest set brought in that the set before did not hold, where it broke
    /// off. A chunk is matched to the set before by OreChunkVisuals, so a chunk that only moved throws nothing.
    /// </summary>
    private void SpawnChunkBursts(SessionSnapshot snapshot)
    {
        var count = _oreVisuals.NewCount;
        if (count <= 0) return;
        var items = _oreVisuals.Items;
        var rules = snapshot.Content.Mining;
        var tick = OreChunkVisuals.ShownTick(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha);
        for (var i = 0; i < count && i < ChunkBurstsPerFrame; i++)
        {
            var index = _oreVisuals.NewAt(i);
            if ((uint)index >= (uint)items.Length) continue;
            ref readonly var chunk = ref items[index];
            var pose = _oreVisuals.Pose(index, tick);
            // Rock dust lit by the beam, with a shade of the ore it carries.
            var tint = new Vector3(0.34f, 0.32f, 0.29f);
            if (snapshot.Galaxy is { } galaxy && (uint)chunk.Resource < (uint)galaxy.Resources.Length)
                tint = Vector3.Lerp(tint, galaxy.Resources[chunk.Resource].Color, 0.45f);
            _rockExhaust.Burst(pose.Position, pose.Velocity, OreChunks.Half(rules, chunk.Mass), tint);
        }

        _oreVisuals.ConsumeNew();
    }

    /// <summary>
    /// A mark's pattern seed from its place in the rock's frame, 0 to 1: the same cut cracks the same way on every
    /// client, and two cuts side by side do not.
    /// </summary>
    private static float Seed(Vector3 local)
    {
        var hash = (uint)(local.X * 731f) * 2654435761u ^ (uint)(local.Y * 733f) * 2246822519u ^ (uint)(local.Z * 739f) * 3266489917u;
        hash ^= hash >> 15;
        hash *= 0x2C1B3C6D;
        hash ^= hash >> 12;
        return (hash & 0xFFFF) / 65536f;
    }
}
