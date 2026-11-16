using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private float? _scriptedRamSpeed;

    private void TickScriptedRam(long tick)
    {
        if (_scriptedRamSpeed is not { } speed || tick < 6f / HullSim.Dt) return;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active) continue;
            var index = connection.Hull.Index;
            ref var state = ref _hulls.State(index);
            var sector = _rocks.Sector(_spawnSector);
            if (sector.AsteroidCount == 0) continue;
            var largest = 0;
            for (var i = 1; i < sector.AsteroidCount; i++)
                if (sector.Asteroids[i].Radius > sector.Asteroids[largest].Radius) largest = i;
            ref var rock = ref sector.Asteroids[largest];
            var direction = Vector3.UnitY;
            var surface = direction * (rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(direction, Quaternion.Conjugate(rock.Rotation))));
            var design = _content.Designs[_hulls.Info(index).Design];
            var bottom = float.PositiveInfinity;
            for (var b = 0; b < design.BlockCount; b++)
                bottom = Math.Min(bottom, design.BlockOffsets[b].Y - design.BlockHalfExtents[b].Y);
            var normal = Vector3.Transform(AsteroidShape.Normal(rock.Shape, Vector3.Transform(surface, Quaternion.Conjugate(rock.Rotation)), rock.Radius, 0.45f), rock.Rotation);
            state.Orientation = Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, normal), 1f + Vector3.Dot(Vector3.UnitY, normal)));
            state.AngularVelocity = Vector3.Zero;
            state.Position = Galaxy.CenterOf(_spawnSector) + rock.LocalPosition + surface + normal * (-bottom - 0.01f);
            state.Velocity = -normal * speed;
        }
        _scriptedRamSpeed = null;
    }
}
