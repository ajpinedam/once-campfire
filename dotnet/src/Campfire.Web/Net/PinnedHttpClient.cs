using System.Net;
using System.Net.Sockets;

namespace Campfire.Web.Net;

/// <summary>
/// An HTTP client that never resolves host names itself: every request names the address
/// (already vetted by <see cref="PrivateNetworkGuard"/>) its connection must go to, while the
/// URL's host still drives the Host header and TLS SNI/certificate validation. No proxy is ever
/// used — a proxy would re-resolve the host and defeat the pin — and redirects are not followed.
///
/// Connections are pooled per host by SocketsHttpHandler. That's still safe: a connection is only
/// ever opened to an address pinned on some request, and every pinned address passed the guard.
/// </summary>
public sealed class PinnedHttpClient : IDisposable
{
    private static readonly HttpRequestOptionsKey<IPAddress> PinnedAddress = new("Campfire.PinnedAddress");

    private readonly HttpClient _client;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;

    public PinnedHttpClient(TimeSpan connectTimeout, TimeSpan requestTimeout, int maxConnectionsPerServer = int.MaxValue)
        : this(connectTimeout, requestTimeout, maxConnectionsPerServer, ConnectAsync)
    {
    }

    /// <summary>Tests substitute the dialer to observe (and redirect) the pinned endpoint.</summary>
    internal PinnedHttpClient(TimeSpan connectTimeout, TimeSpan requestTimeout, int maxConnectionsPerServer, Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect)
    {
        _connect = connect;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            MaxConnectionsPerServer = maxConnectionsPerServer,
            ConnectCallback = ConnectToPinnedAddressAsync
        };

        _client = new HttpClient(handler, disposeHandler: true) { Timeout = requestTimeout };
    }

    /// <summary>Sends <paramref name="request"/> over a connection to <paramref name="address"/>.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, IPAddress address, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        request.Options.Set(PinnedAddress, address);
        return _client.SendAsync(request, completion, cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    private ValueTask<Stream> ConnectToPinnedAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        // Fail closed: a request without a pin must never fall back to resolving the host.
        if (!context.InitialRequestMessage.Options.TryGetValue(PinnedAddress, out var address))
        {
            throw new InvalidOperationException("PinnedHttpClient requests must carry a pinned address");
        }

        return _connect(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
    }

    private static async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
