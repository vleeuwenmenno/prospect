using System.Text.Json;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// What a server's settings file says about the server itself, beside the world settings: the object under
/// <c>"server"</c>. Every value is optional; null means the file does not say.
/// </summary>
/// <param name="Name">The server's name, as written; not yet checked.</param>
/// <param name="Visibility">Public, unlisted or private.</param>
/// <param name="Port">The UDP port to listen on.</param>
/// <param name="MaxPlayers">Player slots.</param>
/// <param name="Password">The password players must know.</param>
/// <param name="Listing">The listing server's address, or <c>off</c>.</param>
public sealed record ServerFile(string? Name, ServerVisibility? Visibility, ushort? Port, int? MaxPlayers, string? Password, string? Listing)
{
    /// <summary>
    /// The limits on pending connections, from the object under <c>"limits"</c>: the defaults with what the
    /// file changes. Null when the file has no such object.
    /// </summary>
    public ConnectionLimits? Limits { get; init; }

    /// <summary>The name of the object in a settings file that holds these, which no world setting may be called.</summary>
    public const string Key = WorldSettingCatalog.ReservedId;

    /// <summary>A file that says nothing about the server.</summary>
    public static ServerFile Empty { get; } = new(null, null, null, null, null, null);

    /// <summary>Reads the <c>"server"</c> object of a settings file. A file without one is <see cref="Empty"/>.</summary>
    public static ServerFile Read(string path, Action<string>? warn = null) => Parse(File.ReadAllBytes(path), path, warn);

    /// <summary>
    /// Reads the <c>"server"</c> object from a settings file's bytes. A name it does not know is ignored with a
    /// warning; a value of the wrong kind throws <see cref="InvalidDataException"/> saying which and why.
    /// </summary>
    public static ServerFile Parse(ReadOnlyMemory<byte> json, string source, Action<string>? warn = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{source}: not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            // Found the way the world settings skip it: whatever its case.
            JsonElement server = default;
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Name.Equals(Key, StringComparison.OrdinalIgnoreCase))
                        server = entry.Value;
                }
            }

            if (server.ValueKind == JsonValueKind.Undefined)
                return Empty;
            if (server.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{source}: \"{Key}\" must be an object");

            var result = Empty;
            foreach (var entry in server.EnumerateObject())
            {
                var value = entry.Value;
                switch (entry.Name)
                {
                    case "name":
                        result = result with { Name = Text(value, source, "name") };
                        break;
                    case "visibility":
                        if (!Visibilities.TryParse(Text(value, source, "visibility"), out var visibility))
                            throw new InvalidDataException($"{source}: {Key}.visibility must be \"public\", \"unlisted\" or \"private\"");
                        result = result with { Visibility = visibility };
                        break;
                    case "port":
                        result = result with { Port = (ushort)Whole(value, source, "port", 1, ushort.MaxValue) };
                        break;
                    case "maxPlayers":
                        result = result with { MaxPlayers = Whole(value, source, "maxPlayers", 1, 256) };
                        break;
                    case "password":
                        result = result with { Password = Text(value, source, "password") };
                        break;
                    case "listing":
                        result = result with { Listing = Text(value, source, "listing") };
                        break;
                    case "limits":
                        result = result with { Limits = ReadLimits(value, source, warn) };
                        break;
                    default:
                        warn?.Invoke($"{source}: unknown server setting \"{entry.Name}\" ignored");
                        break;
                }
            }

            return result;
        }
    }

    private static ConnectionLimits ReadLimits(JsonElement limits, string source, Action<string>? warn)
    {
        if (limits.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{source}: {Key}.limits must be an object");

        var result = ConnectionLimits.Default;
        foreach (var entry in limits.EnumerateObject())
        {
            var value = entry.Value;
            switch (entry.Name)
            {
                case "pendingPerAddress":
                    result = result with { PendingPerAddress = Whole(value, source, "limits.pendingPerAddress", 1, 1024) };
                    break;
                case "pendingTotal":
                    result = result with { PendingTotal = Whole(value, source, "limits.pendingTotal", 1, 1024) };
                    break;
                case "connectionBurst":
                    result = result with { ConnectionBurst = Whole(value, source, "limits.connectionBurst", 1, 10_000) };
                    break;
                case "connectionsPerSecond":
                    result = result with { ConnectionsPerSecond = Number(value, source, "limits.connectionsPerSecond") };
                    break;
                case "handshakeSeconds":
                    result = result with { HandshakeSeconds = Number(value, source, "limits.handshakeSeconds") };
                    break;
                default:
                    warn?.Invoke($"{source}: unknown server setting \"limits.{entry.Name}\" ignored");
                    break;
            }
        }

        // The ranges, and the one rule between two of them, are the server's own: it says them in its words.
        try
        {
            result.Validate();
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{source}: {Key}.{e.Message}", e);
        }

        return result;
    }

    private static double Number(JsonElement value, string source, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"{source}: {Key}.{name} must be a number");

    private static string Text(JsonElement value, string source, string name) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"{source}: {Key}.{name} must be a piece of text");

    private static int Whole(JsonElement value, string source, string name, int minimum, int maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum
            ? number
            : throw new InvalidDataException($"{source}: {Key}.{name} must be a whole number from {minimum} to {maximum}");
}
