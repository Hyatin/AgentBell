using System.Net;

namespace AgentBell.Desktop.Tests;

public sealed class LanAddressResolverTests
{
    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.42.0.8", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.15.255.254", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.20", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", false)]
    public void IsPrivateIpv4_RecognizesOnlyRfc1918(string value, bool expected)
    {
        Assert.Equal(expected, LanAddressResolver.IsPrivateIpv4(IPAddress.Parse(value)));
    }

    [Fact]
    public void Resolve_PrefersGatewayPhysicalAdapterThenStableOrder()
    {
        var resolver = new LanAddressResolver();
        var result = resolver.Resolve(
        [
            Candidate("192.168.1.30", gateway: true, preferred: true, order: 20),
            Candidate("10.0.0.20", gateway: true, preferred: true, order: 10),
            Candidate("192.168.1.10", gateway: false, preferred: true, order: 1),
        ]);

        Assert.Equal(IPAddress.Parse("10.0.0.20"), result);
    }

    [Fact]
    public void Resolve_UsesAddressStringAsDeterministicFinalFallback()
    {
        var resolver = new LanAddressResolver();
        var candidates = new[]
        {
            Candidate("192.168.1.20", gateway: true, preferred: true, order: 10),
            Candidate("192.168.1.10", gateway: true, preferred: true, order: 10),
        };

        Assert.Equal(IPAddress.Parse("192.168.1.10"), resolver.Resolve(candidates));
        Assert.Equal(IPAddress.Parse("192.168.1.10"), resolver.Resolve(candidates.Reverse()));
    }

    [Fact]
    public void Resolve_ExcludesLoopbackApipaDownTunnelAndVirtualCandidates()
    {
        var resolver = new LanAddressResolver();
        var result = resolver.Resolve(
        [
            Candidate("192.168.1.2", gateway: true, preferred: true, order: 1) with { IsUp = false },
            Candidate("192.168.1.3", gateway: true, preferred: true, order: 1) with { IsTunnel = true },
            Candidate("192.168.1.4", gateway: true, preferred: true, order: 1) with { IsVirtual = true },
            Candidate("127.0.0.1", gateway: true, preferred: true, order: 1),
            Candidate("169.254.1.2", gateway: true, preferred: true, order: 1),
            Candidate("10.0.0.9", gateway: false, preferred: false, order: 99),
        ]);

        Assert.Equal(IPAddress.Parse("10.0.0.9"), result);
    }

    [Fact]
    public void Resolve_NoValidCandidate_ReturnsNull()
    {
        var resolver = new LanAddressResolver();

        Assert.Null(resolver.Resolve([]));
        Assert.Null(resolver.Resolve(
            [Candidate("203.0.113.1", gateway: true, preferred: true, order: 1)]));
    }

    private static LanAddressCandidate Candidate(
        string address,
        bool gateway,
        bool preferred,
        int order) =>
        new()
        {
            Address = IPAddress.Parse(address),
            IsUp = true,
            HasDefaultGateway = gateway,
            IsPreferredAdapterType = preferred,
            InterfaceOrder = order,
        };
}
