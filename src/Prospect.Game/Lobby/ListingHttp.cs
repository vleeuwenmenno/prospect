using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Prospect.Game.Protocol;

namespace Prospect.Game.Lobby;

/// <summary>What became of one request to the listing server.</summary>
internal enum ListingOutcome : byte
{
    /// <summary>The service answered; <see cref="ListingReply.Status"/> and <see cref="ListingReply.Body"/> say what.</summary>
    Answered,

    /// <summary>No connection, a broken connection, or an answer that is not HTTP.</summary>
    Unreachable,

    /// <summary>The answer did not arrive in full within the time allowed.</summary>
    TimedOut,

    /// <summary>The answer is larger than this game accepts.</summary>
    TooLarge,
}

/// <summary>An answer from the listing server, or the reason there is none.</summary>
internal readonly record struct ListingReply(ListingOutcome Outcome, int Status, ReadOnlyMemory<byte> Body, TimeSpan? RetryAfter)
{
    public bool Is(int status) => Outcome == ListingOutcome.Answered && Status == status;
}

/// <summary>
/// The one way the game talks to the listing server: HTTPS (plain HTTP only to this machine), every request
/// bounded in time from the first byte to the last, every answer bounded in size, no redirects, no cookies, no
/// decompression. The service is not trusted: nothing it answers can make a request hang or grow without limit.
/// Requests run on the thread pool; nothing here is called from the simulation or the frame loop.
/// </summary>
internal sealed class ListingHttp : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _base;
    private readonly TimeSpan _timeout;

    /// <param name="config">The listing server.</param>
    /// <param name="ipv4Only">
    /// Connect over IPv4 only. The service lists a game at the address its announcement came from, so a server
    /// that players can only reach over IPv4 must not announce itself over IPv6.
    /// </param>
    public ListingHttp(ListingConfig config, bool ipv4Only = false)
    {
        _base = config.Url;
        _timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        var handler = new SocketsHttpHandler
        {
            // A redirect could lead off https or to another host; the service has no reason to send one.
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _timeout,
            MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 2,
            MaxResponseDrainSize = 4096,
            ResponseDrainTimeout = TimeSpan.FromSeconds(1),
            // A long-running server must notice when the service moves to another address.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (ipv4Only)
        {
            handler.ConnectCallback = static async (context, cancel) =>
            {
                // A socket of this family resolves the host to its IPv4 addresses only.
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancel).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("prospect", ProtocolInfo.GameVersion));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Sends one request and reads the whole answer, at most <paramref name="maxBytes"/> of it. Never throws
    /// for anything the network or the service does; only cancellation through <paramref name="cancel"/> throws.
    /// <paramref name="limit"/> shortens the time allowed below the configured time-out.
    /// </summary>
    public async Task<ListingReply> SendAsync(
        HttpMethod method,
        string relative,
        ReadOnlyMemory<byte> json,
        string? bearer,
        int maxBytes,
        CancellationToken cancel,
        TimeSpan? limit = null
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(limit is { } shorter && shorter < _timeout ? shorter : _timeout);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(_base, relative));
            if (!json.IsEmpty)
            {
                request.Content = new ReadOnlyMemoryContent(json);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > maxBytes)
                return new ListingReply(ListingOutcome.TooLarge, (int)response.StatusCode, default, null);

            // One byte more than allowed is asked for, so an answer that is too large is told apart from a full one.
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[Math.Min(maxBytes + 1, 16 * 1024)];
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length > maxBytes)
                        return new ListingReply(ListingOutcome.TooLarge, (int)response.StatusCode, default, null);
                    Array.Resize(ref buffer, Math.Min(maxBytes + 1, buffer.Length * 2));
                }

                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
            }

            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            return new ListingReply(ListingOutcome.Answered, (int)response.StatusCode, buffer.AsMemory(0, length), retryAfter);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new ListingReply(ListingOutcome.TimedOut, 0, default, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or InvalidOperationException or FormatException)
        {
            return new ListingReply(ListingOutcome.Unreachable, 0, default, null);
        }
    }

    /// <summary>
    /// The sentence in an error answer (<c>{"error": "..."}</c>), cleaned and cut short for a log line, or an
    /// empty string when the answer is not one.
    /// </summary>
    public static string ErrorText(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error"u8, out var error)
                || error.ValueKind != JsonValueKind.String)
                return "";

            // The service is not trusted with the log either: printable characters only, and not many.
            var text = error.GetString()!;
            var clean = new StringBuilder(160);
            foreach (var c in text)
            {
                if (clean.Length == 160)
                    break;
                clean.Append(c is >= ' ' and <= '~' ? c : ' ');
            }

            return clean.ToString().Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
