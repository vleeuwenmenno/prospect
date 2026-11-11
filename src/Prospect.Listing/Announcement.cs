using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Prospect.Listing;

/// <summary>A validated, sanitised announcement from a game server. The address is not in it: that comes from the request.</summary>
public sealed record Announcement(
    string Name,
    int Port,
    int Players,
    int MaxPlayers,
    uint Protocol,
    string Version,
    bool Password,
    string? Token)
{
    /// <summary>Longest server name, in Unicode characters, after sanitising.</summary>
    public const int MaxNameLength = 48;

    /// <summary>Longest version text.</summary>
    public const int MaxVersionLength = 32;

    /// <summary>Most players a server may report.</summary>
    public const int MaxPlayerCount = 255;

    // Strict on purpose: exact property names, numbers only as numbers, no comments or trailing commas, and a duplicate
    // property is an error rather than a silent "last one wins". Unknown properties are ignored and never stored, so a
    // newer game can send more without breaking this version of the contract.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };

    /// <summary>
    /// Parses and validates a request body. On failure <paramref name="error"/> is a fixed sentence naming the field;
    /// it never repeats what was sent.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out Announcement? announcement, out string error)
    {
        announcement = null;
        Body? body;
        try
        {
            body = JsonSerializer.Deserialize<Body>(json, JsonOptions);
        }
        catch (JsonException)
        {
            error = "The body is not a valid JSON object for this request.";
            return false;
        }

        if (body is null)
        {
            error = "The body is not a valid JSON object for this request.";
            return false;
        }

        if (body.Name is null)
        {
            error = "name is required.";
            return false;
        }

        var name = SanitizeName(body.Name);
        var nameLength = name.EnumerateRunes().Count();
        if (nameLength < 1 || nameLength > MaxNameLength)
        {
            error = "name must be 1 to 48 characters after control characters are removed.";
            return false;
        }

        if (body.Port is not { } port || port < 1 || port > 65535)
        {
            error = "port must be a whole number from 1 to 65535.";
            return false;
        }

        if (body.MaxPlayers is not { } maxPlayers || maxPlayers < 0 || maxPlayers > MaxPlayerCount)
        {
            error = "maxPlayers must be a whole number from 0 to 255.";
            return false;
        }

        if (body.Players is not { } players || players < 0 || players > maxPlayers)
        {
            error = "players must be a whole number from 0 to maxPlayers.";
            return false;
        }

        if (body.Protocol is not { } protocol || protocol < 0 || protocol > uint.MaxValue)
        {
            error = "protocol must be a whole number from 0 to 4294967295.";
            return false;
        }

        if (body.Version is not { } version || !IsVersion(version))
        {
            error = "version must be 1 to 32 characters of letters, digits and . _ + -";
            return false;
        }

        if (body.Password is not { } password)
        {
            error = "password must be true or false.";
            return false;
        }

        if (body.Token is { Length: 0 or > 128 })
        {
            error = "token is not a token this service issued.";
            return false;
        }

        announcement = new Announcement(
            name, (int)port, (int)players, (int)maxPlayers, (uint)protocol, version, password, body.Token);
        error = "";
        return true;
    }

    /// <summary>
    /// Removes control, formatting (including bidirectional overrides and zero-width characters), private-use and
    /// unassigned characters, collapses runs of whitespace to one space and trims the ends. Names are shown to every
    /// player browsing the list, so nothing that can reorder, hide or break the text survives.
    /// </summary>
    public static string SanitizeName(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        Span<char> units = stackalloc char[2];
        foreach (var rune in raw.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune == Rune.ReplacementChar
                || category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune)
                || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            var count = rune.EncodeToUtf16(units);
            builder.Append(units[..count]);
        }

        return builder.ToString();
    }

    private static bool IsVersion(string version)
    {
        if (version.Length < 1 || version.Length > MaxVersionLength)
        {
            return false;
        }

        foreach (var c in version)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    // Numbers are read as 64-bit so an out-of-range value gets the field's own message instead of a parse error.
    private sealed class Body
    {
        public string? Name { get; set; }
        public long? Port { get; set; }
        public long? Players { get; set; }
        public long? MaxPlayers { get; set; }
        public long? Protocol { get; set; }
        public string? Version { get; set; }
        public bool? Password { get; set; }
        public string? Token { get; set; }
    }
}
