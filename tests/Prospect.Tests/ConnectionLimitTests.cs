using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The limit on how often one address may open a connection, with a clock the test moves: one unit is one second.</summary>
public sealed class ConnectionThrottleTests
{
    private const ulong Address = 0x1_7F00_0001;

    [Fact]
    public void AnAddressHasABurstAndThenEarnsOneConnectionAtATime()
    {
        // Five at once, then one every two seconds.
        var throttle = new ConnectionThrottle(burst: 5, perSecond: 0.5, frequency: 1);
        long now = 100;
        for (var i = 0; i < 5; i++)
            Assert.True(throttle.TryTake(Address, now, out _));

        Assert.False(throttle.TryTake(Address, now, out var wait));
        Assert.Equal(2, wait);
        Assert.False(throttle.TryTake(Address, now + 1, out wait));
        Assert.Equal(1, wait);

        // Asking while there is nothing to have costs nothing: the next one is earned on time.
        Assert.True(throttle.TryTake(Address, now + 2, out _));
        Assert.False(throttle.TryTake(Address, now + 2, out _));
        Assert.True(throttle.TryTake(Address, now + 4, out _));

        // Another address has its own allowance.
        Assert.True(throttle.TryTake(Address + 1, now, out _));

        // Left alone for as long as a burst takes to earn, the address has all of it again.
        now += 4 + 10;
        for (var i = 0; i < 5; i++)
            Assert.True(throttle.TryTake(Address, now, out _));
        Assert.False(throttle.TryTake(Address, now, out _));
    }

    [Fact]
    public void WhenTheTableIsFullEveryAddressWithoutARowSharesOneAllowance()
    {
        var throttle = new ConnectionThrottle(burst: 3, perSecond: 1, frequency: 1);
        for (ulong i = 0; i < ConnectionThrottle.Capacity; i++)
            Assert.True(throttle.TryTake(1000 + i, 0, out _));

        // Thousands of new addresses, one connection each: together they get what one address gets.
        var taken = 0;
        for (ulong i = 0; i < 5000; i++)
            taken += throttle.TryTake(50_000 + i, 0, out _) ? 1 : 0;
        Assert.Equal(3, taken);

        // Addresses that have a row keep their own.
        Assert.True(throttle.TryTake(1000, 0, out _));

        // A second later the rows of those that connected once are free again, and a newcomer has a full burst.
        for (var i = 0; i < 3; i++)
            Assert.True(throttle.TryTake(99_999, 2, out _));
        Assert.False(throttle.TryTake(99_999, 2, out _));
    }
}

/// <summary>
/// What a server does with connections that have not been let in, with the network and the clock in the test's
/// hands: the server is ticked by the test, so nothing here depends on timing.
/// </summary>
public sealed class ConnectionLimitTests
{
    private static readonly GameContent Content = GameContent.Load();
    private const string Password = "open sesame";
    private const ulong Attacker = 0x1_C633_6407;
    private const ulong Household = 0x1_CB00_7107;
    private const ulong Stranger = 0x1_C000_0201;

    [Theory]
    [InlineData(null)]
    [InlineData(Password)]
    public void OneAddressOpeningConnectionsAsFastAsItCanKeepsNobodyElseOut(string? password)
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 8, password);
        var limits = ConnectionLimits.Default;
        var held = 0;
        var newcomers = new List<int>();
        var logged = CountingLogs.OnThisThread;

        // Ten seconds, fifty connections a tick, none of which ever says a word. Each that is given a slot holds
        // it until its time is up, and the address is back for it at once.
        for (var tick = 0; tick < 600; tick++)
        {
            for (var i = 0; i < 50; i++)
                rig.Net.Connect(Attacker);

            // In the middle of it, players from other addresses arrive.
            if (tick is 100 or 300 or 500)
                newcomers.Add(rig.Net.Connect(Stranger + (ulong)tick));
            if (tick is 101 or 301 or 501)
                rig.Hello(newcomers[^1], "Newcomer");
            if (password is not null && tick is 102 or 302 or 502)
                rig.Proof(newcomers[^1], password);

            rig.Tick();
            held = Math.Max(held, rig.Net.OpenFrom(Attacker));
        }

        // The address never held more than its share of the pending connections, whatever it tried...
        Assert.Equal(30_000 + 3, rig.Net.Count);
        Assert.InRange(held, 1, limits.PendingPerAddress);

        // ...in all it was looked at no more often than its allowance, a burst and one more a second, and
        // given a slot less often than that: a connection that finds the address's share taken is turned away.
        var slots = 0;
        var refused = 0;
        for (var peer = 0; peer < rig.Net.Count; peer++)
        {
            if (rig.Net[peer].Origin != Attacker)
                continue;
            if (rig.Net[peer].Reject == RejectReason.TooManyAttempts)
            {
                refused++;
                Assert.InRange(rig.Net[peer].RetryAfterSeconds, 1, 2);
            }
            else
            {
                slots++;
            }
        }

        Assert.InRange(slots, limits.PendingPerAddress, limits.ConnectionBurst + 11);
        Assert.Equal(30_000, slots + refused);

        // ...and every newcomer got in, in the tick their handshake was complete.
        Assert.All(newcomers, peer => Assert.Equal(1, rig.Net[peer].Welcomes));
        Assert.All(newcomers, peer => Assert.True(rig.Net[peer].Open));
        Assert.Equal(3, rig.Server.Status.PlayerCount);

        // Thirty thousand refusals in ten seconds are a line every five seconds in the log, beside the three
        // that say who joined.
        Assert.InRange(CountingLogs.OnThisThread - logged, 3 + 1, 3 + 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Password)]
    public void SeveralPlayersBehindOneAddressJoinInTheSameTick(string? password)
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 8, password);

        // A household, or a LAN party behind one router: eight connections from one address at the same moment.
        var players = new int[8];
        for (var i = 0; i < players.Length; i++)
        {
            players[i] = rig.Net.Connect(Household);
            rig.Hello(players[i], $"Player {i}");
        }

        rig.Tick();
        if (password is not null)
        {
            Assert.All(players, peer => Assert.Equal(1, rig.Net[peer].Challenges));
            foreach (var peer in players)
                rig.Proof(peer, password);
            rig.Tick();
        }

        Assert.All(players, peer => Assert.Equal(1, rig.Net[peer].Welcomes));
        Assert.Equal(8, rig.Server.Status.PlayerCount);

        // A ninth finds the server full, and is told so rather than being held at the door.
        var late = rig.Net.Connect(Household);
        rig.Hello(late);
        rig.Tick();
        Assert.Equal(RejectReason.ServerFull, rig.Net[late].Reject);
    }

    [Fact]
    public void PendingConnectionsNeverTakeAPlayersSlotAndTheDoorClearsByTheClock()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 2);
        var limits = ConnectionLimits.Default;

        // Many addresses, each holding as many pending connections as it may: together they fill the door.
        var silent = new List<int>();
        for (ulong address = 0; address < 6; address++)
        {
            for (var i = 0; i < limits.PendingPerAddress + 2; i++)
                silent.Add(rig.Net.Connect(Attacker + address));
        }

        rig.Tick();
        Assert.Equal(limits.PendingTotal, silent.Count(peer => rig.Net[peer].Open));
        for (ulong address = 0; address < 6; address++)
            Assert.InRange(rig.Net.OpenFrom(Attacker + address), 0, limits.PendingPerAddress);

        // With the door full a newcomer is asked to come back in a moment: that much many addresses can still do.
        var early = rig.Net.Connect(Stranger);
        rig.Tick();
        Assert.Equal(RejectReason.TooManyAttempts, rig.Net[early].Reject);
        Assert.Equal(1, rig.Net[early].RetryAfterSeconds);
        Assert.Equal(0, rig.Server.Status.PlayerCount);

        // No player slot was touched. The moment the handshakes run out of time the door is clear, and both
        // player slots are there for whoever comes.
        rig.Clock.AdvanceSeconds(limits.HandshakeSeconds);
        rig.Tick();
        Assert.All(silent, peer => Assert.False(rig.Net[peer].Open));
        var first = rig.Join(Stranger, name: "First");
        var second = rig.Join(Stranger + 1, name: "Second");
        Assert.Equal(1, rig.Net[first].Welcomes);
        Assert.Equal(1, rig.Net[second].Welcomes);
        Assert.Equal(2, rig.Server.Status.PlayerCount);
    }

    [Fact]
    public void AHandshakeEndsByTheClockEvenWhenTheServerHasNotTicked()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4, Password);
        var limits = ConnectionLimits.Default;

        // One connection that never says hello, and one that says hello and never answers the challenge.
        var silent = rig.Net.Connect(Attacker);
        var challenged = rig.Net.Connect(Attacker + 1);
        rig.Hello(challenged);
        rig.Tick();
        Assert.Equal(1, rig.Net[challenged].Challenges);

        // Just short of the time allowed both still stand.
        rig.Clock.AdvanceSeconds(limits.HandshakeSeconds - 0.1);
        rig.Tick();
        Assert.True(rig.Net[silent].Open);
        Assert.True(rig.Net[challenged].Open);

        // The server stalls: a minute goes by on the clock in which it does not tick once. Counted in ticks the
        // two would have minutes left. By the clock their time is long up, and the first tick ends them.
        rig.Clock.AdvanceSeconds(60);
        rig.Tick();
        Assert.True(rig.Net[silent].ClosedByServer);
        Assert.True(rig.Net[challenged].ClosedByServer);
        Assert.Equal(RejectReason.BadHandshake, rig.Net[silent].Reject);

        // Neither was a wrong password: the address is not locked out, and the right one still gets in.
        var owner = rig.Join(Attacker + 1, Password);
        Assert.Equal(1, rig.Net[owner].Welcomes);
    }

    [Fact]
    public void AnEmptyMessageOrJunkEndsAConnectionThatHasNotBeenLetIn()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4, Password);

        var empty = rig.Net.Connect(Attacker);
        rig.Net.Deliver(empty, []);
        var unreliable = rig.Net.Connect(Attacker + 1);
        rig.Net.Deliver(unreliable, [(byte)MessageId.Hello, 12, 0, 0, 0, 1, (byte)'x'], NetChannel.Unreliable);
        var emptyAfterHello = rig.Net.Connect(Attacker + 2);
        rig.Hello(emptyAfterHello);
        rig.Tick();
        rig.Net.Deliver(emptyAfterHello, []);
        rig.Tick();

        Assert.All(new[] { empty, unreliable, emptyAfterHello }, peer => Assert.True(rig.Net[peer].ClosedByServer));
        Assert.All(new[] { empty, unreliable, emptyAfterHello }, peer => Assert.Equal(RejectReason.BadHandshake, rig.Net[peer].Reject));
    }

    [Fact]
    public void AnAddressThatIsLockedOutIsRefusedBeforeItsNameIsRead()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4, Password);
        for (var i = 0; i < AttemptLimiter.FreeFailures; i++)
        {
            var guesser = rig.Net.Connect(Attacker);
            rig.Hello(guesser);
            rig.Tick();
            rig.Proof(guesser, "wrong");
            rig.Tick();
            Assert.Equal(RejectReason.WrongPassword, rig.Net[guesser].Reject);
        }

        // A hello that ends where the name should begin: read to the end it is a bad handshake. The address is
        // locked out, and that is what it is told, so the server never got as far as the name.
        var locked = rig.Net.Connect(Attacker);
        rig.Net.Deliver(locked, [(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version)]);
        rig.Tick();
        Assert.Equal(RejectReason.TooManyAttempts, rig.Net[locked].Reject);
        Assert.InRange(rig.Net[locked].RetryAfterSeconds, 1, 2);

        // The same hello from an address that is not locked out is what it looks like.
        var cut = rig.Net.Connect(Stranger);
        rig.Net.Deliver(cut, [(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version)]);
        rig.Tick();
        Assert.Equal(RejectReason.BadHandshake, rig.Net[cut].Reject);

        // And a hello of another version is told so, whatever follows the version.
        var other = rig.Net.Connect(Stranger);
        rig.Net.Deliver(other, [(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version + 1), 0xFF, 0xFF]);
        rig.Tick();
        Assert.Equal(RejectReason.VersionMismatch, rig.Net[other].Reject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Password)]
    public void ContentMustMatchToJoinAndOtherContentIsRefusedBeforeAnyChallenge(string? password)
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4, password);

        // The same version with other content: told why and closed, in the tick the hello is read. With a
        // password it is never asked for it, since the right one would not let this client play here either.
        var modded = rig.Net.Connect(Stranger);
        rig.Hello(modded, "Modded", content: OtherContent.Value);
        rig.Tick();
        Assert.Equal(1, rig.Net[modded].Rejects);
        Assert.Equal(RejectReason.ContentMismatch, rig.Net[modded].Reject);
        Assert.Equal(0, rig.Net[modded].RetryAfterSeconds);
        Assert.True(rig.Net[modded].ClosedByServer);
        Assert.Equal(0, rig.Net[modded].Challenges);
        Assert.Equal(0, rig.Net[modded].Welcomes);
        Assert.Equal(0, rig.Server.Status.PlayerCount);

        // It is not a wrong password, however often it happens: nothing is counted against the address.
        for (var i = 0; i < AttemptLimiter.FreeFailures + 3; i++)
        {
            var again = rig.Join(Stranger, password, "Modded", OtherContent.Value);
            Assert.Equal(RejectReason.ContentMismatch, rig.Net[again].Reject);
            Assert.Equal(0, rig.Net[again].Challenges);
        }

        // The server's own content joins, from that same address, and goes through the password as before.
        var stock = rig.Join(Stranger, password, "Stock");
        Assert.Equal(password is null ? 0 : 1, rig.Net[stock].Challenges);
        Assert.Equal(1, rig.Net[stock].Welcomes);
        Assert.Equal(0, rig.Net[stock].Rejects);
        Assert.Equal(1, rig.Server.Status.PlayerCount);
    }

    [Fact]
    public void ContentIsComparedOnlyAfterEverythingElseThatTurnsAHelloAway()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 1, Password);
        var version = BitConverter.GetBytes(ProtocolInfo.Version);
        var identity = new byte[ContentIdentity.WireSize];

        // Another version is told so, whatever content it has: a build that differs cannot be asked about it.
        var otherVersion = rig.Net.Connect(Stranger);
        rig.Hello(otherVersion, version: ProtocolInfo.Version + 1, content: OtherContent.Value);

        // A hello as the last version sent it, a version and a name and no identity, is another version too.
        var lastVersion = rig.Net.Connect(Stranger);
        rig.Net.Deliver(lastVersion, [(byte)MessageId.Hello, .. BitConverter.GetBytes(ProtocolInfo.Version - 1), 3, (byte)'O', (byte)'l', (byte)'d']);

        // This version's hello without the identity, or with part of one, is no hello.
        var missing = rig.Net.Connect(Stranger);
        rig.Net.Deliver(missing, [(byte)MessageId.Hello, .. version, 3, (byte)'C', (byte)'u', (byte)'t']);
        var partial = rig.Net.Connect(Stranger);
        rig.Net.Deliver(partial, [(byte)MessageId.Hello, .. version, 3, (byte)'C', (byte)'u', (byte)'t', .. identity.AsSpan(1)]);

        // A name the server does not take is a bad handshake, with an identity behind it that is not the server's.
        var badName = rig.Net.Connect(Stranger);
        rig.Net.Deliver(badName, [(byte)MessageId.Hello, .. version, 200, .. new byte[200], .. identity]);

        // And a whole hello whose identity is simply another one.
        var zero = rig.Net.Connect(Stranger);
        rig.Net.Deliver(zero, [(byte)MessageId.Hello, .. version, 3, (byte)'C', (byte)'u', (byte)'t', .. identity]);

        rig.Tick();
        Assert.Equal(RejectReason.VersionMismatch, rig.Net[otherVersion].Reject);
        Assert.Equal(RejectReason.VersionMismatch, rig.Net[lastVersion].Reject);
        Assert.Equal(RejectReason.BadHandshake, rig.Net[missing].Reject);
        Assert.Equal(RejectReason.BadHandshake, rig.Net[partial].Reject);
        Assert.Equal(RejectReason.BadHandshake, rig.Net[badName].Reject);
        Assert.Equal(RejectReason.ContentMismatch, rig.Net[zero].Reject);
        Assert.All(new[] { otherVersion, lastVersion, missing, partial, badName, zero }, peer => Assert.True(rig.Net[peer].ClosedByServer));
        Assert.All(new[] { otherVersion, lastVersion, missing, partial, badName, zero }, peer => Assert.Equal(0, rig.Net[peer].Challenges));

        // An address that is locked out is told to wait, as before: its content is not looked at.
        for (var i = 0; i < AttemptLimiter.FreeFailures; i++)
        {
            var guesser = rig.Net.Connect(Attacker);
            rig.Hello(guesser);
            rig.Tick();
            rig.Proof(guesser, "wrong");
            rig.Tick();
            Assert.Equal(RejectReason.WrongPassword, rig.Net[guesser].Reject);
        }

        var locked = rig.Net.Connect(Attacker);
        rig.Hello(locked, content: OtherContent.Value);
        rig.Tick();
        Assert.Equal(RejectReason.TooManyAttempts, rig.Net[locked].Reject);
        Assert.InRange(rig.Net[locked].RetryAfterSeconds, 1, 2);

        // And a full server says it is full, as before.
        var player = rig.Join(Household, Password, "Player");
        Assert.Equal(1, rig.Net[player].Welcomes);
        var late = rig.Net.Connect(Stranger);
        rig.Hello(late, content: OtherContent.Value);
        rig.Tick();
        Assert.Equal(RejectReason.ServerFull, rig.Net[late].Reject);
    }

    [Fact]
    public void AFloodOfJunkNeitherHoldsUpTheTickNorCostsItAnyMemory()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4, Password);
        var limits = ConnectionLimits.Default;
        var player = rig.Join(Household, Password, "Player");
        Assert.Equal(1, rig.Net[player].Welcomes);

        // Junk of every size, made once: the flood itself allocates nothing, so what is measured is the server.
        var random = new Random(99);
        var junk = new byte[64][];
        for (var i = 0; i < junk.Length; i++)
        {
            junk[i] = new byte[random.Next(0, 300)];
            random.NextBytes(junk[i]);
        }

        var hello = new byte[HelloMessage.MaxWireSize];
        var helloWriter = new NetWriter(hello);
        new HelloMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = "Flood", Content = Content.Identity }.Write(ref helloWriter);
        var helloLength = helloWriter.Length;

        // And the hello of a client with other content, which is turned away for that.
        var otherHello = new byte[HelloMessage.MaxWireSize];
        var otherWriter = new NetWriter(otherHello);
        new HelloMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = "Modded", Content = OtherContent.Value.Identity }.Write(ref otherWriter);
        var otherLength = otherWriter.Length;

        var sequence = 0u;
        var pings = 0;
        var silent = -1;
        var silentOpenedAt = 0L;
        var silentClosedAt = 0L;
        var stillOpenAfterJunk = 0;
        var previous = rig.Net.Count;

        void FloodTick(int tick)
        {
            // What was opened a tick ago and given a slot now sends junk, an empty message, a good hello or the
            // hello of a client with other content, followed by junk; all of it many times over.
            for (var peer = previous; peer < rig.Net.Count; peer++)
            {
                if (peer == silent || !rig.Net[peer].Open)
                    continue;
                if (peer % 3 == 0)
                    rig.Net.Deliver(peer, hello, helloLength);
                else if (peer % 3 == 1)
                    rig.Net.Deliver(peer, otherHello, otherLength);
                for (var i = 0; i < 20; i++)
                    rig.Net.Deliver(peer, junk[(peer + i) % junk.Length], (NetChannel)(i % 2));
            }

            // And four hundred packets more from connections the server closed long ago.
            for (var i = 0; i < 400; i++)
                rig.Net.Deliver(1 + i % 50, junk[(tick + i) % junk.Length], (NetChannel)(i % 2));

            // A hundred new connections: two from addresses that have never been seen, which are given a slot,
            // and the rest from one address that has long spent its allowance.
            previous = rig.Net.Count;
            for (var i = 0; i < 2; i++)
                rig.Net.Connect(Attacker + 1000 + (ulong)(tick * 2 + i));
            for (var i = 0; i < 98; i++)
                rig.Net.Connect(Attacker);

            // The player plays on: an input every tick, a probe twice a second.
            rig.Input(player, ++sequence);
            if (tick % 30 == 0)
            {
                rig.Ping(player);
                pings++;
            }

            rig.Tick();
            if (silent >= 0 && silentClosedAt == 0 && !rig.Net[silent].Open)
                silentClosedAt = rig.Clock.GetTimestamp();
            for (var peer = previous - 100; peer < previous; peer++)
                stillOpenAfterJunk += peer >= 0 && peer != silent && peer != player && rig.Net[peer].Open ? 1 : 0;
        }

        // Five seconds to let every path be compiled and the first lines about refusals be written. Half way,
        // one connection that says nothing at all, to see its time run out in the middle of what is measured.
        for (var tick = 0; tick < 310; tick++)
        {
            if (tick == 150)
            {
                silent = rig.Net.Connect(Stranger);
                silentOpenedAt = rig.Clock.GetTimestamp();
            }

            FloodTick(tick);
        }

        var delivered = rig.Net.Delivered;
        var answered = rig.Net[player].Pongs;
        var firstMeasured = rig.Net.Count;
        var logged = CountingLogs.OnThisThread;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 310; tick < 510; tick++)
            FloodTick(tick);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Among what was refused while memory was counted were clients with other content, told so each time.
        var otherContent = 0;
        var challenged = 0;
        for (var peer = firstMeasured; peer < rig.Net.Count; peer++)
        {
            otherContent += rig.Net[peer].Reject == RejectReason.ContentMismatch ? 1 : 0;
            challenged += rig.Net[peer].Challenges;
        }

        Assert.InRange(otherContent, 100, 200);
        Assert.InRange(challenged, 100, 200);

        // More than four hundred packets and connections a tick went through the server, and it allocated nothing.
        Assert.True(rig.Net.Delivered - delivered > 200 * 400, $"the flood delivered {rig.Net.Delivered - delivered} packets");
        Assert.Equal(0, allocated);
        Assert.Equal(logged, CountingLogs.OnThisThread);

        // Every junk sender was closed in the tick its first packet was read.
        Assert.Equal(0, stillOpenAfterJunk);

        // The silent connection ended when its time was up, to the tick, not when the flood let it.
        Assert.InRange(silentClosedAt - silentOpenedAt, (long)(limits.HandshakeSeconds * ManualTime.Frequency), (long)(limits.HandshakeSeconds * ManualTime.Frequency) + ManualTime.Tick);

        // The player was never touched: still connected, every input simulated, every probe answered.
        Assert.True(rig.Net[player].Open);
        Assert.Equal(sequence, rig.Net[player].AckedInput);
        Assert.Equal(pings, rig.Net[player].Pongs);
        Assert.True(rig.Net[player].Pongs > answered);
        Assert.InRange(rig.Net[player].Snapshots, 510, 512);
        Assert.Equal(1, rig.Server.Status.PlayerCount);
    }

    [Fact]
    public void WhenInputFillsTheBudgetTheRestWaitsATickAndNothingOfAPlayersIsLost()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4);
        var limits = ConnectionLimits.Default;
        var player = rig.Join(Household, name: "Player");
        var closed = rig.Net.Connect(Attacker);
        rig.Net.Deliver(closed, []);
        var silent = rig.Net.Connect(Stranger);
        rig.Tick();
        Assert.True(rig.Net[closed].ClosedByServer);
        var opened = rig.Clock.GetTimestamp();

        // Far more than one tick's budget arrives at once, all of it for nobody, and behind it a player's input.
        var perTick = ServerHost.SparePacketsPerTick + 4 * ServerHost.PlayerMessagesPerTick;
        var junk = new byte[40];
        for (var i = 0; i < perTick * 4 + 100; i++)
            rig.Net.Deliver(closed, junk);
        // Target the fifth processing tick, when the bounded receive loop can reach this packet.
        var input = new byte[InputMessage.MaxWireSize];
        var writer = new NetWriter(input);
        InputMessage.Write(ref writer, [new InputRecord
        {
            TargetTick = rig.Server.Status.Tick + 4,
            Sequence = 1,
            Input = InputTimeline.Fallback,
        }], 1);
        rig.Net.Deliver(player, input, writer.Length, NetChannel.Unreliable);

        // Each tick reads its budget and no more, and goes on to simulate: the player hears from the server
        // every tick, and the queued input is read in time for its target interval, without dropping packets.
        for (var tick = 1; tick <= 4; tick++)
        {
            var before = rig.Net.Delivered;
            rig.Tick();
            Assert.Equal(perTick, rig.Net.Delivered - before);
            Assert.Equal(0u, rig.Net[player].AckedInput);
            Assert.Equal(1 + 1 + tick, rig.Net[player].Snapshots);
        }

        var beforeLast = rig.Net.Delivered;
        rig.Tick();
        Assert.Equal(101, rig.Net.Delivered - beforeLast);
        Assert.Equal(1u, rig.Net[player].AckedInput);

        // The same again, for longer than a handshake may take: the silent connection's time still runs out on
        // the clock, with the queue never empty.
        var ticks = (int)(limits.HandshakeSeconds * 60) + 2;
        for (var i = 0; i < perTick * ticks; i++)
            rig.Net.Deliver(closed, junk);
        var closedAt = 0L;
        for (var tick = 0; tick < ticks - 1; tick++)
        {
            rig.Tick();
            if (closedAt == 0 && !rig.Net[silent].Open)
                closedAt = rig.Clock.GetTimestamp();
        }

        Assert.InRange(closedAt - opened, (long)(limits.HandshakeSeconds * ManualTime.Frequency), (long)(limits.HandshakeSeconds * ManualTime.Frequency) + ManualTime.Tick);
        Assert.True(rig.Net[player].Open);
    }

    [Fact]
    public void APlayerSendingFarMoreThanAClientDoesIsCutBackThenRemovedAndABurstIsForgiven()
    {
        using var rig = new ScriptedServer(Content, maxPlayers: 4);
        var honest = rig.Join(Household, name: "Honest");
        var flooder = rig.Join(Attacker, name: "Flooder");
        Assert.Equal(2, rig.Server.Status.PlayerCount);
        var ping = new byte[9];
        ping[0] = (byte)MessageId.Ping;

        // After a hitch a client's packets can arrive together. What is over the allowance for one tick is
        // dropped; the player stays.
        for (var i = 0; i < 200; i++)
            rig.Net.Deliver(honest, ping, NetChannel.Unreliable);
        rig.Tick();
        Assert.Equal(ServerHost.PlayerMessagesPerTick, rig.Net[honest].Pongs);
        rig.Tick(120);
        Assert.True(rig.Net[honest].Open);

        // Five hundred messages every tick is not a hitch. Only the allowance is read, and within a second the
        // player is removed.
        var ticks = 0;
        while (rig.Net[flooder].Open && ticks < 120)
        {
            for (var i = 0; i < 500; i++)
                rig.Net.Deliver(flooder, ping, NetChannel.Unreliable);
            rig.Tick();
            ticks++;
            Assert.InRange(rig.Net[flooder].Pongs, 0, ticks * ServerHost.PlayerMessagesPerTick);
        }

        Assert.True(rig.Net[flooder].ClosedByServer);
        Assert.InRange(ticks, 10, 60);
        Assert.Equal(1, rig.Server.Status.PlayerCount);

        // One oversized message is over the allowance by itself and is not read either.
        var pongs = rig.Net[honest].Pongs;
        var huge = new byte[ServerHost.PlayerBytesPerTick + 1];
        huge[0] = (byte)MessageId.Ping;
        rig.Net.Deliver(honest, huge, NetChannel.Unreliable);
        rig.Tick();
        Assert.Equal(pongs, rig.Net[honest].Pongs);
        Assert.True(rig.Net[honest].Open);
    }

    [Fact]
    public void TheServerKeepsOnlyTheKeyAndClearsItWhenDisposed()
    {
        var rig = new ScriptedServer(Content, maxPlayers: 2, Password);
        var fields = typeof(ServerHost).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

        // Nothing the server holds on to is, or contains, its configuration or the password as text.
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ServerConfig));
        Assert.DoesNotContain(fields, f => f.GetValue(rig.Server) is string text && text.Contains(Password));

        var key = (byte[])fields.Single(f => f.Name == "_passwordKey").GetValue(rig.Server)!;
        Assert.Contains(key, b => b != 0);
        rig.Dispose();
        Assert.All(key, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AClientSessionLetsGoOfThePasswordOnceItHasAnsweredOrEnded()
    {
        var field = typeof(ClientSession).GetField("_password", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // A session that proves itself: the password is gone once the proof is sent.
        var port = TestNet.Port(20);
        using var server = new ServerHost(new ServerConfig { Visibility = ServerVisibility.Unlisted, Port = port, Password = Password }, Content);
        server.Start();
        using (var right = GameSession.Join("127.0.0.1", port, Content, "Right", Password))
        {
            Assert.Equal(Password, field.GetValue(right.Client));
            TestNet.WaitFor(right, s => s.State == SessionState.Active);
            Assert.Null(field.GetValue(right.Client));
        }

        // One that is refused, and one that never reaches a server: gone when the session has ended.
        using (var wrong = GameSession.Join("127.0.0.1", port, Content, "Wrong", "not it"))
        {
            TestNet.WaitFor(wrong, s => s.State == SessionState.Ended);
            Assert.Null(field.GetValue(wrong.Client));
        }

        using var link = new LoopbackLink();
        using var alone = new ClientSession(link.Client, Content, "Alone", Password);
        alone.Start();
        alone.RequestLeave();
        TestNet.WaitUntil(() => alone.AcquireSnapshot().State == SessionState.Ended, "the session ending");
        Assert.Null(field.GetValue(alone));
    }
}

/// <summary>The receive budget of the transports themselves.</summary>
public sealed class ReceiveBudgetTests
{
    private sealed class Inbox : ITransportHandler
    {
        public readonly List<int> Numbers = [];
        public bool Connected;

        public void OnConnected(ITransport transport, int peer) => Connected = true;

        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) { }

        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) => Numbers.Add(BitConverter.ToInt32(payload));
    }

    [Fact]
    public void AUdpPollTakesInNoMoreThanItsBudgetAndTheRestWaitsInOrder()
    {
        var port = TestNet.Port(21);
        using var server = RakNetTransport.Listen(port, 4);
        using var client = RakNetTransport.Connect("127.0.0.1", port);
        var serverInbox = new Inbox();
        var clientInbox = new Inbox();
        TestNet.WaitUntil(
            () =>
            {
                server.Poll(serverInbox);
                client.Poll(clientInbox);
                return serverInbox.Connected && clientInbox.Connected;
            },
            "the connection"
        );

        // A hundred messages of sixty bytes, numbered, and time for all of them to arrive.
        var message = new byte[60];
        for (var i = 0; i < 100; i++)
        {
            BitConverter.TryWriteBytes(message, i);
            Assert.True(client.Send(0, NetChannel.Reliable, message));
        }

        Thread.Sleep(500);

        // Sixteen packets a poll: sixteen are delivered, the poll says there is more, and nothing is lost or
        // out of order.
        Assert.False(server.Poll(serverInbox, new ReceiveBudget(16, int.MaxValue)));
        Assert.Equal(Enumerable.Range(0, 16), serverInbox.Numbers);
        Assert.False(server.Poll(serverInbox, new ReceiveBudget(16, int.MaxValue)));
        Assert.Equal(Enumerable.Range(0, 32), serverInbox.Numbers);

        // A hundred bytes a poll is two of these: the second takes the poll past its budget, and it stops.
        Assert.False(server.Poll(serverInbox, new ReceiveBudget(int.MaxValue, 100)));
        Assert.Equal(Enumerable.Range(0, 34), serverInbox.Numbers);

        // Without a limit the rest comes in one poll, which says the queue is empty.
        Assert.True(server.Poll(serverInbox, ReceiveBudget.Unlimited));
        Assert.Equal(Enumerable.Range(0, 100), serverInbox.Numbers);
    }

    [Fact]
    public void AnInProcessPollKeepsToItsBudgetToo()
    {
        using var link = new LoopbackLink();
        var serverInbox = new Inbox();
        var clientInbox = new Inbox();
        link.Server.Poll(serverInbox);
        link.Client.Poll(clientInbox);
        link.Server.Poll(serverInbox);
        Assert.True(serverInbox.Connected && clientInbox.Connected);

        var message = new byte[60];
        for (var i = 0; i < 40; i++)
        {
            BitConverter.TryWriteBytes(message, i);
            Assert.True(link.Client.Send(0, NetChannel.Reliable, message));
        }

        Assert.False(link.Server.Poll(serverInbox, new ReceiveBudget(10, int.MaxValue)));
        Assert.Equal(Enumerable.Range(0, 10), serverInbox.Numbers);
        Assert.False(link.Server.Poll(serverInbox, new ReceiveBudget(int.MaxValue, 100)));
        Assert.Equal(Enumerable.Range(0, 12), serverInbox.Numbers);
        Assert.True(link.Server.Poll(serverInbox, ReceiveBudget.Unlimited));
        Assert.Equal(Enumerable.Range(0, 40), serverInbox.Numbers);
    }
}

/// <summary>Floods over real UDP against a server running on its own thread, with real clients beside them.</summary>
public sealed class FloodOverUdpTests
{
    private static readonly GameContent Content = GameContent.Load();
    private const string Password = "open sesame";

    /// <summary>
    /// An address of this machine other than the loopback one, or null when it has none. A connection made to it
    /// comes from it, so a test has two addresses to connect from without a second machine.
    /// </summary>
    private static string? SecondAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    return address.Address.ToString();
            }
        }

        return null;
    }

    private static string RequireSecondAddress()
    {
        var address = SecondAddress();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            Assert.NotNull(address);
        Assert.SkipWhen(address is null, "Requires an active non-loopback IPv4 address (mandatory in CI).");
        return address!;
    }

    /// <summary>Opens connections on a few threads for as long as it lives, each sending junk or nothing, and never a hello.</summary>
    private sealed class Flood : IDisposable
    {
        private readonly Thread[] _threads;
        private volatile bool _stop;
        private int _opened;

        public Flood(string address, ushort port, int threads)
        {
            _threads = new Thread[threads];
            for (var i = 0; i < threads; i++)
            {
                var seed = i;
                _threads[i] = new Thread(() => Run(address, port, seed)) { IsBackground = true, Name = "Flood" };
                _threads[i].Start();
            }
        }

        public int Opened => Volatile.Read(ref _opened);

        public void Dispose()
        {
            _stop = true;
            foreach (var thread in _threads)
                thread.Join();
        }

        private void Run(string address, ushort port, int seed)
        {
            var random = new Random(seed);
            var junk = new byte[200];
            var sink = new Sink();
            while (!_stop)
            {
                using var transport = RakNetTransport.Connect(address, port);
                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                while (!sink.Connected && !sink.Closed && Stopwatch.GetTimestamp() < deadline && !_stop)
                {
                    transport.Poll(sink);
                    Thread.Sleep(1);
                }

                if (sink.Connected)
                {
                    Interlocked.Increment(ref _opened);

                    // One in three says nothing and holds on; the others send junk as fast as they can.
                    var silent = random.Next(3) == 0;
                    var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / (silent ? 2 : 20);
                    while (!sink.Closed && Stopwatch.GetTimestamp() < until && !_stop)
                    {
                        if (!silent)
                        {
                            for (var i = 0; i < 50; i++)
                            {
                                random.NextBytes(junk);
                                transport.Send(0, (NetChannel)(i % 2), junk.AsSpan(0, random.Next(0, junk.Length)));
                            }
                        }

                        transport.Poll(sink);
                        Thread.Sleep(1);
                    }
                }

                sink.Connected = sink.Closed = false;
            }
        }

        private sealed class Sink : ITransportHandler
        {
            public bool Connected;
            public bool Closed;

            public void OnConnected(ITransport transport, int peer) => Connected = true;

            public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => Closed = true;

            public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) { }
        }
    }

    [Fact]
    public void HandshakesLeftHalfOpenDoNotShutTheListener()
    {
        // A small server: four players and four pending connections are all it holds itself.
        var port = TestNet.Port(24);
        var limits = new ConnectionLimits { PendingPerAddress = 4, PendingTotal = 4 };
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 4, Visibility = ServerVisibility.Unlisted, Port = port, Limits = limits }, Content);
        server.Start();

        // The second datagram of RakNet's handshake, sent by hand and never followed up: its marker, the
        // address being connected to, a packet size and a made-up identity. RakNet answers each with a slot in
        // its own table, which it holds for ten seconds; the game is told nothing. Twenty of them, each from
        // its own port and far enough apart for the listener's limit of one connection per address every
        // 100 ms: more than the server's own connections, so without room to spare the table would be full.
        byte[] marker = [0x00, 0xFF, 0xFF, 0x00, 0xFE, 0xFE, 0xFE, 0xFE, 0xFD, 0xFD, 0xFD, 0xFD, 0x12, 0x34, 0x56, 0x78];
        var target = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
        var sockets = new List<UdpClient>();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var socket = new UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                sockets.Add(socket);
                socket.Client.ReceiveTimeout = 2000;
                byte[] request = [0x07, .. marker, 4, 0x80, 0xFF, 0xFF, 0xFE, (byte)(port >> 8), (byte)port, 0x05, 0xD4, 9, 9, 9, 9, 9, 9, 9, (byte)i];
                socket.Send(request, request.Length, target);
                System.Net.IPEndPoint? from = null;
                Assert.Equal(0x08, socket.Receive(ref from)[0]);
                Thread.Sleep(110);
            }

            // All twenty are still held, and a player gets in.
            using var player = GameSession.Join("127.0.0.1", port, Content, "Player");
            TestNet.WaitFor(player, s => s.State == SessionState.Active);
            Assert.Equal(1, server.Status.PlayerCount);
        }
        finally
        {
            foreach (var socket in sockets)
                socket.Dispose();
        }
    }

    [Fact]
    public void SixPlayersBehindOneAddressJoinAtTheSameMoment()
    {
        var port = TestNet.Port(22);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 8, Visibility = ServerVisibility.Unlisted, Port = port, Password = Password }, Content);
        server.Start();

        // Non-loopback exercises RakNet's address cooldown; exact retry timing is tested with ConnectRetry.
        var address = RequireSecondAddress();
        var sessions = new List<GameSession>();
        try
        {
            for (var i = 0; i < 6; i++)
                sessions.Add(GameSession.Join(address, port, Content, $"Player {i}", Password));
            foreach (var session in sessions)
                TestNet.WaitFor(session, s => s.State == SessionState.Active);
            Assert.Equal(6, server.Status.PlayerCount);
            Assert.Null(server.Fault);
            // Record real retries without requiring a particular OS scheduling order for the assertion.
            var retries = 0;
            foreach (var session in sessions)
            {
                var transport = typeof(ClientSession).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Client);
                retries += ((ConnectRetry)typeof(RakNetTransport).GetField("_retry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!).Count;
            }
            Console.WriteLine($"Six non-loopback joins to {address}: {retries} RakNet cooldown retries.");
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    [Fact]
    public void AFloodLeavesAnExistingPlayerPlayingAndTheServerOnTime()
    {
        // Existing players must keep playing even when the flood shares their address.
        var second = SecondAddress();
        var port = TestNet.Port(23);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 4, Visibility = ServerVisibility.Unlisted, Port = port, Password = Password }, Content);
        server.Start();

        using var player = GameSession.Join("127.0.0.1", port, Content, "Player", Password);
        TestNet.WaitFor(player, s => s.State == SessionState.Active && s.RoundTripMs >= 0);

        var started = Stopwatch.GetTimestamp();
        var ticksAtStart = server.Status.Tick;
        using (var flood = new Flood(second ?? "127.0.0.1", port, threads: 6))
        {
            // Through three seconds of it the player's view of the server keeps moving, tick after tick.
            var last = player.Client.AcquireSnapshot().ServerTick;
            for (var i = 0; i < 6; i++)
            {
                Thread.Sleep(500);
                var snapshot = player.Client.AcquireSnapshot();
                Assert.Equal(SessionState.Active, snapshot.State);
                Assert.True(snapshot.ServerTick > last + 15, $"the player's server tick went from {last} to {snapshot.ServerTick} in half a second");
                Assert.InRange(snapshot.RoundTripMs, 0, 250);
                last = snapshot.ServerTick;
            }

            Assert.True(flood.Opened > 5, $"the flood opened {flood.Opened} connections");
        }

        // The simulation kept its pace, and nobody was let in who should not have been.
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var ticks = server.Status.Tick - ticksAtStart;
        Assert.True(ticks > seconds * 50, $"{ticks} ticks in {seconds:F1} s");
        Assert.Equal(1, server.Status.PlayerCount);
        Assert.True(server.IsRunning);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void AFloodFromAnotherAddressStillAllowsANewcomer()
    {
        var address = RequireSecondAddress();
        var port = TestNet.Port(25);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 4, Visibility = ServerVisibility.Unlisted, Port = port, Password = Password }, Content);
        server.Start();
        using var flood = new Flood(address, port, threads: 6);
        TestNet.WaitUntil(() => flood.Opened > 5, "flood connections before newcomer");
        using var newcomer = GameSession.Join("127.0.0.1", port, Content, "Newcomer", Password);
        TestNet.WaitFor(newcomer, s => s.State == SessionState.Active);
        Assert.Null(server.Fault);
        Assert.Equal(1, server.Status.PlayerCount);
    }

}
