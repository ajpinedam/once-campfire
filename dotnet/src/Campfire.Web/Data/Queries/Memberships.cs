using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

public static partial class Memberships
{
    private static readonly string Select = $"SELECT {Rows.MembershipColumns()} FROM memberships";

    /// <summary>SQL predicate for Rails' <c>disconnected</c> scope; binds <c>@cutoff</c>.</summary>
    public const string DisconnectedPredicate = "(memberships.connected_at IS NULL OR memberships.connected_at < @cutoff)";

    public static SqlParam ConnectionCutoff() => ("@cutoff", SqlTime.UtcNow() - Membership.ConnectionTtl);

    public static Membership? Find(Sql sql, long roomId, long userId) =>
        sql.First($"{Select} WHERE room_id = @room AND user_id = @user", r => Rows.ReadMembership(r), ("@room", roomId), ("@user", userId));

    /// <summary><c>Current.user.memberships.find_by!(room_id:)</c> with its room (RoomScoped).</summary>
    public static (Membership Membership, Room Room)? FindWithRoom(Sql sql, long userId, long roomId)
    {
        (Membership, Room)? result = null;
        sql.Each($"SELECT {Rows.MembershipColumns()}, {Rows.RoomColumns()} FROM memberships JOIN rooms ON rooms.id = memberships.room_id WHERE memberships.user_id = @user AND memberships.room_id = @room",
            r => result = (Rows.ReadMembership(r), Rows.ReadRoom(r, Rows.MembershipWidth)),
            ("@user", userId), ("@room", roomId));
        return result;
    }

    /// <summary>All of a user's memberships with their rooms, ordered by room name (<c>with_ordered_room</c>).</summary>
    public static List<(Membership Membership, Room Room)> ForUserWithRooms(Sql sql, long userId, bool visibleOnly) =>
        sql.Query($"""
            SELECT {Rows.MembershipColumns()}, {Rows.RoomColumns()} FROM memberships JOIN rooms ON rooms.id = memberships.room_id
            WHERE memberships.user_id = @user {(visibleOnly ? "AND memberships.involvement != 'invisible'" : "")}
            ORDER BY LOWER(rooms.name)
            """, r => (Rows.ReadMembership(r), Rows.ReadRoom(r, Rows.MembershipWidth)), ("@user", userId));

    /// <summary>
    /// <c>room.memberships.grant_to(users)</c>: inserts memberships with the room's default involvement,
    /// skipping users who are already members (insert_all's ON CONFLICT DO NOTHING).
    /// </summary>
    public static void GrantTo(Sql sql, Room room, IEnumerable<long> userIds)
    {
        var ids = IdList.Json(userIds);
        if (ids == "[]")
        {
            return;
        }

        sql.Execute("""
            INSERT INTO memberships (room_id, user_id, involvement, connections, created_at, updated_at)
            SELECT @room, value, @involvement, 0, @now, @now FROM json_each(@ids) WHERE true
            ON CONFLICT (room_id, user_id) DO NOTHING
            """, ("@room", room.Id), ("@involvement", room.DefaultInvolvement.ToStored()), ("@now", SqlTime.UtcNow()), ("@ids", ids));
    }

    /// <summary>Grants every active user membership of an open room (<c>grant_access_to_all_users</c>).</summary>
    public static void GrantToAllActiveUsers(Sql sql, Room room) =>
        sql.Execute("""
            INSERT INTO memberships (room_id, user_id, involvement, connections, created_at, updated_at)
            SELECT @room, id, @involvement, 0, @now, @now FROM users WHERE status = 0
            ON CONFLICT (room_id, user_id) DO NOTHING
            """, ("@room", room.Id), ("@involvement", room.DefaultInvolvement.ToStored()), ("@now", SqlTime.UtcNow()));

    /// <summary>Removes memberships; callers reset the revoked users' cable connections afterwards.</summary>
    public static void RevokeFrom(Sql sql, long roomId, IEnumerable<long> userIds) =>
        sql.Execute("DELETE FROM memberships WHERE room_id = @room AND user_id IN (SELECT value FROM json_each(@ids))",
            ("@room", roomId), ("@ids", IdList.Json(userIds)));

    public static void UpdateInvolvement(Sql sql, long membershipId, Involvement involvement) =>
        sql.Execute("UPDATE memberships SET involvement = @involvement, updated_at = @now WHERE id = @id",
            ("@involvement", involvement.ToStored()), ("@now", SqlTime.UtcNow()), ("@id", membershipId));

    public static void Read(Sql sql, long membershipId) =>
        sql.Execute("UPDATE memberships SET unread_at = NULL, updated_at = @now WHERE id = @id", ("@now", SqlTime.UtcNow()), ("@id", membershipId));

    /// <summary>
    /// Rails' <c>Room#unread_memberships</c>: marks the room unread for visible members who aren't
    /// connected to it right now, other than the message's author.
    /// </summary>
    public static void MarkUnread(Sql sql, long roomId, long exceptUserId, DateTime unreadAt) =>
        sql.Execute($"""
            UPDATE memberships SET unread_at = @unread, updated_at = @now
            WHERE room_id = @room AND user_id != @user AND involvement != 'invisible' AND {DisconnectedPredicate}
            """, ("@unread", unreadAt), ("@now", SqlTime.UtcNow()), ("@room", roomId), ("@user", exceptUserId), ConnectionCutoff());

    public static long UnreadCount(Sql sql, long userId) =>
        sql.ScalarLong("SELECT COUNT(*) FROM memberships WHERE user_id = @user AND unread_at IS NOT NULL", ("@user", userId));

    // Presence (Membership::Connectable)

    /// <summary><c>present</c>: counts one more connection (or starts at one) and marks the room read.</summary>
    public static void Present(Sql sql, Membership membership)
    {
        var now = SqlTime.UtcNow();
        var connections = membership.IsConnected(now) ? membership.Connections + 1 : 1;
        sql.Execute("UPDATE memberships SET connections = @connections, connected_at = @now, unread_at = NULL WHERE id = @id",
            ("@connections", (long)connections), ("@now", now), ("@id", membership.Id));
    }

    /// <summary><c>disconnected</c>: one fewer connection; clears connected_at when none remain.</summary>
    public static void Disconnected(Sql sql, Membership membership)
    {
        var now = SqlTime.UtcNow();
        var connections = membership.IsConnected(now) ? membership.Connections - 1 : 0;
        sql.Execute("UPDATE memberships SET connections = @connections, connected_at = CASE WHEN @connections < 1 THEN NULL ELSE connected_at END, updated_at = @now WHERE id = @id",
            ("@connections", (long)connections), ("@now", now), ("@id", membership.Id));
    }

    /// <summary><c>refresh_connection</c>: keeps a live connection from expiring.</summary>
    public static void RefreshConnection(Sql sql, Membership membership)
    {
        var now = SqlTime.UtcNow();
        var connections = membership.IsConnected(now) ? membership.Connections : 1;
        sql.Execute("UPDATE memberships SET connections = @connections, connected_at = @now, updated_at = @now WHERE id = @id",
            ("@connections", (long)connections), ("@now", now), ("@id", membership.Id));
    }

    /// <summary><c>Membership.disconnect_all</c>; run at boot, when no connection can still be open.</summary>
    public static void DisconnectAll(Sql sql) =>
        sql.Execute("UPDATE memberships SET connected_at = NULL, connections = 0, updated_at = @now WHERE connected_at IS NOT NULL",
            ("@now", SqlTime.UtcNow()));
}
