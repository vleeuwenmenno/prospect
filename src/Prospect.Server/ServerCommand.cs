using System.CommandLine;
using System.CommandLine.Help;
using Prospect.Game.Lobby;
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
    internal readonly Option<bool> Verbose = new("--verbose") { Description = "Log debug messages." };
    private readonly RootCommand _root = new("Prospect dedicated server. CLI overrides the settings file. Password: CLI > PROSPECT_PASSWORD > file; an empty environment variable is absent. Game traffic is not encrypted.");

    internal ServerCommand()
    {
        foreach (var option in new Option[] { _name, _visibility, _port, _players, _password, _listing, _seed, _settings, _set, Verbose })
            _root.Options.Add(option);
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

    internal ServerConfig BuildConfig(ParseResult result, GameContent content, string? environmentPassword, Action<string> warn)
    {
        var path = result.GetValue(_settings);
        var settings = content.SettingCatalog.Build(path, result.GetValue(_set) ?? [], warn);
        var file = path is null ? ServerFile.Empty : ServerFile.Read(path, warn);
        var password = result.GetValue(_password) ?? (string.IsNullOrEmpty(environmentPassword) ? file.Password : environmentPassword);
        var visibility = result.GetValue(_visibility) is { } v && Visibilities.TryParse(v, out var parsed) ? parsed : file.Visibility ?? ServerVisibility.Unlisted;
        if (visibility == ServerVisibility.Private)
            throw new InvalidDataException("A dedicated server cannot be private: nobody could join it. Use unlisted or public.");
        var listing = ListingConfig.Resolve(result.GetValue(_listing) ?? file.Listing);
        if (visibility == ServerVisibility.Public && listing is null)
            warn("The server is public but the listing server is turned off: it will not show in any server browser");
        return new ServerConfig
        {
            Name = result.GetValue(_name) ?? file.Name ?? ServerNames.Default,
            Port = result.GetValue(_port) ?? file.Port ?? ProtocolInfo.DefaultPort,
            MaxPlayers = result.GetValue(_players) ?? file.MaxPlayers ?? 16,
            GalaxySeed = result.GetValue(_seed) ?? new ServerConfig().GalaxySeed,
            Visibility = visibility,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Listing = listing,
            Settings = settings,
            Limits = file.Limits ?? ConnectionLimits.Default,
        };
    }
}
