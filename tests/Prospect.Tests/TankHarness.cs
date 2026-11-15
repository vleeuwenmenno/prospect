using Prospect.Game.World;

namespace Prospect.Tests;

/// <summary>Sets what a stored hull's tanks hold, for tests that need a hull part empty.</summary>
internal static class TankHarness
{
    /// <summary>
    /// Fills every tank of the hull in a slot to a share of its capacity, one share for each fuel, and adds the
    /// tanks up into the hull's state as the simulation does. A tank the hull has lost stays empty.
    /// </summary>
    public static void Fill(HullStore hulls, int index, float nitrogen, float hydrazine, float bipropellant)
    {
        var state = hulls.Info(index).BlockState;
        var design = hulls.BlockStates.Design(state);
        var tanks = design.Tanks;
        var levels = hulls.BlockStates.TankLevels(state);
        var alive = hulls.Blocks(index);
        for (var i = 0; i < tanks.Length; i++)
        {
            var share = tanks[i].Kind switch { FuelKind.Nitrogen => nitrogen, FuelKind.Hydrazine => hydrazine, _ => bipropellant };
            levels[i] = BlockSet.Has(alive, tanks[i].Block) ? (double)tanks[i].Capacity * share : 0.0;
        }

        hulls.State(index).Fuel = TankFuel.Total(levels, tanks);
    }

    /// <summary>Fills every tank of the hull in a slot to one share of its capacity.</summary>
    public static void Fill(HullStore hulls, int index, float share) => Fill(hulls, index, share, share, share);

    /// <summary>What each tank of the hull in a slot holds, copied.</summary>
    public static double[] Levels(HullStore hulls, int index) => hulls.BlockStates.Tanks(hulls.Info(index).BlockState).ToArray();
}
