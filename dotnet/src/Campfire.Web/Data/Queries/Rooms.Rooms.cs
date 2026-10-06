using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

// Queries for the Rooms & Messages workstream (rooms, sidebar, autocomplete, bots).

public static partial class Rooms
{
    public static List<Room> FindMany(Sql sql, IEnumerable<long> ids) =>
        sql.Query($"SELECT {Rows.RoomColumns()} FROM rooms WHERE id IN (SELECT value FROM json_each(@ids))",
            r => Rows.ReadRoom(r), ("@ids", IdList.Json(ids)));

    /// <summary>
    /// Members of several rooms at once, in membership order (Rails' <c>room.users</c> has no order,
    /// which SQLite returns as membership insertion order), keyed by room id.
    /// </summary>
    public static Dictionary<long, List<User>> UsersByRoom(Sql sql, IEnumerable<long> roomIds)
    {
        var users = new Dictionary<long, List<User>>();
        sql.Each($"""
            SELECT m.room_id, {Rows.UserColumns()} FROM users JOIN memberships m ON m.user_id = users.id
            WHERE m.room_id IN (SELECT value FROM json_each(@ids)) ORDER BY m.id
            """, r =>
        {
            var roomId = r.GetInt64(0);
            if (!users.TryGetValue(roomId, out var list))
            {
                users[roomId] = list = [];
            }
            list.Add(Rows.ReadUser(r, 1));
        }, ("@ids", IdList.Json(roomIds)));
        return users;
    }

    /// <summary><c>room_display_name(room, for_user:)</c> for many rooms, sharing one members query.</summary>
    public static Dictionary<long, string> DisplayNames(Sql sql, IReadOnlyCollection<Room> rooms, User? forUser)
    {
        var directIds = rooms.Where(room => room.IsDirect).Select(room => room.Id).Distinct().ToList();
        var members = directIds.Count > 0 ? UsersByRoom(sql, directIds) : [];
        var names = new Dictionary<long, string>();
        foreach (var room in rooms)
        {
            if (!room.IsDirect)
            {
                names[room.Id] = room.Name ?? "";
                continue;
            }

            var others = members.GetValueOrDefault(room.Id, []).Where(user => user.Id != forUser?.Id).Select(user => user.Name).ToList();
            names[room.Id] = others.Count > 0 ? Text.ToSentence(others) : forUser?.Name ?? "";
        }
        return names;
    }

    /// <summary>The room's users other than one (<c>room.users.without(user)</c>), in membership order.</summary>
    public static List<User> UsersExcept(Sql sql, long roomId, long exceptUserId) =>
        sql.Query($"""
            SELECT {Rows.UserColumns()} FROM users JOIN memberships m ON m.user_id = users.id
            WHERE m.room_id = @room AND users.id != @user ORDER BY m.id
            """, r => Rows.ReadUser(r), ("@room", roomId), ("@user", exceptUserId));

    /// <summary>The room's users with the given ids (<c>room.users.where(id:)</c>).</summary>
    public static List<User> UsersAmong(Sql sql, long roomId, IEnumerable<long> userIds) =>
        sql.Query($"""
            SELECT {Rows.UserColumns()} FROM users JOIN memberships m ON m.user_id = users.id
            WHERE m.room_id = @room AND users.id IN (SELECT value FROM json_each(@ids)) ORDER BY m.id
            """, r => Rows.ReadUser(r), ("@room", roomId), ("@ids", IdList.Json(userIds)));
}

public static partial class Users
{
    /// <summary>
    /// <c>User.active.where.not(id:).order(:created_at).limit(n)</c>: people to offer as new
    /// direct-message partners in the sidebar.
    /// </summary>
    public static List<User> ActiveExcept(Sql sql, IEnumerable<long> excludedIds, int limit) =>
        limit <= 0 ? [] : sql.Query($"""
            SELECT {Rows.UserColumns()} FROM users
            WHERE status = 0 AND id NOT IN (SELECT value FROM json_each(@ids))
            ORDER BY created_at, id LIMIT @limit
            """, r => Rows.ReadUser(r), ("@ids", IdList.Json(excludedIds)), ("@limit", (long)limit));

    /// <summary>
    /// Autocompletable users: active, optionally only a room's members, optionally filtered by a
    /// name fragment (<c>name like %query%</c>), ordered by name.
    /// </summary>
    public static List<User> Autocompletable(Sql sql, long? roomId, string? query, int limit, int offset = 0)
    {
        var roomFilter = roomId is null ? "" : "AND users.id IN (SELECT user_id FROM memberships WHERE room_id = @room)";
        var nameFilter = string.IsNullOrEmpty(query) ? "" : "AND users.name LIKE @query";
        return sql.Query($"""
            SELECT {Rows.UserColumns()} FROM users
            WHERE users.status = 0 {roomFilter} {nameFilter}
            ORDER BY LOWER(users.name), users.id LIMIT @limit OFFSET @offset
            """, r => Rows.ReadUser(r),
            ("@room", roomId), ("@query", $"%{query}%"), ("@limit", (long)limit), ("@offset", (long)offset));
    }

    /// <summary><c>User.where(id:)</c> restricted to ids that exist.</summary>
    public static List<long> ExistingIds(Sql sql, IEnumerable<long> ids) =>
        sql.Query("SELECT id FROM users WHERE id IN (SELECT value FROM json_each(@ids))", r => r.GetInt64(0), ("@ids", IdList.Json(ids)));
}

public static partial class Memberships
{
    /// <summary>Ids of everyone who shares a direct room with the user (including the user).</summary>
    public static List<long> DirectPartnerIds(Sql sql, long userId) =>
        sql.Query("""
            SELECT DISTINCT m.user_id FROM memberships m
            WHERE m.room_id IN (
              SELECT rooms.id FROM rooms JOIN memberships mine ON mine.room_id = rooms.id
              WHERE mine.user_id = @user AND rooms.type = 'Rooms::Direct')
            """, r => r.GetInt64(0), ("@user", userId));

    /// <summary>Memberships of a room with their users (for per-member broadcasts).</summary>
    public static List<(Membership Membership, User User)> ForRoomWithUsers(Sql sql, long roomId) =>
        sql.Query($"""
            SELECT {Rows.MembershipColumns()}, {Rows.UserColumns()} FROM memberships JOIN users ON users.id = memberships.user_id
            WHERE memberships.room_id = @room ORDER BY memberships.id
            """, r => (Rows.ReadMembership(r), Rows.ReadUser(r, Rows.MembershipWidth)), ("@room", roomId));
}
