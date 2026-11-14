using System.Net;
using System.Text;
using System.Text.Json;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Explicit private claims use real service tickets before entering the existing save fence.</summary>
[Collection(nameof(AccountClientTests))]
public sealed class LocalClaimProofTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly SaveLimits Limits = new() { MaxOwners = 4, MaxHulls = 32, MaxBlocks = 4096, MaxRocks = 16, MaxMovedSectors = 2 };

    private sealed class Route(string url)
    {
        internal int Issues, Redeems;
        internal string Bearer = "", Ticket = "", Verifier = "";
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Before;
        internal Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? After;
        internal ListingConfig Config
        {
            get
            {
                Assert.True(ListingConfig.TryCreate("https://accounts.example/", out var config, out _));
                return config! with { HandlerFactory = () => new Handler(this, url), TimeoutSeconds = 5 };
            }
        }
        private sealed class Handler(Route route, string url) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/v1/game/tickets")
                {
                    Interlocked.Increment(ref route.Issues);
                    route.Bearer = request.Headers.Authorization!.Parameter!;
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    Assert.DoesNotContain("verifier", body);
                    Assert.DoesNotContain(route.Bearer, body);
                }
                if (path == "/v1/game/tickets/redeem")
                {
                    Interlocked.Increment(ref route.Redeems);
                    Assert.Null(request.Headers.Authorization);
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    route.Ticket = json.RootElement.GetProperty("ticket"u8).GetString()!;
                    route.Verifier = json.RootElement.GetProperty("verifier"u8).GetString()!;
                }
                if (route.Before is { } before && await before(request, cancellationToken) is { } intercepted) return intercepted;
                // Test-only transport mapping: production still requires a canonical HTTPS origin and normal TLS validation.
                request.RequestUri = new Uri(new Uri(url), request.RequestUri.PathAndQuery);
                var response = await base.SendAsync(request, cancellationToken);
                if (route.After is { } after) await after(request, response, cancellationToken);
                return response;
            }
        }
    }

    private static async Task<AccountClient> SignIn(AccountService service, Route route)
    {
        using var browser = new FakeBrowser();
        var client = new AccountClient(route.Config, browser.Open);
        Assert.True(client.SignIn());
        var page = await browser.Opened;
        var local = await browser.ConsentAsync(service, new Uri(new Uri(service.Url), page.PathAndQuery));
        Assert.Equal(HttpStatusCode.OK, (await browser.ReturnAsync(local)).Status);
        await Wait(() => !client.View.Busy);
        Assert.True(client.HasCurrentSession());
        return client;
    }

    private static async Task Wait(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(10, timeout.Token);
    }

    private static async Task Settle(SaveRig rig)
    {
        await Wait(() =>
        {
            rig.Tick();
            return rig.Server.LocalClaim is not (LocalClaimState.Proving or LocalClaimState.Pending);
        });
    }

    [Fact]
    public async Task VerifiedClaimSurvivesCloseReopenAndIsIdempotentWithoutAnotherFleet()
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        var world = SaveRig.NewWorld(Content);
        int hulls;
        ulong controlled;
        using (var rig = new SaveRig(world, Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config }))
        {
            rig.Join();
            rig.Tick(5);
            Assert.Equal(0, route.Issues);
            hulls = rig.Hulls.Count;
            controlled = rig.Owners[0].Controlled;
            Assert.True(rig.Server.RequestLocalClaim(account));
            Assert.False(rig.Server.RequestLocalClaim(account));
            await Settle(rig);
            Assert.Equal(LocalClaimState.Committed, rig.Server.LocalClaim);
            Assert.Equal(Guid.ParseExact(account.View.Id, "N"), rig.Owners[0].Account.Id);
            Assert.True(rig.Server.RequestLocalClaim(account));
            await Settle(rig);
            Assert.Equal(LocalClaimState.AlreadyLinked, rig.Server.LocalClaim);
            Assert.Equal(hulls, rig.Hulls.Count);
            Assert.True(rig.Close().Clean);
        }
        var serialized = Encoding.UTF8.GetString(WorldSnapshotCodec.Encode(world.Decode()));
        Assert.DoesNotContain(route.Bearer, serialized);
        Assert.DoesNotContain(route.Ticket, serialized);
        Assert.DoesNotContain(route.Verifier, serialized);
        using var reopened = new SaveRig(world, Content, limits: Limits);
        reopened.Join();
        Assert.Equal(OwnerKind.Local, reopened.Owners[0].Kind);
        Assert.Equal(hulls, reopened.Hulls.Count);
        Assert.Equal(controlled, reopened.Owners[0].Controlled);
        Assert.False(reopened.Server.RequestLocalClaim(account));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierCommitCannotPublishSuccessForANewProof(bool expire)
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        using var gate = new ManualResetEventSlim(false);
        var world = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(world, Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config });
        rig.Join();
        world.Gate = gate;
        Assert.True(rig.Server.RequestLocalClaim(account));
        try
        {
            rig.Tick();
            await ProofDone(rig);
            rig.Tick();
            Assert.Equal(LocalClaimState.Pending, rig.Server.LocalClaim);
            Assert.False(rig.Server.RequestLocalClaim(account));
            Assert.Equal(default, rig.Owners[0].Account);
        }
        finally { gate.Set(); }
        await YieldUntil(() => rig.Get<SaveWorker>("_save").Latest.Outcome == CommitOutcome.Committed);
        rig.Tick();
        Assert.Equal(LocalClaimState.Committed, rig.Server.LocalClaim);
        var binding = rig.Owners[0].Account;
        Assert.NotEqual(default, binding);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        route.Before = async (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/redeem", StringComparison.Ordinal)) return null;
            entered.SetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        Assert.True(rig.Server.RequestLocalClaim(account));
        rig.Tick();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            rig.Tick(3);
            Assert.Equal(LocalClaimState.Proving, rig.Server.LocalClaim);
            if (expire) rig.Clock.AdvanceSeconds(31);
        }
        finally { release.TrySetResult(); }
        await ProofDone(rig);
        rig.Tick();
        Assert.Equal(expire ? LocalClaimState.Cancelled : LocalClaimState.Refused, rig.Server.LocalClaim);
        rig.Tick(3);
        Assert.Equal(expire ? LocalClaimState.Cancelled : LocalClaimState.Refused, rig.Server.LocalClaim);
        Assert.Equal(binding, rig.Owners[0].Account);
    }

    private static Task ProofDone(SaveRig rig)
    {
        var proof = rig.Get<object>("_localProof");
        var done = proof.GetType().GetField("Done", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return YieldUntil(() => (bool)done.GetValue(proof)!);
    }

    private static async Task YieldUntil(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    [Theory]
    [InlineData(CommitOutcome.Failed, LocalClaimState.Failed)]
    [InlineData(CommitOutcome.Unknown, LocalClaimState.Unknown)]
    public async Task VerifiedDoesNotMeanDurablyClaimed(CommitOutcome failure, LocalClaimState expected)
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        var world = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(world, Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config });
        rig.Join();
        world.FailCommits = 1;
        world.FailWith = failure;
        Assert.True(rig.Server.RequestLocalClaim(account));
        await Settle(rig);
        Assert.Equal(expected, rig.Server.LocalClaim);
        Assert.Equal(default, rig.Owners[0].Account);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reconnect")]
    [InlineData("signout")]
    [InlineData("deadline")]
    public async Task LateVerifiedResultCannotBindReplacementOrClosedSession(string change)
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        route.After = async (request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/redeem", StringComparison.Ordinal))
            {
                entered.SetResult();
                await release.Task; // Deliberately ignores cancellation, as an uncooperative worker could.
            }
        };
        rig.Join();
        Assert.True(rig.Server.RequestLocalClaim(account));
        rig.Tick();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            switch (change)
            {
                case "close": rig.Server.BeginClose(); break;
                case "reconnect": rig.Leave(); rig.Join(); break;
                case "signout": account.SignOut(); break;
                case "deadline": rig.Clock.AdvanceSeconds(31); break;
            }
            rig.Tick();
            Assert.False(rig.Server.RequestLocalClaim(account));
            Assert.Equal(default, rig.Owners[0].Account);
        }
        finally { release.SetResult(); }
        await Settle(rig);
        Assert.Equal(LocalClaimState.Cancelled, rig.Server.LocalClaim);
        Assert.Equal(default, rig.Owners[0].Account);
    }

    [Theory]
    [InlineData("503", 3)]
    [InlineData("404", 1)]
    [InlineData("timeout", 1)]
    [InlineData("wrong-verifier", 1)]
    [InlineData("replayed", 1)]
    public async Task RefusalAndLostResponseNeverClaimAndRetriesAreBounded(string failure, int requests)
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        route.Before = async (request, cancel) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/redeem", StringComparison.Ordinal)) return null;
            switch (failure)
            {
                case "503": return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                case "404": return new HttpResponseMessage(HttpStatusCode.NotFound);
                case "timeout": await Task.Delay(5000, cancel); break;
                case "wrong-verifier":
                    request.Content = new StringContent(JsonSerializer.Serialize(new { ticket = route.Ticket, verifier = AccountClient.Encode(new byte[32]) }), Encoding.UTF8, "application/json");
                    break;
                case "replayed":
                    using (var http = new ListingHttp(service.Config))
                    {
                        var result = await http.SendAsync(HttpMethod.Post, "v1/game/tickets/redeem", JsonSerializer.SerializeToUtf8Bytes(new { ticket = route.Ticket, verifier = route.Verifier }), null, 4096, cancel);
                        Assert.True(result.Is(200));
                    }
                    break;
            }
            return null;
        };
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config with { TimeoutSeconds = 0.2 } });
        rig.Join();
        Assert.True(rig.Server.RequestLocalClaim(account));
        await Settle(rig);
        Assert.Equal(LocalClaimState.Refused, rig.Server.LocalClaim);
        Assert.Equal(requests, route.Redeems);
        Assert.Equal(1, route.Issues);
        Assert.Equal(default, rig.Owners[0].Account);
    }

    [Fact]
    public async Task ExpiredRealTicketDoesNotClaim()
    {
        var clock = new ManualTime();
        await using var service = await AccountService.StartAsync(clock);
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        route.Before = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/redeem", StringComparison.Ordinal)) clock.AdvanceSeconds(31);
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config });
        rig.Join();
        Assert.True(rig.Server.RequestLocalClaim(account));
        await Settle(rig);
        Assert.Equal(LocalClaimState.Refused, rig.Server.LocalClaim);
        Assert.Equal(default, rig.Owners[0].Account);
    }

    [Fact]
    public async Task RemoteConnectionCannotStartProofAndLinkedAuthorityCannotChange()
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config });
        var stranger = rig.Remote.Connect(0x0A000002);
        rig.Remote.Deliver(stranger, OwnerLink.HelloBytes(Content, "Pilot"));
        rig.Tick();
        Assert.False(rig.Server.RequestLocalClaim(account));
        Assert.Equal(0, route.Issues);
        rig.Join();
        var previous = new SavedAccount("https://previous.example/", Guid.NewGuid());
        Assert.Equal(ServerHost.ClaimState.Pending, rig.Server.StageVerifiedLocalClaim(previous));
        rig.Tick();
        rig.WaitIdle();
        rig.Tick();
        Assert.True(rig.Server.RequestLocalClaim(account));
        await Settle(rig);
        Assert.Equal(LocalClaimState.Cancelled, rig.Server.LocalClaim);
        Assert.Equal(previous, rig.Owners[0].Account);
        Assert.Equal(0, route.Issues);
    }

    [Fact]
    public async Task PublicNonvisualSessionApiUsesRealClientAndServer()
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        var world = SaveRig.NewWorld(Content);
        var opened = MemoryWorldStore.Open(world, SaveCompatibility.Of(Content));
        using var session = GameSession.StartLocal(new ServerConfig
        {
            AccountAuthority = route.Config,
            World = new WorldPersistence(opened.Store!, opened.Snapshot!, opened.Generation) { Limits = Limits },
        }, Content, "Pilot");
        await Wait(() => session.Client.AcquireSnapshot().State == SessionState.Active);
        Assert.Equal(0, route.Issues);
        Assert.True(session.RequestLocalClaim(account));
        await Wait(() => session.LocalClaim == LocalClaimState.Committed);
        session.BeginClose();
        await Wait(() => session.Close.Completed);
        Assert.True(session.Close.Clean);
        Assert.Equal(Guid.ParseExact(account.View.Id, "N"), world.Decode().Owners[0].Account.Id);
    }

    [Fact]
    public async Task LostConsumedResponseNeedsFreshVerifierAndTicket()
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        route.After = (request, _, _) => request.RequestUri!.AbsolutePath.EndsWith("/redeem", StringComparison.Ordinal)
            ? Task.FromException(new IOException("Lost response")) : Task.CompletedTask;
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits, config: new ServerConfig { AccountAuthority = route.Config });
        rig.Join();
        Assert.True(rig.Server.RequestLocalClaim(account));
        await Settle(rig);
        Assert.Equal(LocalClaimState.Refused, rig.Server.LocalClaim);
        var verifier = route.Verifier;
        var ticket = route.Ticket;
        Assert.Equal(1, route.Redeems);
        route.After = null;
        Assert.True(rig.Server.RequestLocalClaim(account));
        await Settle(rig);
        Assert.Equal(LocalClaimState.Committed, rig.Server.LocalClaim);
        Assert.NotEqual(verifier, route.Verifier);
        Assert.NotEqual(ticket, route.Ticket);
    }

    [Fact]
    public async Task ClientRequiresMatchingAuthorityPurposeAndCurrentExplicitIntent()
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        Assert.Null(account.LocalClaimIntent("https://other.example/"));
        var intent = account.LocalClaimIntent(route.Config.Url.AbsoluteUri)!;
        var challenge = AccountClient.Challenge(AccountClient.Encode(new byte[32]));
        Assert.Null(await intent.TicketAsync("https://other.example/", "local-claim", challenge, TestContext.Current.CancellationToken));
        Assert.Null(await intent.TicketAsync(intent.Authority, "remote-join", challenge, TestContext.Current.CancellationToken));
        account.SignOut();
        Assert.Null(await intent.TicketAsync(intent.Authority, "local-claim", challenge, TestContext.Current.CancellationToken));
        Assert.Equal(0, route.Issues);
    }

    [Theory]
    [InlineData(null, ServerVisibility.Private)]
    [InlineData("https://other.example/", ServerVisibility.Private)]
    [InlineData("http://localhost/", ServerVisibility.Private)]
    [InlineData("https://accounts.example/path/", ServerVisibility.Private)]
    [InlineData("https://accounts.example/", ServerVisibility.Unlisted)]
    [InlineData("https://accounts.example/", ServerVisibility.Public)]
    public async Task IncompatibleAuthorityOrHostedWorldMakesNoAccountRequests(string? url, ServerVisibility visibility)
    {
        await using var service = await AccountService.StartAsync();
        var route = new Route(service.Url);
        using var account = await SignIn(service, route);
        ListingConfig? authority = null;
        if (url is not null) Assert.True(ListingConfig.TryCreate(url, out authority, out _));
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits,
            config: new ServerConfig { AccountAuthority = authority, Visibility = visibility, Port = TestNet.Port(visibility == ServerVisibility.Public ? 15 : 14) });
        rig.Join();
        Assert.False(rig.Server.RequestLocalClaim(account));
        rig.Tick(3);
        Assert.Equal(0, route.Issues);
        Assert.Equal(0, route.Redeems);
    }
}
