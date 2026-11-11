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

        void Route(string method, string path, RequestKind kind, Func<HttpContext, Task<IResult>> handler)
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
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
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
    private static async Task<IResult> Protected(HttpContext c)
    {
        var headers = c.Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } header || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new AccountFailure(401);
        var token = header[7..];
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
