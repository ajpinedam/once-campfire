using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Jobs;
using Campfire.Web.RichText;
using Campfire.Web.Views;

namespace Campfire.Web.Push;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>Rails' Room::PushMessageJob.</summary>
public sealed record PushMessageJob(long RoomId, long MessageId) : IBackgroundJob
{
    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        services.GetRequiredService<MessagePusher>().Push(RoomId, MessageId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Rails' Room::MessagePusher: builds the notification for a new message and queues it for every
/// subscription that should hear about it. Badges are computed here (on the enqueue path) and DNS
/// is left to the delivery workers.
/// </summary>
public sealed class MessagePusher(Database database, RichTextService richText, WebPushPool pool)
{
    public void Push(long roomId, long messageId)
    {
        using var sql = database.Open();
        if (Messages.FindInRoom(sql, roomId, messageId) is not { } message ||
            Rooms.Find(sql, roomId) is not { } room ||
            Users.Find(sql, message.CreatorId) is not { } creator)
        {
            return; // gone before the job ran
        }

        var body = RichTexts.Find(sql, "Message", message.Id);
        var mentionees = body is null ? [] : richText.MentionedUserIds(body);

        // Most messages notify nobody; find that out before flattening the body for the payload.
        var subscriptions = PushSubscriptions.ForNewMessage(sql, room.Id, creator.Id, mentionees);
        if (subscriptions.Count == 0)
        {
            return;
        }

        var (title, text) = Payload(room, creator, PlainTextBody(sql, message, body));

        var badges = PushSubscriptions.UnreadRoomCounts(sql, subscriptions.Select(subscription => subscription.UserId).Distinct());
        pool.Queue(subscriptions.Select(subscription => new PushDelivery(
            subscription.Id, subscription.Endpoint, subscription.P256dhKey, subscription.AuthKey,
            new WebPushMessage(title, text, Paths.Room(room.Id), badges.GetValueOrDefault(subscription.UserId), Paths.AccountLogo))));
    }

    /// <summary>Direct rooms read like a conversation with the author; shared rooms name the room.</summary>
    internal static (string Title, string Body) Payload(Room room, User creator, string plainText) =>
        room.IsDirect
            ? (creator.Name, plainText)
            : (room.Name ?? "", $"{creator.Name}: {plainText}");

    /// <summary>Rails' <c>plain_text_body</c>: the text, else the attachment's filename, else nothing.</summary>
    private string PlainTextBody(Sql sql, Message message, string? body)
    {
        var text = body is null ? "" : richText.ToPlainText(sql, body);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return Attachments.Find(sql, "Message", message.Id, "attachment")?.Filename ?? "";
    }
}
