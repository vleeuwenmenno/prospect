using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    // How often a connection's rocks are looked over for edits it has not been sent, in ticks, when no edit was made.
    private const int RockEditSweepTicks = 5;

    // Edits sent to one connection in one tick at most: a rock that comes into view with a long list takes a few ticks.
    private const int RockEditsPerTick = 4 * RockEditsMessage.MaxEdits;

    private readonly RockEdits _rockEdits;
    private bool _rockEditsMade;

    // Where the seed put each edited rock and how large it is, by its slot in the log, taken when it is first
    // scraped: asking who has a rock in view must never generate a sector inside the tick.
    private readonly Double3[] _rockEditOrigins;
    private readonly float[] _rockEditRadii;

    /// <summary>The scrapes made into this world's rocks.</summary>
    internal RockEdits RockEdits => _rockEdits;

    /// <summary>
    /// Scrapes a rock, if the rules and the world's limits allow it: the one way an edit is made, whoever asks. The
    /// edit is in the rock's own frame and already in whole units. Clients that have the rock in view are told.
    /// </summary>
    internal RockEditRefusal ScrapeRock(RockRef rock, in RockScrape edit) => ScrapeRock(rock, new ReadOnlySpan<RockScrape>(in edit));

    /// <summary>The same for a stroke of several scrapes: all are made or none.</summary>
    internal RockEditRefusal ScrapeRock(RockRef rock, ReadOnlySpan<RockScrape> edits)
    {
        var rules = _content.RockEdits;
        if ((uint)rock.Index >= (uint)Galaxy.MaxAsteroidsPerSector) return RockEditRefusal.Rock;
        var sector = _rocks.Sector(rock.Sector);
        if (rock.Index >= sector.AsteroidCount || !RockEditRules.Scrapable(sector.Asteroids[rock.Index].Shape)) return RockEditRefusal.Rock;
        var radius = sector.Asteroids[rock.Index].Radius;
        var slot = _rockEdits.Log.SlotOf(rock);
        var bricks = 0;
        foreach (ref readonly var edit in edits)
        {
            if (!rules.Valid(edit, radius)) return RockEditRefusal.Shape;
            bricks += _rockEdits.Bricks.MayAdd(slot, edit);
        }

        var refusal = _rockEdits.Log.Check(rock, edits.Length);
        if (refusal == RockEditRefusal.None) refusal = _rockEdits.Bricks.Check(slot, bricks);
        // A saved world never holds more than its save has room for: asked before anything grows. HasRoom counts
        // its own refusals; one that comes from the edits' limits is counted here.
        if (refusal == RockEditRefusal.None && _save is not null
            && (_rockEdits.Log.Count + edits.Length > _saveLimits.MaxRockEdits || (slot < 0 && _rockEdits.Log.RockCount >= _saveLimits.MaxEditedRocks)))
            refusal = RockEditRefusal.WorldFull;
        if (refusal != RockEditRefusal.None)
        {
            if (_save is not null && refusal == RockEditRefusal.WorldFull)
                Volatile.Write(ref _refusedGrowth, _refusedGrowth + 1);
            return refusal;
        }

        if (!HasRoom(0, 0)) return RockEditRefusal.WorldFull;

        foreach (ref readonly var edit in edits)
            _rockEdits.Bricks.Apply(_rockEdits.Log.Append(rock, edit));
        if (slot < 0) Place(rock, sector.Asteroids[rock.Index]);
        _rockEditsMade = true;
        return RockEditRefusal.None;
    }

    private void Place(RockRef rock, in Asteroid asteroid)
    {
        var slot = _rockEdits.Log.SlotOf(rock);
        _rockEditOrigins[slot] = Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition;
        _rockEditRadii[slot] = asteroid.Radius;
    }

    // Copies the edits rock by rock, each rock's in the order they were made. Takes no memory.
    private void CaptureRockEdits(Persistence.WorldSnapshot buffer)
    {
        var log = _rockEdits.Log;
        var at = 0;
        for (var slot = 0; slot < log.RockCount; slot++)
        {
            buffer.EditedRocks[slot] = log.RockAt(slot);
            buffer.EditedRockEdits[slot] = log.EditsOf(slot);
            for (var index = log.FirstOf(slot); index >= 0; index = log.NextOf(index))
                buffer.RockEdits[at++] = log.EditAt(index);
        }

        buffer.EditedRockCount = log.RockCount;
        buffer.RockEditCount = at;
    }

    // The load rule: every saved edit must be one the rules allow on an asteroid this galaxy really has, and the
    // world's limits must hold them all. A save that fails is refused whole; nothing is dropped to make it fit.
    private void RestoreRockEdits(Persistence.WorldSnapshot world)
    {
        var rules = _content.RockEdits;
        var at = 0;
        for (var i = 0; i < world.EditedRockCount; i++)
        {
            var rock = world.EditedRocks[i];
            var count = world.EditedRockEdits[i];
            var sector = (uint)rock.Index < (uint)Galaxy.MaxAsteroidsPerSector ? _rocks.Sector(rock.Sector) : null;
            if (sector is null || rock.Index >= sector.AsteroidCount || !RockEditRules.Scrapable(sector.Asteroids[rock.Index].Shape)
                || _rockEdits.Log.SlotOf(rock) >= 0 || count < 1 || count > world.RockEditCount - at)
                throw new InvalidDataException($"Saved rock edits {i} are not of a rock of this galaxy that can be scraped, or name it twice.");
            var radius = sector.Asteroids[rock.Index].Radius;
            for (var e = 0; e < count; e++, at++)
            {
                ref readonly var edit = ref world.RockEdits[at];
                var index = rules.Valid(edit, radius) ? _rockEdits.Log.Append(rock, edit) : -1;
                if (index < 0)
                    throw new InvalidDataException($"Saved rock edit {at} is not one the rules allow, or is one more than a world may hold.");
                _rockEdits.Bricks.Apply(index);
            }

            Place(rock, sector.Asteroids[rock.Index]);
        }

        if (at != world.RockEditCount || _rockEdits.Bricks.Overflowed)
            throw new InvalidDataException("The saved rock edits do not add up, or need more bricks than a world may hold.");
    }

    /// <summary>
    /// The temporary debug scrape: local games only (<see cref="ServerConfig.AllowDebugFill"/> and an in-process
    /// link). The sender must fly a live hull that is not in transit, wait out the interval, and every scrape must
    /// land within range of that hull. The server finds the rock's surface along each ray itself.
    /// </summary>
    private void DebugScrape(Connection connection, in RockScrapeRequest request)
    {
        var rules = _content.RockEdits;
        if (!_allowDebugFill || !connection.Transport.IsInProcess) return;
        if (!_hulls.IsAlive(connection.Hull) || _hulls.Info(connection.Hull.Index).Owner != connection.PlayerId
            || _hulls.State(connection.Hull.Index).Jump != JumpPhase.None) return;
        if (_tick < connection.NextRockScrape) return;
        connection.NextRockScrape = _tick + rules.DebugIntervalTicks;
        if (request.RadiusMm < rules.MinRadiusMm || request.RadiusMm > rules.MaxRadiusMm) return;
        if ((uint)request.Rock.Index >= (uint)Galaxy.MaxAsteroidsPerSector) return;
        var sector = _rocks.Sector(request.Rock.Sector);
        if (request.Rock.Index >= sector.AsteroidCount) return;
        var rock = sector.Asteroids[request.Rock.Index];
        if (!RockEditRules.Scrapable(rock.Shape)) return;

        _rocks.Displacement(request.Rock, _tick, out var offset, out _);
        var centre = Galaxy.CenterOf(request.Rock.Sector) + rock.LocalPosition + offset;
        var toRock = Quaternion.Conjugate(rock.Rotation);
        var hull = Vector3.Transform(_hulls.State(connection.Hull.Index).Position.RelativeTo(centre), toRock);
        var cut = _rockEdits.CutOf(request.Rock);
        var radius = request.RadiusMm / 1000f;
        var range = rules.DebugRangeMetres;

        // Every hit is found against the rock as it is now, before any scrape of this request changes it.
        Span<RockScrape> points = stackalloc RockScrape[RockScrapeRequest.MaxRays];
        Span<RockScrape> edits = stackalloc RockScrape[RockScrapeRequest.MaxRays];
        for (var i = 0; i < request.Rays; i++)
        {
            var origin = request.Origins[i];
            var direction = request.Directions[i];
            var length = direction.Length();
            if (!(length > 1e-3f) || !(origin.Length() <= rock.Radius + range * 2)) return;
            direction /= length;
            if (!RockPicking.Hit(Vector3.Transform(direction, rock.Rotation), Vector3.Transform(-origin, rock.Rotation), rock, cut, range * 2, out var distance)) return;
            var hit = origin + direction * distance;
            if (!((hit - hull).Length() <= range)) return;

            // The capsule's far side sits one bite under the surface found.
            var point = RockScrape.From(hit + direction * (rules.DebugBiteMetres - radius), default, radius);
            points[i] = point;
        }

        var count = Math.Max(1, request.Rays - 1);
        for (var i = 0; i < count; i++)
        {
            var from = points[i];
            var to = points[Math.Min(i + 1, request.Rays - 1)];
            edits[i] = new RockScrape { Ax = from.Ax, Ay = from.Ay, Az = from.Az, Bx = to.Ax, By = to.Ay, Bz = to.Az, RadiusMm = request.RadiusMm };
        }

        ScrapeRock(request.Rock, edits[..count]);
    }

    /// <summary>
    /// Sends each connection the edits it lacks of the rocks its sensors reach: new ones in the tick they are made,
    /// the whole list of a rock that comes into view. A connection is sent a rock's edits in order and each once, so
    /// what it holds is always the start of the rock's list.
    /// </summary>
    private void SendRockEdits()
    {
        var log = _rockEdits.Log;
        if (log.Count == 0 || (!_rockEditsMade && _tick % RockEditSweepTicks != 0)) return;
        _rockEditsMade = false;
        Span<RockScrape> run = stackalloc RockScrape[RockEditsMessage.MaxEdits];
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady || connection.RockEditsSeen == log.Count) continue;
            var budget = RockEditsPerTick;
            var behind = false;
            for (var slot = 0; slot < log.RockCount && budget > 0; slot++)
            {
                var sent = connection.RockEditsSent[slot];
                var held = log.EditsOf(slot);
                if (sent >= held) continue;
                if (!RockInView(connection, slot))
                {
                    behind = true;
                    continue;
                }

                // Walk to the first edit the connection lacks; a rock's edits are linked in the order they were made.
                var last = sent == 0 ? -1 : connection.RockEditsLast[slot];
                while (sent < held && budget > 0)
                {
                    var count = 0;
                    while (count < run.Length && count < budget && sent + count < held)
                    {
                        last = last < 0 ? log.FirstOf(slot) : log.NextOf(last);
                        run[count++] = log.EditAt(last);
                    }

                    var writer = new NetWriter(_sendBuffer);
                    RockEditsMessage.Write(ref writer, log.RockAt(slot), sent, run[..count]);
                    if (!connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written))
                    {
                        budget = 0;
                        behind = true;
                        break;
                    }

                    sent += count;
                    budget -= count;
                    connection.RockEditsSent[slot] = sent;
                    connection.RockEditsLast[slot] = last;
                }

                behind |= sent < held;
            }

            // Everything made so far has reached this connection: nothing to look for until the next edit.
            if (!behind && budget > 0) connection.RockEditsSeen = log.Count;
        }
    }

    // A rock is in view of a connection when one of its sensors reaches the rock's surface. Gas is not asked: a
    // hull that can touch a rock must have its grooves, or its own contact would be predicted against another rock.
    private bool RockInView(Connection connection, int slot)
    {
        _rocks.Displacement(_rockEdits.Log.RockAt(slot), _tick, out var offset, out _);
        var centre = _rockEditOrigins[slot] + offset;
        var radius = _rockEditRadii[slot];
        for (var i = connection.SensorHead; i >= 0; i = _sensorNext[i])
        {
            if (!_hulls.IsAliveAt(i) || _hulls.Info(i).Owner != connection.PlayerId) continue;
            var d = centre - _hulls.State(i).Position;
            var reach = (double)radius + _sensorRadius[i];
            if (d.X * d.X + d.Y * d.Y + d.Z * d.Z <= reach * reach) return true;
        }

        return false;
    }
}
