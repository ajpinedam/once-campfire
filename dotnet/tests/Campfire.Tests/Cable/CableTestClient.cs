using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Security;

namespace Campfire.Tests.Cable;

/// <summary>A minimal @rails/actioncable client over the test server's WebSocket client.</summary>
internal sealed class CableTestClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly WebSocket _socket;

    private CableTestClient(WebSocket socket) => _socket = socket;

    public WebSocket Socket => _socket;

    public static CableTestClient Wrap(WebSocket socket) => new(socket);

    public static async Task<CableTestClient> ConnectAsync(CampfireApp app, User? user, string origin = "http://localhost")
    {
        var client = app.Server.CreateWebSocketClient();
        client.SubProtocols.Add("actioncable-v1-json");
        client.SubProtocols.Add("actioncable-unsupported");
        var cookie = user is null ? null : SessionCookie(app, user);
        client.ConfigureRequest = request =>
        {
            request.Headers.Origin = origin;
            if (cookie is not null)
            {
                request.Headers.Cookie = cookie;
            }
        };

        using var timeout = new CancellationTokenSource(DefaultTimeout);
        return new CableTestClient(await client.ConnectAsync(new Uri(app.Server.BaseAddress, "cable"), timeout.Token));
    }

    public static string SessionCookie(CampfireApp app, User user)
    {
        var session = app.Sql(sql => Campfire.Web.Data.Queries.Sessions.Start(sql, user.Id, "test", "127.0.0.1"));
        return $"{AppCookies.SessionToken}={app.Service<KeyRing>().Cookies.Sign(session.Token)}";
    }

    public static string Identifier(string channel, object? parameters = null)
    {
        var values = new Dictionary<string, object?> { ["channel"] = channel };
        if (parameters is not null)
        {
            foreach (var property in parameters.GetType().GetProperties())
            {
                values[property.Name] = property.GetValue(parameters);
            }
        }
        return JsonSerializer.Serialize(values);
    }

    public Task SendAsync(object command) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command)), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SubscribeAsync(string identifier) => SendAsync(new { command = "subscribe", identifier });

    public Task UnsubscribeAsync(string identifier) => SendAsync(new { command = "unsubscribe", identifier });

    public Task PerformAsync(string identifier, string action) =>
        SendAsync(new { command = "message", identifier, data = JsonSerializer.Serialize(new { action }) });

    /// <summary>The next message, optionally skipping pings. Null when the socket closed.</summary>
    public async Task<JsonElement?> ReceiveAsync(bool skipPings = true, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var length = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), cancellation.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                length += result.Count;
            }
            while (!result.EndOfMessage);

            var message = JsonDocument.Parse(buffer.AsMemory(0, length)).RootElement.Clone();
            if (skipPings && message.TryGetProperty("type", out var type) && type.GetString() == "ping")
            {
                continue;
            }
            return message;
        }
    }

    public async Task<JsonElement> ExpectAsync(Func<JsonElement, bool>? match = null, string? because = null)
    {
        var message = await ReceiveAsync();
        Assert.True(message is not null, because ?? "the socket closed");
        if (match is not null)
        {
            Assert.True(match(message!.Value), $"{because ?? "unexpected message"}: {message}");
        }
        return message!.Value;
    }

    public async Task ExpectWelcomeAsync() =>
        await ExpectAsync(m => Type(m) == "welcome", "expected the welcome message");

    public async Task SubscribeAndExpectAsync(string identifier, string expectedType)
    {
        await SubscribeAsync(identifier);
        await ExpectAsync(m => Type(m) == expectedType && m.GetProperty("identifier").GetString() == identifier,
            $"expected {expectedType}");
    }

    public static string? Type(JsonElement message) =>
        message.TryGetProperty("type", out var type) ? type.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
            }
        }
        _socket.Dispose();
    }
}
