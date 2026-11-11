using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Prospect.Game.Lobby;

/// <summary>What the browser brought back to the game: a one-use code, or a fixed error word.</summary>
/// <param name="Code">The handoff code (43 base64url characters), or null when the sign-in did not succeed.</param>
/// <param name="Error"><c>access_denied</c> or <c>failed</c>, or null on success.</param>
internal readonly record struct LoopbackResult(string? Code, string? Error);

/// <summary>
/// The game's end of a Discord sign-in (docs/accounts.md): a tiny HTTP listener on literal 127.0.0.1 that waits
/// for the one browser request the account service redirects to, <c>GET /prospect/signin?attempt=..&amp;code=..</c>
/// (or <c>&amp;error=access_denied|failed</c>).
/// <para>
/// Not a web server. It reads one bounded request head per connection, serves at most
/// <see cref="MaxConnections"/> connections at once (more are closed unread), and gives each a few seconds. The
/// method, path, <c>Host</c>, every query field and the attempt it was started for are checked; anything else gets
/// a 400 and changes nothing, so a stray or hostile request cannot end or use up a real sign-in. The first valid
/// answer completes <see cref="Result"/>; the owner then disposes the receiver, which closes the port. The page
/// it returns is fixed text that names nothing from the request.
/// </para>
/// </summary>
internal sealed class LoopbackReceiver : IDisposable
{
    /// <summary>The only path answered.</summary>
    public const string SigninPath = "/prospect/signin";

    /// <summary>Connections served at once; any more are closed without being read.</summary>
    public const int MaxConnections = 4;

    /// <summary>The longest request head read, request line and headers together.</summary>
    public const int MaxHeadBytes = 4096;

    private static readonly TimeSpan ConnectionTime = TimeSpan.FromSeconds(3);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<LoopbackResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _host;
    private byte[]? _attempt;
    private int _active;
    private int _claimed;
    private int _disposed;

    private LoopbackReceiver(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        _host = "127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = AcceptAsync();
    }

    /// <summary>The port the operating system gave the listener, 1024 to 65535.</summary>
    public int Port { get; }

    /// <summary>Completes with the first valid answer for the expected attempt. Never faults.</summary>
    public Task<LoopbackResult> Result => _result.Task;

    /// <summary>
    /// Opens the listener on 127.0.0.1 and an ephemeral port. Throws <see cref="IOException"/> when no port in
    /// the range the service accepts can be had.
    /// </summary>
    public static LoopbackReceiver Start()
    {
        for (var tries = 0; tries < 4; tries++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                // Windows lets another process bind the same port with address reuse unless this one forbids it.
                if (OperatingSystem.IsWindows())
                    listener.ExclusiveAddressUse = true;
                listener.Start(MaxConnections);
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (port is >= 1024 and <= 65535)
                    return new LoopbackReceiver(listener, port);
            }
            catch (SocketException e)
            {
                listener.Stop();
                throw new IOException("The game could not open a local port for the sign-in.", e);
            }

            listener.Stop();
        }

        throw new IOException("The game could not open a local port for the sign-in.");
    }

    /// <summary>Sets the attempt the service created for this receiver. Requests before this are refused.</summary>
    public void Expect(string attempt) => Volatile.Write(ref _attempt, Encoding.ASCII.GetBytes(attempt));

    /// <summary>Closes the port and ends every connection. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _listener.Stop();
        _result.TrySetCanceled();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (_stop.IsCancellationRequested)
                    return;
                continue;
            }

            if (Interlocked.Increment(ref _active) > MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                socket.Dispose();
                continue;
            }

            _ = ServeAsync(socket);
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        try
        {
            using (socket)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(ConnectionTime);
                var head = new byte[MaxHeadBytes];
                var length = 0;
                var end = -1;
                while (end < 0 && length < head.Length)
                {
                    var read = await socket.ReceiveAsync(head.AsMemory(length), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        return;
                    length += read;
                    end = head.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                }

                var answer = default(LoopbackResult);
                var page = end < 0 ? Page.Invalid : Handle(head.AsSpan(0, end), out answer);
                try
                {
                    await socket.SendAsync(Response(page), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                    socket.Shutdown(SocketShutdown.Both);
                }
                finally
                {
                    // Completed only after the page is on its way: the owner closes the port as soon as it sees the
                    // answer, which would otherwise cut this connection off before the browser had its page.
                    if (page is Page.Signed or Page.Denied or Page.Failed)
                        _result.TrySetResult(answer);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // A connection that is slow, broken or cut off by disposal just ends.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private enum Page : byte
    {
        Invalid,
        Signed,
        Denied,
        Failed,
        Already,
    }

    /// <summary>Checks one request head; the first valid answer for the expected attempt claims the result.</summary>
    private Page Handle(ReadOnlySpan<byte> head, out LoopbackResult answer)
    {
        answer = default;
        foreach (var b in head)
        {
            if (b is not ((>= 0x20 and <= 0x7E) or (byte)'\r' or (byte)'\n'))
                return Page.Invalid;
        }

        var text = Encoding.ASCII.GetString(head);
        var lines = text.Split("\r\n");
        if (lines.Length > 64)
            return Page.Invalid;
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1")
            return Page.Invalid;

        var hosts = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                return Page.Invalid;
            if (!lines[i].AsSpan(0, colon).Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;
            hosts++;
            if (!lines[i].AsSpan(colon + 1).Trim(' ').SequenceEqual(_host))
                return Page.Invalid;
        }

        if (hosts != 1)
            return Page.Invalid;

        var target = request[1];
        var question = target.IndexOf('?');
        if (question < 0 || !target.AsSpan(0, question).SequenceEqual(SigninPath))
            return Page.Invalid;

        string? attempt = null, code = null, error = null;
        foreach (var part in target[(question + 1)..].Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
                return Page.Invalid;
            var value = part[(equals + 1)..];
            if (value.Length is 0 or > 64 || !AccountClient.IsBase64UrlText(value))
                return Page.Invalid;
            switch (part[..equals])
            {
                case "attempt" when attempt is null:
                    attempt = value;
                    break;
                case "code" when code is null:
                    code = value;
                    break;
                case "error" when error is null:
                    error = value;
                    break;
                default:
                    // An unknown field or a field given twice.
                    return Page.Invalid;
            }
        }

        var expected = Volatile.Read(ref _attempt);
        if (expected is null || attempt is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(attempt), expected))
            return Page.Invalid;
        if ((code is null) == (error is null))
            return Page.Invalid;
        if (code is not null && !AccountClient.IsCanonicalSecret(code))
            return Page.Invalid;
        if (error is not null and not ("access_denied" or "failed"))
            return Page.Invalid;

        if (Interlocked.Exchange(ref _claimed, 1) != 0)
            return Page.Already;
        answer = new LoopbackResult(code, error);
        return code is not null ? Page.Signed : error == "access_denied" ? Page.Denied : Page.Failed;
    }

    private static byte[] Response(Page page)
    {
        var (status, title, line) = page switch
        {
            Page.Signed => ("200 OK", "Signed in", "PROSPECT has your sign-in. You can close this tab and return to the game."),
            Page.Denied => ("200 OK", "Sign-in cancelled", "The sign-in was cancelled. You can close this tab; the game is still open."),
            Page.Failed => ("200 OK", "Sign-in failed", "The sign-in did not work. Close this tab and try again from the game."),
            Page.Already => ("200 OK", "Already done", "This sign-in has already been handled. You can close this tab."),
            _ => ("400 Bad Request", "Not a sign-in", "This is not a PROSPECT sign-in."),
        };
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">"
                + "<meta name=\"viewport\" content=\"width=device-width\"><title>PROSPECT: " + title + "</title>"
                + "<style>body{background:#05070a;color:#d8dee6;font:16px system-ui,sans-serif;margin:15vh auto;max-width:32em;padding:0 16px}"
                + "h1{font-size:20px;letter-spacing:.08em}</style></head><body><h1>PROSPECT</h1><p>" + line + "</p></body></html>"
        );
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status + "\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Referrer-Policy: no-referrer\r\n"
                + "X-Content-Type-Options: nosniff\r\n"
                + "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n"
                + "Connection: close\r\n\r\n"
        );
        return [.. head, .. body];
    }
}
