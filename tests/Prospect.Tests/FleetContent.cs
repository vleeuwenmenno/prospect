using Prospect.Game.World;

namespace Prospect.Tests;

/// <summary>
/// The content most session tests were written against: a small command pod and two vessels, a mining vessel with a
/// wormhole generator and a survey vessel with a rift cutter, which every new player is given beside the pod. The
/// game ships one ship and no fleet; these designs live in <c>fixtures/ships</c> and exist only for tests that need a
/// player with several hulls, every drive kind, or the small pod's layout. The catalogue keeps the order those tests
/// name designs by: pod 0, mining vessel 1, survey vessel 2, drone 3, work drone 4.
/// </summary>
internal static class FleetContent
{
    private static readonly Lazy<GameContent> Loaded = new(() =>
    {
        var copy = new ContentCopy(fleet: true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => copy.Dispose();
        return copy.Load();
    });

    /// <summary>The shipped content with the test fleet in place of the shipped ship, loaded once.</summary>
    public static GameContent Value => Loaded.Value;

    /// <summary>The fixture designs' folder beside the test binary.</summary>
    public static string Ships => Path.Combine(AppContext.BaseDirectory, "fixtures", "ships");

    /// <summary>Puts the test fleet into a copy of the content laid out as <c>content/</c> is.</summary>
    public static void Fit(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Ships, "*.json"))
            File.Copy(file, Path.Combine(root, "ships", Path.GetFileName(file)), overwrite: true);
        File.WriteAllText(Path.Combine(root, "ships", "fleet.json"), """
            {
              "designs": ["command_pod", "mining_vessel", "survey_vessel", "drone", "work_drone"],
              "pod": "command_pod",
              "drone": "drone",
              "workDrone": "work_drone",
              "startingFleet": ["mining_vessel", "survey_vessel"]
            }
            """);
    }
}
