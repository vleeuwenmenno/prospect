using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

// What the client knows of mining lasers: the beams of the hulls it may see (refreshed every tick one fires), the loose
// ore chunks in its sensors' cover, and the flown hull's action group changes. It predicts its own hull's laser draw
// from its own beams, so the battery it shows drains as the server's does.
internal sealed partial class ClientWorld
{
    // Ticks a beam is shown after the last message that held it.
    private const long BeamHoldTicks = 6;

    private readonly LaserBeamView[] _beams = new LaserBeamView[LaserBeamsMessage.MaxBeams];
    private int _beamCount;
    private long _beamTick = -1, _beamReceivedTick = long.MinValue / 2;

    private readonly OreChunk[] _oreChunks = new OreChunk[OreChunks.Capacity];
    private long _oreChunkTick = -1;
    private int _oreChunkCount;

    private readonly byte[] _groups = new byte[ActionGroupsMessage.MaxParts];
    private uint _groupsHull;
    private int _groupCount;
    private uint _groupsRevision;

    // The client tick of the newest Tick, which prediction steps use to judge whether the beams still stand.
    private long _laserClock;

    // The own hull's laser heat as the newest snapshot gave it: where prediction starts from.
    private LaserHeatState _ownLasers;

    // The pilot's aim and group changes, once a received snapshot allows reliable commands.
    private void SendLaserCommands(PlayerInputBridge bridge, ITransport transport, bool sendCommands)
    {
        if (!sendCommands) return;
        Span<byte> bytes = stackalloc byte[LaserAimMessage.MaxWireSize];
        if (bridge.ConsumeLaserAim(out var aim))
        {
            var writer = new NetWriter(bytes);
            aim.Write(ref writer);
            transport.Send(0, NetChannel.Reliable, writer.Written);
        }

        if (bridge.ConsumeSetActionGroup(out var group))
        {
            var writer = new NetWriter(bytes);
            group.Write(ref writer);
            transport.Send(0, NetChannel.Reliable, writer.Written);
        }
    }

    internal void OnLaserBeams(ref NetReader reader, long clientTick)
    {
        Span<LaserBeamView> incoming = stackalloc LaserBeamView[LaserBeamsMessage.MaxBeams];
        if (!LaserBeamsMessage.TryRead(ref reader, incoming, out var tick, out var count) || tick <= _beamTick) return;
        incoming[..count].CopyTo(_beams);
        _beamCount = count;
        _beamTick = tick;
        _beamReceivedTick = clientTick;
    }

    internal void OnOreChunks(ref NetReader reader)
    {
        Span<OreChunk> incoming = stackalloc OreChunk[OreChunks.Capacity];
        if (!OreChunkMessage.TryRead(ref reader, _content, incoming, out var tick, out var count) || tick <= _oreChunkTick) return;
        incoming[..count].CopyTo(_oreChunks);
        _oreChunkTick = tick;
        _oreChunkCount = count;
    }

    internal void OnActionGroups(ref NetReader reader)
    {
        Span<byte> incoming = stackalloc byte[ActionGroupsMessage.MaxParts];
        if (!ActionGroupsMessage.TryRead(ref reader, incoming, out var hull, out var count)) return;
        incoming[..count].CopyTo(_groups);
        _groups.AsSpan(count).Fill(ActionGroups.NotOverridden);
        _groupsHull = hull;
        _groupCount = count;
        _groupsRevision++;
    }

    // Whether the beams last received still stand at this client tick.
    private bool BeamsFresh(long clientTick) => clientTick - _beamReceivedTick <= BeamHoldTicks;

    // The own hull's lasers for this predicted step: every own beam the server last reported fires, at its laser's
    // power, unless prediction has that laser overheated already, as the server's plan leaves a shut-down laser out,
    // or has the lasers waiting for the battery, when the plan leaves them all out.
    // The lasers that fire are what LaserHeat heats in the step; the others cool.
    private float OwnLaserDemand(long clientTick, in LaserHeatState lasers, out ulong firing)
    {
        firing = 0;
        if (!BeamsFresh(clientTick) || _ownHullId == 0 || !LaserHeat.MayFire(lasers)) return 0;
        var locked = lasers.Locked;
        var design = OwnDesign;
        var count = LaserHeat.Count(design);
        var demand = 0f;
        for (var b = 0; b < _beamCount; b++)
        {
            ref readonly var beam = ref _beams[b];
            if (beam.Hull != _ownHullId || beam.Part >= count) continue;
            var bit = 1UL << beam.Part;
            if ((locked & bit) != 0 || (firing & bit) != 0) continue;
            firing |= bit;
            demand += design.Mounts[design.Lasers[beam.Part]].Type.Laser!.Power;
        }
        return demand;
    }

    internal void PublishLasers(SessionSnapshot snapshot, long clientTick)
    {
        var fresh = BeamsFresh(clientTick);
        snapshot.LaserBeamCount = fresh ? _beamCount : 0;
        snapshot.LaserBeamTick = _beamTick;
        if (fresh) _beams.AsSpan(0, _beamCount).CopyTo(snapshot.LaserBeams);
        snapshot.OreChunkCount = _oreChunkCount;
        snapshot.OreChunkTick = _oreChunkTick;
        _oreChunks.AsSpan(0, _oreChunkCount).CopyTo(snapshot.OreChunks);

        // The flown hull's laser heat as prediction has it: the server's figures of the last snapshot, stepped on.
        var predicted = _hasOwnState && _near.Slots > 0 && _near.IsAliveAt(0);
        snapshot.LaserHeatHull = predicted ? _ownHullId : 0;
        snapshot.LaserHeat = predicted ? _near.Info(0).Lasers : default;
        snapshot.ActionGroupHull = _groupsHull;
        snapshot.ActionGroupCount = _groupCount;
        snapshot.ActionGroupRevision = _groupsRevision;
        _groups.CopyTo(snapshot.ActionGroupOverrides, 0);
    }
}
