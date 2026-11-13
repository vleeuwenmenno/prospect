using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;
using Prospect.Game.Lobby;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Server;

/// <summary>The dedicated server's command metadata and startup configuration.</summary>
internal sealed class ServerCommand
{
    private readonly Option<string?> _name = new("--name") { Description = $"Server name, 1 to 48 characters (default: {ServerNames.Default})." };
    private readonly Option<string?> _visibility = new("--visibility") { Description = "public or unlisted (default): joined by address; public also announces to the list." };
    private readonly Option<ushort?> _port = new("--port") { Description = $"UDP port, 1..65535 (default: {ProtocolInfo.DefaultPort})." };
    private readonly Option<int?> _players = new("--max-players") { Description = "Player slots, 1..256 (default: 16)." };
    private readonly Option<string?> _password = new("--password") { Description = "Join password. Prefer PROSPECT_PASSWORD: arguments show in process lists. An empty argument clears the password." };
    private readonly Option<string?> _listing = new("--listing") { HelpName = "url|off", Description = "Listing URL or off (default: content/net/listing.json)." };
    private readonly Option<ulong?> _seed = new("--seed") { Description = $"Galaxy seed, 0..{ulong.MaxValue} (default: {new ServerConfig().GalaxySeed})." };
    private readonly Option<string?> _settings = new("--settings") { HelpName = "file", Description = "JSON object of world setting ids and values; optional server object: name, visibility, port, maxPlayers, password, listing, limits." };
    private readonly Option<string[]> _set = new("--set")
    {
        HelpName = "id=value",
        Description = "World setting after the file; repeat in order (last wins). Ids/ranges: content/rules/settings.json.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = false,
    };
    private readonly Option<string?> _world = new("--world")
    {
        HelpName = "folder",
        Description = "Run the saved world in this folder and keep saving it. The folder must hold a world, or add --create-world. "
            + "Not joinable yet: until the server can verify who owns what, a saved world lets no player in.",
    };
    private readonly Option<bool> _createWorld = new("--create-world")
    {
        Description = "With --world: create the world if the folder holds none, from --seed, --settings and --set. A world that is there is opened, and must agree with them.",
    };
    private readonly Option<int?> _autosave = new("--autosave") { HelpName = "seconds", Description = $"With --world: seconds between automatic saves, 1..3600 (default: {DefaultAutosaveSeconds})." };
    internal readonly Option<bool> Verbose = new("--verbose") { Description = "Log debug messages." };
    private readonly RootCommand _root = new("Prospect dedicated server. CLI overrides the settings file. Password: CLI > PROSPECT_PASSWORD > file; an empty environment variable is absent. Game traffic is not encrypted.");

    /// <summary>Seconds between automatic saves of a saved world when none are given.</summary>
    internal const int DefaultAutosaveSeconds = 30;

    /// <summary>The saved world the last <see cref="BuildConfig"/> opened or created, for the log: its folder, absolute; null without one.</summary>
    internal string? WorldFolder { get; private set; }

    /// <summary>True if that world was created by this start, false if it was there and was opened.</summary>
    internal bool WorldCreated { get; private set; }

    /// <summary>For a world that was opened: whether its last run ended with a final save.</summary>
    internal bool WorldWasCleanlyClosed { get; private set; }

    internal ServerCommand()
    {
        foreach (var option in new Option[] { _name, _visibility, _port, _players, _password, _listing, _seed, _settings, _set, _world, _createWorld, _autosave, Verbose })
            _root.Options.Add(option);
        _autosave.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value, out var seconds) && seconds is < 1 or > 3600)
                result.AddError("Autosave must be 1..3600 seconds.");
        });
        _port.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && ushort.TryParse(result.Tokens[0].Value, out var port) && port == 0)
                result.AddError("Port must be 1..65535.");
        });
        _players.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value, out var players) && players is < 1 or > 256)
                result.AddError("Player slots must be 1..256.");
        });
        _visibility.Validators.Add(result =>
        {
            if (result.GetValue(_visibility) is { } value && (!Visibilities.TryParse(value, out var visibility) || visibility == ServerVisibility.Private))
                result.AddError("Visibility must be public or unlisted.");
        });
        // Diagnostic directives can print the entire parse tree, including passwords.
        _root.Directives.Clear();
    }

    internal ParseResult? Parse(string[] args, TextWriter output, TextWriter error, out int exitCode)
    {
        var result = _root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        exitCode = 0;
        if (result.Action is HelpAction || result.Action is not null && result.Errors.Count == 0)
        {
            exitCode = result.Invoke(new InvocationConfiguration { Output = output, Error = error });
            return null;
        }
        if (result.Errors.Count == 0)
            return result;

        // Parser diagnostics can repeat any token, including a misplaced or repeated password.
        // Name only known options, never input values or unknown tokens.
        foreach (var issue in result.Errors)
            error.WriteLine(issue.SymbolResult is System.CommandLine.Parsing.OptionResult { Option: var option } ? $"Invalid or missing value for {option.Name}." : "Unknown or unexpected argument.");
        error.WriteLine("Use --help for options, ranges and examples of settings.");
        exitCode = 2;
        return null;
    }

    /// <summary>
    /// Turns a parsed command line into the server's configuration. With <c>--world</c> this also opens the
    /// saved world, or creates it when <c>--create-world</c> is given and the folder holds none, as its last
    /// step: the configuration that comes back then carries the world's leased store, which the server takes
    /// over when it is constructed with it. Throws <see cref="InvalidDataException"/> for options that do not
    /// go together or disagree with the world, and <see cref="IOException"/> for a world that cannot be had.
    /// </summary>
    internal ServerConfig BuildConfig(ParseResult result, GameContent content, string? environmentPassword, Action<string> warn)
    {
        var path = result.GetValue(_settings);
        var assignments = result.GetValue(_set) ?? [];
        var settings = content.SettingCatalog.Build(path, assignments, warn);
        var file = path is null ? ServerFile.Empty : ServerFile.Read(path, warn);
        var password = result.GetValue(_password) ?? (string.IsNullOrEmpty(environmentPassword) ? file.Password : environmentPassword);
        var visibility = result.GetValue(_visibility) is { } v && Visibilities.TryParse(v, out var parsed) ? parsed : file.Visibility ?? ServerVisibility.Unlisted;
        if (visibility == ServerVisibility.Private)
            throw new InvalidDataException("A dedicated server cannot be private: nobody could join it. Use unlisted or public.");
        var listing = ListingConfig.Resolve(result.GetValue(_listing) ?? file.Listing);
        if (visibility == ServerVisibility.Public && listing is null)
            warn("The server is public but the listing server is turned off: it will not show in any server browser");
        var name = result.GetValue(_name) ?? file.Name ?? ServerNames.Default;
        var seed = result.GetValue(_seed);
        if (!ServerNames.TryNormalize(name, out _))
            throw new InvalidDataException($"A server's name must be 1 to {ServerNames.MaxLength} characters.");
        if (!string.IsNullOrEmpty(password) && !Passwords.IsUsable(password))
            throw new InvalidDataException($"A server's password must be 1 to {Passwords.MaxChars} characters, without control characters.");

        // Last, after everything that can refuse the command line without touching a world.
        var world = OpenWorld(result, content, name, seed, path, assignments, settings);
        return new ServerConfig
        {
            Name = name,
            Port = result.GetValue(_port) ?? file.Port ?? ProtocolInfo.DefaultPort,
            MaxPlayers = result.GetValue(_players) ?? file.MaxPlayers ?? 16,

            // A saved world brings its own seed and settings; what was asked for has been held against them.
            GalaxySeed = world is null ? seed ?? new ServerConfig().GalaxySeed : new ServerConfig().GalaxySeed,
            Visibility = visibility,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Listing = listing,
            Settings = world is null ? settings : null,
            Limits = file.Limits ?? ConnectionLimits.Default,
            World = world,
        };
    }

    /// <summary>
    /// The saved world the command line names, opened or created, or null when it names none. An existing world
    /// is first read without being run: the seed and the settings the command line gives are held against the
    /// world's own, and a command line that disagrees is refused while the world is still exactly as it was,
    /// not even marked as having been started. Validation and writable open retain the same lease. A folder
    /// without a world is only filled when the command line says so: a mistyped folder must not become a new world.
    /// </summary>
    private WorldPersistence? OpenWorld(ParseResult result, GameContent content, string name, ulong? seed, string? settingsPath, IReadOnlyList<string> assignments, in WorldSettings asked)
    {
        var folder = result.GetValue(_world);
        var create = result.GetValue(_createWorld);
        var autosave = result.GetValue(_autosave);
        WorldFolder = null;
        WorldCreated = false;
        WorldWasCleanlyClosed = false;
        if (folder is null)
        {
            if (create || autosave is not null)
                throw new InvalidDataException("--create-world and --autosave are for a saved world: name its folder with --world.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidDataException("--world needs a folder.");
        folder = Path.GetFullPath(folder);
        var namedSettings = NamedSettings(content.SettingCatalog, settingsPath, assignments);
        var compatibility = SaveCompatibility.Of(content);
        var requested = asked;
        string? conflict = null;
        var opened = SqliteWorldStore.Open(folder, compatibility,
            snapshot => conflict = LaunchConflict(content, snapshot, seed, namedSettings, requested));
        if (conflict is not null)
            throw new InvalidDataException(conflict);

        WorldPersistence world;
        switch (opened.Outcome)
        {
            case OpenOutcome.Opened:
                WorldCreated = false;
                WorldWasCleanlyClosed = opened.WasCleanClose;
                world = new WorldPersistence(opened.Store!, opened.Snapshot!, opened.Generation) { AutosaveSeconds = autosave ?? DefaultAutosaveSeconds };
                break;

            case OpenOutcome.Missing:
                if (!create)
                    throw new InvalidDataException($"There is no world in {folder}, and none was created. To create one there, add --create-world.");
                if (!ServerNames.TryNormalize(name, out var worldName))
                    throw new InvalidDataException($"A server's name must be 1 to {ServerNames.MaxLength} characters.");
                var initial = WorldSnapshot.NewWorld(content, Guid.NewGuid(), worldName, DateTimeOffset.UtcNow, seed ?? new ServerConfig().GalaxySeed, asked, default);
                var created = SqliteWorldStore.Create(folder, initial);
                if (created.Outcome != CreateOutcome.Created)
                    throw new IOException($"The world could not be created in {folder}: {created.Error}");
                WorldCreated = true;
                WorldWasCleanlyClosed = false;
                world = new WorldPersistence(created.Store!, initial, created.Generation) { AutosaveSeconds = autosave ?? DefaultAutosaveSeconds };
                break;

            default:
                throw new IOException(Explain(folder, opened.Outcome, opened.Error));
        }

        WorldFolder = folder;
        return world;
    }

    /// <summary>
    /// Returns the reason a command line conflicts with the saved world, or null when it agrees. Only what was actually
    /// given counts as asked: a seed on the command line, and the settings named in the file or with
    /// <c>--set</c>. A setting that was not named is not a wish for its default.
    /// </summary>
    private static string? LaunchConflict(GameContent content, WorldSnapshot world, ulong? seed, IReadOnlyList<string> namedSettings, in WorldSettings asked)
    {
        if (seed is { } wanted && wanted != world.Seed)
            return $"The world was created with seed {world.Seed} and keeps it; --seed {wanted} cannot change it. Leave --seed out or give the world's own. The world was not opened.";

        var catalog = content.SettingCatalog;
        var differing = new List<string>();
        foreach (var id in namedSettings)
        {
            var definition = catalog.Find(id)!;
            var value = asked.Get(definition);
            var saved = SavedValue(world, definition.Id);
            if (saved is not { } kept || kept != value)
                differing.Add(saved is { } was ? $"{definition.Id} is {definition.Format(was)}, not {definition.Format(value)}" : $"{definition.Id} is not a setting of the world");
        }

        if (differing.Count > 0)
            return $"The world keeps the settings it was created with: {string.Join("; ", differing)}. Leave them out or give the world's own values. The world was not opened.";
        return null;
    }

    /// <summary>The ids of the world settings a command line names, in the settings file and with <c>--set</c>, each once.</summary>
    private static List<string> NamedSettings(WorldSettingCatalog catalog, string? settingsPath, IReadOnlyList<string> assignments)
    {
        var named = new List<string>();
        void Name(ReadOnlySpan<char> id)
        {
            if (catalog.Find(id.Trim()) is { } definition && !named.Contains(definition.Id))
                named.Add(definition.Id);
        }

        if (settingsPath is not null)
        {
            // The catalogue has read this file already and refused it if it could not be read as settings.
            using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (!entry.Name.Equals(WorldSettingCatalog.ReservedId, StringComparison.OrdinalIgnoreCase))
                        Name(entry.Name);
                }
            }
        }

        foreach (var assignment in assignments)
        {
            var split = assignment.IndexOf('=');
            if (split > 0)
                Name(assignment.AsSpan(0, split));
        }

        return named;
    }

    /// <summary>The value a saved world has for a setting, or null if it has none by that id.</summary>
    private static float? SavedValue(WorldSnapshot world, string id)
    {
        for (var i = 0; i < world.SettingCount; i++)
        {
            if (world.SettingIds[i] == id)
                return world.SettingValues[i];
        }

        return null;
    }

    /// <summary>Why a world could not be had, for whoever started the server: this is their own machine, so the folder is named.</summary>
    private static string Explain(string folder, OpenOutcome outcome, string? error) =>
        outcome switch
        {
            OpenOutcome.Busy => $"The world in {folder} is in use by another server. It was left alone.",
            OpenOutcome.Incompatible => $"The world in {folder} was saved with other game files or by another build of the game. It was left as it is. ({error})",
            OpenOutcome.Damaged => $"The world in {folder} is damaged or is not a world this game wrote. It was left as it is. ({error})",
            OpenOutcome.Missing => $"The world in {folder} is no longer there.",
            _ => $"The world in {folder} could not be opened: {error}",
        };
}
