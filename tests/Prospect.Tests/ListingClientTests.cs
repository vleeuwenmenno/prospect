using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Which listing server addresses the game accepts, and what the content says the default is.</summary>
public sealed class ListingConfigTests
{
    [Theory]
    [InlineData("https://prospect.mvl.sh", "https://prospect.mvl.sh/")]
    [InlineData("https://prospect.mvl.sh/", "https://prospect.mvl.sh/")]
    [InlineData("https://example.org/prospect", "https://example.org/prospect/")]
    [InlineData("https://example.org:8443/a/b/", "https://example.org:8443/a/b/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/")]
    [InlineData("  https://prospect.mvl.sh  ", "https://prospect.mvl.sh/")]
    public void AnHttpsAddressOrPlainHttpOnThisMachineIsAccepted(string url, string expected)
    {
        Assert.True(ListingConfig.TryCreate(url, out var config, out var error), error);
        Assert.Equal(expected, config!.Url.AbsoluteUri);
        Assert.Equal(config.Url.Scheme == "http", config.IsLocal);
    }

    [Theory]
    [InlineData("http://prospect.mvl.sh")]
    [InlineData("http://192.168.1.10:8080")]
    [InlineData("http://203.0.113.7")]
    [InlineData("ftp://prospect.mvl.sh")]
    [InlineData("file:///etc/passwd")]
    [InlineData("prospect.mvl.sh")]
    [InlineData("/v1/servers")]
    [InlineData("https://user:secret@prospect.mvl.sh")]
    [InlineData("https://prospect.mvl.sh/?next=1")]
    [InlineData("https://prospect.mvl.sh/#x")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? url)
    {
        Assert.False(ListingConfig.TryCreate(url, out var config, out var error));
        Assert.Null(config);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TheContentNamesTheDefaultAndTheCommandLineCanReplaceItOrTurnItOff()
    {
        var standard = ListingConfig.Resolve(null);
        Assert.NotNull(standard);
        Assert.Equal("https://prospect.mvl.sh/", standard.Url.AbsoluteUri);
        Assert.Equal(30, standard.RefreshSeconds);
        Assert.False(standard.IsLocal);

        Assert.Null(ListingConfig.Resolve("off"));
        Assert.Null(ListingConfig.Resolve(" OFF "));

        // Another address keeps the content's timings.
        var local = ListingConfig.Resolve("http://127.0.0.1:8080");
        Assert.NotNull(local);
        Assert.True(local.IsLocal);
        Assert.Equal(standard.RefreshSeconds, local.RefreshSeconds);
        Assert.Equal(standard.TimeoutSeconds, local.TimeoutSeconds);

        Assert.Throws<InvalidDataException>(() => ListingConfig.Resolve("http://example.org"));
    }

    [Fact]
    public void TheContentFileCanTurnTheListingOffAndBadFiguresAreNamed()
    {
        Assert.Null(ListingConfig.Parse("""{ "url": "" }"""u8.ToArray(), "test"));
        Assert.Null(ListingConfig.Parse("""{ "url": "off" }"""u8.ToArray(), "test"));
        Assert.Null(ListingConfig.Parse("{}"u8.ToArray(), "test"));

        var config = ListingConfig.Parse("""{ "url": "https://example.org", "refreshSeconds": 20, "timeoutSeconds": 4 }"""u8.ToArray(), "test");
        Assert.Equal(20, config!.RefreshSeconds);
        Assert.Equal(4, config.TimeoutSeconds);

        Assert.Contains("refreshSeconds", Assert.Throws<InvalidDataException>(() => ListingConfig.Parse("""{ "url": "https://example.org", "refreshSeconds": 0 }"""u8.ToArray(), "test")).Message);
        Assert.Contains("timeoutSeconds", Assert.Throws<InvalidDataException>(() => ListingConfig.Parse("""{ "url": "https://example.org", "timeoutSeconds": "soon" }"""u8.ToArray(), "test")).Message);
        Assert.Throws<InvalidDataException>(() => ListingConfig.Parse("""{ "url": "http://example.org" }"""u8.ToArray(), "test"));
        Assert.Throws<InvalidDataException>(() => ListingConfig.Parse("nope"u8.ToArray(), "test"));
    }
}

/// <summary>What the server browser makes of a list it does not trust.</summary>
public sealed class ServerListParsingTests
{
    private static string Entry(
        string name = "\"Ceres Station\"",
        string address = "\"203.0.113.7\"",
        string port = "27420",
        string players = "3",
        string maxPlayers = "16",
        string password = "false",
        string version = "\"0.1.0\""
    ) => $$"""{"name":{{name}},"address":{{address}},"port":{{port}},"players":{{players}},"maxPlayers":{{maxPlayers}},"password":{{password}},"version":{{version}}}""";

    private static ServerList? Parse(string json, bool local = false) => ServerBrowser.Parse(Encoding.UTF8.GetBytes(json), local);

    private static ServerList List(params string[] entries) => Parse($$"""{"servers":[{{string.Join(',', entries)}}]}""")!;

    [Fact]
    public void ASaneListComesThroughAsItIs()
    {
        var list = List(
            Entry(),
            Entry(name: "\"Köln ☄ 東京\"", address: "\"198.51.100.20\"", port: "65535", players: "0", maxPlayers: "255", password: "true", version: "\"1.2.3-rc.1+build_7\"")
        );

        Assert.Equal(BrowserState.Ready, list.State);
        Assert.Equal(0, list.Dropped);
        Assert.Equal(
            [
                new ListedGame("Ceres Station", "203.0.113.7", 27420, 3, 16, false, "0.1.0"),
                new ListedGame("Köln ☄ 東京", "198.51.100.20", 65535, 0, 255, true, "1.2.3-rc.1+build_7"),
            ],
            list.Games
        );
    }

    [Fact]
    public void ANameIsCleanedBeforeAnyoneSeesIt()
    {
        var list = List(Entry(name: "\"  free \\u202Egnp.exe\\u202C  \\u0007 \\n ore \\u200B \"", address: "\"203.0.113.8\""));
        Assert.Equal("free gnp.exe ore", Assert.Single(list.Games).Name);
    }

    [Theory]
    // Names that are nothing, or too long, or not text.
    [InlineData("name", "\"\"")]
    [InlineData("name", "\"\\u200B\\u0000\"")]
    [InlineData("name", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("name", "42")]
    [InlineData("name", "null")]
    // Addresses that are not one plain, public, reachable IPv4 address.
    [InlineData("address", "\"play.example.org\"")]
    [InlineData("address", "\"localhost\"")]
    [InlineData("address", "\"203.0.113.7:27420\"")]
    [InlineData("address", "\"127.1\"")]
    [InlineData("address", "\"0x7f.0.0.1\"")]
    [InlineData("address", "\"0313.0.113.7\"")]
    [InlineData("address", "\"3405803783\"")]
    [InlineData("address", "\" 203.0.113.7\"")]
    [InlineData("address", "\"203.0.113.7\\u0000\"")]
    [InlineData("address", "\"0.0.0.0\"")]
    [InlineData("address", "\"255.255.255.255\"")]
    [InlineData("address", "\"224.0.0.1\"")]
    [InlineData("address", "\"127.0.0.1\"")]
    [InlineData("address", "\"10.0.0.5\"")]
    [InlineData("address", "\"192.168.1.1\"")]
    [InlineData("address", "\"172.16.0.9\"")]
    [InlineData("address", "\"169.254.1.1\"")]
    [InlineData("address", "\"100.64.0.1\"")]
    [InlineData("address", "\"[2001:db8::7]\"")]
    [InlineData("address", "\"::ffff:203.0.113.7\"")]
    [InlineData("address", "\"fe80::1%eth0\"")]
    [InlineData("address", "\"\"")]
    [InlineData("address", "203")]
    // Ports, counts, flags and versions the contract does not allow.
    [InlineData("port", "0")]
    [InlineData("port", "65536")]
    [InlineData("port", "-1")]
    [InlineData("port", "27420.5")]
    [InlineData("port", "\"27420\"")]
    [InlineData("port", "1e99")]
    [InlineData("players", "17")]
    [InlineData("players", "-1")]
    [InlineData("players", "\"3\"")]
    [InlineData("maxPlayers", "256")]
    [InlineData("maxPlayers", "2")]
    [InlineData("password", "\"false\"")]
    [InlineData("password", "0")]
    [InlineData("password", "null")]
    [InlineData("version", "\"\"")]
    [InlineData("version", "\"1.0 <b>\"")]
    [InlineData("version", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("version", "7")]
    public void AnEntryThatBreaksARuleIsDroppedAndTheRestStand(string field, string value)
    {
        var broken = field switch
        {
            "name" => Entry(name: value),
            "address" => Entry(address: value),
            "port" => Entry(port: value),
            "players" => Entry(players: value),
            "maxPlayers" => Entry(maxPlayers: value),
            "password" => Entry(password: value),
            _ => Entry(version: value),
        };

        var list = List(Entry(address: "\"198.51.100.1\""), broken, Entry(address: "\"198.51.100.2\""));
        Assert.Equal(["198.51.100.1", "198.51.100.2"], list.Games.Select(g => g.Address));
        Assert.Equal(1, list.Dropped);
    }

    [Fact]
    public void EntriesThatAreNotObjectsOrLackAFieldAreDropped()
    {
        var list = List(
            "7",
            "null",
            "[]",
            "\"203.0.113.7\"",
            "{}",
            """{"name":"No address","port":27420,"players":0,"maxPlayers":4,"password":false,"version":"1"}""",
            """{"name":"No password flag","address":"203.0.113.9","port":27420,"players":0,"maxPlayers":4,"version":"1"}""",
            Entry()
        );
        Assert.Equal("203.0.113.7", Assert.Single(list.Games).Address);
        Assert.Equal(7, list.Dropped);
    }

    [Fact]
    public void AnAddressAndPortAreListedOnceAndIpv6IsLeftOutWhileTheTransportCannotReachIt()
    {
        var list = List(Entry(), Entry(name: "\"Impostor\""), Entry(port: "27421"), Entry(address: "\"2001:db8::7\""));
        Assert.Equal(["Ceres Station", "Ceres Station"], list.Games.Select(g => g.Name));
        Assert.Equal([27420, 27421], list.Games.Select(g => (int)g.Port));
        Assert.Equal(2, list.Dropped);
    }

    [Fact]
    public void AListFromThisMachineMayNameThisNetworkAndOneFromTheInternetMayNot()
    {
        var json = $$"""{"servers":[{{Entry(address: "\"127.0.0.1\"")}},{{Entry(address: "\"192.168.1.20\"")}},{{Entry(address: "\"0.0.0.0\"")}},{{Entry(address: "\"224.0.0.1\"")}}]}""";
        Assert.Equal(["127.0.0.1", "192.168.1.20"], Parse(json, local: true)!.Games.Select(g => g.Address));
        Assert.Empty(Parse(json, local: false)!.Games);
    }

    [Fact]
    public void NoMoreThanTwoHundredGamesAreKept()
    {
        var entries = Enumerable.Range(0, 1000).Select(i => Entry(address: $"\"198.51.{100 + i / 250}.{1 + i % 250}\"")).ToArray();
        var list = List(entries);
        Assert.Equal(ServerBrowser.MaxGames, list.Games.Length);
        Assert.Equal(800, list.Dropped);
        Assert.Equal("198.51.100.1", list.Games[0].Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"servers":7}""")]
    [InlineData("""{"servers":{"0":{}}}""")]
    [InlineData("""{"servers":[""")]
    [InlineData("""{"servers":[],"servers":[]} trailing""")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    public void SomethingThatIsNotAListIsNotAList(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void DeeplyNestedOrBrokenBytesDoNotThrow()
    {
        Assert.Null(Parse(new string('[', 5000)));
        Assert.Null(Parse("{\"servers\":[" + new string('[', 100) + new string(']', 100) + "]}"));
        Assert.Null(ServerBrowser.Parse(new byte[] { 0xFF, 0xFE, 0x00, 0x7B }, false));

        var random = new Random(99);
        var noise = new byte[512];
        for (var i = 0; i < 500; i++)
        {
            random.NextBytes(noise);
            ServerBrowser.Parse(noise.AsMemory(0, random.Next(noise.Length)), i % 2 == 0);
        }
    }
}

/// <summary>
/// The game against the listing server: the real service running in this process, and a fake one that goes
/// down, stalls and answers rubbish.
/// </summary>
public sealed class ListingTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static ListingAnnouncer Announcer(ListingConfig config, bool password = false) =>
        new(config, "Fake world", 27420, 16, password, () => 3);

    private static void WaitListed(ListingAnnouncer announcer) =>
        TestNet.WaitUntil(() => announcer.State == AnnounceState.Listed, "the server being listed");

    /// <summary>An address nothing listens on: a port that was free a moment ago.</summary>
    private static string ClosedUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    [Fact]
    public async Task APublicServerAnnouncesRefreshesReportsItsPlayersAndRemovesItselfOnShutdown()
    {
        await using var listing = await RealListing.StartAsync();
        var port = TestNet.Port(16);
        var server = new ServerHost(
            new ServerConfig
            {
                Name = "Ceres ☄ Station",
                MaxPlayers = 12,
                Visibility = ServerVisibility.Public,
                Port = port,
                Password = "open sesame",
                Listing = TestNet.Listing(listing.Url),
            },
            Content
        );
        try
        {
            server.Start();
            TestNet.WaitUntil(() => listing.Listed.Count == 1, "the server appearing on the list");
            TestNet.WaitUntil(() => server.Announce == AnnounceState.Listed, "the server knowing it is listed");

            var entry = listing.Listed[0];
            Assert.Equal("Ceres ☄ Station", entry.Name);
            Assert.Equal("127.0.0.1", entry.Address);
            Assert.Equal(port, entry.Port);
            Assert.Equal(0, entry.Players);
            Assert.Equal(12, entry.MaxPlayers);
            Assert.True(entry.Password);
            Assert.Equal(ProtocolInfo.GameVersion, entry.Version);

            // An entry lives 90 seconds without a heartbeat. Two minutes of the service's clock pass, a minute at
            // a time with real heartbeats in between: the entry is still there, and it is still the one entry.
            for (var i = 0; i < 3; i++)
            {
                listing.Clock.Advance(TimeSpan.FromSeconds(60));
                Thread.Sleep(900);
            }

            Assert.Single(listing.Listed);
            Assert.Equal(1, listing.Store.Count);

            // A player joins, and the next heartbeat says so.
            using var player = GameSession.Join("127.0.0.1", port, Content, "Player", "open sesame");
            TestNet.WaitFor(player, s => s.State == SessionState.Active);
            TestNet.WaitUntil(() => listing.Listed[0].Players == 1, "the list showing the player");
            Assert.Null(server.Fault);
        }
        finally
        {
            server.Dispose();
        }

        // Removed, not left to expire: the service's clock has not moved since.
        Assert.Empty(listing.Listed);
        Assert.Equal(0, listing.Store.Count);
    }

    [Fact]
    public async Task AnEntryLeftBehindByACrashIsWaitedOutAndThenTheServerIsListedAgain()
    {
        await using var listing = await RealListing.StartAsync();

        // An earlier run of this server announced itself and died without removing its entry or keeping its token.
        using (var http = new HttpClient())
        {
            var body = $$"""{"name":"Before the crash","port":27420,"players":0,"maxPlayers":16,"protocol":{{ProtocolInfo.Version}},"version":"0.1.0","password":false}""";
            using var response = await http.PostAsync($"{listing.Url}/v1/servers", new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var announcer = Announcer(TestNet.Listing(listing.Url));
        announcer.Start();

        // 409 after 409: the old entry is still there and its token is gone. The server keeps asking.
        TestNet.WaitUntil(() => announcer.State == AnnounceState.Retrying, "the announcer meeting the old entry");
        Thread.Sleep(800);
        Assert.Equal(AnnounceState.Retrying, announcer.State);
        Assert.Equal("Before the crash", Assert.Single(listing.Listed).Name);

        // The old entry expires, and the next announcement takes its place.
        listing.Clock.Advance(TimeSpan.FromSeconds(91));
        WaitListed(announcer);
        Assert.Equal("Fake world", Assert.Single(listing.Listed).Name);
    }

    [Fact]
    public async Task TheBrowserListsAHostedPublicGameAndAPlayerJoinsItFromTheList()
    {
        await using var listing = await RealListing.StartAsync();
        var config = TestNet.Listing(listing.Url);
        var port = TestNet.Port(17);
        using var browser = new ServerBrowser(config);
        Assert.Equal(BrowserState.Idle, browser.State);
        Assert.True(browser.Enabled);
        Assert.Empty(browser.List.Games);

        // The host starts a public local game from the client: the same server, listening and announcing.
        using var host = GameSession.StartLocal(
            new ServerConfig { Name = "Open house", MaxPlayers = 4, Visibility = ServerVisibility.Public, Port = port, Password = "knock", Listing = config },
            Content,
            "Host"
        );
        var hosting = TestNet.WaitFor(host, s => s.State == SessionState.Active);
        Assert.Equal(ServerVisibility.Public, hosting.Visibility);
        TestNet.WaitUntil(() => host.LocalAnnounce == AnnounceState.Listed, "the hosted game being listed");

        // The list shows it once a heartbeat has carried the host's presence.
        ListedGame? game = null;
        TestNet.WaitUntil(
            () =>
            {
                browser.Refresh();
                var list = browser.List;
                game = list.State == BrowserState.Ready && list.Games.Length == 1 ? list.Games[0] : null;
                return game is { Players: 1 };
            },
            "the browser showing the hosted game with its host in it"
        );
        Assert.Equal(new ListedGame("Open house", "127.0.0.1", port, 1, 4, true, ProtocolInfo.GameVersion), game);

        // Picking it joins it; the entry says a password is needed, and without one the server says so too.
        using (var hasty = GameSession.Join(game!, Content, "Hasty"))
            Assert.Equal(SessionEndReason.PasswordRequired, TestNet.WaitFor(hasty, s => s.State == SessionState.Ended).EndReason);

        using var guest = GameSession.Join(game!, Content, "Guest", "knock");
        var joined = TestNet.WaitFor(guest, s => s.State == SessionState.Active && s.PlayerCount == 2);
        Assert.Equal("Open house", joined.ServerName);
        Assert.Equal(ServerVisibility.Public, joined.Visibility);
        Assert.Equal(hosting.GalaxySeed, joined.GalaxySeed);
        Assert.Null(host.Fault);
    }

    [Fact]
    public void AnUnlistedServerListensButNeverContactsTheListingServer()
    {
        using var fake = new FakeListing((_, _) => FakeAnswer.Listed());
        var port = TestNet.Port(18);
        using var server = new ServerHost(
            new ServerConfig { Name = "Quiet", Visibility = ServerVisibility.Unlisted, Port = port, Listing = TestNet.Listing(fake.Url, refresh: 0.05) },
            Content
        );
        server.Start();
        Assert.Equal(AnnounceState.Off, server.Announce);

        // It is there for whoever has the address.
        using var client = GameSession.Join("127.0.0.1", port, Content, "Friend");
        Assert.Equal(ServerVisibility.Unlisted, TestNet.WaitFor(client, s => s.State == SessionState.Active).Visibility);

        // Twenty refresh intervals later the listing server has not heard a word, and shutting down says nothing either.
        Thread.Sleep(1000);
        server.Dispose();
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void AListingServerThatIsDownIsRetriedWithGrowingPausesAndNeverStopsTheSimulation()
    {
        var port = TestNet.Port(19);
        using var server = new ServerHost(
            new ServerConfig { Visibility = ServerVisibility.Public, Port = port, Listing = TestNet.Listing(ClosedUrl()) },
            Content
        );
        server.Start();
        TestNet.WaitUntil(() => server.Announce == AnnounceState.Retrying, "the first failed announcement");

        // The world runs at its own pace all the while: a second of ticks, none late, and players can join.
        var before = server.Status.Tick;
        using var client = GameSession.Join("127.0.0.1", port, Content, "Player");
        TestNet.WaitFor(client, s => s.State == SessionState.Active);
        Thread.Sleep(1000);
        Assert.InRange(server.Status.Tick - before, 50, 200);
        Assert.Equal(AnnounceState.Retrying, server.Announce);
        Assert.True(server.IsRunning);
        Assert.Null(server.Fault);

        // And shutting down does not wait for a listing server that is not there.
        var started = Stopwatch.GetTimestamp();
        server.Dispose();
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ARedirectIsNotFollowedWhereverItPointsAndWhateverItsKind()
    {
        // Where the redirects point: a listing server that works, and would answer everything it is asked.
        const string list = """{"servers":[{"name":"Elsewhere","address":"127.0.0.1","port":27420,"players":0,"maxPlayers":4,"password":false,"version":"0.1.0"}]}""";
        using var destination = new FakeListing((request, _) => request.Method == "GET" ? FakeAnswer.Json(200, list) : FakeAnswer.Listed());

        // The listing server the game is set up with answers every request by sending it there.
        var status = 302;
        using var redirecting = new FakeListing((request, _) => new FakeAnswer { Status = status, Location = destination.Url + request.Path });

        // The destination does answer: asked directly, it gives its list. So if it stays unasked below, that is
        // because the redirect was not followed, not because there was nothing to follow it to.
        using (var direct = new ListingHttp(TestNet.Listing(destination.Url)))
            Assert.True((await direct.SendAsync(HttpMethod.Get, "v1/servers", default, null, 4096, CancellationToken.None)).Is(200));
        Assert.Single(destination.Requests);

        // Every kind of redirect, for a request without a body and for one with a body and a token: the answer
        // is handed back as the redirect it is, and neither the body nor the token travels on.
        using (var http = new ListingHttp(TestNet.Listing(redirecting.Url)))
        {
            foreach (var kind in new[] { 301, 302, 303, 307, 308 })
            {
                status = kind;
                var get = await http.SendAsync(HttpMethod.Get, "v1/servers", default, null, 4096, CancellationToken.None);
                Assert.True(get.Is(kind), $"a GET answered {kind} came back as {get.Outcome} {get.Status}");
                var post = await http.SendAsync(HttpMethod.Post, "v1/servers", "{}"u8.ToArray(), "secret-token", 4096, CancellationToken.None);
                Assert.True(post.Is(kind), $"a POST answered {kind} came back as {post.Outcome} {post.Status}");
            }
        }

        Assert.Equal(10, redirecting.Requests.Count);

        // The browser shows a failure and none of the destination's games.
        status = 302;
        using (var browser = new ServerBrowser(TestNet.Listing(redirecting.Url)))
        {
            Assert.True(browser.Refresh());
            TestNet.WaitUntil(() => browser.State == BrowserState.Failed, "the fetch failing");
            Assert.Empty(browser.List.Games);
        }

        // A public server is not listed, there or anywhere, and keeps asking the server it was set up with.
        using (var announcer = Announcer(TestNet.Listing(redirecting.Url, retry: 0.02, maxRetry: 0.05)))
        {
            announcer.Start();
            TestNet.WaitUntil(() => redirecting.Count("POST") >= 5 + 3, "the announcement being tried again");
            Assert.NotEqual(AnnounceState.Listed, announcer.State);
        }

        // Through all of it the destination heard nothing but the one request the test made itself.
        Assert.Single(destination.Requests);
    }

    [Fact]
    public void FailuresBackOffInsteadOfHammering()
    {
        // Every connection is dropped without an answer. First retry after 0.1 s, doubling, at most 0.4 s apart.
        using var fake = new FakeListing((_, _) => new FakeAnswer { Raw = [] });
        using var announcer = Announcer(TestNet.Listing(fake.Url, retry: 0.1, maxRetry: 0.4));
        announcer.Start();
        Thread.Sleep(1500);

        // At 0, 0.1, 0.3, 0.7, 1.1 and perhaps 1.5 seconds: five or six, not hundreds.
        Assert.InRange(fake.Count("POST"), 3, 7);
        Assert.Equal(AnnounceState.Retrying, announcer.State);
    }

    [Fact]
    public void ASlowListingServerIsGivenUpOnAndTriedAgain()
    {
        // The first two requests are accepted and never answered.
        using var fake = new FakeListing((_, index) => index < 2 ? new FakeAnswer { Hang = true } : FakeAnswer.Listed());
        using var announcer = Announcer(TestNet.Listing(fake.Url, timeout: 0.3));
        var started = Stopwatch.GetTimestamp();
        announcer.Start();

        WaitListed(announcer);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(0.6), "two time-outs should have passed");
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.True(fake.Count("POST") >= 3);
    }

    [Fact]
    public void A409IsAskedAgainWithoutATokenUntilItSucceedsAndThenTheTokenIsKept()
    {
        using var fake = new FakeListing(
            (request, index) =>
                request.Method != "POST" ? FakeAnswer.Json(204, "")
                : index < 3 ? FakeAnswer.Json(409, """{"error":"This address and port are already listed. Send the entry's token to refresh it."}""")
                : request.Body.Contains("\"token\"") ? FakeAnswer.Json(200, """{"id":"entry-1","token":"token-1","ttlSeconds":90}""")
                : FakeAnswer.Listed()
        );
        using var announcer = Announcer(TestNet.Listing(fake.Url), password: true);
        announcer.Start();
        WaitListed(announcer);
        TestNet.WaitUntil(() => fake.Count("POST") >= 6, "a few refreshes");

        var posts = fake.Requests.Where(r => r.Method == "POST").ToList();
        Assert.All(posts, r => Assert.Equal("/v1/servers", r.Path));
        Assert.All(posts.Take(4), r => Assert.DoesNotContain("token", r.Body));
        Assert.All(posts.Skip(4), r => Assert.Contains("\"token\":\"token-1\"", r.Body));

        // What it says about itself is what the contract asks for, by the exact names.
        using var body = System.Text.Json.JsonDocument.Parse(posts[0].Body);
        var root = body.RootElement;
        Assert.Equal("Fake world", root.GetProperty("name"u8).GetString());
        Assert.Equal(27420, root.GetProperty("port"u8).GetInt32());
        Assert.Equal(3, root.GetProperty("players"u8).GetInt32());
        Assert.Equal(16, root.GetProperty("maxPlayers"u8).GetInt32());
        Assert.Equal(ProtocolInfo.Version, root.GetProperty("protocol"u8).GetUInt32());
        Assert.Equal(ProtocolInfo.GameVersion, root.GetProperty("version"u8).GetString());
        Assert.True(root.GetProperty("password"u8).GetBoolean());
        Assert.Equal(7, root.EnumerateObject().Count());
    }

    [Fact]
    public void RubbishAnswersAreNotBelievedAndTheServerKeepsTrying()
    {
        var rubbish = new[]
        {
            FakeAnswer.Json(200, "not json at all"),
            FakeAnswer.Json(201, "[]"),
            FakeAnswer.Json(201, "{}"),
            FakeAnswer.Json(201, """{"id":5,"token":"token-1"}"""),
            FakeAnswer.Json(201, """{"id":"entry-1"}"""),
            FakeAnswer.Json(201, """{"id":"entry 1/../..","token":"token-1"}"""),
            FakeAnswer.Json(201, """{"id":"entry-1","token":"evil\r\nX-Injected: 1"}"""),
            FakeAnswer.Json(201, $$"""{"id":"{{new string('i', 65)}}","token":"token-1"}"""),
            FakeAnswer.Json(201, $$"""{"id":"entry-1","token":"{{new string('t', 129)}}"}"""),
            FakeAnswer.Json(201, "{\"id\":\"entry-1\",\"token\":\"token-1\""),
            FakeAnswer.Json(500, "<html>Internal Server Error</html>"),
            FakeAnswer.Json(302, ""),
            FakeAnswer.Json(429, """{"error":"Too many requests from this address."}"""),
            new FakeAnswer { Raw = "SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray() },
            new FakeAnswer { Raw = [] },
        };
        using var fake = new FakeListing((request, index) => request.Method != "POST" ? FakeAnswer.Json(204, "") : index < rubbish.Length ? rubbish[index] : FakeAnswer.Listed());
        using var announcer = Announcer(TestNet.Listing(fake.Url, retry: 0.02, maxRetry: 0.05));
        announcer.Start();

        // After each of them the server is not listed and tries again; none of them is taken for an entry.
        TestNet.WaitUntil(() => fake.Count("POST") > rubbish.Length, "every rubbish answer having been given");
        WaitListed(announcer);
        Assert.All(fake.Requests.Where(r => r.Method == "POST").Take(rubbish.Length + 1), r => Assert.DoesNotContain("token", r.Body));
    }

    [Fact]
    public void AnOversizedAnswerIsCutOffNotReadToTheEnd()
    {
        var huge = new string('x', 200_000);
        using var fake = new FakeListing(
            (request, index) =>
                request.Method != "POST" ? FakeAnswer.Json(204, "")
                : index switch
                {
                    0 => new FakeAnswer { Body = "{}", ClaimedLength = 10_000_000_000 },
                    1 => FakeAnswer.Json(201, $$"""{"id":"entry-1","token":"token-1","padding":"{{huge}}"}"""),
                    2 => new FakeAnswer { Endless = true },
                    _ => FakeAnswer.Listed(),
                }
        );
        using var announcer = Announcer(TestNet.Listing(fake.Url, retry: 0.02, maxRetry: 0.05));
        announcer.Start();
        WaitListed(announcer);

        // The answer without an end was dropped after a few kilobytes; what the fake kept sending went nowhere.
        var sent = fake.EndlessBytes;
        Thread.Sleep(300);
        Assert.Equal(sent, fake.EndlessBytes);
        Assert.InRange(sent, 0, 16 * 1024 * 1024);
    }

    [Fact]
    public void ARefusalIsNotAskedAgainSoonAndALostTokenStartsOver()
    {
        // The service says this address may not be listed. Asking again at once changes nothing: the next try
        // comes after the longest pause, not the shortest.
        using (var refusing = new FakeListing((_, _) => FakeAnswer.Json(403, """{"error":"Only servers on public addresses can be listed."}""")))
        using (var announcer = Announcer(TestNet.Listing(refusing.Url, retry: 0.02, maxRetry: 5)))
        {
            announcer.Start();
            TestNet.WaitUntil(() => refusing.Count("POST") == 1, "the refused announcement");
            Thread.Sleep(600);
            Assert.Equal(1, refusing.Count("POST"));
            Assert.Equal(AnnounceState.Retrying, announcer.State);
        }

        // Listed, then the service no longer knows the token (someone else holds the entry now). The server
        // forgets the token and announces as new, and uses the new token from then on.
        var posts = 0;
        using var fake = new FakeListing(
            (request, _) =>
            {
                if (request.Method != "POST")
                    return FakeAnswer.Json(204, "");
                return Interlocked.Increment(ref posts) switch
                {
                    1 => FakeAnswer.Listed("entry-1", "token-1"),
                    2 => FakeAnswer.Json(403, """{"error":"The token does not match the entry for this address and port."}"""),
                    _ => request.Body.Contains("token-1") ? FakeAnswer.Json(403, "{}") : FakeAnswer.Listed("entry-2", "token-2"),
                };
            }
        );
        var second = Announcer(TestNet.Listing(fake.Url));
        second.Start();
        TestNet.WaitUntil(() => fake.Count("POST") >= 5 && second.State == AnnounceState.Listed, "the server listed anew");
        var requests = fake.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Contains("\"token\":\"token-1\"", requests[1].Body);
        Assert.DoesNotContain("token", requests[2].Body);
        Assert.Contains("\"token\":\"token-2\"", requests[3].Body);

        // On shutdown it removes the entry it holds now, with that entry's token.
        second.Dispose();
        var removal = Assert.Single(fake.Requests, r => r.Method == "DELETE");
        Assert.Equal("/v1/servers/entry-2", removal.Path);
        Assert.Equal("Bearer token-2", removal.Authorization);
    }

    [Fact]
    public void ShutdownRemovesTheEntryWithItsTokenAndDoesNotWaitForeverForASlowListingServer()
    {
        using (var fake = new FakeListing((request, _) => request.Method == "POST" ? FakeAnswer.Listed("abc_DEF-123", "tok.en~1") : FakeAnswer.Json(204, "")))
        {
            var announcer = Announcer(TestNet.Listing(fake.Url));
            announcer.Start();
            WaitListed(announcer);
            announcer.Dispose();

            var removal = Assert.Single(fake.Requests, r => r.Method == "DELETE");
            Assert.Equal("/v1/servers/abc_DEF-123", removal.Path);
            Assert.Equal("Bearer tok.en~1", removal.Authorization);
            Assert.Equal(AnnounceState.Off, announcer.State);

            // Nothing more is sent once it has stopped.
            var count = fake.Requests.Count;
            Thread.Sleep(500);
            Assert.Equal(count, fake.Requests.Count);
        }

        using (var slow = new FakeListing((request, _) => request.Method == "POST" ? FakeAnswer.Listed() : new FakeAnswer { Hang = true }))
        {
            var announcer = Announcer(TestNet.Listing(slow.Url, timeout: 30));
            announcer.Start();
            WaitListed(announcer);

            var started = Stopwatch.GetTimestamp();
            announcer.Dispose();
            Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 0, 6);
            Assert.Equal(1, slow.Count("DELETE"));
        }
    }

    [Fact]
    public void TheBrowserNeverBlocksAndSaysWhatWentWrong()
    {
        // Down: nothing listens there.
        using (var browser = new ServerBrowser(TestNet.Listing(ClosedUrl())))
        {
            Assert.True(browser.Refresh());
            TestNet.WaitUntil(() => browser.State == BrowserState.Failed, "the fetch failing");
            Assert.NotEmpty(browser.List.Error);
            Assert.Empty(browser.List.Games);
        }

        // Slow: the request is accepted and never answered. Asking returns at once, the state says loading, a
        // second ask while loading does nothing, and the time-out ends it.
        using (var slow = new FakeListing((_, _) => new FakeAnswer { Hang = true }))
        using (var browser = new ServerBrowser(TestNet.Listing(slow.Url, timeout: 0.4)))
        {
            var started = Stopwatch.GetTimestamp();
            Assert.True(browser.Refresh());
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(200), "asking for the list must not wait for it");
            Assert.Equal(BrowserState.Loading, browser.State);
            Assert.False(browser.Refresh());
            Assert.Equal(BrowserState.Idle, browser.List.State);

            TestNet.WaitUntil(() => browser.State == BrowserState.Failed, "the fetch timing out", seconds: 5);
            Assert.Contains("in time", browser.List.Error);
            Assert.Single(slow.Requests);
        }

        // Turned off: there is nothing to ask.
        using (var off = new ServerBrowser(null))
        {
            Assert.False(off.Enabled);
            Assert.Equal(BrowserState.Off, off.State);
            Assert.False(off.Refresh());
            Assert.Empty(off.List.Games);
        }
    }

    [Fact]
    public void TheBrowserKeepsTheLastGoodListThroughRubbishAndOversizedAnswers()
    {
        var good = """{"servers":[{"name":"Ceres Station","address":"127.0.0.1","port":27420,"players":3,"maxPlayers":16,"password":true,"version":"0.1.0"},{"name":"Broken","address":"nowhere","port":1,"players":0,"maxPlayers":1,"password":false,"version":"1"}]}""";
        var answers = new[]
        {
            FakeAnswer.Json(200, good),
            FakeAnswer.Json(200, "not a list"),
            FakeAnswer.Json(200, """{"servers":"none"}"""),
            FakeAnswer.Json(500, good),
            FakeAnswer.Json(429, """{"error":"Too many requests from this address."}"""),
            FakeAnswer.Json(302, ""),
            new FakeAnswer { Body = "{}", ClaimedLength = 10_000_000_000 },
            FakeAnswer.Json(200, "{\"servers\":[],\"padding\":\"" + new string('x', ServerBrowser.MaxListBytes) + "\"}"),
            new FakeAnswer { Endless = true },
            new FakeAnswer { Raw = "\0\0\0garbage"u8.ToArray() },
            FakeAnswer.Json(200, """{"servers":[]}"""),
        };
        using var fake = new FakeListing((_, index) => answers[index]);
        using var browser = new ServerBrowser(TestNet.Listing(fake.Url, timeout: 3));

        ServerList Fetch()
        {
            var before = browser.List.Sequence;
            Assert.True(browser.Refresh());
            TestNet.WaitUntil(() => browser.State != BrowserState.Loading && browser.List.Sequence == before + 1, "the fetch finishing");
            return browser.List;
        }

        // A good list first: the sane entry is shown, the broken one is counted and left out.
        var first = Fetch();
        Assert.Equal(BrowserState.Ready, first.State);
        Assert.Equal(new ListedGame("Ceres Station", "127.0.0.1", 27420, 3, 16, true, "0.1.0"), Assert.Single(first.Games));
        Assert.Equal(1, first.Dropped);
        Assert.Equal($"/v1/servers?protocol={ProtocolInfo.Version}", fake.Requests[0].Path);
        Assert.Equal("GET", fake.Requests[0].Method);

        // Then everything that can go wrong with an answer. Each time the browser says it failed, in a sentence,
        // and what it showed before is still there.
        for (var i = 1; i < answers.Length - 1; i++)
        {
            var failed = Fetch();
            Assert.Equal(BrowserState.Failed, failed.State);
            Assert.NotEmpty(failed.Error);
            Assert.Same(first.Games, failed.Games);
        }

        // The answer without an end stopped being read; the fake's bytes went nowhere.
        var sent = fake.EndlessBytes;
        Thread.Sleep(300);
        Assert.Equal(sent, fake.EndlessBytes);

        // And an empty list is an empty list, not a failure.
        var empty = Fetch();
        Assert.Equal(BrowserState.Ready, empty.State);
        Assert.Empty(empty.Games);
        Assert.Empty(empty.Error);
    }

    [Fact]
    public void NothingIsSentToAListedGameUntilThePlayerPicksIt()
    {
        // A UDP socket stands where the listed game claims to be. Fetching and reading the list must not touch it.
        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)game.Client.LocalEndPoint!).Port;
        var list = $$"""{"servers":[{"name":"Bait","address":"127.0.0.1","port":{{port}},"players":0,"maxPlayers":8,"password":false,"version":"0.1.0"}]}""";
        using var fake = new FakeListing((_, _) => FakeAnswer.Json(200, list));
        using var browser = new ServerBrowser(TestNet.Listing(fake.Url));

        for (var i = 0; i < 3; i++)
        {
            var before = browser.List.Sequence;
            browser.Refresh();
            TestNet.WaitUntil(() => browser.List.Sequence > before, "the fetch finishing");
        }

        var listed = Assert.Single(browser.List.Games);
        Thread.Sleep(300);
        Assert.Equal(0, game.Available);

        // Picking it is what sends the first packet.
        using var session = GameSession.Join(listed, Content, "Player");
        TestNet.WaitUntil(() => game.Available > 0, "the first packet to the picked game");
    }

    [Fact]
    public void AnEntryWhoseAddressIsNotAnAddressIsNeverDialled()
    {
        // The browser would not have listed these; a caller that built one by hand gets the same answer.
        foreach (var address in new[] { "play.example.org", "localhost", "203.0.113.7:27420", "0.0.0.0", "2001:db8::7", "" })
            Assert.Throws<IOException>(() => GameSession.Join(new ListedGame("Bait", address, 27420, 0, 8, false, "1"), Content, "Player"));
        Assert.Throws<IOException>(() => GameSession.Join(new ListedGame("Bait", "203.0.113.7", 0, 0, 8, false, "1"), Content, "Player"));
    }
}
