using System.Numerics;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.World;

// The temporary debug scrape (local games only; the server refuses it anywhere else), until the mining laser
// exists: a key in the pilot view, and the scripted groove of --open rock-scrape. Both only ask: the request goes
// through PlayerInputBridge and the server finds the rock's surface and makes the edits. Remove with the debug
// scrape request.
internal sealed partial class WorldView
{
    // Seconds between two requests while the key is held, and how long a stroke may pause before a new one starts.
    private const float ScrapeInterval = 0.12f, ScrapeStrokeGap = 0.6f;

    private float _scrapeWait, _scrapeIdle;
    private bool _scrapeStroke;
    private RockRef _scrapeRock;
    private Vector3 _scrapeOrigin, _scrapeDirection;

    private int _scrapeScriptPasses, _scrapeScriptSent;
    private float _scrapeScriptWait;

    /// <summary>Scripted runs: the world point the scripted groove is scraped at, once its rock is found.</summary>
    internal Double3? RockScrapeFocus { get; private set; }

    /// <summary>Scripted runs: the outward direction of the rock at <see cref="RockScrapeFocus"/>.</summary>
    internal Vector3 RockScrapeNormal { get; private set; }

    /// <summary>Scripted runs: whether every pass of the scripted groove has been asked for.</summary>
    internal bool RockScrapeAsked => _scrapeScriptPasses > 0 && _scrapeScriptSent >= _scrapeScriptPasses;

    /// <summary>Muted capture hook for <c>--open rock-scrape</c>: scrapes a groove into the rock the hull is put above, this many passes.</summary>
    internal void ScriptRockScrape(int passes)
    {
        _scrapeScriptPasses = Math.Max(1, passes);
        _scrapeScriptSent = 0;
        _scrapeScriptWait = 1.5f;
    }

    // While the key is held in the pilot view: a scrape where the view's centre meets a rock, every interval, joined
    // to the one before while the stroke lasts.
    private void UpdateDebugScrape(PlayerInputBridge bridge, float dt, bool held, int radiusMm)
    {
        _scrapeWait = Math.Max(0, _scrapeWait - dt);
        _scrapeIdle += dt;
        if (!held || _scrapeWait > 0 || _galaxy is null) return;
        if (!PickRock(CameraPosition, _lookDirection, out var name, out var rock, out var position)) return;
        var toRock = Quaternion.Conjugate(rock.Rotation);
        var origin = Vector3.Transform(CameraPosition.RelativeTo(position), toRock);
        var direction = Vector3.Transform(_lookDirection, toRock);
        var request = new RockScrapeRequest { Rock = name, RadiusMm = (ushort)radiusMm };
        if (_scrapeStroke && _scrapeRock == name && _scrapeIdle < ScrapeStrokeGap)
        {
            request.Origins[0] = _scrapeOrigin;
            request.Directions[0] = _scrapeDirection;
            request.Rays = 1;
        }

        request.Origins[request.Rays] = origin;
        request.Directions[request.Rays] = direction;
        request.Rays++;
        bridge.DebugScrape(request);
        _scrapeStroke = true;
        _scrapeRock = name;
        _scrapeOrigin = origin;
        _scrapeDirection = direction;
        _scrapeWait = ScrapeInterval;
        _scrapeIdle = 0;
    }

    // The scripted groove: once the hull hangs still above a rock, sixteen half-metre scrapes along the surface
    // under it, one pass a request, each pass against the surface the one before left.
    private void RunRockScrapeCapture(PlayerInputBridge bridge, float dt, int radiusMm)
    {
        if (_scrapeScriptSent >= _scrapeScriptPasses || _galaxy is null) return;
        _scrapeScriptWait -= dt;
        if (_scrapeScriptWait > 0) return;
        if (!NearestRock(OwnPosition, out var name, out var rock, out var position, out var gap) || gap > 2000 || Own.Velocity.LengthSquared() > 0.01f)
        {
            _scrapeScriptWait = 0.25f;
            return;
        }

        var toRock = Quaternion.Conjugate(rock.Rotation);
        var hull = Vector3.Transform(OwnPosition.RelativeTo(position), toRock);
        var normal = Vector3.Normalize(hull);
        var surface = normal * (rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, normal));
        var along = Vector3.Cross(normal, Vector3.UnitY);
        along = along.LengthSquared() > 1e-4f ? Vector3.Normalize(along) : Vector3.UnitX;
        var request = new RockScrapeRequest { Rock = name, RadiusMm = (ushort)radiusMm, Rays = RockScrapeRequest.MaxRays };
        for (var i = 0; i < RockScrapeRequest.MaxRays; i++)
        {
            request.Origins[i] = hull;
            request.Directions[i] = Vector3.Normalize(surface + along * ((i - (RockScrapeRequest.MaxRays - 1) * 0.5f) * 0.5f) - hull);
        }

        bridge.DebugScrape(request);
        RockScrapeFocus = position + Vector3.Transform(surface, rock.Rotation);
        RockScrapeNormal = Vector3.Transform(normal, rock.Rotation);
        _scrapeScriptSent++;
        _scrapeScriptWait = 0.4f;
    }

    // The first rock a ray from a world point meets, among the loaded sectors.
    private bool PickRock(Double3 from, Vector3 ray, out RockRef name, out Asteroid rock, out Double3 position)
    {
        name = default;
        rock = default;
        position = default;
        var best = float.PositiveInfinity;
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy)) continue;
            var centre = _galaxy!.CenterOf(slot.Coord);
            for (var i = 0; i < slot.Content.AsteroidCount; i++)
            {
                ref readonly var candidate = ref slot.Content.Asteroids[i];
                var at = centre + candidate.LocalPosition + (slot.Displaced ? slot.Offsets[i] : Vector3.Zero);
                var relative = at.RelativeTo(from);
                var ahead = Vector3.Dot(relative, ray);
                if (ahead < -candidate.Radius || ahead - candidate.Radius > best) continue;
                if (Vector3.Cross(relative, ray).LengthSquared() > candidate.Radius * candidate.Radius * 1.05f) continue;
                if (!RockPicking.Hit(ray, relative, candidate, out var distance) || distance >= best) continue;
                best = distance;
                name = new RockRef(slot.Coord, i);
                rock = candidate;
                position = at;
            }
        }

        return best < float.PositiveInfinity;
    }

    // The rock whose bounding sphere is nearest a world point, among the loaded sectors, and the gap to that sphere.
    private bool NearestRock(Double3 from, out RockRef name, out Asteroid rock, out Double3 position, out float gap)
    {
        name = default;
        rock = default;
        position = default;
        gap = float.PositiveInfinity;
        foreach (var slot in _slots)
        {
            if (slot.State != SectorSlot.Ready || !ReferenceEquals(slot.Galaxy, _galaxy)) continue;
            var centre = _galaxy!.CenterOf(slot.Coord);
            for (var i = 0; i < slot.Content.AsteroidCount; i++)
            {
                ref readonly var candidate = ref slot.Content.Asteroids[i];
                var at = centre + candidate.LocalPosition + (slot.Displaced ? slot.Offsets[i] : Vector3.Zero);
                var distance = at.RelativeTo(from).Length() - candidate.Radius;
                if (distance >= gap) continue;
                gap = distance;
                name = new RockRef(slot.Coord, i);
                rock = candidate;
                position = at;
            }
        }

        return gap < float.PositiveInfinity;
    }
}
