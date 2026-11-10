using Prospect.Engine.IO;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>The data both the server and the client need to run the simulation, loaded from <c>content/</c>.</summary>
public sealed class GameContent
{
    private GameContent(Galaxy galaxy, HullDesign pod)
    {
        Galaxy = galaxy;
        Pod = pod;
    }

    /// <summary>The galaxy's rules. Apply a server's seed with <see cref="Galaxy.WithSeed"/> to get its galaxy.</summary>
    public Galaxy Galaxy { get; }

    /// <summary>The command pod every player starts in.</summary>
    public HullDesign Pod { get; }

    public static GameContent Load() =>
        new(
            Galaxy.Load(AppPaths.Content("galaxy", "galaxy.json")),
            HullDesign.Load(AppPaths.Content("ships", "command_pod.json"))
        );
}

/// <summary>One simulation step of a hull under its pilot or its standing order.</summary>
public static class HullSim
{
    public const float Dt = (float)TickLoop.SimulationDt;

    /// <summary>
    /// Advances a hull one tick. With an order in force the autopilot flies and the pilot's input is ignored; the
    /// order clears itself on arrival. The server and the predicting client both call exactly this.
    /// </summary>
    public static void Step(ref HullState state, ref HullOrder order, in FlightInput pilot, in FlightParams flight)
    {
        var input = pilot;
        if (order.Kind == OrderKind.MoveTo)
        {
            input = Autopilot.Steer(state, order, flight, Dt, out var arrived);
            if (arrived)
                order.Kind = OrderKind.None;
        }

        FlightModel.Step(ref state, input, flight, Dt);
    }
}
