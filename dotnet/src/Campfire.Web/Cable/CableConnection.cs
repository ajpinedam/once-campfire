using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Campfire.Web.Domain;

namespace Campfire.Web.Cable;

/// <summary>A confirmed subscription on one connection.</summary>
internal sealed record CableSubscription(string Identifier, CableChannel Channel, IReadOnlyList<string> Streams, StreamSubscriber Subscriber);

/// <summary>
/// One WebSocket client (ActionCable::Connection::Base) for one signed-in user. A receive loop
/// processes commands in order; everything sent goes through a bounded outbox drained by a single
/// writer, so broadcasters never touch the socket and never wait on a slow client.
/// </summary>
internal sealed class CableConnection
{
    private const int OutboxCapacity = 1024;
    private const int MaxMessageBytes = 1 << 20;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly CableServer _server;
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly Channel<OutgoingFrame> _outbox = Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(OutboxCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait // TryWrite fails when full; we treat that as a stalled client
    });
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<string, CableSubscription> _subscriptions = new(StringComparer.Ordinal);
    private int _closing;

    public CableConnection(CableServer server, WebSocket socket, User user, ILogger logger, CancellationToken stopping)
    {
        _server = server;
        _socket = socket;
        _logger = logger;
        User = user;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public User User { get; }

    /// <summary>Queues a frame; a client too slow to keep its outbox from filling is dropped.</summary>
    public void Enqueue(OutgoingFrame frame)
    {
        if (!_outbox.Writer.TryWrite(frame) && Volatile.Read(ref _closing) == 0)
        {
            _logger.LogWarning("Dropping cable connection for user {UserId}: it isn't keeping up with broadcasts", User.Id);
            Abort();
        }
    }

    /// <summary>Sends a last frame (e.g. a disconnect notice), then closes the socket.</summary>
    public void Close(byte[] finalFrame)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        if (_outbox.Writer.TryWrite(OutgoingFrame.Of(finalFrame)) && _outbox.Writer.TryWrite(OutgoingFrame.Close))
        {
            _outbox.Writer.TryComplete();
            CancelAfterSafe(CloseTimeout); // in case the client never answers the close handshake
        }
        else
        {
            Abort();
        }
    }

    public async Task RunAsync()
    {
        var writer = Task.Run(WriteLoopAsync);
        Enqueue(OutgoingFrame.Of(CableFrames.Welcome));

        try
        {
            await ReceiveLoopAsync(_lifetime.Token);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, was dropped, or the server is stopping.
        }
        finally
        {
            UnsubscribeAll();

            // Answer a client-initiated close, or finish ours, after whatever is still queued.
            if (Interlocked.Exchange(ref _closing, 1) == 0 && !_outbox.Writer.TryWrite(OutgoingFrame.Close))
            {
                Abort();
            }
            _outbox.Writer.TryComplete();

            await Task.WhenAny(writer, Task.Delay(CloseTimeout));
            if (_socket.State != WebSocketState.Closed)
            {
                Abort();
            }

            await writer; // finishes promptly once aborted; never throws (it handles its own failures)
            _lifetime.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length >= MaxMessageBytes)
                    {
                        _logger.LogWarning("Closing cable connection for user {UserId}: message too large", User.Id);
                        return;
                    }
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var result = await _socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                length += result.Count;
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Dispatch(buffer.AsMemory(0, length));
                }
                length = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteLoopAsync()
    {
        var token = _lifetime.Token;
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (frame.IsClose)
                {
                    if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
                    }
                    return;
                }

                await SendAsync(frame, token);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            Abort();
        }
    }

    private async ValueTask SendAsync(OutgoingFrame frame, CancellationToken token)
    {
        if (frame.Head.IsEmpty && frame.Tail.IsEmpty)
        {
            await _socket.SendAsync(frame.Body, WebSocketMessageType.Text, endOfMessage: true, token);
            return;
        }

        var length = frame.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            frame.Head.Span.CopyTo(buffer);
            frame.Body.Span.CopyTo(buffer.AsSpan(frame.Head.Length));
            frame.Tail.Span.CopyTo(buffer.AsSpan(frame.Head.Length + frame.Body.Length));
            await _socket.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Text, endOfMessage: true, token);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Commands (ActionCable::Connection::Subscriptions)

    private void Dispatch(ReadOnlyMemory<byte> message)
    {
        string? command, identifier, data;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            command = StringProperty(root, "command");
            identifier = StringProperty(root, "identifier");
            data = StringProperty(root, "data");
        }
        catch (JsonException)
        {
            _logger.LogDebug("Ignoring malformed cable message from user {UserId}", User.Id);
            return;
        }

        if (identifier is null)
        {
            return;
        }

        switch (command)
        {
            case "subscribe":
                Subscribe(identifier);
                break;
            case "unsubscribe":
                Unsubscribe(identifier);
                break;
            case "message":
                Perform(identifier, data);
                break;
        }
    }

    private void Subscribe(string identifier)
    {
        // The client's subscription guarantor resends until confirmed; one subscription per identifier.
        if (_subscriptions.ContainsKey(identifier))
        {
            return;
        }

        var parameters = SubscriptionParams.Parse(identifier);
        var channel = parameters is null ? null : CableChannel.Create(new ChannelContext(_server, User), parameters);

        IReadOnlyList<string>? streams = null;
        try
        {
            streams = channel?.Subscribe();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not subscribe user {UserId} to {Channel}", User.Id, parameters?.Channel);
        }

        if (channel is null || streams is null)
        {
            Enqueue(OutgoingFrame.Of(CableFrames.Reject(identifier)));
            return;
        }

        var subscription = new CableSubscription(identifier, channel, streams, new StreamSubscriber(this, CableFrames.MessageHead(identifier)));
        _subscriptions[identifier] = subscription;
        foreach (var stream in streams)
        {
            _server.PubSub.Subscribe(stream, subscription.Subscriber);
        }

        Enqueue(OutgoingFrame.Of(CableFrames.Confirm(identifier)));
    }

    private void Unsubscribe(string identifier)
    {
        if (_subscriptions.Remove(identifier, out var subscription))
        {
            Remove(subscription);
        }
    }

    private void Perform(string identifier, string? data)
    {
        if (data is null || !_subscriptions.TryGetValue(identifier, out var subscription))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && StringProperty(root, "action") is { Length: > 0 } action)
            {
                subscription.Channel.Perform(action, root);
            }
        }
        catch (JsonException)
        {
            _logger.LogDebug("Ignoring malformed cable action from user {UserId}", User.Id);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Cable action failed for user {UserId}", User.Id);
        }
    }

    private void UnsubscribeAll()
    {
        foreach (var subscription in _subscriptions.Values)
        {
            Remove(subscription);
        }
        _subscriptions.Clear();
    }

    private void Remove(CableSubscription subscription)
    {
        foreach (var stream in subscription.Streams)
        {
            _server.PubSub.Unsubscribe(stream, subscription.Subscriber);
        }

        try
        {
            subscription.Channel.Unsubscribed();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Cable unsubscribe failed for user {UserId}", User.Id);
        }
    }

    private void Abort()
    {
        Interlocked.Exchange(ref _closing, 1);
        _outbox.Writer.TryComplete();
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelAfterSafe(TimeSpan delay)
    {
        try
        {
            _lifetime.CancelAfter(delay);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
