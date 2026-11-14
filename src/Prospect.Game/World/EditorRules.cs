using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>
/// What a server allows of the ship editor, from <c>content/net/editor.json</c>. Only the server enforces these
/// and it tells a client how long it has to wait, so they are not part of the content identity: a client's own
/// copy changes nothing.
/// </summary>
public sealed class EditorRules
{
    /// <summary>Seconds a hull must have gone without damage before it can be edited.</summary>
    public int QuietSeconds { get; init; }
    public int NewShipNameChars { get; init; } = 32;
    public int MaxOwnedShips { get; init; } = 16;
    public float NewShipDistance { get; init; } = 50;
    public float NewShipClearance { get; init; } = 5;
    public string NewShipRoot { get; init; } = "core";

    /// <summary>Designs the server holds at once while they are received and checked, over all players.</summary>
    public int Uploads { get; init; }

    /// <summary>Seconds a design may take to arrive after its commit was accepted.</summary>
    public int UploadSeconds { get; init; }

    /// <summary>Seconds an accepted design waits for its owner's client to be ready to show it.</summary>
    public int ReadySeconds { get; init; }

    /// <summary>Seconds the replacement waits for the other players' clients once the owner's is ready.</summary>
    public int PeerWaitSeconds { get; init; }

    /// <summary>Seconds between two commits of one player.</summary>
    public int CommitSeconds { get; init; }

    /// <summary><see cref="QuietSeconds"/> in simulation ticks.</summary>
    public long QuietTicks => (long)QuietSeconds * TickLoop.SimulationHz;

    /// <summary>
    /// Ticks until a hull last damaged in <paramref name="damageTick"/> may be edited, at <paramref name="tick"/>;
    /// zero when it may. A hull that never took damage carries <see cref="long.MinValue"/>, which is compared and
    /// never subtracted from.
    /// </summary>
    public long QuietTicksLeft(long tick, long damageTick)
    {
        var quiet = QuietTicks;
        if (damageTick == long.MinValue || damageTick <= tick - quiet)
            return 0;

        // From here the damage is within the quiet time of now, or stamped ahead of it: the difference is small.
        return damageTick >= tick ? quiet : quiet - (tick - damageTick);
    }

    /// <summary>
    /// Reads and checks the rules. The designs held at once must fit the bytes <paramref name="transfers"/> lets a
    /// server hold for bulk data, so the editor adds no budget of its own.
    /// </summary>
    internal static EditorRules Load(string path, BlockTransferRules transfers)
    {
        EditorRules rules;
        try
        {
            rules = JsonSerializer.Deserialize<EditorRules>(File.ReadAllBytes(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"{path}: no editor rules");
        }
        catch (JsonException error) { throw new InvalidDataException($"{path}: invalid JSON: {error.Message}", error); }

        if (rules.NewShipNameChars is < 1 or > 32 || rules.MaxOwnedShips is < 1 or > 128 || !float.IsFinite(rules.NewShipDistance) || rules.NewShipDistance is < 10 or > 1000
            || !float.IsFinite(rules.NewShipClearance) || rules.NewShipClearance is < 1 or > 100 || string.IsNullOrWhiteSpace(rules.NewShipRoot)
            || rules.QuietSeconds is < 0 or > 3600 || rules.Uploads is < 1 or > 16 || (long)rules.Uploads * transfers.MaxBytes > transfers.HeldBytes
            || rules.UploadSeconds is < 1 or > 600 || rules.ReadySeconds is < 1 or > 600 || rules.PeerWaitSeconds is < 0 or > 60
            || rules.PeerWaitSeconds > rules.ReadySeconds || rules.CommitSeconds is < 0 or > 600)
            throw new InvalidDataException($"{path}: invalid editor limits");
        return rules;
    }
}
