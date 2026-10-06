using System.Net;
using Campfire.Tests.Integrations.Support;
using Campfire.Web.Net;

namespace Campfire.Tests.Integrations;

/// <summary>Port of test/lib/restricted_http/private_network_guard_test.rb.</summary>
public sealed class PrivateNetworkGuardTests
{
    [Theory]
    // "This" network (RFC 1700), loopback, RFC 1918, link-local / cloud metadata
    [InlineData("0.0.0.0"), InlineData("0.255.255.255")]
    [InlineData("127.0.0.0"), InlineData("127.0.0.1"), InlineData("127.255.255.255")]
    [InlineData("10.0.0.0"), InlineData("10.255.255.255"), InlineData("172.16.0.0"), InlineData("172.31.255.255"), InlineData("192.168.0.0"), InlineData("192.168.255.255")]
    [InlineData("169.254.0.1"), InlineData("169.254.169.254"), InlineData("169.254.255.255")]
    // Carrier-grade NAT, documentation, benchmarking, multicast, reserved, broadcast
    [InlineData("100.64.0.1"), InlineData("100.127.255.255"), InlineData("192.0.2.1"), InlineData("198.18.0.1"), InlineData("203.0.113.9")]
    [InlineData("224.0.0.1"), InlineData("240.0.0.1"), InlineData("255.255.255.255")]
    // IPv4-mapped, even for public addresses (DNS never answers that way)
    [InlineData("::ffff:192.168.1.1"), InlineData("::ffff:10.0.0.1"), InlineData("::ffff:172.16.0.1"), InlineData("::ffff:169.254.169.254"), InlineData("::ffff:93.184.216.34"), InlineData("::ffff:c0a8:0101")]
    // IPv4-compatible
    [InlineData("::192.168.1.1"), InlineData("::10.0.0.1"), InlineData("::169.254.169.254"), InlineData("::93.184.216.34")]
    // NAT64 carrying private addresses, and the whole local-use NAT64 block
    [InlineData("64:ff9b::a9fe:a9fe"), InlineData("64:ff9b::a00:5"), InlineData("64:ff9b:1::a00:1"), InlineData("64:ff9b:1::808:808"), InlineData("64:ff9b:1:ffff::1")]
    // SIIT
    [InlineData("::ffff:0:169.254.169.254"), InlineData("::ffff:0:a9fe:a9fe"), InlineData("::ffff:0:127.0.0.1"), InlineData("::ffff:0:192.168.0.1")]
    // 6to4 and Teredo, loopback, ULA, link-local, multicast, documentation, benchmarking
    [InlineData("2002:a9fe:a9fe::"), InlineData("2001::1"), InlineData("::1"), InlineData("::"), InlineData("fd00:ec2::254"), InlineData("fe80::1")]
    [InlineData("ff02::1"), InlineData("2001:db8::1"), InlineData("2001:2::1"), InlineData("3fff::1")]
    // Garbage
    [InlineData("not-an-ip"), InlineData("")]
    public void Classifies_non_public_addresses_as_private(string address) =>
        Assert.True(PrivateNetworkGuard.IsPrivateIp(address), $"Expected {address} to be classified as private");

    [Theory]
    [InlineData("93.184.216.34"), InlineData("8.8.8.8"), InlineData("142.250.185.206")]
    [InlineData("64:ff9b::808:808")] // DNS64 synthesizes these for public sites on IPv6-only hosts
    [InlineData("2606:4700:4700::1111"), InlineData("2001:4860:4860::8888")]
    public void Classifies_public_addresses_as_public(string address) =>
        Assert.False(PrivateNetworkGuard.IsPrivateIp(address), $"Expected {address} to be classified as public");

    [Fact]
    public async Task Resolve_raises_a_violation_for_a_private_hostname()
    {
        var guard = new PrivateNetworkGuard(new FakeHostResolver().Answer("private.example.com", "192.168.1.1"));
        await Assert.ThrowsAsync<PrivateNetworkViolationException>(() => guard.ResolvePublicAsync("private.example.com"));
    }

    [Fact]
    public async Task Resolve_returns_the_address_of_a_public_hostname()
    {
        var guard = new PrivateNetworkGuard(new FakeHostResolver().Answer("example.com", "93.184.216.34"));
        Assert.Equal(IPAddress.Parse("93.184.216.34"), await guard.ResolvePublicAsync("example.com"));
    }

    [Fact]
    public async Task Resolve_picks_a_public_address_from_a_mixed_answer()
    {
        var guard = new PrivateNetworkGuard(new FakeHostResolver().Answer("mixed.example.com", "10.0.0.1", "93.184.216.34"));
        Assert.Equal(IPAddress.Parse("93.184.216.34"), await guard.ResolvePublicAsync("mixed.example.com"));
    }

    [Fact]
    public async Task Resolve_raises_unresolvable_not_violation_when_the_host_resolves_to_nothing()
    {
        var guard = new PrivateNetworkGuard(new FakeHostResolver());
        await Assert.ThrowsAsync<UnresolvableHostException>(() => guard.ResolvePublicAsync("nxdomain.example.com"));
    }

    [Fact]
    public async Task Address_literals_are_judged_without_resolving()
    {
        var resolver = new FakeHostResolver();
        var guard = new PrivateNetworkGuard(resolver);

        await Assert.ThrowsAsync<PrivateNetworkViolationException>(() => guard.ResolvePublicAsync("169.254.169.254"));
        await Assert.ThrowsAsync<PrivateNetworkViolationException>(() => guard.ResolvePublicAsync("[::1]"));
        Assert.Equal(IPAddress.Parse("8.8.8.8"), await guard.ResolvePublicAsync("8.8.8.8"));
        Assert.Equal(0, resolver.Lookups);
    }
}
