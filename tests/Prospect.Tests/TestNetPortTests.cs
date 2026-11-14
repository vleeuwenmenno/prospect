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
}
