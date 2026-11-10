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
    public string PlayerName { get; set; } = "Prospector";
    public string LastHost { get; set; } = "127.0.0.1";
    public int LastPort { get; set; } = ProtocolInfo.DefaultPort;

    private static string FilePath => AppPaths.UserConfig("settings.json");

    /// <summary>Loads the settings file. A missing or unreadable file yields defaults; it is never fatal.</summary>
    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), SettingsJson.Default.ClientSettings);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not read {FilePath} ({e.Message}); using default settings");
        }

        return new ClientSettings();
    }

    /// <summary>Writes the settings file atomically, so an interrupted save never corrupts it.</summary>
    public void Save()
    {
        try
        {
            Sanitize();
            AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.ClientSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not save {FilePath}: {e.Message}");
        }
    }

    private void Sanitize()
    {
        FrameCap = FrameCap <= 0 ? 0 : Math.Clamp(FrameCap, 30, 1000);
        WindowWidth = Math.Clamp(WindowWidth, 960, 16384);
        WindowHeight = Math.Clamp(WindowHeight, 540, 16384);
        UiScale = float.IsFinite(UiScale) ? Math.Clamp(UiScale, 0.75f, 2.0f) : 1.0f;
        PlayerName = PlayerNames.Sanitize(PlayerName);
        LastHost = string.IsNullOrWhiteSpace(LastHost) ? "127.0.0.1" : LastHost.Trim();
        LastPort = LastPort is > 0 and <= ushort.MaxValue ? LastPort : ProtocolInfo.DefaultPort;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClientSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
