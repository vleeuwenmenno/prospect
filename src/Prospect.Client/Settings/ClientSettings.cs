using System.Text.Json;
using System.Text.Json.Serialization;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.IO;
using Prospect.Game.Protocol;

namespace Prospect.Client.Settings;

/// <summary>The player's preferences, kept as JSON in the user config directory.</summary>
internal sealed class ClientSettings
{
    public bool VSync { get; set; } = true;

    /// <summary>Frames per second limit when vsync is off; 0 means unlimited.</summary>
    public int FrameCap { get; set; }
    public bool Fullscreen { get; set; }
    public int WindowWidth { get; set; } = 1600;
    public int WindowHeight { get; set; } = 900;
    public float UiScale { get; set; } = 1.0f;

    /// <summary>Pointer sensitivity for steering, as a multiplier of the default.</summary>
    public float MouseSensitivity { get; set; } = 1.0f;
    public bool InvertPitch { get; set; }
    public string PlayerName { get; set; } = "Prospector";
    public string LastHost { get; set; } = "127.0.0.1";
    public int LastPort { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>Seconds between automatic saves of a world this game runs, from 10 to 600. Read when a world is opened.</summary>
    public int AutosaveSeconds { get; set; } = 30;

    /// <summary>
    /// The ids of the in-game tips this client will not show again, because the player ticked "Don't show this
    /// again". Kept per client, not per world: a tip is about the game, not about a save. Unknown ids are kept so a
    /// newer build's choices survive an older one; "Reset tips" empties the list.
    /// </summary>
    public List<string> HiddenTips { get; set; } = [];

    /// <summary>The most tip ids kept, and the longest one: a hand-edited file cannot grow the list without limit.</summary>
    public const int MaxHiddenTips = 256;
    public const int MaxTipIdLength = 64;

    /// <summary>
    /// Where the player dragged tip panels to, by anchor (<c>TipPlacement.KeyOf</c>, such as "Screen.TopRight"): the
    /// shift from the place the anchor gives, in unscaled pixels, so it follows a resize and a change of interface
    /// scale. "Reset tips" empties it.
    /// </summary>
    public Dictionary<string, TipOffset> TipPositions { get; set; } = [];

    /// <summary>The most anchors kept, the longest key and the largest shift: a hand-edited file cannot grow or throw a panel away.</summary>
    public const int MaxTipPositions = 32;
    public const int MaxTipPositionKeyLength = 48;
    public const float MaxTipShift = 16384f;

    private static string FilePath => AppPaths.UserConfig("settings.json");

    /// <summary>Loads the settings file. A missing or unreadable file yields defaults; it is never fatal.</summary>
    public static ClientSettings Load() => Load(FilePath);

    /// <summary>Loads the settings from <paramref name="path"/>, with the same fallbacks as <see cref="Load()"/>.</summary>
    public static ClientSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(path), SettingsJson.Default.ClientSettings);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not read {path} ({e.Message}); using default settings");
        }

        return new ClientSettings();
    }

    /// <summary>Writes the settings file atomically, so an interrupted save never corrupts it.</summary>
    public void Save() => Save(FilePath);

    /// <summary>Writes the settings to <paramref name="path"/>, atomically.</summary>
    public void Save(string path)
    {
        try
        {
            Sanitize();
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.ClientSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not save {path}: {e.Message}");
        }
    }

    private void Sanitize()
    {
        FrameCap = FrameCap <= 0 ? 0 : Math.Clamp(FrameCap, 30, 1000);
        WindowWidth = Math.Clamp(WindowWidth, 960, 16384);
        WindowHeight = Math.Clamp(WindowHeight, 540, 16384);
        UiScale = float.IsFinite(UiScale) ? Math.Clamp(UiScale, 0.75f, 2.0f) : 1.0f;
        MouseSensitivity = float.IsFinite(MouseSensitivity) ? Math.Clamp(MouseSensitivity, 0.25f, 3.0f) : 1.0f;
        PlayerName = PlayerNames.Sanitize(PlayerName);
        LastHost = string.IsNullOrWhiteSpace(LastHost) ? "127.0.0.1" : LastHost.Trim();
        LastPort = LastPort is > 0 and <= ushort.MaxValue ? LastPort : ProtocolInfo.DefaultPort;
        AutosaveSeconds = Math.Clamp(AutosaveSeconds, 10, 600);

        // A file can say anything: keep sane, distinct ids, in the order they were hidden.
        HiddenTips ??= [];
        var kept = new List<string>(Math.Min(HiddenTips.Count, MaxHiddenTips));
        foreach (var id in HiddenTips)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxTipIdLength || kept.Contains(id))
                continue;
            if (kept.Count < MaxHiddenTips)
                kept.Add(id);
        }

        HiddenTips = kept;

        TipPositions ??= [];
        var positions = new Dictionary<string, TipOffset>(Math.Min(TipPositions.Count, MaxTipPositions));
        foreach (var (key, offset) in TipPositions)
        {
            if (offset is null || string.IsNullOrWhiteSpace(key) || key.Length > MaxTipPositionKeyLength || positions.Count >= MaxTipPositions)
                continue;
            if (float.IsFinite(offset.X) && float.IsFinite(offset.Y))
                positions[key] = new TipOffset { X = Math.Clamp(offset.X, -MaxTipShift, MaxTipShift), Y = Math.Clamp(offset.Y, -MaxTipShift, MaxTipShift) };
        }

        TipPositions = positions;
    }
}

/// <summary>A saved shift of a tip panel from where its anchor puts it, in unscaled pixels.</summary>
internal sealed class TipOffset
{
    public float X { get; set; }
    public float Y { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClientSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
