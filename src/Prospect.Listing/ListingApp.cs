using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Prospect.Listing;

/// <summary>Builds the listing server: configuration, limits, the proxy rule and the version 1 endpoints.</summary>
public static class ListingApp
{
    private const string PostPolicy = "post";
    private const string ListPolicy = "list";

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

        var options = builder.Configuration.GetSection(ListingOptions.Section).Get<ListingOptions>() ?? new ListingOptions();
        options.Validate();

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ListingStore>();
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

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, cancel) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return new ValueTask(WriteError(context.HttpContext.Response, 429, "Too many requests from this address."));
            };
            limiter.AddPolicy(PostPolicy, context => PerAddress(context, options, options.PostsPerMinute));
            limiter.AddPolicy(ListPolicy, context => PerAddress(context, options, options.ListsPerMinute));
        });

        var app = builder.Build();

        // Proxy trust decides every address the service sees, so the log says at start which proxies it believes.
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Prospect.Listing");
        log.LogInformation(
            "Listing server: entries live {Ttl} s, at most {Max} ({PerAddress} per address); "
            + "trusted proxies: {Proxies}; private addresses {Private}",
            options.EntryTtlSeconds,
            options.MaxEntries,
            options.MaxEntriesPerAddress,
            options.TrustedNetworks.Count == 0 ? "none" : string.Join(", ", options.TrustedNetworks),
            options.AllowPrivateAddresses ? "allowed" : "refused");

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

        app.UseRateLimiter();

        app.MapPost("/v1/servers", Announce).RequireRateLimiting(PostPolicy);
        app.MapDelete("/v1/servers/{id}", Remove).RequireRateLimiting(PostPolicy);
        app.MapGet("/v1/servers", List).RequireRateLimiting(ListPolicy);
        app.MapGet("/healthz", () => Results.Text("ok"));
        return app;
    }

    /// <summary>The address the request comes from, after the trusted-proxy rule, in its one normal form.</summary>
    public static IPAddress ClientAddress(HttpContext context) =>
        AddressRules.Normalize(context.Connection.RemoteIpAddress ?? IPAddress.IPv6None);

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

    private static RateLimitPartition<string> PerAddress(HttpContext context, ListingOptions options, int perMinute) =>
        // A bucket of a minute's allowance, refilled a sixth at a time: a burst up to the limit, then the limit per
        // minute on average. The token bucket also tells the caller when to retry.
        RateLimitPartition.GetTokenBucketLimiter(
            AddressRules.GroupKey(ClientAddress(context), options.Ipv6PrefixLength),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = Math.Max(1, (perMinute + 5) / 6),
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
            });

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
}
