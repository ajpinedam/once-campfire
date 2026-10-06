using Campfire.Web.Cable;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Push;
using Campfire.Web.RichText;
using Campfire.Web.Turbo;
using Campfire.Web.Views;
using Campfire.Web.Webhooks;

namespace Campfire.Web.Features.Messages;

/// <summary>
/// Posting a message, everything Rails does around <c>room.messages.create_with_attachment!</c>:
/// store it, mark the room unread for members who aren't looking, queue pushes, broadcast it,
/// and (optionally) hand it to bots' webhooks.
/// </summary>
public sealed class MessagePosting(
    RichTextService richText,
    MessageRenderer renderer,
    WriteBatcher writes,
    MessageBroadcasts broadcasts,
    MessagePushes pushes,
    WebhookDispatcher webhooks,
    ILogger<MessagePosting> logger)
{
    /// <param name="bodyHtml">Submitted rich text (canonicalized before storing); null for attachment-only messages.</param>
    /// <param name="attachment">An already-stored blob to attach, or null.</param>
    /// <param name="baseUrl">Origin for absolute URLs in rendered HTML, e.g. <c>https://chat.example.com</c>.</param>
    /// <param name="deliverWebhooks">
    /// MessagesController#create delivers to bots (in direct rooms: every bot member; elsewhere: mentioned bots),
    /// never to the creator. Webhook replies pass false.
    /// </param>
    public async Task<Message> PostAsync(Sql sql, Room room, User creator, string? bodyHtml, Blob? attachment, string? clientMessageId, string baseUrl, bool deliverWebhooks, CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(sql, bodyHtml);
        var body = prepared?.Canonical;
        var plainText = string.IsNullOrWhiteSpace(prepared?.PlainText) ? attachment?.Filename ?? "" : prepared.PlainText;

        // One group-committed write: the message, its attachment and Room#receive's unread marks
        // (after_create_commit in Rails) land together, batched with concurrent posts.
        var message = await writes.RunAsync(tx =>
        {
            var created = Data.Queries.Messages.Create(tx, room.Id, creator.Id, clientMessageId, body, plainText);
            if (attachment is not null)
            {
                Attachments.Attach(tx, "Message", created.Id, "attachment", attachment.Id);
            }
            Memberships.MarkUnread(tx, room.Id, creator.Id, created.CreatedAt);
            return created;
        });

        pushes.PushLater(room.Id, message.Id);

        broadcasts.Created(sql, message, await renderer.RenderNewAsync(sql, message, creator, room, body, attachment, plainText, baseUrl, cancellationToken));

        if (deliverWebhooks)
        {
            foreach (var bot in BotsEligibleForWebhook(sql, room, prepared?.MentionedUserIds ?? []))
            {
                if (bot.Id != creator.Id)
                {
                    webhooks.DeliverLater(bot.Id, message.Id, baseUrl);
                }
            }
        }

        return message;
    }

    /// <summary>In direct rooms every bot hears everything; elsewhere only bots that were @mentioned.</summary>
    private static List<User> BotsEligibleForWebhook(Sql sql, Room room, IReadOnlyList<long> mentionedUserIds)
    {
        var candidates = room.IsDirect
            ? Data.Queries.Rooms.Users(sql, room.Id)
            : mentionedUserIds.Count == 0 ? [] : Data.Queries.Rooms.UsersAmong(sql, room.Id, mentionedUserIds);

        return candidates.Where(user => user.IsBot && user.IsActive).ToList();
    }

    /// <summary>Canonical body, plain text and mentions from one parse (null for attachment-only messages).</summary>
    private PreparedBody? Prepare(Sql sql, string? bodyHtml)
    {
        if (bodyHtml is null)
        {
            return null;
        }

        try
        {
            return richText.Prepare(sql, bodyHtml);
        }
        catch (Exception error)
        {
            // As MessageRenderer.PlainText did: a body that can't be flattened is still stored.
            logger.LogError(error, "Couldn't prepare a message body; storing it without plain text");
            return new PreparedBody(richText.Canonicalize(bodyHtml), "", []);
        }
    }
}

/// <summary>Rails' Message::Broadcasts.</summary>
public sealed class MessageBroadcasts(CableServer cable, MessageRenderer renderer)
{
    /// <summary><c>broadcast_create</c>: appends the message to its room's stream and pings members' unread streams.</summary>
    public async Task CreatedAsync(Sql sql, Message message, string baseUrl, CancellationToken cancellationToken = default) =>
        Created(sql, message, await renderer.RenderStringAsync(sql, message, baseUrl, cancellationToken));

    /// <summary><c>broadcast_create</c> with the message's fragment already rendered.</summary>
    public void Created(Sql sql, Message message, string html)
    {
        cable.BroadcastTurboStream(StreamNames.RoomMessages(message.RoomId), TurboStream.Append(DomId.Room(message.RoomId, "messages"), html));
        BroadcastUnreadRoom(sql, message.RoomId);
    }

    /// <summary><c>broadcast_remove</c>: removes the message from its room's stream.</summary>
    public void Removed(Message message) =>
        cable.BroadcastTurboStream(StreamNames.RoomMessages(message.RoomId), TurboStream.Remove(DomId.For(message)));

    /// <summary>MessagesController#update: swaps in the edited presentation, keeping readers' scroll position.</summary>
    public async Task UpdatedAsync(Sql sql, Message message, string baseUrl, CancellationToken cancellationToken = default)
    {
        var body = RichTexts.Find(sql, "Message", message.Id);
        var attachment = Attachments.Find(sql, "Message", message.Id, "attachment");
        var plainText = renderer.PlainText(sql, body, attachment);
        var presentation = renderer.Presentation(sql, message, body, attachment, plainText, baseUrl);
        var html = await Slices.RenderAsync(Views.Messages.Presentation.Create(new PresentationView(message, presentation)), cancellationToken);

        cable.BroadcastTurboStream(StreamNames.RoomMessages(message.RoomId),
            TurboStream.Replace(DomId.For(message, "presentation"), html, maintainScroll: true));
    }

    // Fanned out to the room's members rather than published on one global stream, so that the
    // timing of activity in a room only reaches people who are in it.
    private void BroadcastUnreadRoom(Sql sql, long roomId)
    {
        var payload = $"{{\"roomId\":{roomId}}}";
        foreach (var userId in Data.Queries.Rooms.UserIds(sql, roomId))
        {
            cable.Broadcast(StreamNames.UserUnreads(userId), payload);
        }
    }
}
