using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// What the Charts window's jump action does, font-free so the tests run it as the window does. It is the same
/// contract as the course menus: one <see cref="JumpPlan"/> says who can go, and the order goes through
/// <see cref="WorldView.OrderJump"/>, which plots for the ship being flown and orders the others.
/// </summary>
internal static class ChartActions
{
    /// <summary>
    /// What a jump to <paramref name="target"/> would do: for the selected ships, or, with none selected, for the ship
    /// being flown alone, so the window is useful from the flight view.
    /// </summary>
    public static JumpPlan Plan(SessionSnapshot snapshot, WorldView world, Double3 target)
    {
        if (world.Selection.Length > 0)
            return world.PlanJump(target);
        if (world.Own.Id == 0)
            return default;
        ReadOnlySpan<uint> own = [world.Own.Id];
        return JumpPlan.Evaluate(snapshot, world, own, target);
    }

    /// <summary>
    /// Orders the jump the plan promised. With nothing selected the ship being flown is selected first, so the order
    /// goes where the plan said it would.
    /// </summary>
    public static void Jump(WorldView world, PlayerInputBridge bridge, Double3 target)
    {
        if (world.Selection.Length == 0 && world.Own.Id != 0)
            world.Select(world.Own.Id, false);
        world.OrderJump(bridge, target);
    }
}
