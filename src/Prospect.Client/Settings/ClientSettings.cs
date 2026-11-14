using System.Text.Json;
using System.Text.Json.Serialization;
using Prospect.Client.Render;
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

    /// <summary>
    /// How finely gas clouds are drawn: Low for weak graphics cards, Medium (the default), High and Ultra for strong
    /// ones. A preference of this client: nothing about it reaches a server or a saved world. Applies at once. The
    /// figures of each preset are in <c>content/visuals/gas.json</c>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<GasQuality>))]
    public GasQuality GasQuality { get; set; } = GasQuality.Medium;

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
    public Dictionary<string, PanelOffset> TipPositions { get; set; } = [];

    /// <summary>The most anchors kept, the longest key and the largest shift: a hand-edited file cannot grow or throw a panel away.</summary>
    public const int MaxTipPositions = 32;
    public const int MaxTipPositionKeyLength = 48;
    public const float MaxTipShift = 16384f;

    /// <summary>
    /// Where the player dragged the galaxy map's panels to (<c>MapPanels.KeyOf</c>, such as "Map.Units"), by the
    /// same rule as <see cref="TipPositions"/>: the shift from the panel's default place in unscaled pixels. Window
    /// layout is a preference of this client, like the tips, not of a world or a server, so it is kept here and
    /// follows the player into every game. "Reset panel positions" empties it.
    /// </summary>
    public Dictionary<string, PanelOffset> PanelPositions { get; set; } = [];

    /// <summary>The most panels kept: three today, with room for the next ones.</summary>
    public const int MaxPanelPositions = 16;

    /// <summary>
    /// The interface windows (Social's, Charts, Inventory, Player) by name: whether each is open and pinned, and
    /// where the player left it, in unscaled pixels so it follows a change of interface scale. Kept per client.
    /// A window with no entry takes its first place and defaults.
    /// </summary>
    public Dictionary<string, WindowState> Windows { get; set; } = [];

    /// <summary>The most windows kept, the longest name and the largest corner: a hand-edited file cannot grow the list or throw a window off screen for good.</summary>
    public const int MaxWindows = 16;
    public const int MaxWindowNameLength = 24;
    public const float MaxWindowCoordinate = 16384f;

    /// <summary>
    /// The controls the player changed from the shipped bindings, kept per client: an action by its name
    /// (<c>Boost</c>), the keys of an axis as <c>Strafe.Negative</c> and <c>Strafe.Positive</c>. Each value is a list of
    /// controls written as in <c>content/input/bindings.json</c> (<c>Key:Space</c>, <c>Mouse:Right</c>,
    /// <c>Pad:A</c>); an empty list means the player cleared it. Only what differs from the defaults is stored, so a
    /// later change to a default reaches everyone who never touched it. Unknown names are kept for a newer build.
    /// </summary>
    public Dictionary<string, List<string>> ControlOverrides { get; set; } = [];

    /// <summary>The most remaps kept, controls per remap and the longest name: a hand-edited file cannot grow the list without limit.</summary>
    public const int MaxControlOverrides = 96;
    public const int MaxControlOverrideBindings = 4;
    public const int MaxControlNameLength = 48;

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
        if (!Enum.IsDefined(GasQuality)) GasQuality = GasQuality.Medium;
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

        TipPositions = PanelOffsets.Clean(TipPositions, MaxTipPositions);
        PanelPositions = PanelOffsets.Clean(PanelPositions, MaxPanelPositions);

        Windows ??= [];
        var windows = new Dictionary<string, WindowState>(Math.Min(Windows.Count, MaxWindows));
        foreach (var (name, state) in Windows)
        {
            if (state is null || string.IsNullOrWhiteSpace(name) || name.Length > MaxWindowNameLength || windows.Count >= MaxWindows)
                continue;
            var placed = state.Placed && float.IsFinite(state.X) && float.IsFinite(state.Y);
            windows[name] = new WindowState
            {
                Open = state.Open,
                Pinned = state.Pinned,
                Placed = placed,
                X = placed ? Math.Clamp(state.X, -MaxWindowCoordinate, MaxWindowCoordinate) : 0f,
                Y = placed ? Math.Clamp(state.Y, -MaxWindowCoordinate, MaxWindowCoordinate) : 0f,
            };
        }

        Windows = windows;

        ControlOverrides ??= [];
        var overrides = new Dictionary<string, List<string>>(Math.Min(ControlOverrides.Count, MaxControlOverrides));
        foreach (var (name, values) in ControlOverrides)
        {
            if (values is null || string.IsNullOrWhiteSpace(name) || name.Length > MaxControlNameLength || overrides.Count >= MaxControlOverrides)
                continue;
            var controls = new List<string>(MaxControlOverrideBindings);
            foreach (var text in values)
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 32 && controls.Count < MaxControlOverrideBindings && !controls.Contains(text))
                    controls.Add(text);
            overrides[name] = controls;
        }

        ControlOverrides = overrides;
    }
}

/// <summary>A saved shift of a panel from where the game puts it, in unscaled pixels.</summary>
internal sealed class PanelOffset
{
    public float X { get; set; }
    public float Y { get; set; }
}

/// <summary>What the client keeps of one interface window.</summary>
internal sealed class WindowState
{
    public bool Open { get; set; }
    public bool Pinned { get; set; }

    /// <summary>True when the player has put the window somewhere: <see cref="X"/> and <see cref="Y"/> are its top left corner, unscaled.</summary>
    public bool Placed { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClientSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
