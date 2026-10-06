using System.Globalization;
using System.Text.Json;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Turbo;

namespace Campfire.Web.Cable;

/// <summary>What a subscription's identifier asks for: <c>{"channel":"PresenceChannel","room_id":1}</c>.</summary>
internal sealed record SubscriptionParams(string Channel, long? RoomId, string? SignedStreamName)
{
    public static SubscriptionParams? Parse(string identifier)
    {
        try
        {
            using var document = JsonDocument.Parse(identifier);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new SubscriptionParams(
                channel.GetString()!,
                root.TryGetProperty("room_id", out var roomId) ? Long(roomId) : null,
                root.TryGetProperty("signed_stream_name", out var signed) && signed.ValueKind == JsonValueKind.String ? signed.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Current.room.id arrives as a number; Rails would also accept "5".
    private static long? Long(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null
    };
}

/// <summary>What every channel instance knows: the server, and who is connected.</summary>
internal sealed record ChannelContext(CableServer Server, User User);

/// <summary>
/// One subscription's channel (ActionCable::Channel::Base). <see cref="Subscribe"/> runs the
/// <c>subscribed</c> callbacks and returns the streams to listen to, or null to reject.
/// Methods are only ever called from the owning connection's receive loop, one at a time.
/// </summary>
internal abstract class CableChannel(ChannelContext context)
{
    protected CableServer Server => context.Server;
    protected User User => context.User;

    public abstract IReadOnlyList<string>? Subscribe();

    /// <summary>A client <c>perform(action, data)</c>; unknown actions are ignored, as Rails does.</summary>
    public virtual void Perform(string action, JsonElement data)
    {
    }

    /// <summary>The <c>unsubscribed</c> callbacks; only called for confirmed subscriptions.</summary>
    public virtual void Unsubscribed()
    {
    }

    protected Sql OpenSql() => Server.Database.Open();

    public static CableChannel? Create(ChannelContext context, SubscriptionParams parameters) => parameters.Channel switch
    {
        "Turbo::StreamsChannel" => new TurboStreamsChannel(context, parameters.SignedStreamName),
        "RoomMessagesChannel" => new RoomMessagesChannel(context, parameters.SignedStreamName),
        "PresenceChannel" => new PresenceChannel(context, parameters.RoomId),
        "TypingNotificationsChannel" => new TypingNotificationsChannel(context, parameters.RoomId),
        "ReadRoomsChannel" => new FixedStreamChannel(context, StreamNames.UserReads(context.User.Id)),
        "UnreadRoomsChannel" => new FixedStreamChannel(context, StreamNames.UserUnreads(context.User.Id)),
        "HeartbeatChannel" => new FixedStreamChannel(context, null),
        _ => null
    };
}

/// <summary>
/// ReadRoomsChannel and UnreadRoomsChannel (scoped to the user, so nobody learns about activity in
/// rooms they aren't in), and HeartbeatChannel (no stream; it only reports connectivity).
/// </summary>
internal sealed class FixedStreamChannel(ChannelContext context, string? stream) : CableChannel(context)
{
    public override IReadOnlyList<string> Subscribe() => stream is null ? [] : [stream];
}

/// <summary>
/// turbo-rails' stock channel, prepended with RoomStreamsAreAuthorized: the subscriber names the
/// channel it wants, so room message streams are turned away here and RoomMessagesChannel — which
/// checks membership — is the only door to them.
/// </summary>
internal sealed class TurboStreamsChannel(ChannelContext context, string? signedStreamName) : CableChannel(context)
{
    public override IReadOnlyList<string>? Subscribe() =>
        Server.VerifyStreamName(signedStreamName) is { } stream && !StreamNames.IsGuarded(stream) ? [stream] : null;
}

/// <summary>
/// Authorizes the room message stream when the subscription is made, so revoking a membership
/// actually stops delivery: a stream name harvested while a member is useless afterwards, even
/// though its signature never expires. The room comes from the verified name, not a parameter.
/// </summary>
internal sealed class RoomMessagesChannel(ChannelContext context, string? signedStreamName) : CableChannel(context)
{
    public override IReadOnlyList<string>? Subscribe()
    {
        if (Server.VerifyStreamName(signedStreamName) is not { } stream || StreamNames.RoomIdFromMessagesStream(stream) is not { } roomId)
        {
            return null;
        }

        using var sql = OpenSql();
        return Rooms.FindForUser(sql, User.Id, roomId) is null ? null : [stream];
    }
}

/// <summary>RoomChannel: subscriptions scoped to one of the user's rooms (<c>current_user.rooms.find_by(id:)</c>).</summary>
internal abstract class RoomChannel(ChannelContext context, long? roomId) : CableChannel(context)
{
    protected long RoomId { get; private set; }

    protected bool FindRoom(Sql sql)
    {
        if (roomId is not { } id || Rooms.FindForUser(sql, User.Id, id) is null)
        {
            return false;
        }

        RoomId = id;
        return true;
    }
}

/// <summary>
/// Tracks which members are looking at a room right now: connected members don't get the room
/// marked unread or receive pushes. Subscribing (and the <c>present</c> action, sent when the tab
/// becomes visible again) marks the room read and tells the user's other tabs.
/// </summary>
internal sealed class PresenceChannel(ChannelContext context, long? roomId) : RoomChannel(context, roomId)
{
    public override IReadOnlyList<string>? Subscribe()
    {
        using var sql = OpenSql();
        if (!FindRoom(sql))
        {
            return null;
        }

        Present(sql);
        return [];
    }

    public override void Perform(string action, JsonElement data)
    {
        using var sql = OpenSql();
        switch (action)
        {
            case "present":
                Present(sql);
                break;
            case "absent":
                Absent(sql);
                break;
            case "refresh":
                if (Membership(sql) is { } membership)
                {
                    Memberships.RefreshConnection(sql, membership);
                }
                break;
        }
    }

    public override void Unsubscribed()
    {
        using var sql = OpenSql();
        Absent(sql);
    }

    private void Present(Sql sql)
    {
        if (Membership(sql) is not { } membership)
        {
            return;
        }

        Memberships.Present(sql, membership);
        Server.Broadcast(StreamNames.UserReads(User.Id), CableFrames.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("room_id", membership.RoomId);
            writer.WriteEndObject();
        }));
    }

    private void Absent(Sql sql)
    {
        if (Membership(sql) is { } membership)
        {
            Memberships.Disconnected(sql, membership);
        }
    }

    // Reloaded every time: other connections (tabs) change the same row.
    private Membership? Membership(Sql sql) => Memberships.Find(sql, RoomId, User.Id);
}

/// <summary>"Jason is typing…": relays start/stop to everyone in the room (the sender ignores its own).</summary>
internal sealed class TypingNotificationsChannel(ChannelContext context, long? roomId) : RoomChannel(context, roomId)
{
    public override IReadOnlyList<string>? Subscribe()
    {
        using var sql = OpenSql();
        return FindRoom(sql) ? [StreamNames.Typing(RoomId)] : null;
    }

    public override void Perform(string action, JsonElement data)
    {
        if (action is not ("start" or "stop"))
        {
            return;
        }

        Server.Broadcast(StreamNames.Typing(RoomId), CableFrames.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteStartObject("user");
            writer.WriteNumber("id", User.Id);
            writer.WriteString("name", User.Name);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));
    }
}
