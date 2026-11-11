using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The rules for server names, passwords and the messages of the password handshake, without a network.</summary>
public sealed class HostingProtocolTests
{
    [Theory]
    [InlineData("Ceres Station")]
    [InlineData("  spaced \t out \n name  ")]
    [InlineData("bidi ‮evil‬ and zero​width")]
    [InlineData("\u0007bell\u001b[31m")]
    [InlineData("Köln ☄ 東京 🚀")]
    [InlineData("\uD800 lone surrogate")]
    [InlineData("private  use")]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameIsCleanedExactlyAsTheListingServerCleansIt(string raw)
    {
        // The service's own rule, from its own code: what the game keeps is what the list would show.
        var expected = Prospect.Listing.Announcement.SanitizeName(raw);

        Span<char> clean = stackalloc char[ServerNames.MaxChars];
        var length = ServerNames.Clean(raw, clean, out var truncated);
        Assert.False(truncated);
        Assert.Equal(expected, clean[..length].ToString());
        Assert.Equal(expected.Length > 0, ServerNames.TryNormalize(raw, out var name));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void ANameMayBeFortyEightCharactersAndNotOneMore()
    {
        // Counted in characters, not UTF-16 units: forty-eight rockets are ninety-six units.
        var rockets = string.Concat(Enumerable.Repeat("🚀", 48));
        Assert.True(ServerNames.TryNormalize(rockets, out var name));
        Assert.Equal(rockets, name);
        Assert.Equal(ServerNames.MaxBytes, Encoding.UTF8.GetByteCount(name));

        Assert.False(ServerNames.TryNormalize(rockets + "x", out _));
        Assert.False(ServerNames.TryNormalize(new string('a', 49), out _));

        // What comes from the network is cut instead of refused, and never empty.
        Assert.Equal(new string('a', 48), ServerNames.Clean(new string('a', 500)));
        Assert.Equal(ServerNames.Unnamed, ServerNames.Clean("​\u0000"));
    }

    [Fact]
    public void AWelcomeCarriesTheLongestNameTheVisibilityAndThePasswordFlagWithinItsStatedSize()
    {
        var catalog = GameContent.Load().SettingCatalog;
        var longest = string.Concat(Enumerable.Repeat("🚀", ServerNames.MaxLength));
        Span<byte> buffer = stackalloc byte[WelcomeMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        new WelcomeMessage
        {
            PlayerId = 7,
            ServerName = longest,
            Settings = catalog.Defaults,
            Visibility = ServerVisibility.Public,
            PasswordProtected = true,
            MaxPlayers = 128,
        }.Write(ref writer, catalog);

        var reader = new NetReader(writer.Written[1..]);
        Assert.True(WelcomeMessage.TryRead(ref reader, catalog, out var welcome));
        Assert.Equal(longest, welcome.ServerName);
        Assert.Equal(ServerVisibility.Public, welcome.Visibility);
        Assert.True(welcome.PasswordProtected);
        Assert.Equal(128, welcome.MaxPlayers);
    }

    [Fact]
    public void AWelcomeWithAHostileNameIsCleanedOnArrival()
    {
        var catalog = GameContent.Load().SettingCatalog;
        Span<byte> buffer = stackalloc byte[WelcomeMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        new WelcomeMessage { ServerName = "nice‮\u0007 \n  server", Settings = catalog.Defaults, Visibility = (ServerVisibility)200 }.Write(ref writer, catalog);

        var reader = new NetReader(writer.Written[1..]);
        Assert.True(WelcomeMessage.TryRead(ref reader, catalog, out var welcome));
        Assert.Equal("nice server", welcome.ServerName);
        Assert.Equal(ServerVisibility.Unlisted, welcome.Visibility);
    }

    [Fact]
    public void ARefusalFromAnOlderServerStillSaysWhy()
    {
        // Before the wait was added a refusal was the reason alone. A version mismatch must stay readable.
        var reader = new NetReader([(byte)RejectReason.VersionMismatch]);
        Assert.True(RejectMessage.TryRead(ref reader, out var old));
        Assert.Equal(RejectReason.VersionMismatch, old.Reason);
        Assert.Equal(0, old.RetryAfterSeconds);

        var buffer = new byte[RejectMessage.WireSize];
        var writer = new NetWriter(buffer);
        new RejectMessage { Reason = RejectReason.TooManyAttempts, RetryAfterSeconds = 37 }.Write(ref writer);
        Assert.Equal(RejectMessage.WireSize, writer.Length);
        var current = new NetReader(buffer.AsSpan(1));
        Assert.True(RejectMessage.TryRead(ref current, out var reject));
        Assert.Equal(RejectReason.TooManyAttempts, reject.Reason);
        Assert.Equal(37, reject.RetryAfterSeconds);
    }

    [Fact]
    public void AProofFitsOnePasswordOneSaltAndOneChallengeOnly()
    {
        var salt = new byte[Passwords.SaltSize];
        var challenge = new byte[Passwords.ChallengeSize];
        var key = new byte[Passwords.KeySize];
        var proof = new byte[Passwords.ProofSize];
        salt[0] = 1;
        challenge[0] = 2;
        Passwords.DeriveKey("open sesame", salt, key);
        Passwords.ComputeProof(key, salt, challenge, proof);
        Assert.True(Passwords.Verify(key, salt, challenge, proof));

        // Another challenge: the same server, another attempt.
        var otherChallenge = (byte[])challenge.Clone();
        otherChallenge[31] ^= 1;
        Assert.False(Passwords.Verify(key, salt, otherChallenge, proof));

        // Another salt: another server with the same password.
        var otherSalt = (byte[])salt.Clone();
        otherSalt[15] ^= 1;
        var otherKey = new byte[Passwords.KeySize];
        Passwords.DeriveKey("open sesame", otherSalt, otherKey);
        Assert.NotEqual(key, otherKey);
        Assert.False(Passwords.Verify(otherKey, otherSalt, challenge, proof));

        // Another password.
        Passwords.DeriveKey("open sesamf", salt, otherKey);
        Assert.False(Passwords.Verify(otherKey, salt, challenge, proof));

        // And the proof says nothing a listener could read the password from: it is not the key.
        Assert.NotEqual(key, proof);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("correct horse battery staple", true)]
    [InlineData("pässwörd ☄", true)]
    [InlineData("", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\there", false)]
    public void APasswordIsOneToSixtyFourCharactersWithoutControlCharacters(string password, bool usable)
    {
        Assert.Equal(usable, Passwords.IsUsable(password));
        Assert.True(Passwords.IsUsable(new string('x', Passwords.MaxChars)));
        Assert.False(Passwords.IsUsable(new string('x', Passwords.MaxChars + 1)));
    }

    [Fact]
    public void HandshakeMessagesOfTheWrongLengthAreNotRead()
    {
        var salt = new byte[Passwords.SaltSize];
        var challenge = new byte[Passwords.ChallengeSize];
        var proof = new byte[Passwords.ProofSize];
        for (var length = 0; length < 100; length++)
        {
            var bytes = new byte[length];
            var reader = new NetReader(bytes);
            Assert.Equal(length == Passwords.ProofSize, ProofMessage.TryRead(ref reader, proof));
            reader = new NetReader(bytes);
            Assert.Equal(length == Passwords.SaltSize + Passwords.ChallengeSize, ChallengeMessage.TryRead(ref reader, salt, challenge));
        }
    }

    [Fact]
    public void ASettingsFileKeepsTheServersOwnSectionApartFromTheWorldSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prospect-server-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              "hullStrength": 2,
              "server": {
                "name": "Ceres Station",
                "visibility": "Public",
                "port": 27999,
                "maxPlayers": 32,
                "password": "open sesame",
                "listing": "off",
                "motd": "ignored"
              }
            }
            """
        );
        try
        {
            var warnings = new List<string>();
            var file = ServerFile.Read(path, warnings.Add);
            Assert.Equal(new ServerFile("Ceres Station", ServerVisibility.Public, 27999, 32, "open sesame", "off"), file);
            Assert.Contains(warnings, w => w.Contains("motd"));

            // The world settings read the same file and pass over the server's section without a word.
            var catalog = GameContent.Load().SettingCatalog;
            warnings.Clear();
            var settings = catalog.Build(path, [], warnings.Add);
            Assert.Empty(warnings);
            Assert.Equal(2f, catalog.Number(settings, "hullStrength"));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(ServerFile.Empty, ServerFile.Parse("""{ "hullStrength": 2 }"""u8.ToArray(), "test"));
        Assert.Throws<InvalidDataException>(() => ServerFile.Parse("""{ "server": { "port": "27420" } }"""u8.ToArray(), "test"));
        Assert.Throws<InvalidDataException>(() => ServerFile.Parse("""{ "server": { "visibility": "secret" } }"""u8.ToArray(), "test"));
        Assert.Throws<InvalidDataException>(() => ServerFile.Parse("""{ "server": { "password": 1234 } }"""u8.ToArray(), "test"));
        Assert.Throws<InvalidDataException>(() => ServerFile.Parse("""{ "server": [] }"""u8.ToArray(), "test"));
        Assert.Throws<InvalidDataException>(() => ServerFile.Parse("not json"u8.ToArray(), "test"));
    }

    [Fact]
    public void AServerRefusesANameOrAPasswordItCouldNotUse()
    {
        var content = GameContent.Load();
        Assert.Throws<InvalidDataException>(() => new ServerHost(new ServerConfig { Name = "​" }, content));
        Assert.Throws<InvalidDataException>(() => new ServerHost(new ServerConfig { Name = new string('n', 49) }, content));
        Assert.Throws<InvalidDataException>(() => new ServerHost(new ServerConfig { Password = "" }, content));
        Assert.Throws<InvalidDataException>(() => new ServerHost(new ServerConfig { Password = new string('p', 65) }, content));
    }
}

/// <summary>The limit on wrong passwords, with a clock the test moves: one unit is one second.</summary>
public sealed class AttemptLimiterTests
{
    private const ulong Address = 0x1_7F00_0001;

    [Fact]
    public void TheFirstWrongPasswordsAreFreeAndEachFurtherOneDoublesTheWait()
    {
        var limiter = new AttemptLimiter(frequency: 1);
        long now = 1000;
        Assert.Equal(0, limiter.SecondsBlocked(Address, now));

        limiter.RecordFailure(Address, now);
        limiter.RecordFailure(Address, now);
        Assert.Equal(0, limiter.SecondsBlocked(Address, now));

        var expected = new[] { 2, 4, 8, 16, 32, 64, 128, 256, 300, 300, 300 };
        foreach (var seconds in expected)
        {
            limiter.RecordFailure(Address, now);
            Assert.Equal(seconds, limiter.SecondsBlocked(Address, now));
            Assert.Equal(1, limiter.SecondsBlocked(Address, now + seconds - 1));
            now += seconds;
            Assert.Equal(0, limiter.SecondsBlocked(Address, now));
        }

        // Another address is not held back by this one.
        Assert.Equal(0, limiter.SecondsBlocked(Address + 1, now - 1));
    }

    [Fact]
    public void AnAddressIsForgottenAQuarterOfAnHourAfterItsLastWrongPassword()
    {
        var limiter = new AttemptLimiter(frequency: 1);
        for (var i = 0; i < 6; i++)
            limiter.RecordFailure(Address, 0);
        Assert.Equal(16, limiter.SecondsBlocked(Address, 0));

        // Just before, the count still stands: one more is the seventh. After, it starts from nothing.
        limiter.RecordFailure(Address, AttemptLimiter.ForgetSeconds - 1);
        Assert.Equal(32, limiter.SecondsBlocked(Address, AttemptLimiter.ForgetSeconds - 1));

        var later = AttemptLimiter.ForgetSeconds * 2;
        limiter.RecordFailure(Address, later);
        Assert.Equal(0, limiter.SecondsBlocked(Address, later));
    }

    [Fact]
    public void WhenTheTableIsFullEveryAddressWithoutARowSharesOne()
    {
        var limiter = new AttemptLimiter(frequency: 1);
        for (ulong i = 0; i < AttemptLimiter.Capacity; i++)
            limiter.RecordFailure(1000 + i, 0);

        // Thousands of new addresses, one wrong password each: together they are treated as one address, so
        // the flood is locked out instead of every address getting its own free tries.
        for (ulong i = 0; i < 5000; i++)
        {
            if (limiter.SecondsBlocked(50_000 + i, 0) == 0)
                limiter.RecordFailure(50_000 + i, 0);
        }

        Assert.True(limiter.SecondsBlocked(99_999, 0) > 0, "an address the table has no room for should be held back with the rest");

        // Addresses that have a row keep their own count.
        Assert.Equal(0, limiter.SecondsBlocked(1000, 0));

        // Once rows have been forgotten there is room again, and a newcomer starts free.
        var later = AttemptLimiter.ForgetSeconds + 1;
        Assert.Equal(0, limiter.SecondsBlocked(99_999, later));
    }
}

/// <summary>Passwords, visibility and hosted local games, with real servers and clients over UDP.</summary>
public sealed class HostingTests
{
    private static readonly GameContent Content = GameContent.Load();
    private const string Password = "open sesame";

    private static ServerHost Protected(int slot, string password = Password)
    {
        var server = new ServerHost(new ServerConfig { Name = "Locked", MaxPlayers = 4, Visibility = ServerVisibility.Unlisted, Port = TestNet.Port(slot), Password = password }, Content);
        server.Start();
        return server;
    }

    private static byte[] Proof(string password, byte[] salt, byte[] challenge)
    {
        var key = new byte[Passwords.KeySize];
        var proof = new byte[Passwords.ProofSize];
        Passwords.DeriveKey(password, salt, key);
        Passwords.ComputeProof(key, salt, challenge, proof);
        return proof;
    }

    [Fact]
    public void TheRightPasswordJoinsAWrongOneIsRefusedAndNoneIsToldOneIsNeeded()
    {
        var port = TestNet.Port(0);
        using var server = Protected(0);

        using var right = GameSession.Join("127.0.0.1", port, Content, "Right", Password);
        var joined = TestNet.WaitFor(right, s => s.State == SessionState.Active);
        Assert.Equal("Locked", joined.ServerName);
        Assert.True(joined.PasswordProtected);
        Assert.Equal(ServerVisibility.Unlisted, joined.Visibility);
        Assert.Equal(4, joined.MaxPlayers);

        using var wrong = GameSession.Join("127.0.0.1", port, Content, "Wrong", "open sesamf");
        Assert.Equal(SessionEndReason.WrongPassword, TestNet.WaitFor(wrong, s => s.State == SessionState.Ended).EndReason);

        using var none = GameSession.Join("127.0.0.1", port, Content, "None");
        Assert.Equal(SessionEndReason.PasswordRequired, TestNet.WaitFor(none, s => s.State == SessionState.Ended).EndReason);

        // Being told a password is needed is not a wrong password: the player can give it and get in.
        using var retry = GameSession.Join(none.RemoteHost!, none.RemotePort, Content, "None", Password);
        TestNet.WaitFor(retry, s => s.State == SessionState.Active);
        Assert.Equal(2, server.Status.PlayerCount);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void AServerWithoutAPasswordTakesAClientThatOffersOne()
    {
        var port = TestNet.Port(1);
        using var server = new ServerHost(new ServerConfig { Visibility = ServerVisibility.Unlisted, Port = port }, Content);
        server.Start();

        using var client = GameSession.Join("127.0.0.1", port, Content, "Keen", "not needed");
        var joined = TestNet.WaitFor(client, s => s.State == SessionState.Active);
        Assert.False(joined.PasswordProtected);
    }

    [Fact]
    public void AProofCapturedFromOneHandshakeOpensNoOther()
    {
        var port = TestNet.Port(2);
        using var server = Protected(2);

        // A handshake that succeeds, with everything that crossed the wire written down.
        using var first = new RawClient(port);
        first.SendHello();
        var (salt, challenge) = first.ExpectChallenge();
        var captured = Proof(Password, salt, challenge);
        first.SendProof(captured);
        first.Expect(MessageId.Welcome);

        // A second connection is challenged with other bytes, and the captured proof does not answer them.
        using var replay = new RawClient(port);
        replay.SendHello();
        var (salt2, challenge2) = replay.ExpectChallenge();
        Assert.Equal(salt, salt2);
        Assert.NotEqual(challenge, challenge2);
        replay.SendProof(captured);
        Assert.Equal(RejectReason.WrongPassword, replay.ExpectReject().Reason);
        replay.ExpectClosed();

        // The password itself still works there: it is the proof that was spent, not the server that is stuck.
        using var again = new RawClient(port);
        again.SendHello();
        var (salt3, challenge3) = again.ExpectChallenge();
        again.SendProof(Proof(Password, salt3, challenge3));
        again.Expect(MessageId.Welcome);

        // Another server with the same password has its own salt: a proof made for one fits no challenge of the other.
        using var other = Protected(3);
        using var elsewhere = new RawClient(TestNet.Port(3));
        elsewhere.SendHello();
        var (otherSalt, otherChallenge) = elsewhere.ExpectChallenge();
        Assert.NotEqual(salt, otherSalt);
        elsewhere.SendProof(Proof(Password, salt, otherChallenge));
        Assert.Equal(RejectReason.WrongPassword, elsewhere.ExpectReject().Reason);
        Assert.Equal(1 + 1, server.Status.PlayerCount);
    }

    [Fact]
    public void ASecondProofOnOneConnectionIsNotAnotherGuess()
    {
        var port = TestNet.Port(4);
        using var server = Protected(4);

        // One connection, one try: after the wrong proof the connection is gone, and the right one sent after
        // it on the same connection opens nothing.
        using var client = new RawClient(port);
        client.SendHello();
        var (salt, challenge) = client.ExpectChallenge();
        client.SendProof(Proof("wrong", salt, challenge));
        client.SendProof(Proof(Password, salt, challenge));
        Assert.Equal(RejectReason.WrongPassword, client.ExpectReject().Reason);
        client.ExpectClosed();
        Assert.Equal(0, server.Status.PlayerCount);
    }

    [Fact]
    public void RepeatedWrongPasswordsFromOneAddressAreSlowed()
    {
        var port = TestNet.Port(5);
        using var server = Protected(5);

        for (var i = 0; i < AttemptLimiter.FreeFailures; i++)
        {
            using var wrong = GameSession.Join("127.0.0.1", port, Content, "Guesser", $"guess {i}");
            Assert.Equal(SessionEndReason.WrongPassword, TestNet.WaitFor(wrong, s => s.State == SessionState.Ended).EndReason);
        }

        // The address is now locked out. Not even the right password is looked at, and no challenge is given:
        // there is nothing to guess against until the wait is over.
        var lockedAt = Stopwatch.GetTimestamp();
        using (var locked = GameSession.Join("127.0.0.1", port, Content, "Guesser", Password))
        {
            var ended = TestNet.WaitFor(locked, s => s.State == SessionState.Ended);
            Assert.Equal(SessionEndReason.TooManyAttempts, ended.EndReason);
            Assert.InRange(ended.RetryAfterSeconds, 1, 2);
        }

        using (var raw = new RawClient(port))
        {
            raw.SendHello();
            var reject = raw.ExpectReject();
            Assert.Equal(RejectReason.TooManyAttempts, reject.Reason);
            Assert.InRange(reject.RetryAfterSeconds, 1, 2);
        }

        // After the wait the right password gets in.
        var wait = TimeSpan.FromSeconds(2.2) - Stopwatch.GetElapsedTime(lockedAt);
        if (wait > TimeSpan.Zero)
            Thread.Sleep(wait);
        using var right = GameSession.Join("127.0.0.1", port, Content, "Owner", Password);
        TestNet.WaitFor(right, s => s.State == SessionState.Active);

        // One more wrong one, and the wait is twice as long.
        using var fourth = GameSession.Join("127.0.0.1", port, Content, "Guesser", "guess again");
        Assert.Equal(SessionEndReason.WrongPassword, TestNet.WaitFor(fourth, s => s.State == SessionState.Ended).EndReason);
        using var after = GameSession.Join("127.0.0.1", port, Content, "Guesser", Password);
        var slowed = TestNet.WaitFor(after, s => s.State == SessionState.Ended);
        Assert.Equal(SessionEndReason.TooManyAttempts, slowed.EndReason);
        Assert.InRange(slowed.RetryAfterSeconds, 3, 4);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ProofsSentInParallelFromOneAddressAreNotExtraGuesses()
    {
        var port = TestNet.Port(6);
        using var server = Protected(6);

        // Six connections challenged before any of them answers. Only the free tries are judged; the rest are
        // turned away unread once the address is locked out.
        var clients = new List<RawClient>();
        var challenges = new List<(byte[] Salt, byte[] Challenge)>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var client = new RawClient(port);
                client.SendHello();
                challenges.Add(client.ExpectChallenge());
                clients.Add(client);
            }

            var reasons = new List<RejectReason>();
            for (var i = 0; i < clients.Count; i++)
            {
                // The last one even has the right password: it is not looked at.
                clients[i].SendProof(Proof(i == clients.Count - 1 ? Password : "wrong", challenges[i].Salt, challenges[i].Challenge));
                reasons.Add(clients[i].ExpectReject().Reason);
            }

            Assert.Equal(AttemptLimiter.FreeFailures, reasons.Count(r => r == RejectReason.WrongPassword));
            Assert.Equal(clients.Count - AttemptLimiter.FreeFailures, reasons.Count(r => r == RejectReason.TooManyAttempts));
            Assert.Equal(0, server.Status.PlayerCount);
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public void MalformedHandshakeMessagesNeverThrow()
    {
        var port = TestNet.Port(7);
        using var server = Protected(7);

        var scripts = new Action<RawClient>[]
        {
            // Nothing a handshake starts with.
            c => c.Send([]),
            c => c.Send([(byte)MessageId.Hello]),
            c => c.Send([(byte)MessageId.Hello, 1, 2]),
            c => c.Send([(byte)MessageId.Proof, .. new byte[Passwords.ProofSize]]),
            c => c.Send([(byte)MessageId.Challenge, .. new byte[Passwords.SaltSize + Passwords.ChallengeSize]]),
            c => c.Send([(byte)MessageId.Welcome, .. new byte[64]]),
            c => c.Send([0xFF, .. new byte[300]]),
            // A hello with a name that claims to be longer than the message, and one far longer than allowed.
            c => c.Send([(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version), 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]),
            c => c.Send([(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version), 200, .. new byte[200]]),
            // A good hello on the channel that may lose it.
            c =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new NetWriter(buffer);
                new HelloMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = "Raw" }.Write(ref writer);
                c.Send(writer.Written, NetChannel.Unreliable);
                c.Send(writer.Written, NetChannel.Unreliable);
            },
            // A good hello, then anything but one proof.
            c => HelloThen(c, [(byte)MessageId.Proof]),
            c => HelloThen(c, [(byte)MessageId.Proof, .. new byte[Passwords.ProofSize - 1]]),
            c => HelloThen(c, [(byte)MessageId.Proof, .. new byte[Passwords.ProofSize + 1]]),
            c => HelloThen(c, [(byte)MessageId.Proof, .. new byte[4096]]),
            c => HelloThen(c, [(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version), 3, (byte)'R', (byte)'a', (byte)'w']),
            c => HelloThen(c, [(byte)MessageId.Input, .. new byte[40]]),
            c => HelloThen(c, [(byte)MessageId.Control, 0, 0, 0, 0]),
            c => HelloThen(c, []),
            c =>
            {
                c.SendHello();
                c.ExpectChallenge();
                c.Send([(byte)MessageId.Proof, .. new byte[Passwords.ProofSize]], NetChannel.Unreliable);
                c.Send([(byte)MessageId.Proof, .. new byte[Passwords.ProofSize]], NetChannel.Unreliable);
            },
        };

        foreach (var script in scripts)
        {
            using var client = new RawClient(port);
            script(client);
            client.ExpectClosed();
            Assert.True(server.IsRunning);
            Assert.Null(server.Fault);
        }

        // And noise: random bytes thrown at every stage of the handshake.
        var random = new Random(4321);
        var junk = new byte[96];
        for (var round = 0; round < 24; round++)
        {
            using var client = new RawClient(port);
            if (round % 2 == 1)
            {
                client.SendHello();

                // A server that has locked this address out after earlier noise answers with a refusal instead.
                try
                {
                    client.ExpectChallenge();
                }
                catch (TimeoutException) when (client.Disconnected) { }
            }

            for (var i = 0; i < 8 && !client.Disconnected; i++)
            {
                random.NextBytes(junk);
                client.Send(junk.AsSpan(0, random.Next(0, junk.Length)), (NetChannel)(i % 2));
            }

            client.ExpectClosed();
        }

        Assert.True(server.IsRunning);
        Assert.Null(server.Fault);
        Assert.Equal(0, server.Status.PlayerCount);

        static void HelloThen(RawClient client, byte[] next)
        {
            client.SendHello();
            client.ExpectChallenge();
            client.Send(next);
        }
    }

    [Fact]
    public void AMaliciousServerCannotMakeAClientAnswerChallengeAfterChallenge()
    {
        // The client's end of a link whose other end the test plays: a server that keeps challenging.
        using var link = new LoopbackLink();
        var collector = new Collector();
        using var client = new ClientSession(link.Client, Content, "Victim", Password);
        client.Start();

        TestNet.WaitUntil(
            () =>
            {
                link.Server.Poll(collector);
                return collector.Hellos == 1;
            },
            "the client's hello"
        );

        var challenge = new byte[ChallengeMessage.WireSize];
        var writer = new NetWriter(challenge);
        ChallengeMessage.Write(ref writer, new byte[Passwords.SaltSize], new byte[Passwords.ChallengeSize]);
        link.Server.Send(0, NetChannel.Reliable, challenge);
        TestNet.WaitUntil(
            () =>
            {
                link.Server.Poll(collector);
                return collector.Proofs == 1;
            },
            "the client's proof"
        );

        // A second challenge gets no second proof: the session ends instead.
        link.Server.Send(0, NetChannel.Reliable, challenge);
        TestNet.WaitUntil(() => client.AcquireSnapshot().State == SessionState.Ended, "the session ending");
        link.Server.Poll(collector);
        Assert.Equal(1, collector.Proofs);
        Assert.Equal(SessionEndReason.Refused, client.AcquireSnapshot().EndReason);
        Assert.Null(client.Fault);
    }

    [Fact]
    public void APrivateGameOpensNoListenerSoNothingCanConnect()
    {
        var port = TestNet.Port(8);
        using var session = GameSession.StartLocal(new ServerConfig { Name = "Alone", MaxPlayers = 1, Port = port }, Content, "Host");
        var host = TestNet.WaitFor(session, s => s.State == SessionState.Active);
        Assert.Equal(ServerVisibility.Private, host.Visibility);
        Assert.False(host.PasswordProtected);
        Assert.Equal(AnnounceState.Off, session.LocalAnnounce);

        // Nothing holds the UDP port: a socket that refuses to share it can bind it.
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.ExclusiveAddressUse = true;
            probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
        }

        // And a client that tries gets no answer.
        using var outsider = GameSession.Join("127.0.0.1", port, Content, "Outsider");
        var ended = TestNet.WaitFor(outsider, s => s.State == SessionState.Ended, seconds: 30);
        Assert.Equal(SessionEndReason.ConnectFailed, ended.EndReason);
        Assert.Equal(1, session.LocalServerStatus!.Value.PlayerCount);
    }

    [Fact]
    public void AnUnlistedOrPublicGameHoldsItsPortAndASecondOneOnItFailsToStart()
    {
        var port = TestNet.Port(9);
        using var session = GameSession.StartLocal(new ServerConfig { Visibility = ServerVisibility.Unlisted, Port = port, MaxPlayers = 2 }, Content, "Host");
        TestNet.WaitFor(session, s => s.State == SessionState.Active);

        // Starting a second game on the same port fails with a message, and leaves nothing running behind it.
        var error = Assert.Throws<IOException>(() => GameSession.StartLocal(new ServerConfig { Visibility = ServerVisibility.Unlisted, Port = port }, Content, "Second"));
        Assert.Contains(port.ToString(), error.Message);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void ASecondPlayerJoinsTheHostsLocalGameOverUdpAndBothSeeEachOthersHulls()
    {
        var port = TestNet.Port(10);
        using var host = GameSession.StartLocal(
            new ServerConfig { Name = "Host's game", MaxPlayers = 4, Visibility = ServerVisibility.Unlisted, Port = port, Password = Password },
            Content,
            "Host"
        );

        // The host plays over the in-process link and is not asked for their own game's password.
        var hosting = TestNet.WaitFor(host, s => s.State == SessionState.Active);
        Assert.True(host.IsLocal);
        Assert.Equal("Host's game", hosting.ServerName);
        Assert.Equal(ServerVisibility.Unlisted, hosting.Visibility);
        Assert.True(hosting.PasswordProtected);

        // A guest without the password is told one is needed; with it they are in the same world.
        using (var stranger = GameSession.Join("127.0.0.1", port, Content, "Stranger"))
            Assert.Equal(SessionEndReason.PasswordRequired, TestNet.WaitFor(stranger, s => s.State == SessionState.Ended).EndReason);

        using var guest = GameSession.Join("127.0.0.1", port, Content, "Guest", Password);
        var guesting = TestNet.WaitFor(guest, s => s.State == SessionState.Active && s.PlayerCount == 2);
        Assert.False(guest.IsLocal);
        Assert.Equal("Host's game", guesting.ServerName);
        Assert.Equal(hosting.GalaxySeed, guesting.GalaxySeed);
        Assert.NotEqual(hosting.PlayerId, guesting.PlayerId);

        // Each sees their own fleet and the other's.
        var hostView = TestNet.WaitFor(host, s => s.PlayerCount == 2 && Owns(s, hosting.PlayerId) && Owns(s, guesting.PlayerId));
        var guestView = TestNet.WaitFor(guest, s => Owns(s, hosting.PlayerId) && Owns(s, guesting.PlayerId));
        Assert.Equal(2, hostView.PlayerCount);
        Assert.Equal(1 + Content.StartingFleet.Length, Count(hostView, guesting.PlayerId));
        Assert.Equal(1 + Content.StartingFleet.Length, Count(guestView, hosting.PlayerId));
        Assert.Equal(2, host.LocalServerStatus!.Value.PlayerCount);

        // The guest leaves; the host's game goes on with the host in it.
        guest.Client.RequestLeave();
        TestNet.WaitUntil(() => host.LocalServerStatus!.Value.PlayerCount == 1, "the guest's slot being freed");
        TestNet.WaitFor(host, s => s.State == SessionState.Active && !Owns(s, guesting.PlayerId));
        Assert.Null(host.Fault);

        static bool Owns(SessionSnapshot snapshot, uint player) => Count(snapshot, player) > 0;

        static int Count(SessionSnapshot snapshot, uint player)
        {
            var count = 0;
            for (var i = 0; i < snapshot.HullCount; i++)
            {
                if (snapshot.Hulls[i].Owner == player)
                    count++;
            }

            return count;
        }
    }

    private sealed class Collector : ITransportHandler
    {
        public int Hellos;
        public int Proofs;

        public void OnConnected(ITransport transport, int peer) { }

        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) { }

        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (payload.IsEmpty)
                return;
            if (payload[0] == (byte)MessageId.Hello)
                Hellos++;
            else if (payload[0] == (byte)MessageId.Proof)
                Proofs++;
        }
    }
}
