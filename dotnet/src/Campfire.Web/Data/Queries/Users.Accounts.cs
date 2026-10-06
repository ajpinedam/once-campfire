using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

// Queries for the Accounts & People workstream.

public static partial class Users
{
    /// <summary>
    /// The people an account settings page lists, ordered by name, one page at a time:
    /// administrators see active and banned humans (so they can lift bans), everyone else only
    /// active ones (<c>AccountsController#account_users ... .ordered.without_bots</c>).
    /// Returns one extra row beyond <paramref name="perPage"/> so callers can tell if more follow.
    /// </summary>
    public static List<User> HumansPage(Sql sql, bool includeBanned, int page, int perPage) =>
        sql.Query($"""
            SELECT {Rows.UserColumns()} FROM users
            WHERE role != 2 AND status IN ({(includeBanned ? "0, 2" : "0")})
            ORDER BY LOWER(name), id LIMIT @limit OFFSET @offset
            """, r => Rows.ReadUser(r), ("@limit", (long)perPage + 1), ("@offset", (long)(Math.Max(page, 1) - 1) * perPage));

    /// <summary><c>User.create_bot!</c>: a bot with a fresh token (and the open-room memberships every user gets).</summary>
    public static User CreateBot(Sql sql, string name) =>
        Create(sql, name, emailAddress: null, password: null, UserRole.Bot, botToken: Security.SecureTokens.BotToken());

    public static void Rename(Sql sql, long userId, string name) =>
        sql.Execute("UPDATE users SET name = @name, updated_at = @now WHERE id = @id",
            ("@name", name), ("@now", SqlTime.UtcNow()), ("@id", userId));
}

public static partial class Messages
{
    /// <summary>Every message a user wrote, oldest first (<c>user.messages</c>).</summary>
    public static List<Message> ByCreator(Sql sql, long userId) =>
        sql.Query($"SELECT {Rows.MessageColumns()} FROM messages WHERE creator_id = @user ORDER BY created_at, id",
            r => Rows.ReadMessage(r), ("@user", userId));
}

public static partial class Rooms
{
    /// <summary><c>user.rooms.without_directs.ordered</c>.</summary>
    public static List<Room> SharedForUserOrdered(Sql sql, long userId) =>
        sql.Query($"""
            SELECT {Rows.RoomColumns()} FROM rooms JOIN memberships m ON m.room_id = rooms.id
            WHERE m.user_id = @user AND rooms.type != 'Rooms::Direct' ORDER BY LOWER(rooms.name)
            """, r => Rows.ReadRoom(r), ("@user", userId));
}

public static partial class Bans
{
    public static long CountForUser(Sql sql, long userId) =>
        sql.ScalarLong("SELECT COUNT(*) FROM bans WHERE user_id = @user", ("@user", userId));
}
