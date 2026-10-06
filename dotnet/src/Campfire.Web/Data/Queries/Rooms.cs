using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

public static partial class Rooms
{
    private static readonly string Select = $"SELECT {Rows.RoomColumns()} FROM rooms";

    public static Room? Find(Sql sql, long id) =>
        sql.First($"{Select} WHERE id = @id", r => Rows.ReadRoom(r), ("@id", id));

    /// <summary><c>Current.user.rooms.find_by(id:)</c>, optionally narrowed to some room types.</summary>
    public static Room? FindForUser(Sql sql, long userId, long roomId, RoomTypes types = RoomTypes.All) =>
        sql.First($"""
            {Select} JOIN memberships m ON m.room_id = rooms.id
            WHERE rooms.id = @room AND m.user_id = @user AND {TypeFilter(types)}
            """, r => Rows.ReadRoom(r), ("@room", roomId), ("@user", userId));

    /// <summary><c>Room.original</c>: the account's first room (the welcome room).</summary>
    public static Room? Original(Sql sql) =>
        sql.First($"{Select} ORDER BY created_at, id LIMIT 1", r => Rows.ReadRoom(r));

    /// <summary><c>Current.user.rooms.original</c>.</summary>
    public static Room? OriginalForUser(Sql sql, long userId) =>
        sql.First($"{Select} JOIN memberships m ON m.room_id = rooms.id WHERE m.user_id = @user ORDER BY rooms.created_at, rooms.id LIMIT 1",
            r => Rows.ReadRoom(r), ("@user", userId));

    /// <summary><c>Current.user.rooms.last</c> (by id).</summary>
    public static Room? LastForUser(Sql sql, long userId) =>
        sql.First($"{Select} JOIN memberships m ON m.room_id = rooms.id WHERE m.user_id = @user ORDER BY rooms.id DESC LIMIT 1",
            r => Rows.ReadRoom(r), ("@user", userId));

    public static bool AnyForUser(Sql sql, long userId) =>
        sql.Exists("SELECT 1 FROM memberships WHERE user_id = @user", ("@user", userId));

    public static Room Create(Sql sql, string? name, RoomType type, long creatorId)
    {
        var now = SqlTime.UtcNow();
        var id = sql.Insert("INSERT INTO rooms (name, type, creator_id, created_at, updated_at) VALUES (@name, @type, @creator, @now, @now)",
            ("@name", name), ("@type", type.ToStored()), ("@creator", creatorId), ("@now", now));
        return new Room(id, name, type, creatorId, now, now);
    }

    public static void Touch(Sql sql, long roomId) =>
        sql.Execute("UPDATE rooms SET updated_at = @now WHERE id = @id", ("@now", SqlTime.UtcNow()), ("@id", roomId));

    /// <summary>Renames and/or converts a room. Direct rooms never change type (enforced by callers' scopes too).</summary>
    public static Room Update(Sql sql, Room room, string? name, RoomType type)
    {
        if (room.IsDirect && type != RoomType.Direct)
        {
            throw new InvalidOperationException("Type can't be changed for a direct room");
        }

        var now = SqlTime.UtcNow();
        sql.Execute("UPDATE rooms SET name = @name, type = @type, updated_at = @now WHERE id = @id",
            ("@name", name), ("@type", type.ToStored()), ("@now", now), ("@id", room.Id));
        return room with { Name = name, Type = type, UpdatedAt = now };
    }

    /// <summary>
    /// Destroys a room and everything in it, as Rails' dependent: :destroy / :delete_all chain does:
    /// boosts, message bodies, attachments, search entries, messages and memberships.
    /// Blob files are left for <see cref="Campfire.Web.Storage"/> to purge (returned keys).
    /// </summary>
    public static List<string> Delete(Sql sql, long roomId) => sql.Transaction(tx =>
    {
        const string messageIds = "SELECT id FROM messages WHERE room_id = @room";
        var blobs = tx.Query($"""
            SELECT b.id, b.key FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id
            WHERE a.record_type = 'Message' AND a.record_id IN ({messageIds})
            """, r => (Id: r.GetInt64(0), Key: r.GetString(1)), ("@room", roomId));

        tx.Execute($"DELETE FROM boosts WHERE message_id IN ({messageIds})", ("@room", roomId));
        tx.Execute($"DELETE FROM action_text_rich_texts WHERE record_type = 'Message' AND record_id IN ({messageIds})", ("@room", roomId));
        tx.Execute($"DELETE FROM message_search_index WHERE rowid IN ({messageIds})", ("@room", roomId));
        tx.Execute($"DELETE FROM active_storage_attachments WHERE record_type = 'Message' AND record_id IN ({messageIds})", ("@room", roomId));
        Attachments.DeleteBlobRows(tx, blobs.Select(blob => blob.Id));
        tx.Execute("DELETE FROM messages WHERE room_id = @room", ("@room", roomId));
        var blobKeys = blobs.Select(blob => blob.Key).ToList();
        tx.Execute("DELETE FROM memberships WHERE room_id = @room", ("@room", roomId));
        tx.Execute("DELETE FROM rooms WHERE id = @room", ("@room", roomId));
        return blobKeys;
    });

    /// <summary>Ids of every user in a room.</summary>
    public static List<long> UserIds(Sql sql, long roomId) =>
        sql.Query("SELECT user_id FROM memberships WHERE room_id = @room", r => r.GetInt64(0), ("@room", roomId));

    /// <summary>Users in a room, ordered by name.</summary>
    public static List<User> Users(Sql sql, long roomId) =>
        sql.Query($"SELECT {Rows.UserColumns()} FROM users JOIN memberships m ON m.user_id = users.id WHERE m.room_id = @room ORDER BY LOWER(users.name)",
            r => Rows.ReadUser(r), ("@room", roomId));

    /// <summary>
    /// Rails' <c>room_display_name</c>: a direct room is named after its other participants
    /// ("Ann, Bob, and Cy"), falling back to the viewer's own name when they're alone in it.
    /// Pass <paramref name="forUserId"/> = null to list everyone (as cached message fragments do).
    /// </summary>
    public static string DisplayName(Sql sql, Room room, User? forUser)
    {
        if (!room.IsDirect)
        {
            return room.Name ?? "";
        }

        var names = sql.Query("""
            SELECT users.name FROM users JOIN memberships m ON m.user_id = users.id
            WHERE m.room_id = @room AND users.id != @user ORDER BY m.id
            """, r => r.GetString(0), ("@room", room.Id), ("@user", forUser?.Id ?? 0));

        return names.Count > 0 ? Text.ToSentence(names) : forUser?.Name ?? "";
    }

    /// <summary>Direct rooms with exactly the given set of members (<c>Rooms::Direct.find_for</c>).</summary>
    public static Room? FindDirectWithExactly(Sql sql, IReadOnlyCollection<long> userIds)
    {
        var distinct = userIds.Distinct().ToArray();
        return sql.First($"""
            {Select} WHERE rooms.type = 'Rooms::Direct'
              AND (SELECT COUNT(*) FROM memberships m WHERE m.room_id = rooms.id) = @count
              AND (SELECT COUNT(*) FROM memberships m WHERE m.room_id = rooms.id AND m.user_id IN (SELECT value FROM json_each(@ids))) = @count
            ORDER BY rooms.id LIMIT 1
            """, r => Rows.ReadRoom(r), ("@count", (long)distinct.Length), ("@ids", IdList.Json(distinct)));
    }

    internal static string TypeFilter(RoomTypes types) => types switch
    {
        RoomTypes.All => "1 = 1",
        RoomTypes.WithoutDirects => "rooms.type != 'Rooms::Direct'",
        RoomTypes.DirectsOnly => "rooms.type = 'Rooms::Direct'",
        _ => throw new ArgumentOutOfRangeException(nameof(types))
    };
}

/// <summary>The room scopes controllers act within (<c>room_scope</c> in the Rails controllers).</summary>
public enum RoomTypes
{
    All,
    WithoutDirects,
    DirectsOnly
}

public static class Text
{
    /// <summary>Rails' <c>Array#to_sentence</c>: "a", "a and b", "a, b, and c".</summary>
    public static string ToSentence(IReadOnlyList<string> words, string twoWordsConnector = " and ") => words.Count switch
    {
        0 => "",
        1 => words[0],
        2 => words[0] + twoWordsConnector + words[1],
        _ => string.Join(", ", words.Take(words.Count - 1)) + ", and " + words[^1]
    };
}
