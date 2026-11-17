using System.Numerics;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private float? _scriptedScrapeRadius;
    private float _scriptedScrapeStandoff;
    private float? _scriptedScrapeRest;
    private bool _scriptedScrapePlaced;
    private long _scriptedScrapeRestUntil = -1;
    private RockRef _scriptedScrapeRock;
    private Vector3 _scriptedScrapeNormal;

    // The capture fixture of --open rock-scrape. It only moves the flown hull: the scrapes themselves are asked for
    // by the client through the debug scrape, and contact with the groove is ordinary collision.
    private void TickScriptedRockScrape(long tick)
    {
        if (_scriptedScrapeRadius is not { } wanted) return;
        var seconds = tick * HullSim.Dt;
        var sector = _rocks.Sector(_spawnSector);
        if (!_scriptedScrapePlaced)
        {
            if (seconds < 2f || sector.AsteroidCount == 0) return;
            var chosen = -1;
            for (var i = 0; i < sector.AsteroidCount; i++)
                if (RockEditRules.Scrapable(sector.Asteroids[i].Shape)
                    && (chosen < 0 || MathF.Abs(sector.Asteroids[i].Radius - wanted) < MathF.Abs(sector.Asteroids[chosen].Radius - wanted)))
                    chosen = i;
            if (chosen < 0) return;
            _scriptedScrapeRock = new RockRef(_spawnSector, chosen);
            ref readonly var rock = ref sector.Asteroids[chosen];

            // On the rock's upper side, leaning toward the star, so the groove is lit and seen from above.
            var sun = sector.Summary.SunDirection;
            var direction = Vector3.Normalize(new Vector3(sun.X, 0, sun.Z) * 0.6f + Vector3.UnitY);
            _scriptedScrapeNormal = direction;
            var surface = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(direction, Quaternion.Conjugate(rock.Rotation)));
            foreach (var connection in _connections)
            {
                if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
                ref var state = ref _hulls.State(connection.Hull.Index);
                state.Position = Galaxy.CenterOf(_spawnSector) + rock.LocalPosition + direction * (surface + _scriptedScrapeStandoff);

                // Nose to the rock.
                state.Orientation = Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitZ, direction), 1f + Vector3.Dot(Vector3.UnitZ, direction)));
                state.Velocity = Vector3.Zero;
                state.AngularVelocity = Vector3.Zero;
            }

            _scriptedScrapePlaced = true;
            return;
        }

        if (_scriptedScrapeRest is not { } rest || seconds < rest) return;
        var slot = _rockEdits.Log.SlotOf(_scriptedScrapeRock);
        if (slot < 0) return;
        ref readonly var scraped = ref sector.Asteroids[_scriptedScrapeRock.Index];
        var centre = Galaxy.CenterOf(_spawnSector) + scraped.LocalPosition;
        if (_scriptedScrapeRestUntil < 0)
        {
            // Over the middle of the first pass, belly to the rock.
            var index = _rockEdits.Log.FirstOf(slot);
            for (var i = 0; i < Math.Min(8, _rockEdits.Log.EditsOf(slot) - 1); i++) index = _rockEdits.Log.NextOf(index);
            var point = Vector3.Transform(_rockEdits.Log.EditAt(index).A, scraped.Rotation);
            var normal = Vector3.Normalize(point);
            _scriptedScrapeNormal = normal;
            foreach (var connection in _connections)
            {
                if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
                var design = _content.Designs[_hulls.Info(connection.Hull.Index).Design];
                var bottom = float.PositiveInfinity;
                for (var b = 0; b < design.BlockCount; b++)
                    bottom = Math.Min(bottom, design.BlockOffsets[b].Y - design.BlockHalfExtents[b].Y);
                ref var state = ref _hulls.State(connection.Hull.Index);
                state.Orientation = Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, normal), 1f + Vector3.Dot(Vector3.UnitY, normal)));
                state.AngularVelocity = Vector3.Zero;
                state.Position = centre + point + normal * (-bottom + 2.5f);
            }

            _scriptedScrapeRestUntil = tick + (long)(3f / HullSim.Dt);
        }

        // Pressed down for a while, then left where ordinary contact stopped it.
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
            ref var state = ref _hulls.State(connection.Hull.Index);
            state.Velocity = tick < _scriptedScrapeRestUntil ? -_scriptedScrapeNormal * 0.8f : Vector3.Zero;
            state.AngularVelocity = Vector3.Zero;
        }

        if (tick >= _scriptedScrapeRestUntil) _scriptedScrapeRest = null;
    }
}
