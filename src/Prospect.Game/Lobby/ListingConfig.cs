using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Game.Lobby;

/// <summary>
/// Which listing server the game talks to and how patiently: the address public games announce themselves to and
/// the server browser fetches its list from. The default comes from <c>content/net/listing.json</c>; the contract
/// is in docs/listing-server.md. The timings can be changed with a <c>with</c> expression; the address only
/// through <see cref="TryCreate"/>, which checks it.
/// </summary>
public sealed record ListingConfig
{
    private ListingConfig(Uri url)
    {
        Url = url;
    }

    /// <summary>The service's base address, ending in a slash. HTTPS, or plain HTTP on this machine only.</summary>
    public Uri Url { get; private init; }

    /// <summary>Seconds between two heartbeats of a listed server. The service drops an entry after 90 without one.</summary>
    public double RefreshSeconds { get; init; } = 30;

    /// <summary>Seconds one request may take from start to the last byte of the answer.</summary>
    public double TimeoutSeconds { get; init; } = 10;

    /// <summary>Seconds before the first retry after a failed announcement; each further failure doubles it.</summary>
    public double RetrySeconds { get; init; } = 5;

    /// <summary>The longest wait between two attempts, however often they fail.</summary>
    public double MaxRetrySeconds { get; init; } = 300;

    /// <summary>
    /// True when the service runs on this machine, as <c>make listing</c> does. Only then may the list name
    /// addresses that are not public: a list from the internet must not point players at their own network.
    /// </summary>
    public bool IsLocal => Url.IsLoopback;

    /// <summary>
    /// Checks an address for the listing server. It must be an absolute <c>https://</c> address, or
    /// <c>http://</c> on a loopback host, without a user name, a query or a fragment.
    /// </summary>
    public static bool TryCreate(string? url, out ListingConfig? config, out string error)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "The listing address must be a full address such as https://prospect.mvl.sh.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
        {
            error = "The listing address must use https; plain http is only accepted for this machine (127.0.0.1, localhost).";
            return false;
        }

        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            error = "The listing address must not carry a user name, a query or a fragment.";
            return false;
        }

        // Requests are made relative to this, so the path has to end in a slash to keep its last part.
        var builder = new UriBuilder(parsed);
        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";
        config = new ListingConfig(builder.Uri);
        error = "";
        return true;
    }

    /// <summary>
    /// The listing server to use, given what the command line or a settings file asked for: nothing (the
    /// content's default), <c>off</c> (none, which is null), or an address, which keeps the content's timings.
    /// Throws <see cref="InvalidDataException"/> with a message that says what is wrong.
    /// </summary>
    public static ListingConfig? Resolve(string? requested)
    {
        var path = AppPaths.Content("net", "listing.json");
        return Parse(File.ReadAllBytes(path), path, requested);
    }

    /// <summary>
    /// Reads <c>listing.json</c>: the address and the timings. <paramref name="url"/>, when given, replaces the
    /// file's address. An empty address or <c>off</c> means no listing server, which is null.
    /// </summary>
    public static ListingConfig? Parse(ReadOnlyMemory<byte> json, string source, string? url = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{source}: not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{source}: expected one object");

            var refresh = Seconds(root, "refreshSeconds", 30, 1, 3600, source);
            var timeout = Seconds(root, "timeoutSeconds", 10, 1, 120, source);
            var retry = Seconds(root, "retrySeconds", 5, 1, 3600, source);
            var maxRetry = Seconds(root, "maxRetrySeconds", Math.Max(300, retry), retry, 86400, source);

            var address = url ?? (root.TryGetProperty("url"u8, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "");
            address = address.Trim();
            if (address.Length == 0 || address.Equals("off", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!TryCreate(address, out var config, out var error))
                throw new InvalidDataException(url is null ? $"{source}: {error}" : error);

            return config! with
            {
                RefreshSeconds = refresh,
                TimeoutSeconds = timeout,
                RetrySeconds = retry,
                MaxRetrySeconds = maxRetry,
            };
        }
    }

    private static double Seconds(JsonElement root, string name, double fallback, double minimum, double maximum, string source)
    {
        if (!root.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !(seconds >= minimum && seconds <= maximum))
            throw new InvalidDataException($"{source}: {name} must be a number from {minimum:0.##} to {maximum:0.##}");
        return seconds;
    }
}
