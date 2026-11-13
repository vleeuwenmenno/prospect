using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Prospect.Listing.Accounts;

internal static class AccountRoutes
{
    internal static void Map(WebApplication app)
    {
        Route("POST", "/v1/accounts/signin", RequestKind.Auth, Create);
        Route("GET", "/v1/accounts/signin/{attempt}", RequestKind.Auth, Start);
        Route("GET", "/v1/accounts/discord/callback", RequestKind.Auth, Callback);
        Route("POST", "/v1/accounts/sessions", RequestKind.Auth, Redeem);
        Route("DELETE", "/v1/accounts/sessions/current", RequestKind.Account, Protected);
        Route("GET", "/v1/accounts/me", RequestKind.Account, Protected);
        Route("GET", "/v1/friends", RequestKind.Account, Protected);
        Route("POST", "/v1/friends/requests", RequestKind.Account, Protected);
        Route("POST", "/v1/friends/requests/{id}/accept", RequestKind.Account, Protected);
        Route("POST", "/v1/friends/requests/{id}/decline", RequestKind.Account, Protected);
        Route("DELETE", "/v1/friends/requests/{id}", RequestKind.Account, Protected);
        Route("DELETE", "/v1/friends/{id}", RequestKind.Account, Protected);
        Route("POST", "/v1/game/tickets", RequestKind.TicketIssue, IssueTicket, strict: true);
        Route("POST", "/v1/game/tickets/redeem", RequestKind.TicketRedeem, RedeemTicket, strict: true);

        // A strict route checks its own input and throws AccountFailure for it, so any other exception is a fault of
        // the service: it is left to the application's handler (a fixed 500) instead of being called a bad request.
        void Route(string method, string path, RequestKind kind, Func<HttpContext, Task<IResult>> handler, bool strict = false)
        {
            app.MapMethods(path, [method], async (HttpContext context) =>
            {
                try
                {
                    if (!context.RequestServices.GetRequiredService<AccountsOptions>().Enabled)
                        return Results.Json(new { error = "Accounts disabled." }, statusCode: 503);
                    return await handler(context);
                }
                catch (AccountFailure failure) { return Error(failure.Status); }
                catch (BadHttpRequestException failure) { return Error(failure.StatusCode); }
                catch (JsonException) { return Error(400); }
                catch (Exception e) when (!strict && e is InvalidOperationException or KeyNotFoundException or FormatException)
                { return Error(400); }
            }).WithMetadata(new ListingApp.LimitedBy(kind));
        }
    }
    private static IResult Error(int status) => Results.Json(new { error = status == 503 ? "Accounts unavailable." : "Account request failed." }, statusCode: status);
    private static async Task<JsonElement> Body(HttpContext c)
    {
        if (!c.Request.HasJsonContentType()) throw new AccountFailure(415);
        var max = c.RequestServices.GetRequiredService<ListingOptions>().MaxBodyBytes;
        if (c.Request.ContentLength > max) throw new AccountFailure(413);
        var bytes = new byte[max + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            var n = await c.Request.Body.ReadAsync(bytes.AsMemory(count), c.RequestAborted);
            if (n == 0) break;
            count += n;
        }
        if (count > max) throw new AccountFailure(413);
        return JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(0, count), AccountCrypto.Json);
    }
    private static string Field(JsonElement body, string key) => body.GetProperty(key).GetString() ?? throw new AccountFailure(400);
    private static async Task<IResult> Create(HttpContext c)
    {
        var body = await Body(c);
        var challenge = Field(body, "challenge");
        var port = body.GetProperty("port").GetInt32();
        if (!AccountCrypto.Canonical(challenge) || port is < 1024 or > 65535) throw new AccountFailure(400);
        var options = c.RequestServices.GetRequiredService<ListingOptions>();
        var attempt = c.RequestServices.GetRequiredService<SigninAttempts>().Create(challenge, port,
            AddressRules.GroupKey(ListingApp.ClientAddress(c), options.Ipv6PrefixLength));
        return Results.Json(new { attempt, expiresIn = 600 }, statusCode: 201);
    }
    private static Task<IResult> Start(HttpContext c)
    {
        var a = c.RequestServices.GetRequiredService<SigninAttempts>().Start((string)c.Request.RouteValues["attempt"]!);
        var options = c.RequestServices.GetRequiredService<AccountsOptions>();
        return Task.FromResult(Results.Redirect(QueryHelpers.AddQueryString("https://discord.com/oauth2/authorize",
            new Dictionary<string, string?>
            {
                ["client_id"] = options.ClientId,
                ["redirect_uri"] = options.Callback,
                ["response_type"] = "code",
                ["scope"] = "identify",
                ["prompt"] = "consent",
                ["state"] = a.State,
                ["code_challenge"] = AccountCrypto.Challenge(a.ProviderVerifier!),
                ["code_challenge_method"] = "S256",
            })));
    }
    private static string Query(HttpContext c, string key)
    {
        var value = c.Request.Query[key];
        if (value.Count != 1 || value[0] is not { Length: > 0 and <= 4096 } text) throw new AccountFailure(400);
        return text;
    }
    private static async Task<IResult> Callback(HttpContext c)
    {
        var state = Query(c, "state");
        if (!AccountCrypto.Canonical(state)) throw new AccountFailure(400);
        var attempts = c.RequestServices.GetRequiredService<SigninAttempts>();
        var a = attempts.ConsumeState(state);
        try
        {
            if (c.Request.Query.ContainsKey("iss") && Query(c, "iss") != "https://discord.com") throw new AccountFailure(400);
            if (c.Request.Query.ContainsKey("error"))
            {
                if (Query(c, "error") != "access_denied" || c.Request.Query.ContainsKey("code")) throw new AccountFailure(400);
                attempts.Abort(a);
                return Results.Redirect($"http://127.0.0.1:{a.Port}/prospect/signin?attempt={a.Id}&error=access_denied");
            }
            var result = await c.RequestServices.GetRequiredService<DiscordClient>().Exchange(Query(c, "code"), a.ProviderVerifier!, c.RequestAborted);
            var code = attempts.Complete(a, result.Id, result.Name);
            return Results.Redirect($"http://127.0.0.1:{a.Port}/prospect/signin?attempt={a.Id}&code={code}");
        }
        catch (AccountFailure)
        {
            // A consumed state fixes the recipient; never reflect provider error text or leave the game waiting.
            attempts.Abort(a);
            return Results.Redirect($"http://127.0.0.1:{a.Port}/prospect/signin?attempt={a.Id}&error=failed");
        }
    }
    private static async Task<IResult> Redeem(HttpContext c)
    {
        var body = await Body(c);
        var attempt = Field(body, "attempt");
        var code = Field(body, "code");
        var verifier = Field(body, "verifier");
        if (!AccountCrypto.Canonical(attempt) || !AccountCrypto.Canonical(code) || !AccountCrypto.Canonical(verifier))
            throw new AccountFailure(400);
        var result = c.RequestServices.GetRequiredService<SigninAttempts>().Redeem(attempt, code, verifier,
            c.RequestServices.GetRequiredService<AccountStore>());
        return Results.Json(result, statusCode: 201);
    }
    private static string Bearer(HttpContext c)
    {
        var headers = c.Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } header || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new AccountFailure(401);
        return header[7..];
    }

    // The body must be one object holding exactly these properties, each a string: no other, missing or repeated
    // name (the parser refuses repeats) and no other type.
    private static async Task<string[]> Strings(HttpContext c, params string[] names)
    {
        try
        {
            var body = await Body(c);
            if (body.ValueKind != JsonValueKind.Object) throw new AccountFailure(400);
            var values = new string[names.Length];
            var seen = 0;
            foreach (var property in body.EnumerateObject())
            {
                var index = Array.IndexOf(names, property.Name);
                if (index < 0 || property.Value.ValueKind != JsonValueKind.String) throw new AccountFailure(400);
                values[index] = property.Value.GetString()!;
                seen++;
            }
            return seen == names.Length ? values : throw new AccountFailure(400);
        }
        catch (InvalidOperationException)
        {
            // Text that is valid JSON but not valid Unicode (a lone surrogate escape) fails only when it is read.
            throw new AccountFailure(400);
        }
    }

    // Everything is checked before the database is asked: a malformed request costs no database work.
    private static async Task<IResult> IssueTicket(HttpContext c)
    {
        var token = Bearer(c);
        if (!AccountCrypto.Canonical(token)) throw new AccountFailure(401);
        var challenge = (await Strings(c, "challenge"))[0];
        if (!AccountCrypto.Canonical(challenge)) throw new AccountFailure(400);
        var (ticket, expiresIn) = c.RequestServices.GetRequiredService<GameTickets>().Issue(token, challenge);
        return Results.Json(new { ticket, expiresIn }, statusCode: 201);
    }

    // No bearer token: the caller is a game server, which proves itself only by holding the ticket and the verifier.
    // From here on the request's cancellation is not observed, so a redemption that began is always settled.
    private static async Task<IResult> RedeemTicket(HttpContext c)
    {
        var fields = await Strings(c, "ticket", "verifier");
        if (!AccountCrypto.Canonical(fields[0]) || !AccountCrypto.Canonical(fields[1])) throw new AccountFailure(400);
        var holder = c.RequestServices.GetRequiredService<GameTickets>().Redeem(fields[0], fields[1]);

        // Written out field by field: an account's friend code must never reach a game server.
        return Results.Json(new { account = new { id = holder.Id, name = holder.Name } });
    }

    private static async Task<IResult> Protected(HttpContext c)
    {
        var token = Bearer(c);
        var path = c.Request.Path.Value!.TrimEnd('/');
        string? friendCode = null;
        if (path.Equals("/v1/friends/requests", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(c.Request.Method))
        {
            friendCode = Field(await Body(c), "friendCode");
            if (friendCode.Length != 16 || !friendCode.All(char.IsAsciiHexDigitUpper)) throw new AccountFailure(400);
        }
        var store = c.RequestServices.GetRequiredService<AccountStore>();
        return store.Run(db =>
        {
            var account = store.Authenticate(db, token);
            if (path.Equals("/v1/accounts/me", StringComparison.OrdinalIgnoreCase)) return Results.Json(account);
            if (path.Equals("/v1/accounts/sessions/current", StringComparison.OrdinalIgnoreCase))
            {
                AccountStore.Execute(db, "DELETE FROM sessions WHERE hash=$h", ("$h", AccountCrypto.Hash(token)));
                return Results.NoContent();
            }
            if (path.Equals("/v1/friends", StringComparison.OrdinalIgnoreCase)) return Results.Json(store.List(db, account.Id));
            int status;
            if (friendCode is not null) status = store.Request(db, account.Id, friendCode);
            else
            {
                var id = (string)c.Request.RouteValues["id"]!;
                if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)) throw new AccountFailure(404);
                var action = path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase) ? "accept"
                    : path.EndsWith("/decline", StringComparison.OrdinalIgnoreCase) ? "decline"
                    : path.StartsWith("/v1/friends/requests/", StringComparison.OrdinalIgnoreCase) ? "cancel" : "remove";
                status = store.Change(db, account.Id, id, action);
            }
            return status >= 400 ? Error(status) : Results.StatusCode(status);
        });
    }
}
