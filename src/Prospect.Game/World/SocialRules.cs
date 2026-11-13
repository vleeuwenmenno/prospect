using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>Content-owned social limits, loaded before simulation starts.</summary>
public sealed class SocialRules
{
    /// <summary>Maximum UTF-16 units per submitted chat line.</summary>
    public int ChatChars { get; init; }
    /// <summary>Maximum UTF-8 bytes per submitted chat line.</summary>
    public int ChatBytes { get; init; }
    /// <summary>Requests allowed in a burst, including malformed ones.</summary>
    public int Burst { get; init; }
    /// <summary>Requests replenished per second.</summary>
    public int PerSecond { get; init; }
    /// <summary>Maximum party members.</summary>
    public int PartySize { get; init; }
    /// <summary>Invitation lifetime in seconds.</summary>
    public int InviteSeconds { get; init; }
    /// <summary>Party refreshes per second.</summary>
    public int StatusHz { get; init; }
    internal static SocialRules Load(string path)
    {
        var rules = JsonSerializer.Deserialize<SocialRules>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (rules.ChatChars is < 1 or > 256 || rules.ChatBytes is < 1 or > 768 || rules.Burst is < 1 or > 32 || rules.PerSecond is < 1 or > 10 || rules.PartySize is < 2 or > 16 || rules.InviteSeconds is < 1 or > 300 || rules.StatusHz is < 1 or > 10 || Prospect.Engine.Time.TickLoop.SimulationHz % rules.StatusHz != 0)
            throw new InvalidDataException("Invalid social limits.");
        return rules;
    }
}
