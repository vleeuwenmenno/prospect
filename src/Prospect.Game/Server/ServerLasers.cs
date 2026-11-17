using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Game.Server;

// Mining lasers: the pilot's intent (groups on and the aim) comes in through LaserAimMessage; before the hull round
// the lasers that may fire are planned and their power asked of the flight step; after it, each laser that was paid
// for cuts the rock it meets through ScrapeRock, the one way an edit is made, and the ore it frees adds up into
// loose chunks. Nothing here takes memory in the tick.
public sealed partial class ServerHost
{
    /// <summary>Beams fired at once over the whole world, at most.</summary>
    private const int MaxBeams = 256;

    // Interiors described lately, so a laser on a rock does not describe it again every scrape.
    private const int InteriorCache = 8;

    /// <summary>Server-only state of one laser that fires: planned before the round, cut after it, carried tick to tick.</summary>
    private struct ServerBeam
    {
        public HullHandle Hull;
        public int Mount;
        public byte Part;
        public float Demand;

        /// <summary>Where the pilot aims, in the world, found before the round.</summary>
        public Double3 Target;

        public LaserBeamState State;

        /// <summary>Where the beam ended this tick, relative to the hull's position.</summary>
        public Vector3 End;

        // The stroke: the rock, the last scrape made (its B end is where a continuing scrape starts) and when.
        public bool Stroke;
        public RockRef Rock;
        public RockScrape Last;
        public long LastTick;

        /// <summary>The last tick the beam was on <see cref="Rock"/>: a stroke ends only when the beam has been off it a while.</summary>
        public long OnTick;

        /// <summary>Cubic metres the beam has cut since its last scrape, not yet given to one.</summary>
        public double Volume;

        // Ore freed and the rock it came in, for the rock's main ore and its two mix-ins, since the last chunk of each.
        public RockRef OreRock;
        public double Ore0, Ore1, Ore2, Host0, Host1, Host2;
    }

    private readonly ServerBeam[] _beams = new ServerBeam[MaxBeams];
    private readonly ServerBeam[] _beamsBefore = new ServerBeam[MaxBeams];
    private int _beamCount, _beamCountBefore;
    private readonly LaserBeamView[] _beamSend = new LaserBeamView[LaserBeamsMessage.MaxBeams];

    // Per hull slot and groupable part (design order): the group the hull fires it with, or NotOverridden.
    private readonly byte[] _groupOverrides = CreateOverrides();
    private readonly uint[] _groupRevisions = new uint[HullSlots];
    private const int HullSlots = 256;

    private readonly RockInterior[] _interiors = new RockInterior[InteriorCache];
    private readonly RockRef[] _interiorRocks = new RockRef[InteriorCache];
    private int _interiorCount, _interiorNext;

    private readonly OreChunks _chunks = new();
    private readonly OreChunk[] _chunkSend = new OreChunk[OreChunks.Capacity];

    /// <summary>The loose ore chunks of this world.</summary>
    internal OreChunks OreChunks => _chunks;

    private static byte[] CreateOverrides()
    {
        var overrides = new byte[HullSlots * ActionGroupsMessage.MaxParts];
        Array.Fill(overrides, ActionGroups.NotOverridden);
        return overrides;
    }

    // A new hull in a slot starts with its design's groups.
    private void ClearActionGroups(int slot)
    {
        if ((uint)slot >= HullSlots) return;
        _groupOverrides.AsSpan(slot * ActionGroupsMessage.MaxParts, ActionGroupsMessage.MaxParts).Fill(ActionGroups.NotOverridden);
        _groupRevisions[slot]++;
    }

    /// <summary>The group a hull fires its groupable part <paramref name="part"/> with: its override, else the design's.</summary>
    internal int GroupOf(int slot, HullDesign design, int part)
    {
        var mount = design.Mounts[design.Lasers[part]];
        var overridden = (uint)slot < HullSlots && part < ActionGroupsMessage.MaxParts ? _groupOverrides[slot * ActionGroupsMessage.MaxParts + part] : ActionGroups.NotOverridden;
        return ActionGroups.Effective(mount, overridden);
    }

    // The pilot's intent: for the flown hull only. Kept until the next message, a change of control or the hull's end.
    private void ReceiveLaserAim(Connection connection, in LaserAimMessage message)
    {
        if (message.Hull != connection.Hull.Packed || !_hulls.IsAlive(connection.Hull)
            || _hulls.Info(connection.Hull.Index).Owner != connection.PlayerId
            || message.Offset.Length() > _content.Mining.MaxAimOffsetMetres)
        {
            connection.FireGroups = 0;
            return;
        }

        connection.FireGroups = message.Groups;
        connection.AimHull = message.Hull;
        connection.AimOffset = message.Offset;
        connection.AimDirection = Vector3.Normalize(message.Direction);
    }

    // One part's group on an owned hull, without a rebuild. Only a groupable part, and only a group it may have.
    private void ReceiveSetActionGroup(Connection connection, in SetActionGroupMessage message)
    {
        var handle = new HullHandle((int)(message.Hull & 65535), (int)(message.Hull >> 16));
        if (!_hulls.IsAlive(handle) || (uint)handle.Index >= HullSlots) return;
        ref readonly var info = ref _hulls.Info(handle.Index);
        if (info.Owner != connection.PlayerId || info.Wreck || info.WorkDrone) return;
        var design = _content.Designs[info.Design];
        if (message.Part >= design.Lasers.Length) return;
        var mount = design.Mounts[design.Lasers[message.Part]];
        if (message.Group != ActionGroups.NotOverridden && !ActionGroups.Allowed(mount.Type, message.Group)) return;
        ref var slot = ref _groupOverrides[handle.Index * ActionGroupsMessage.MaxParts + message.Part];
        if (slot == message.Group) return;
        slot = message.Group;
        _groupRevisions[handle.Index]++;
        _touched = true;
    }

    // Before the round: which lasers fire, where the pilot aims, and the power each hull asks of its flight step. A
    // laser fires only from the flown hull of a player who holds a group of it, with its block there, the hull not in
    // transit, the aim within its gimbal limit and a clear line out past its own hull.
    private void PlanLasers()
    {
        Array.Copy(_beams, _beamsBefore, _beamCount);
        _beamCountBefore = _beamCount;
        _beamCount = 0;
        for (var i = 0; i < _beamCountBefore; i++)
            if (_hulls.IsAlive(_beamsBefore[i].Hull)) _hulls.Info(_beamsBefore[i].Hull.Index).LaserDemand = 0;

        var rules = _content.Mining;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || connection.FireGroups == 0) continue;
            var handle = connection.Hull;
            if (connection.AimHull != handle.Packed || !_hulls.IsAlive(handle) || (uint)handle.Index >= HullSlots) continue;
            ref var info = ref _hulls.Info(handle.Index);
            ref readonly var state = ref _hulls.State(handle.Index);
            if (info.Owner != connection.PlayerId || info.Wreck || state.Jump != JumpPhase.None) continue;
            var design = _content.Designs[info.Design];
            var lasers = Math.Min(design.Lasers.Length, Math.Min(rules.MaxLasersPerHull, ActionGroupsMessage.MaxParts));
            if (lasers == 0) continue;

            // The aim: the first rock along the ray from the camera through the pointer, else a point as far out as
            // the farthest laser reaches. Every laser turns to it from where it sits.
            var reach = 0f;
            for (var k = 0; k < lasers; k++) reach = Math.Max(reach, design.Mounts[design.Lasers[k]].Type.Laser!.Range);
            var origin = state.Position + connection.AimOffset;
            var length = connection.AimOffset.Length() + reach;
            var target = RockRay.First(_rocks, origin, connection.AimDirection, length, _tick, out _, out var distance)
                ? origin + connection.AimDirection * distance
                : origin + connection.AimDirection * length;

            var blocks = _hulls.Blocks(handle.Index);
            var pivot = LaserAim.Pivot(design);
            var inverse = Quaternion.Conjugate(state.Orientation);
            for (var k = 0; k < lasers && _beamCount < MaxBeams; k++)
            {
                var m = design.Lasers[k];
                var mount = design.Mounts[m];
                if (!BlockSet.Has(blocks, mount.Parent)) continue;
                var group = GroupOf(handle.Index, design, k);
                if ((connection.FireGroups & ActionGroups.Bit(group)) == 0) continue;
                var spec = mount.Type.Laser!;
                LaserAim.Local(design, m, out var grid, out var outward);
                var local = HullRotation.Rotate(target.RelativeTo(state.Position), inverse) + pivot - grid;
                var span = local.Length();
                if (!(span > 1e-3f)) continue;
                var direction = local / span;
                if (!spec.WithinGimbal(outward, direction)) continue;
                // Only the hull's own blocks are asked here; a rock in the way is what the beam cuts.
                var clear = Math.Min(span, _hulls.Flight(handle.Index).Radius * 2 + design.BlockSize);
                if (!TractorReach.Clear(design.Boxes, blocks, design.BlockSize, grid, grid + direction * clear)) continue;

                ref var beam = ref _beams[_beamCount++];
                beam = Carried(handle, (byte)k);
                beam.Hull = handle;
                beam.Mount = m;
                beam.Part = (byte)k;
                beam.Demand = spec.Power;
                beam.Target = target;
                beam.State = 0;
                info.LaserDemand += spec.Power;
            }
        }
    }

    // The stroke and the ore a laser carries from the tick before, if it fired then.
    private ServerBeam Carried(HullHandle hull, byte part)
    {
        for (var i = 0; i < _beamCountBefore; i++)
            if (_beamsBefore[i].Hull == hull && _beamsBefore[i].Part == part) return _beamsBefore[i];
        return default;
    }

    // After the round: each laser its hull paid for fires from where the hull now is, toward the aim found before,
    // and cuts the first rock within its range.
    private void FireLasers()
    {
        for (var b = 0; b < _beamCount; b++)
        {
            ref var beam = ref _beams[b];
            beam.State = 0;
            if (!_hulls.IsAlive(beam.Hull)) continue;
            ref readonly var info = ref _hulls.Info(beam.Hull.Index);
            ref readonly var state = ref _hulls.State(beam.Hull.Index);
            if (!(info.Power.Laser > 0) || state.Jump != JumpPhase.None)
            {
                // A beam cut short loses the cut it had not yet made into a scrape.
                beam.Stroke = false;
                beam.Volume = 0;
                continue;
            }

            var design = _content.Designs[info.Design];
            if (!BlockSet.Has(_hulls.Blocks(beam.Hull.Index), design.Mounts[beam.Mount].Parent)) continue;
            var spec = design.Mounts[beam.Mount].Type.Laser!;
            LaserAim.World(design, beam.Mount, state.Position, state.Orientation, out var emitter, out var outward);
            var toward = beam.Target.RelativeTo(emitter);
            var span = toward.Length();
            if (!(span > 1e-3f)) continue;
            var direction = toward / span;
            if (!spec.WithinGimbal(outward, direction)) continue;
            if (!RockRay.First(_rocks, emitter, direction, spec.Range, _tick, out var rock, out var distance))
            {
                beam.State = LaserBeamState.Firing;
                beam.End = (emitter + direction * spec.Range).RelativeTo(state.Position);
                continue;
            }

            var hit = emitter + direction * distance;
            beam.State = LaserBeamState.Cutting;
            beam.End = hit.RelativeTo(state.Position);
            Cut(ref beam, rock, hit, direction, spec);
        }
    }

    // A beam on a rock adds to what it has cut; once that fills a scrape (or the spot has moved on), one is made
    // through ScrapeRock, joined to the last while the stroke lasts. What the scrape takes out yields its ore.
    private void Cut(ref ServerBeam beam, RockRef rock, Double3 hit, Vector3 direction, LaserSpec spec)
    {
        var rules = _content.Mining;
        var edits = _content.RockEdits;
        var sector = _rocks.Sector(rock.Sector);
        if ((uint)rock.Index >= (uint)sector.AsteroidCount) return;
        ref readonly var asteroid = ref sector.Asteroids[rock.Index];
        if (!RockEditRules.Scrapable(asteroid.Shape)) return;
        _rocks.Displacement(rock, _tick, out var offset, out var rockVelocity);
        var centre = Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition + offset;
        var toRock = Quaternion.Conjugate(asteroid.Rotation);
        var hitLocal = Vector3.Transform(hit.RelativeTo(centre), toRock);
        var along = Vector3.Transform(direction, toRock);
        var radius = spec.CutRadius;
        var face = MathF.PI * radius * radius;

        beam.Volume += face * spec.CutRate * HullSim.Dt;
        var fresh = !beam.Stroke || beam.Rock != rock || _tick - beam.OnTick > rules.StrokeGapTicks;
        if (beam.Stroke && beam.Rock == rock) beam.OnTick = _tick;
        if (!fresh && _tick - beam.LastTick < rules.EditIntervalTicks) return;

        // Where a full bite would put the scrape, and how far that is from the last one.
        var spot = hitLocal + along * (rules.BiteMetres - radius);
        var length = fresh ? 0f : Vector3.Distance(spot, beam.Last.B);
        if (!fresh && length * RockScrape.Units > edits.MaxSegmentUnits)
        {
            fresh = true;
            length = 0;
        }

        var area = face + 2 * radius * length;
        var due = beam.Volume >= area * rules.BiteMetres || (!fresh && length >= rules.MoveRadii * radius);
        if (!due) return;

        // A spot swept on before the bite is full is cut shallower: what was cut, spread over the stroke.
        var depth = (float)Math.Clamp(beam.Volume / area, rules.BiteMetres * 0.1, rules.BiteMetres);
        spot = hitLocal + along * (depth - radius);
        var end = RockScrape.From(spot, spot, radius);
        var edit = fresh ? end : new RockScrape { Ax = beam.Last.Bx, Ay = beam.Last.By, Az = beam.Last.Bz, Bx = end.Bx, By = end.By, Bz = end.Bz, RadiusMm = end.RadiusMm };
        if (ScrapeRock(rock, edit) != RockEditRefusal.None)
        {
            // A rock or a world that is full takes no more: the beam still shows, and nothing comes off.
            beam.Stroke = false;
            beam.Volume = 0;
            return;
        }

        beam.Volume = Math.Max(0, beam.Volume - area * depth);
        beam.Stroke = true;
        beam.Rock = rock;
        beam.Last = edit;
        beam.LastTick = _tick;
        beam.OnTick = _tick;
        var kilograms = rules.ScrapeKg(radius, length, Galaxy.AsteroidDensity) * (depth / rules.BiteMetres);
        // Chunks leave outward from the rock's centre, not back up the beam into the ship that cut them.
        var outward = Vector3.Normalize(hit.RelativeTo(centre));
        Yield(ref beam, rock, asteroid, spot + along * radius, kilograms, hit, -outward, rockVelocity);
    }

    // The ore at the scrape's deepest point, from the rock's interior, added to the laser's tally; each ore that
    // passes the content's mark breaks off as a chunk at the cut.
    private void Yield(ref ServerBeam beam, RockRef rock, in Asteroid asteroid, Vector3 deepest, double kilograms, Double3 hit, Vector3 direction, Vector3 rockVelocity)
    {
        if (Galaxy.Interiors is null || !(kilograms > 0)) return;
        ref readonly var interior = ref Interior(rock, asteroid);
        interior.Sample(deepest, out var sample);
        if (beam.OreRock != rock)
        {
            beam.OreRock = rock;
            beam.Ore0 = beam.Ore1 = beam.Ore2 = beam.Host0 = beam.Host1 = beam.Host2 = 0;
        }

        Add(ref beam.Ore0, ref beam.Host0, asteroid.Resource, sample.MainShare);
        Add(ref beam.Ore1, ref beam.Host1, asteroid.Mix1, sample.Mix1Share);
        Add(ref beam.Ore2, ref beam.Host2, asteroid.Mix2, sample.Mix2Share);

        void Add(ref double ore, ref double host, int resource, float share)
        {
            if (resource < 0 || !(share > 0)) return;
            ore += kilograms * share;
            host += kilograms;
            if (ore < _content.Mining.ChunkOreKg) return;
            SpawnChunk(resource, (float)Math.Clamp(ore / host, 1e-4, 1), (float)host, hit, direction, rockVelocity);
            ore = 0;
            host = 0;
        }
    }

    private ref readonly RockInterior Interior(RockRef rock, in Asteroid asteroid)
    {
        for (var i = 0; i < _interiorCount; i++)
            if (_interiorRocks[i] == rock) return ref _interiors[i];
        var slot = _interiorCount < InteriorCache ? _interiorCount++ : _interiorNext++ % InteriorCache;
        RockInterior.Describe(Galaxy, rock, asteroid, out _interiors[slot]);
        _interiorRocks[slot] = rock;
        return ref _interiors[slot];
    }

    // A chunk breaks off where the beam meets the rock, thrown out along -direction (the rock's outward direction at
    // the cut) with the rock's own motion and a turn
    // that comes from its identity, so no randomness is needed.
    private void SpawnChunk(int resource, float grade, float mass, Double3 hit, Vector3 direction, Vector3 rockVelocity)
    {
        var rules = _content.Mining;
        mass = Math.Min(mass, 1e6f);
        var half = OreChunks.Half(rules, mass);
        var seed = _chunks.NextId * 2654435761u;
        var axis = Vector3.Normalize(new Vector3((seed & 255) - 127.5f, (seed >> 8 & 255) - 127.5f, (seed >> 16 & 255) - 127.5f) + new Vector3(0.1f));
        var chunk = new OreChunk
        {
            Position = hit - direction * (half * 1.8f + 0.05f),
            Velocity = rockVelocity - direction * rules.ChunkSpeed,
            AngularVelocity = axis * rules.ChunkSpin,
            Orientation = Quaternion.CreateFromAxisAngle(axis, (seed >> 24) / 40f),
            Resource = resource,
            Grade = grade,
            Mass = mass,
            Born = _tick,
        };
        _chunks.Add(_content, chunk, out _);
    }

    private void CleanupOreChunks()
    {
        if (_chunks.Items.IsEmpty) return;
        Span<Double3> players = stackalloc Double3[byte.MaxValue];
        var count = 0;
        foreach (var connection in _connections)
            if (connection.State == ConnectionState.Active && _hulls.IsAlive(connection.Hull) && count < players.Length)
                players[count++] = _hulls.State(connection.Hull.Index).Position;
        _chunks.Cleanup(_content.Mining.ChunkLifetimeTicks, players[..count], _content.Exploration.SensorRadius);
    }

    // Every tick a beam fires, to every connection that may see its hull and knows its design; one empty set after
    // the last, which a client does not need (it lets an unrefreshed beam go) but which ends it at once.
    private void SendLaserBeams()
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady) continue;
            var count = 0;
            for (var b = 0; b < _beamCount && count < _beamSend.Length; b++)
            {
                ref readonly var beam = ref _beams[b];
                if (beam.State == 0 || !_hulls.IsAlive(beam.Hull) || !MaySeeHull(connection, beam.Hull.Index, _tick)
                    || !BlockSet.Has(connection.ReadyDesigns, _hulls.Info(beam.Hull.Index).Design)) continue;
                _beamSend[count++] = new LaserBeamView { Hull = beam.Hull.Packed, Part = beam.Part, State = beam.State, End = beam.End };
            }

            if (count == 0 && !connection.BeamsSent) continue;
            var writer = new NetWriter(_sendBuffer);
            LaserBeamsMessage.Write(ref writer, _tick, _beamSend.AsSpan(0, count));
            connection.Transport.Send(connection.Peer, NetChannel.Unreliable, writer.Written);
            connection.BeamsSent = count != 0;
        }
    }

    private void SendOreChunks()
    {
        if (_tick % 6 != 0) return;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady) continue;
            var count = 0;
            foreach (ref readonly var chunk in _chunks.Items)
                if (Covered(connection.PlayerId, chunk.Position)) _chunkSend[count++] = chunk;
            if (count == 0 && !connection.OreChunksSent) continue;
            var writer = new NetWriter(_sendBuffer);
            OreChunkMessage.Write(ref writer, _tick, _chunkSend.AsSpan(0, count));
            if (connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written)) connection.OreChunksSent = count != 0;
        }
    }

    // The flown hull's group changes, when they or the flown hull change.
    private void SendActionGroups()
    {
        Span<byte> groups = stackalloc byte[ActionGroupsMessage.MaxParts];
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady || !_hulls.IsAlive(connection.Hull)) continue;
            var slot = connection.Hull.Index;
            if ((uint)slot >= HullSlots) continue;
            if (connection.GroupsHull == connection.Hull.Packed && connection.GroupsRevision == _groupRevisions[slot]) continue;
            var design = _content.Designs[_hulls.Info(slot).Design];
            var count = Math.Min(design.Lasers.Length, ActionGroupsMessage.MaxParts);
            _groupOverrides.AsSpan(slot * ActionGroupsMessage.MaxParts, count).CopyTo(groups);
            var writer = new NetWriter(_sendBuffer);
            ActionGroupsMessage.Write(ref writer, connection.Hull.Packed, groups[..count]);
            if (!connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written)) continue;
            connection.GroupsHull = connection.Hull.Packed;
            connection.GroupsRevision = _groupRevisions[slot];
        }
    }

    // The hull's changed groups, by its index in the save. Takes no memory.
    private void CaptureActionGroups(Persistence.WorldSnapshot buffer, int slot, int saved)
    {
        if ((uint)slot >= HullSlots) return;
        var parts = Math.Min(_content.Designs[_hulls.Info(slot).Design].Lasers.Length, ActionGroupsMessage.MaxParts);
        for (var part = 0; part < parts; part++)
        {
            var group = _groupOverrides[slot * ActionGroupsMessage.MaxParts + part];
            if (group != ActionGroups.NotOverridden)
                buffer.ActionGroups[buffer.ActionGroupCount++] = new Persistence.SavedActionGroup(saved, (byte)part, group);
        }
    }

    // The load rule: every chunk physically sound and of an ore this galaxy has, every group change of a groupable
    // part a restored hull really has, and a group that part may take. A save that fails is refused whole.
    private void RestoreMining(Persistence.WorldSnapshot world, ReadOnlySpan<HullHandle> hulls)
    {
        if ((uint)world.OreChunkCount > OreChunks.Capacity) throw new InvalidDataException("Too many ore chunks.");
        for (var i = 0; i < world.OreChunkCount; i++)
        {
            ref readonly var chunk = ref world.OreChunks[i];
            if (!OreChunks.Valid(_content, chunk) || chunk.Id == 0 || world.NextOreChunkId != 0 && chunk.Id >= world.NextOreChunkId)
                throw new InvalidDataException($"Saved ore chunk {i} is not one this world can hold.");
        }

        _chunks.Restore(world.OreChunks.AsSpan(0, world.OreChunkCount), world.NextOreChunkId);
        _chunks.EnforceCaps(_content);

        for (var i = 0; i < world.ActionGroupCount; i++)
        {
            var saved = world.ActionGroups[i];
            if ((uint)saved.Hull >= (uint)hulls.Length || !_hulls.IsAlive(hulls[saved.Hull]) || (uint)hulls[saved.Hull].Index >= HullSlots)
                throw new InvalidDataException($"Saved action group {i} names no hull.");
            var slot = hulls[saved.Hull].Index;
            var design = _content.Designs[_hulls.Info(slot).Design];
            if (saved.Part >= Math.Min(design.Lasers.Length, ActionGroupsMessage.MaxParts)
                || !ActionGroups.Allowed(design.Mounts[design.Lasers[saved.Part]].Type, saved.Group))
                throw new InvalidDataException($"Saved action group {i} is not one that hull's part may have.");
            _groupOverrides[slot * ActionGroupsMessage.MaxParts + saved.Part] = saved.Group;
            _groupRevisions[slot]++;
        }
    }
}
