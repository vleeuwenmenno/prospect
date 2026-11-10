using Prospect.Engine.IO;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>The data both the server and the client need to run the simulation, loaded from <c>content/</c>.</summary>
public sealed class GameContent
{
    private GameContent(Galaxy galaxy, BlockCatalog blocks, HullDesign pod)
    {
        Galaxy = galaxy;
        Blocks = blocks;
        Pod = pod;
    }

    public BlockCatalog Blocks { get; }

    /// <summary>The galaxy's rules. Apply a server's seed with <see cref="Galaxy.WithSeed"/> to get its galaxy.</summary>
    public Galaxy Galaxy { get; }

    /// <summary>The command pod every player starts in.</summary>
    public HullDesign Pod { get; }

    public static GameContent Load()
    {
        var blocks = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
        return new GameContent(
            Galaxy.Load(AppPaths.Content("galaxy", "galaxy.json")),
            blocks,
            HullDesign.Load(AppPaths.Content("ships", "command_pod.json"), blocks)
        );
    }
}

/// <summary>One simulation step of a hull under its pilot or its standing order.</summary>
public static class HullSim
{
    public const float Dt = (float)TickLoop.SimulationDt;

    /// <summary>
    /// Advances a hull one tick. With an order in force the autopilot flies and the pilot's input is ignored; the
    /// order clears itself on arrival. The server and the predicting client both call exactly this.
    /// </summary>
    public static void Step(ref HullState state, ref HullOrder order, in FlightInput pilot, in FlightParams flight, SectorCache? rocks = null)
    {
        var input = pilot;
        if (order.Kind == OrderKind.MoveTo)
        {
            input = Autopilot.Steer(state, order, flight, Dt, out var arrived);
            if (arrived)
                order.Kind = OrderKind.None;
        }

        FlightModel.Step(ref state, input, flight, Dt);
        if (rocks is not null && Collide(ref state, flight, rocks))
        {
            // Destroyed: the pod is rebuilt at the arrival point, and whatever it was doing is forgotten.
            state = HullState.Spawn(Galaxy.SpawnPoint, flight);
            order = default;
        }
    }

    /// <summary>The share of an asteroid's nominal radius that is solid all the way round.</summary>
    private const float RockSolidFraction = 0.88f;

    /// <summary>
    /// Pushes the hull out of any asteroid it has run into, bounces it and applies impact damage. Hull and rock
    /// are both treated as spheres. Returns true if the impact destroyed the hull.
    /// </summary>
    private static bool Collide(ref HullState state, in FlightParams flight, SectorCache rocks)
    {
        var galaxy = rocks.Galaxy;
        var reach = new Double3(flight.Radius, flight.Radius, flight.Radius);
        var low = galaxy.SectorOf(state.Position - reach);
        var high = galaxy.SectorOf(state.Position + reach);

        // Every asteroid lies wholly inside its sector, so only the sectors the hull itself touches matter.
        for (var x = low.X; x <= high.X; x++)
            for (var y = low.Y; y <= high.Y; y++)
                for (var z = low.Z; z <= high.Z; z++)
                {
                    var coord = new SectorCoord(x, y, z);
                    var content = rocks.Get(coord);
                    var local = state.Position.RelativeTo(galaxy.CenterOf(coord));
                    for (var i = 0; i < content.AsteroidCount; i++)
                    {
                        ref var rock = ref content.Asteroids[i];
                        var offset = local - rock.LocalPosition;
                        var contact = rock.Radius * RockSolidFraction + flight.Radius;
                        var distanceSquared = offset.LengthSquared();
                        if (distanceSquared >= contact * contact)
                            continue;

                        var distance = MathF.Sqrt(distanceSquared);
                        var normal = distance > 1e-3f ? offset / distance : System.Numerics.Vector3.UnitY;
                        state.Position += normal * (contact - distance);
                        local += normal * (contact - distance);

                        var closing = -System.Numerics.Vector3.Dot(state.Velocity, normal);
                        if (closing <= 0)
                            continue;

                        state.Velocity += normal * (closing * (1f + flight.CollisionBounce));
                        var damage = (closing - flight.CollisionSafeSpeed) * flight.Mass * flight.CollisionDamagePerTonneSpeed;
                        if (damage > 0 && FlightModel.Damage(ref state, flight, damage))
                            return true;
                    }
                }

        return false;
    }
}
