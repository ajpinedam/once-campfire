using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Security;

namespace Campfire.Web.Cable;

/// <summary>
/// The ActionCable server: accepts WebSocket connections at <c>/cable</c> speaking the
/// <c>actioncable-v1-json</c> protocol that @rails/actioncable and turbo-rails' client use,
/// authenticates them with the session cookie, routes subscriptions to channels, and fans out
/// broadcasts published by the rest of the app. In-process pub/sub (no Redis).
/// </summary>
public sealed class CableServer : IHostedService
{
    /// <summary>ActionCable's heartbeat: clients consider a connection stale after two missed pings.</summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(2);

    private readonly AppCookies _cookies;
    private readonly KeyRing _keys;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CableServer> _logger;
    private readonly ConcurrentDictionary<CableConnection, byte> _connections = new();
    private CancellationTokenSource? _pings;

    public CableServer(Database database, AppCookies cookies, KeyRing keys, IHostApplicationLifetime lifetime, ILogger<CableServer> logger)
    {
        Database = database;
        _cookies = cookies;
        _keys = keys;
        _lifetime = lifetime;
        _logger = logger;
    }

    internal Database Database { get; }

    internal CablePubSub PubSub { get; } = new();

    /// <summary>Open connections right now.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Handles one WebSocket request for its whole lifetime.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        if (!AllowedOrigin(context))
        {
            _logger.LogWarning("Cable request origin not allowed");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(CableFrames.Protocol))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        ReleaseRequestConnection(context);
        var user = Authenticate(context);

        using var socket = await context.WebSockets.AcceptWebSocketAsync(CableFrames.Protocol);

        if (user is null)
        {
            await RejectUnauthorizedAsync(socket);
            return;
        }

        var connection = new CableConnection(this, socket, user, _logger, _lifetime.ApplicationStopping);
        _connections.TryAdd(connection, 0);
        try
        {
            await connection.RunAsync();
        }
        finally
        {
            _connections.TryRemove(connection, out _);
        }
    }

    /// <summary>
    /// Publishes a message to every subscription streaming from <paramref name="streamName"/>.
    /// <paramref name="jsonMessage"/> is a complete JSON value (object, string, ...) sent as the
    /// frame's <c>"message"</c> member: <c>{"identifier": "...", "message": jsonMessage}</c>.
    /// </summary>
    public void Broadcast(string streamName, string jsonMessage) => PubSub.Publish(streamName, Encoding.UTF8.GetBytes(jsonMessage));

    /// <summary>Publishes an already UTF-8 encoded JSON value (see <see cref="Broadcast(string, string)"/>).</summary>
    public void Broadcast(string streamName, ReadOnlyMemory<byte> utf8JsonMessage) => PubSub.Publish(streamName, utf8JsonMessage);

    /// <summary>Publishes Turbo Stream HTML (sent as a JSON string, as turbo-rails broadcasts do).</summary>
    public void BroadcastTurboStream(string streamName, string html) => PubSub.Publish(streamName, CableFrames.JsonString(html));

    /// <summary>
    /// Rails' <c>ActionCable.server.remote_connections.where(current_user:).disconnect(reconnect:)</c>:
    /// sends <c>{"type":"disconnect","reason":"remote","reconnect":...}</c> to each of the user's
    /// connections and closes them.
    /// </summary>
    public void DisconnectUser(long userId, bool reconnect)
    {
        byte[]? frame = null;
        foreach (var connection in _connections.Keys)
        {
            if (connection.User.Id == userId)
            {
                connection.Close(frame ??= CableFrames.Disconnect("remote", reconnect));
            }
        }
    }

    /// <summary>How many subscriptions currently stream from a stream (diagnostics and tests).</summary>
    public int SubscriberCount(string streamName) => PubSub.SubscriberCount(streamName);

    internal string? VerifyStreamName(string? signedStreamName) =>
        string.IsNullOrEmpty(signedStreamName) ? null : _keys.StreamNames.Verify(signedStreamName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pings = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        _ = PingLoopAsync(_pings.Token);

        // Kestrel waits for open requests on shutdown, and a WebSocket never finishes on its own:
        // tell clients to come back (Rails' server_restart) and close them as soon as stopping begins.
        _lifetime.ApplicationStopping.Register(() =>
        {
            var restart = CableFrames.Disconnect("server_restart", reconnect: true);
            foreach (var connection in _connections.Keys)
            {
                connection.Close(restart);
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pings?.Cancel();
        _pings?.Dispose();
        _pings = null;
        return Task.CompletedTask;
    }

    private async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_connections.IsEmpty)
                {
                    continue;
                }

                var ping = OutgoingFrame.Of(CableFrames.Ping(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                foreach (var connection in _connections.Keys)
                {
                    connection.Enqueue(ping);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>ApplicationCable::Connection#find_verified_user: the signed session cookie's user.</summary>
    private User? Authenticate(HttpContext context)
    {
        if (_cookies.ReadSessionToken(context) is not { } token)
        {
            return null;
        }

        using var sql = Database.Open();
        return Sessions.FindWithUser(sql, token)?.User;
    }

    /// <summary>
    /// Cross-site WebSocket hijacking protection (Rails' <c>allow_same_origin_as_host</c>): browsers
    /// always send Origin on WebSocket handshakes, and it must name this host.
    /// </summary>
    private static bool AllowedOrigin(HttpContext context) =>
        Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) &&
        string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The request pipeline resolved a scoped database connection (to load the account); a WebSocket
    /// request lives for hours, so hand that connection back to the pool now instead of pinning one
    /// per open socket. Disposing it again at the end of the request is harmless.
    /// </summary>
    private static void ReleaseRequestConnection(HttpContext context) => context.RequestServices.GetService<Sql>()?.Dispose();

    /// <summary>Rails' <c>reject_unauthorized_connection</c>: say why, then close for good.</summary>
    private static async Task RejectUnauthorizedAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(HandshakeTimeout);
        try
        {
            await socket.SendAsync(CableFrames.Disconnect("unauthorized", reconnect: false), WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);

            var buffer = new byte[256];
            while (socket.State == WebSocketState.CloseSent)
            {
                if ((await socket.ReceiveAsync(buffer, timeout.Token)).MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            socket.Abort();
        }
    }
}
