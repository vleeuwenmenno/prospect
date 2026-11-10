namespace Prospect.Engine.IO;

/// <summary>Where the game finds its shipped files and where it keeps the player's.</summary>
public static class AppPaths
{
    private const string AppFolder = "prospect";

    /// <summary>The directory holding the executable, with <c>content/</c> and <c>assets/</c> beside it.</summary>
    public static string InstallRoot { get; } = AppContext.BaseDirectory;

    /// <summary>Game data: blocks, materials, stations, prices, input bindings.</summary>
    public static string Content(params ReadOnlySpan<string> parts) => Combine(Path.Combine(InstallRoot, "content"), parts);

    /// <summary>Runtime assets: fonts and branding images.</summary>
    public static string Asset(params ReadOnlySpan<string> parts) => Combine(Path.Combine(InstallRoot, "assets"), parts);

    /// <summary>Per-user settings: <c>%APPDATA%/prospect</c> or <c>$XDG_CONFIG_HOME/prospect</c>.</summary>
    public static string UserConfig(params ReadOnlySpan<string> parts) => Combine(UserRoot("XDG_CONFIG_HOME", ".config"), parts);

    /// <summary>Per-user saves and logs: <c>%APPDATA%/prospect</c> or <c>$XDG_DATA_HOME/prospect</c>.</summary>
    public static string UserData(params ReadOnlySpan<string> parts) =>
        Combine(UserRoot("XDG_DATA_HOME", Path.Combine(".local", "share")), parts);

    private static string UserRoot(string xdgVariable, string homeFallback)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);

        var xdg = Environment.GetEnvironmentVariable(xdgVariable);
        var root = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), homeFallback);
        return Path.Combine(root, AppFolder);
    }

    private static string Combine(string root, ReadOnlySpan<string> parts)
    {
        foreach (var part in parts)
            root = Path.Combine(root, part);
        return root;
    }
}
