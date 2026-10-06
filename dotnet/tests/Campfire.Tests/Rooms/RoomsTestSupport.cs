namespace Campfire.Tests.Rooms;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Campfire.Tests.Support;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Security;

/// <summary>A fresh, seeded app per test: these tests post, edit and delete freely.</summary>
public abstract class RoomsTest : IDisposable
{
    protected RoomsTest()
    {
        App = new CampfireApp();
        F = App.Fixtures;
    }

    protected CampfireApp App { get; }
    protected Fixtures F { get; }

    protected CampfireClient SignIn(User user) => App.SignedInAs(user);

    protected Message CreateMessage(Room room, User creator, string text, string? clientMessageId = null, DateTime? createdAt = null)
    {
        var message = App.Sql(sql => Messages.Create(sql, room.Id, creator.Id, clientMessageId, $"<div>{text}</div>", text));
        if (createdAt is { } at)
        {
            App.Sql(sql => sql.Execute("UPDATE messages SET created_at = @at, updated_at = @at WHERE id = @id", ("@at", at), ("@id", message.Id)));
            message = message with { CreatedAt = at, UpdatedAt = at };
        }
        return message;
    }

    /// <summary>Several messages a second apart, oldest first.</summary>
    protected List<Message> CreateMessages(Room room, User creator, int count, string prefix = "Message")
    {
        var start = DateTime.UtcNow.AddHours(-1);
        return Enumerable.Range(0, count)
            .Select(i => CreateMessage(room, creator, $"{prefix} {i}", $"{prefix.ToLowerInvariant()}-{i}", start.AddSeconds(i)))
            .ToList();
    }

    protected Room Reload(Room room) => App.Sql(sql => Web.Data.Queries.Rooms.Find(sql, room.Id))!;

    protected List<long> MemberIds(Room room) => App.Sql(sql => Web.Data.Queries.Rooms.UserIds(sql, room.Id)).Order().ToList();

    protected long UserCount() => App.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM users"));

    protected long RoomCount() => App.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM rooms"));

    protected long MessageCount() => App.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM messages"));

    protected static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    protected static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage response) => Parse(await response.BodyAsync());

    public void Dispose()
    {
        App.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Subscribes to a cable channel over a real WebSocket (as the browser would) and records what's
/// broadcast to it, so tests can assert on Turbo Stream and channel broadcasts.
/// </summary>
public sealed class CableProbe : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly string _identifier;
    private readonly List<JsonElement> _messages = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly TaskCompletionSource<bool> _confirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CableProbe(WebSocket socket, string identifier)
    {
        _socket = socket;
        _identifier = identifier;
        _reader = ReadAsync();
    }

    /// <summary>Streams of the given Turbo stream name through <paramref name="channel"/>.</summary>
    public static Task<CableProbe> StreamAsync(CampfireApp app, User user, string streamName, string channel = "Turbo::StreamsChannel") =>
        ConnectAsync(app, user, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["channel"] = channel,
            ["signed_stream_name"] = app.Service<KeyRing>().StreamNames.Sign(streamName)
        }));

    /// <summary>A plain channel subscription, e.g. <c>{"channel":"UnreadRoomsChannel"}</c>.</summary>
    public static Task<CableProbe> ChannelAsync(CampfireApp app, User user, string channel) =>
        ConnectAsync(app, user, JsonSerializer.Serialize(new Dictionary<string, object> { ["channel"] = channel }));

    private static async Task<CableProbe> ConnectAsync(CampfireApp app, User user, string identifier)
    {
        var session = app.Sql(sql => Sessions.Start(sql, user.Id, "Mozilla/5.0 Chrome/130.0", "127.0.0.1"));
        var client = app.Server.CreateWebSocketClient();
        client.SubProtocols.Add("actioncable-v1-json");
        client.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = $"{AppCookies.SessionToken}={app.Service<KeyRing>().Cookies.Sign(session.Token)}";
            request.Headers["Origin"] = "http://localhost";
        };

        var socket = await client.ConnectAsync(new Uri(app.Server.BaseAddress, "cable"), CancellationToken.None);
        var probe = new CableProbe(socket, identifier);
        await probe.SendAsync(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = "subscribe", ["identifier"] = identifier }));

        if (await Task.WhenAny(probe._confirmed.Task, Task.Delay(TimeSpan.FromSeconds(5))) != probe._confirmed.Task || !await probe._confirmed.Task)
        {
            throw new InvalidOperationException($"Subscription to {identifier} wasn't confirmed");
        }
        return probe;
    }

    /// <summary>Broadcast messages received so far (Turbo Streams arrive as JSON strings).</summary>
    public IReadOnlyList<JsonElement> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    /// <summary>Turbo Stream HTML broadcast so far.</summary>
    public IReadOnlyList<string> TurboStreams => Messages.Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToList();

    /// <summary>Waits until at least <paramref name="count"/> messages arrived (or a short timeout), then returns them.</summary>
    public async Task<IReadOnlyList<JsonElement>> WaitForAsync(int count = 1, int timeoutMilliseconds = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (Messages.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        return Messages;
    }

    /// <summary>Gives in-flight broadcasts a moment to arrive, for asserting that none did.</summary>
    public async Task<IReadOnlyList<JsonElement>> SettleAsync(int milliseconds = 300)
    {
        await Task.Delay(milliseconds);
        return Messages;
    }

    private Task SendAsync(string json) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _stop.Token);
                    frame.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _confirmed.TrySetResult(false);
                    return;
                }

                using var document = JsonDocument.Parse(frame.ToArray());
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

                if (type == "confirm_subscription")
                {
                    _confirmed.TrySetResult(true);
                }
                else if (type == "reject_subscription" || type == "disconnect")
                {
                    _confirmed.TrySetResult(false);
                }
                else if (type is null && root.TryGetProperty("message", out var message) &&
                         root.TryGetProperty("identifier", out var identifier) && identifier.GetString() == _identifier)
                {
                    lock (_messages)
                    {
                        _messages.Add(message.Clone());
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await _reader;
        }
        catch (Exception)
        {
        }
        _socket.Dispose();
        _stop.Dispose();
    }
}
