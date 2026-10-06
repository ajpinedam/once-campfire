using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

public static partial class Messages
{
    private static readonly string Select = $"SELECT {Rows.MessageColumns()} FROM messages";

    public static Message? Find(Sql sql, long id) =>
        sql.First($"{Select} WHERE id = @id", r => Rows.ReadMessage(r), ("@id", id));

    public static Message? FindInRoom(Sql sql, long roomId, long id) =>
        sql.First($"{Select} WHERE room_id = @room AND id = @id", r => Rows.ReadMessage(r), ("@room", roomId), ("@id", id));

    /// <summary><c>Current.user.reachable_messages.find(id)</c>: a message in one of the user's rooms.</summary>
    public static Message? FindReachable(Sql sql, long userId, long id) =>
        sql.First($"{Select} JOIN memberships m ON m.room_id = messages.room_id WHERE messages.id = @id AND m.user_id = @user",
            r => Rows.ReadMessage(r), ("@id", id), ("@user", userId));

    /// <summary>
    /// Inserts a message (and its rich text body when given), touches the room, and indexes the
    /// plain text for search — the synchronous half of Rails' create + after_create_commit hooks.
    /// Unread marking, pushes, broadcasts and webhooks are the caller's job (see Features/Messages).
    /// </summary>
    public static Message Create(Sql sql, long roomId, long creatorId, string? clientMessageId, string? bodyHtml, string plainText) =>
        sql.Transaction(tx =>
        {
            var now = SqlTime.UtcNow();
            var clientId = string.IsNullOrWhiteSpace(clientMessageId) ? Guid.NewGuid().ToString() : clientMessageId;
            var id = tx.Insert("INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at) VALUES (@room, @creator, @client, @now, @now)",
                ("@room", roomId), ("@creator", creatorId), ("@client", clientId), ("@now", now));

            if (bodyHtml is not null)
            {
                RichTexts.Upsert(tx, "Message", id, "body", bodyHtml);
            }

            Rooms.Touch(tx, roomId);
            tx.Execute("INSERT INTO message_search_index (rowid, body) VALUES (@id, @body)", ("@id", id), ("@body", plainText));
            return new Message(id, roomId, creatorId, clientId, now, now);
        });

    /// <summary>Replaces a message's body, touching the message and room and re-indexing it.</summary>
    public static Message UpdateBody(Sql sql, Message message, string bodyHtml, string plainText) =>
        sql.Transaction(tx =>
        {
            var now = SqlTime.UtcNow();
            RichTexts.Upsert(tx, "Message", message.Id, "body", bodyHtml);
            tx.Execute("UPDATE messages SET updated_at = @now WHERE id = @id", ("@now", now), ("@id", message.Id));
            Rooms.Touch(tx, message.RoomId);
            tx.Execute("UPDATE message_search_index SET body = @body WHERE rowid = @id", ("@body", plainText), ("@id", message.Id));
            return message with { UpdatedAt = now };
        });

    public static void Touch(Sql sql, long messageId) =>
        sql.Execute("UPDATE messages SET updated_at = @now WHERE id = @id", ("@now", SqlTime.UtcNow()), ("@id", messageId));

    /// <summary>Destroys a message with its boosts, body, attachment rows and search entry. Returns orphaned blob keys.</summary>
    public static List<string> Delete(Sql sql, long messageId) => sql.Transaction(tx =>
    {
        var blobs = tx.Query("""
            SELECT b.id, b.key FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id
            WHERE a.record_type = 'Message' AND a.record_id = @id
            """, r => (Id: r.GetInt64(0), Key: r.GetString(1)), ("@id", messageId));

        tx.Execute("DELETE FROM boosts WHERE message_id = @id", ("@id", messageId));
        tx.Execute("DELETE FROM action_text_rich_texts WHERE record_type = 'Message' AND record_id = @id", ("@id", messageId));
        tx.Execute("DELETE FROM message_search_index WHERE rowid = @id", ("@id", messageId));
        tx.Execute("DELETE FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = @id", ("@id", messageId));
        Attachments.DeleteBlobRows(tx, blobs.Select(blob => blob.Id));
        tx.Execute("DELETE FROM messages WHERE id = @id", ("@id", messageId));
        return blobs.Select(blob => blob.Key).ToList();
    });

    public static long CountInRoom(Sql sql, long roomId) =>
        sql.ScalarLong("SELECT COUNT(*) FROM messages WHERE room_id = @room", ("@room", roomId));

    /// <summary><c>room.messages.paged?</c>: more than one page of messages exists.</summary>
    public static bool Paged(Sql sql, long roomId) =>
        sql.Exists($"SELECT 1 FROM messages WHERE room_id = @room LIMIT 1 OFFSET {Message.PageSize}", ("@room", roomId));

    // Pagination (Message::Pagination). Pages are always returned oldest first.

    public static List<Message> LastPage(Sql sql, long roomId, int size = Message.PageSize) =>
        Reverse(sql.Query($"{Select} WHERE room_id = @room ORDER BY created_at DESC, id DESC LIMIT @size",
            r => Rows.ReadMessage(r), ("@room", roomId), ("@size", (long)size)));

    public static List<Message> PageBefore(Sql sql, long roomId, Message message, int size = Message.PageSize) =>
        Reverse(sql.Query($"{Select} WHERE room_id = @room AND created_at < @at ORDER BY created_at DESC, id DESC LIMIT @size",
            r => Rows.ReadMessage(r), ("@room", roomId), ("@at", message.CreatedAt), ("@size", (long)size)));

    public static List<Message> PageAfter(Sql sql, long roomId, Message message, int size = Message.PageSize) =>
        sql.Query($"{Select} WHERE room_id = @room AND created_at > @at ORDER BY created_at, id LIMIT @size",
            r => Rows.ReadMessage(r), ("@room", roomId), ("@at", message.CreatedAt), ("@size", (long)size));

    /// <summary><c>page_around</c>: the page before a message, the message, and the page after it.</summary>
    public static List<Message> PageAround(Sql sql, long roomId, Message message)
    {
        var page = PageBefore(sql, roomId, message);
        page.Add(message);
        page.AddRange(PageAfter(sql, roomId, message));
        return page;
    }

    public static List<Message> PageCreatedSince(Sql sql, long roomId, DateTime since) =>
        sql.Query($"{Select} WHERE room_id = @room AND created_at > @since ORDER BY created_at, id LIMIT {Message.PageSize}",
            r => Rows.ReadMessage(r), ("@room", roomId), ("@since", since));

    /// <summary>Messages updated since a time, excluding the ids given (the new messages already sent).</summary>
    public static List<Message> PageUpdatedSince(Sql sql, long roomId, DateTime since, IEnumerable<long> excludingIds) =>
        Reverse(sql.Query($"""
            {Select} WHERE room_id = @room AND updated_at > @since AND id NOT IN (SELECT value FROM json_each(@ids))
            ORDER BY created_at DESC, id DESC LIMIT {Message.PageSize}
            """, r => Rows.ReadMessage(r), ("@room", roomId), ("@since", since), ("@ids", IdList.Json(excludingIds))));

    public static bool ExistsBefore(Sql sql, long roomId, Message message) =>
        sql.Exists("SELECT 1 FROM messages WHERE room_id = @room AND created_at < @at", ("@room", roomId), ("@at", message.CreatedAt));

    public static bool ExistsAfter(Sql sql, long roomId, Message message) =>
        sql.Exists("SELECT 1 FROM messages WHERE room_id = @room AND created_at > @at", ("@room", roomId), ("@at", message.CreatedAt));

    /// <summary>
    /// Full-text search over the messages a user can reach, newest <paramref name="limit"/> matches,
    /// returned oldest first. Walks the FTS index by rowid (= message id), as the Rails app does.
    /// </summary>
    public static List<Message> Search(Sql sql, long userId, string query, int limit = 100)
    {
        var terms = MatchTerms(query);
        if (terms.Length == 0)
        {
            return [];
        }

        return Reverse(sql.Query($"""
            {Select} JOIN message_search_index idx ON messages.id = idx.rowid
            WHERE idx.body MATCH @terms
              AND messages.room_id IN (SELECT room_id FROM memberships WHERE user_id = @user)
            ORDER BY idx.rowid DESC LIMIT @limit
            """, r => Rows.ReadMessage(r), ("@terms", terms), ("@user", userId), ("@limit", (long)limit)));
    }

    /// <summary>Quotes each word so FTS5 treats AND/OR/NOT/NEAR as words, not operators.</summary>
    public static string MatchTerms(string query) =>
        string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => $"\"{word.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

    private static List<Message> Reverse(List<Message> messages)
    {
        messages.Reverse();
        return messages;
    }
}
