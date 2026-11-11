using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Prospect.Listing;

/// <summary>Builds the listing server: configuration, limits, the proxy rule and the version 1 endpoints.</summary>
public static class ListingApp
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the application without starting it. <paramref name="configure"/> runs before the options are read, so a
    /// test can add configuration, swap the clock or choose the listening address.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        configure?.Invoke(builder);
        RefuseFrameworkAddressRewriting(builder.Configuration);

        var options = builder.Configuration.GetSection(ListingOptions.Section).Get<ListingOptions>() ?? new ListingOptions();
        options.Validate();

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ListingStore>();
        builder.Services.AddSingleton<RequestLimiter>();
        builder.Services.AddHostedService<ExpirySweeper>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = options.MaxHeaderBytes;
            kestrel.Limits.MaxRequestLineSize = options.MaxHeaderBytes;
            kestrel.Limits.MaxConcurrentConnections = options.MaxConnections;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 0;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.RequestHeadersTimeoutSeconds);
        });

        var app = builder.Build();

        // Proxy trust decides every address the service sees, so the log says at start which proxies it believes.
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Prospect.Listing");
        log.LogInformation(
            "Listing server: entries live {Ttl} s, at most {Max} ({PerAddress} per address); "
            + "trusted proxies: {Proxies}; private addresses {Private}; "
            + "{Global} requests/s in all, rate-limit state for {Slots} addresses",
            options.EntryTtlSeconds,
            options.MaxEntries,
            options.MaxEntriesPerAddress,
            options.TrustedNetworks.Count == 0 ? "none" : string.Join(", ", options.TrustedNetworks),
            options.AllowPrivateAddresses ? "allowed" : "refused",
            options.GlobalRequestsPerSecond,
            options.RateLimitAddresses);

        // A slot stays taken for at most a minute after its address's last request, so the requests the whole
        // service lets through in a minute bound how many slots can be held at once. With fewer slots than that,
        // enough addresses working together can hold every slot and push everyone else into the shared buckets.
        if (options.RateLimitAddresses < options.SlotsThatCannotAllBeHeld)
        {
            log.LogWarning(
                "RateLimitAddresses is {Slots}, below the {Needed} that GlobalRequestsPerSecond {Global} can hold at "
                + "once: many addresses together can take every slot and starve new ones. Raise RateLimitAddresses, "
                + "lower GlobalRequestsPerSecond, or limit floods by address prefix at the proxy.",
                options.RateLimitAddresses,
                options.SlotsThatCannotAllBeHeld,
                options.GlobalRequestsPerSecond);
        }

        // Whatever goes wrong, a caller sees one fixed sentence: never a stack trace, never what it sent.
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context => WriteError(context.Response, 500, "Internal error."),
        });
        // The built-in middleware treats two empty trust lists as "trust every peer", so with no trusted proxy it must
        // not run at all. Then the address is always the one the request connected from.
        if (options.TrustedNetworks.Count > 0)
        {
            app.UseForwardedHeaders(ForwardedOptions(options));
        }

        // Routing runs before the gate so the gate knows which allowance the request draws on.
        app.UseRouting();
        var limiter = app.Services.GetRequiredService<RequestLimiter>();
        app.Use((context, next) => Gate(context, next, options, limiter));

        app.MapPost("/v1/servers", Announce).WithMetadata(new LimitedBy(RequestKind.Post));
        app.MapDelete("/v1/servers/{id}", Remove).WithMetadata(new LimitedBy(RequestKind.Post));
        app.MapGet("/v1/servers", List).WithMetadata(new LimitedBy(RequestKind.List));
        app.MapGet("/healthz", () => Results.Text("ok"));
        return app;
    }

    /// <summary>The address the request comes from, after the trusted-proxy rule, in its one normal form.</summary>
    public static IPAddress ClientAddress(HttpContext context) =>
        AddressRules.Normalize(context.Connection.RemoteIpAddress ?? IPAddress.IPv6None);

    /// <summary>
    /// The configuration key that makes the framework install its own forwarded-headers middleware
    /// (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>, or <c>ForwardedHeaders_Enabled</c> from any configuration source).
    /// </summary>
    public const string FrameworkForwardedHeadersKey = "ForwardedHeaders_Enabled";

    // ASP.NET Core has two switches that run code ahead of this service's own pipeline. ForwardedHeaders_Enabled adds a
    // forwarded-headers middleware that believes X-Forwarded-For from any peer, so every address would already be
    // rewritten before TrustedProxies is consulted. Hosting startup assemblies can add any middleware at all. Neither
    // has a safe use here, so either one stops the service at start, naming the key. (The slim builder registers no
    // IIS integration, the third framework path that reads forwarding headers.)
    private static void RefuseFrameworkAddressRewriting(IConfiguration configuration)
    {
        var forwarded = configuration[FrameworkForwardedHeadersKey];
        if (!string.IsNullOrEmpty(forwarded) && !forwarded.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{FrameworkForwardedHeadersKey} (ASPNETCORE_FORWARDEDHEADERS_ENABLED) is '{forwarded}'. It makes the "
                + "framework believe X-Forwarded-For from anyone and bypasses Listing:TrustedProxies. Unset it; set "
                + "Listing__TrustedProxies to the proxy's address instead.");
        }

        var startups = configuration[WebHostDefaults.HostingStartupAssembliesKey];
        if (!string.IsNullOrWhiteSpace(startups))
        {
            throw new InvalidOperationException(
                $"{WebHostDefaults.HostingStartupAssembliesKey} (ASPNETCORE_HOSTINGSTARTUPASSEMBLIES) is set. Hosting "
                + "startup assemblies can run middleware ahead of the listing server's own, including middleware that "
                + "rewrites client addresses. Unset it.");
        }
    }

    // The built-in middleware does the trust walk: starting from the connection, each hop is replaced by the address
    // left of it in X-Forwarded-For only while the current hop is a trusted proxy, so entries a client wrote into the
    // header itself are never reached. Only the address is taken; the scheme and host are irrelevant here. Its
    // default trust of loopback is cleared: only the configured proxies count.
    private static ForwardedHeadersOptions ForwardedOptions(ListingOptions options)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = null,
        };
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var network in options.TrustedNetworks)
        {
            forwarded.KnownIPNetworks.Add(network);
        }

        return forwarded;
    }

    // Runs ahead of every rate-limited endpoint, before any per-address state is touched.
    //
    // A request that still resolves to a trusted proxy after the trust walk is refused: the proxy sent no forwarding
    // header, or one the middleware could not read (an unmatched quote a client put in front of the proxy's own entry
    // makes the whole value unreadable), or a chain made only of trusted proxies. Accepting it would list a server at
    // the proxy's address, and rate-limit it in the proxy's shared bucket instead of its own, so a client could step
    // out of its own limit by sending a malformed header. A trusted proxy is never a client.
    private static Task Gate(HttpContext context, RequestDelegate next, ListingOptions options, RequestLimiter limiter)
    {
        var limitedBy = context.GetEndpoint()?.Metadata.GetMetadata<LimitedBy>();
        if (limitedBy is null)
        {
            return next(context);
        }

        var address = ClientAddress(context);
        if (IsTrustedProxy(address, context.Connection.RemoteIpAddress, options))
        {
            return WriteError(context.Response, 400, "The proxy did not say which address this request came from.");
        }

        var decision = limiter.TryAcquire(AddressRules.GroupKey(address, options.Ipv6PrefixLength), limitedBy.Kind);
        if (decision.Allowed)
        {
            return next(context);
        }

        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return WriteError(
            context.Response,
            429,
            decision.Global ? "The service is busy. Try again shortly." : "Too many requests from this address.");
    }

    private static bool IsTrustedProxy(IPAddress normalized, IPAddress? raw, ListingOptions options)
    {
        foreach (var network in options.TrustedNetworks)
        {
            if (network.Contains(normalized) || (raw is not null && network.Contains(raw)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IResult> Announce(
        HttpContext context, ListingStore store, ListingOptions options, ILoggerFactory logs)
    {
        var request = context.Request;
        if (!IsJsonUtf8(request))
        {
            return Error(415, "Content-Type must be application/json in UTF-8.");
        }

        if (request.ContentLength > options.MaxBodyBytes)
        {
            return Error(413, "The body is too large.");
        }

        byte[] body;
        int length;
        try
        {
            // One byte more than allowed is read so a body that is too large is told apart from one that is exactly full.
            body = new byte[options.MaxBodyBytes + 1];
            length = 0;
            int read;
            while (length < body.Length
                && (read = await request.Body.ReadAsync(body.AsMemory(length), context.RequestAborted)) > 0)
            {
                length += read;
            }
        }
        catch (BadHttpRequestException exception)
        {
            return Error(exception.StatusCode, exception.StatusCode == 413 ? "The body is too large." : "Bad request.");
        }

        if (length > options.MaxBodyBytes)
        {
            return Error(413, "The body is too large.");
        }

        if (!Announcement.TryParse(body.AsSpan(0, length), out var announcement, out var error))
        {
            return Error(400, error);
        }

        var address = ClientAddress(context);
        if (!options.AllowPrivateAddresses && !AddressRules.IsPublic(address))
        {
            return Error(403, "Only servers on public addresses can be listed.");
        }

        var result = store.Announce(announcement!, address);
        switch (result.Outcome)
        {
            case AnnounceOutcome.Created:
                logs.CreateLogger("Prospect.Listing").LogDebug("Listed {Address} port {Port}", address, announcement!.Port);
                return Results.Json(new AnnounceResponse(result.Id, result.Token, options.EntryTtlSeconds), ResponseJson, statusCode: 201);
            case AnnounceOutcome.Refreshed:
                return Results.Json(new AnnounceResponse(result.Id, result.Token, options.EntryTtlSeconds), ResponseJson);
            case AnnounceOutcome.AlreadyListed:
                return Error(409, "This address and port are already listed. Send the entry's token to refresh it.");
            case AnnounceOutcome.WrongToken:
                return Error(403, "The token does not match the entry for this address and port.");
            case AnnounceOutcome.AddressFull:
                return Error(429, "This address has as many listed servers as it may.");
            default:
                return Error(429, "The list is full. Try again later.");
        }
    }

    private static IResult Remove(string id, HttpContext context, ListingStore store)
    {
        var header = context.Request.Headers.Authorization;
        const string scheme = "Bearer ";
        if (header.Count != 1 || header[0] is not { } value
            || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || value.Length > scheme.Length + 128)
        {
            return Error(401, "Send the entry's token as 'Authorization: Bearer <token>'.");
        }

        if (id.Length > 64)
        {
            return Error(404, "No such entry.");
        }

        return store.Remove(id, value[scheme.Length..].Trim()) switch
        {
            RemoveOutcome.Removed => Results.NoContent(),
            RemoveOutcome.WrongToken => Error(403, "The token does not match this entry."),
            _ => Error(404, "No such entry."),
        };
    }

    private static IResult List(HttpContext context, ListingStore store)
    {
        var values = context.Request.Query["protocol"];
        if (values.Count != 1
            || !uint.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var protocol))
        {
            return Error(400, "protocol must be given once, as a whole number from 0 to 4294967295.");
        }

        return Results.Json(new ListResponse(store.List(protocol)), ResponseJson);
    }

    private static bool IsJsonUtf8(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !type.Charset.HasValue
            || type.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || type.Charset.Equals("utf8", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Error(int status, string message) =>
        Results.Json(new ErrorResponse(message), ResponseJson, statusCode: status);

    private static Task WriteError(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new ErrorResponse(message), ResponseJson);
    }

    private sealed record AnnounceResponse(string Id, string Token, int TtlSeconds);

    private sealed record ListResponse(List<ListedServer> Servers);

    private sealed record ErrorResponse(string Error);

    // Endpoint metadata: which per-address allowance the endpoint draws on.
    private sealed record LimitedBy(RequestKind Kind);
}
