using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Prospect.Tests;

/// <summary>Rules for the UDP ports tests listen on, which a flaky bind once broke.</summary>
public sealed class TestNetPortTests
{
    [Fact]
    public void PortsLieBelowTheEphemeralRangeAndAreStable()
    {
        for (var slot = 0; slot < 32; slot++)
        {
            var port = TestNet.Port(slot);
            Assert.InRange(port, TestNet.PortBase, 32767);
            Assert.Equal(port, TestNet.Port(slot));
        }
    }

    [Fact]
    public void SlotsInOneRunNeverShareAPort()
    {
        var ports = Enumerable.Range(0, 32).Select(s => TestNet.Port(s)).ToArray();
        Assert.Equal(ports.Length, ports.Distinct().Count());
    }

    [Fact]
    public void AHeldPortIsNotFree()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Any, 0));
        var held = (ushort)((IPEndPoint)holder.LocalEndPoint!).Port;
        Assert.False(TestNet.IsFree(held));
    }

    [Fact]
    public void ProcessesClaimDisjointBlocksEvenFromTheSameStart()
    {
        // Two simulated processes: separate claims over one lock directory, with the same starting point.
        var dir = Path.Combine(Path.GetTempPath(), "prospect-test-ports-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new PortBlocks(dir, 8);
            using var second = new PortBlocks(dir, 8);
            var claimed = new HashSet<int> { first.Main };
            Assert.True(claimed.Add(second.Main));
            for (var i = 0; i < 3; i++)
            {
                Assert.True(claimed.Add(first.ClaimAnother()));
                Assert.True(claimed.Add(second.ClaimAnother()));
            }

            // 8 blocks, all held: a ninth claim fails rather than sharing.
            Assert.Equal(8, claimed.Count);
            Assert.Throws<IOException>(() => first.ClaimAnother());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ABlockIsFreeAgainWhenItsHolderEnds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prospect-test-ports-" + Guid.NewGuid().ToString("N"));
        try
        {
            int block;
            using (var gone = new PortBlocks(dir, 1))
                block = gone.Main;
            using var later = new PortBlocks(dir, 1);
            Assert.Equal(block, later.Main);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
