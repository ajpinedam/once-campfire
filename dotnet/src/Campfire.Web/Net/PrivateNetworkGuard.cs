using System.Net;
using System.Net.Sockets;

namespace Campfire.Web.Net;

/// <summary>Resolves host names to addresses. Swappable so tests never touch real DNS.</summary>
public interface IHostResolver
{
    /// <summary>Every address the host answers with; empty when it answers with none.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemHostResolver : IHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>Base for the guard's refusals.</summary>
public abstract class RestrictedHttpException(string message) : Exception(message);

/// <summary>The host resolved, but only to addresses we refuse to reach (Rails' RestrictedHTTP::Violation).</summary>
public sealed class PrivateNetworkViolationException(string host) : RestrictedHttpException($"Attempt to access private IP via {host}");

/// <summary>
/// The host resolved to nothing (NXDOMAIN, timeout, empty answer) — Surfguard::Unresolvable.
/// Kept distinct from a violation so a transient DNS miss is never misreported as an SSRF attempt.
/// </summary>
public sealed class UnresolvableHostException(string host) : RestrictedHttpException($"Could not resolve {host}");

/// <summary>
/// The SSRF address policy (Rails' RestrictedHTTP::PrivateNetworkGuard over the surfguard gem):
/// a hostname goes in, a public address comes out, and callers pin their connection to that
/// address so a DNS rebind between check and connect can't redirect them inward.
/// </summary>
public sealed class PrivateNetworkGuard(IHostResolver resolver)
{
    /// <summary>The first public address <paramref name="host"/> resolves to.</summary>
    /// <exception cref="UnresolvableHostException">Nothing came back.</exception>
    /// <exception cref="PrivateNetworkViolationException">Only blocked addresses came back.</exception>
    public async Task<IPAddress> ResolvePublicAsync(string? host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new UnresolvableHostException(host ?? "");
        }

        var name = host.Trim('[', ']');
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(name, out var literal)
            ? [literal]
            : await resolver.ResolveAsync(name, cancellationToken);

        if (addresses.Count == 0)
        {
            throw new UnresolvableHostException(host);
        }

        return addresses.FirstOrDefault(address => !IsBlocked(address)) ?? throw new PrivateNetworkViolationException(host);
    }

    /// <summary>The address, or null when the host is unresolvable or not public (Rails' <c>resolve(...) rescue nil</c>).</summary>
    public async Task<IPAddress?> TryResolvePublicAsync(string? host, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ResolvePublicAsync(host, cancellationToken);
        }
        catch (RestrictedHttpException)
        {
            return null;
        }
    }

    /// <summary>Rails' <c>private_ip?</c>: true for anything that isn't a plainly public address, including garbage.</summary>
    public static bool IsPrivateIp(string? address) => !IPAddress.TryParse(address, out var ip) || IsBlocked(ip);

    public static bool IsBlocked(IPAddress address) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => IsBlockedV4(address.GetAddressBytes()),
        AddressFamily.InterNetworkV6 => IsBlockedV6(address.GetAddressBytes()),
        _ => true
    };

    private static bool IsBlockedV4(ReadOnlySpan<byte> b) =>
        b[0] == 0 ||                                          // "this" network (RFC 1122)
        b[0] == 10 ||                                         // private (RFC 1918)
        b[0] == 100 && (b[1] & 0xC0) == 64 ||                 // carrier-grade NAT 100.64/10 (RFC 6598)
        b[0] == 127 ||                                        // loopback
        b[0] == 169 && b[1] == 254 ||                         // link-local, cloud metadata
        b[0] == 172 && (b[1] & 0xF0) == 16 ||                 // private 172.16/12
        b[0] == 192 && b[1] == 0 && b[2] == 0 ||              // IETF protocol assignments 192.0.0/24
        b[0] == 192 && b[1] == 0 && b[2] == 2 ||              // TEST-NET-1
        b[0] == 192 && b[1] == 88 && b[2] == 99 ||            // 6to4 relay anycast
        b[0] == 192 && b[1] == 168 ||                         // private 192.168/16
        b[0] == 198 && (b[1] & 0xFE) == 18 ||                 // benchmarking 198.18/15
        b[0] == 198 && b[1] == 51 && b[2] == 100 ||           // TEST-NET-2
        b[0] == 203 && b[1] == 0 && b[2] == 113 ||            // TEST-NET-3
        b[0] >= 224;                                          // multicast, reserved, broadcast

    private static bool IsBlockedV6(ReadOnlySpan<byte> b)
    {
        // NAT64 well-known prefix 64:ff9b::/96 (RFC 6052): DNS64 synthesizes these for public
        // sites on IPv6-only hosts, so judge the IPv4 address it carries.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return IsBlockedV4(b[12..]);
        }

        // Only global unicast 2000::/3 is reachable. This alone refuses unspecified, loopback,
        // IPv4-mapped (::ffff:0:0/96), IPv4-compatible (::/96), SIIT (::ffff:0:0:0/96), the whole
        // local-use NAT64 block 64:ff9b:1::/48 (its Pref64 length isn't recoverable, RFC 6052 §2.2),
        // discard-only 100::/64, ULA fc00::/7, link-local fe80::/10, site-local and multicast ff00::/8.
        if ((b[0] & 0xE0) != 0x20)
        {
            return true;
        }

        return
            b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0x00 ||          // 2001::/23 IETF assignments: Teredo 2001::/32, benchmarking 2001:2::/48, ORCHID
            b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8 ||   // documentation 2001:db8::/32
            b[0] == 0x20 && b[1] == 0x02 ||                                   // 6to4 2002::/16 (tunnels to any IPv4, private included)
            b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xF0) == 0;               // documentation 3fff::/20 (RFC 9637)
    }
}
