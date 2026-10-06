using System.Collections.Frozen;
using System.Net;
using Campfire.Web.Net;

namespace Campfire.Web.Push;

/// <summary>
/// Rails' Push::Subscription endpoint rules. A subscription endpoint is a URL the server POSTs to,
/// so it's held to the browser push services we know, over HTTPS on 443, at a public address —
/// checked when saved and again (with a fresh resolution the connection is pinned to) on every delivery.
/// </summary>
public sealed class PushEndpoints(PrivateNetworkGuard guard)
{
    public static readonly FrozenSet<string> PermittedHosts = FrozenSet.ToFrozenSet(
    [
        "jmt17.google.com",
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com",
        "notify.windows.com"
    ], StringComparer.Ordinal);

    /// <summary>Validation errors for an endpoint (Rails' <c>errors[:endpoint]</c>), empty when valid.</summary>
    public async Task<IReadOnlyList<string>> ValidateAsync(string? endpoint, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>(2);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errors.Add("can't be blank");
        }

        var shape = EndpointShape.Parse(endpoint);
        if (shape is null)
        {
            errors.Add("is not a valid URL");
        }
        else if (shape.Scheme != "https")
        {
            errors.Add("must use HTTPS");
        }
        else if (shape.Port != 443)
        {
            errors.Add("must use the default HTTPS port");
        }
        else if (!IsPermittedHost(shape.Host))
        {
            errors.Add("is not a permitted push service");
        }
        else if (await ResolvedEndpointIpAsync(endpoint, cancellationToken) is null)
        {
            errors.Add("resolves to a private or invalid IP address");
        }

        return errors;
    }

    /// <summary>
    /// The public address to deliver to, validated at the point of use (not just when saved):
    /// null unless the endpoint is a permitted HTTPS push service resolving to a public address.
    /// </summary>
    public async Task<IPAddress?> ResolvedEndpointIpAsync(string? endpoint, CancellationToken cancellationToken = default)
    {
        var shape = EndpointShape.Parse(endpoint);
        return shape is { Scheme: "https", Port: 443 } && IsPermittedHost(shape.Host)
            ? await guard.TryResolvePublicAsync(shape.Host, cancellationToken)
            : null;
    }

    /// <summary>The host is a permitted service or a subdomain of one (never a mere suffix match).</summary>
    public static bool IsPermittedHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var lower = host.ToLowerInvariant();
        foreach (var permitted in PermittedHosts)
        {
            if (lower == permitted || lower.EndsWith("." + permitted, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// What Ruby's <c>URI.parse</c> tells the validations: a relative string still parses (with no
    /// scheme, failing "must use HTTPS"); only blank or malformed input gives no URI at all.
    /// </summary>
    private sealed record EndpointShape(string? Scheme, string? Host, int Port)
    {
        public static EndpointShape? Parse(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Any(char.IsWhiteSpace))
            {
                return null;
            }

            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return new EndpointShape(uri.Scheme, uri.IdnHost, uri.Port);
            }

            return Uri.TryCreate(endpoint, UriKind.Relative, out _) ? new EndpointShape(null, null, -1) : null;
        }
    }
}
