using System.Net;
using System.Net.Sockets;

namespace Campfire.Web.Features.People;

/// <summary>Rails' <c>Ban#ip_address_is_public</c>: loopback, private and link-local addresses can't be banned.</summary>
public static class BanAddresses
{
    public static bool IsPublic(string? ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 10 ||
                     (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                     (bytes[0] == 192 && bytes[1] == 168) ||
                     (bytes[0] == 169 && bytes[1] == 254));
        }

        // IPv6: unique local fc00::/7 (Ruby's IPAddr#private?) and link-local fe80::/10
        var first = address.GetAddressBytes()[0];
        return (first & 0xFE) != 0xFC && !address.IsIPv6LinkLocal;
    }
}
