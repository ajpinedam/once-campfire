using System.Text;
using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

/// <summary>A permitted <c>push_subscription</c> parameter: whether it was sent, and its value.</summary>
public readonly record struct SubmittedValue(bool Present, string? Value)
{
    public static readonly SubmittedValue Absent = new(false, null);
}

public static partial class PushSubscriptions
{
    /// <summary>
    /// Room::MessagePusher's audience: subscriptions of the room's members who can see it, aren't
    /// connected to it, didn't write the message, and want everything — or want mentions and were mentioned.
    /// </summary>
    public static List<PushSubscription> ForNewMessage(Sql sql, long roomId, long creatorId, IEnumerable<long> mentioneeIds) =>
        sql.Query($"""
            SELECT {Rows.PushSubscriptionColumns} FROM push_subscriptions
            JOIN memberships ON memberships.user_id = push_subscriptions.user_id
            WHERE memberships.room_id = @room
              AND memberships.user_id != @creator
              AND memberships.involvement != 'invisible'
              AND {Memberships.DisconnectedPredicate}
              AND (memberships.involvement = 'everything'
                OR (memberships.involvement = 'mentions' AND memberships.user_id IN (SELECT value FROM json_each(@mentionees))))
            ORDER BY push_subscriptions.id
            """, r => Rows.ReadPushSubscription(r),
            ("@room", roomId), ("@creator", creatorId), ("@mentionees", IdList.Json(mentioneeIds)), Memberships.ConnectionCutoff());

    /// <summary>Each user's count of unread rooms (the app badge), for many users at once.</summary>
    public static Dictionary<long, long> UnreadRoomCounts(Sql sql, IEnumerable<long> userIds)
    {
        var counts = new Dictionary<long, long>();
        sql.Each("""
            SELECT user_id, COUNT(*) FROM memberships
            WHERE user_id IN (SELECT value FROM json_each(@users)) AND unread_at IS NOT NULL
            GROUP BY user_id
            """, r => counts[r.GetInt64(0)] = r.GetInt64(1), ("@users", IdList.Json(userIds)));
        return counts;
    }

    /// <summary>
    /// Rails' <c>push_subscriptions.find_by(endpoint:, p256dh_key:, auth_key:)</c> over only the keys
    /// that were submitted (a submitted null matches NULL).
    /// </summary>
    public static PushSubscription? FindBySubmitted(Sql sql, long userId, SubmittedValue endpoint, SubmittedValue p256dhKey, SubmittedValue authKey)
    {
        var where = new StringBuilder("user_id = @user");
        if (endpoint.Present) where.Append(" AND endpoint IS @endpoint");
        if (p256dhKey.Present) where.Append(" AND p256dh_key IS @p256dh");
        if (authKey.Present) where.Append(" AND auth_key IS @auth");

        return sql.First($"SELECT {Rows.PushSubscriptionColumns} FROM push_subscriptions WHERE {where} ORDER BY id LIMIT 1",
            r => Rows.ReadPushSubscription(r),
            ("@user", userId), ("@endpoint", endpoint.Value), ("@p256dh", p256dhKey.Value), ("@auth", authKey.Value));
    }
}
