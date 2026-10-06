using Campfire.Web.Domain;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Data.Queries;

/// <summary>
/// Column lists and ordinal-based readers for each table. A <c>Columns</c> constant and its
/// <c>Read</c> function always agree on order; <c>offset</c> lets joins read several records
/// from one row (e.g. <c>SELECT {Rows.MessageColumns}, {Rows.UserColumns("u")} ...</c>).
/// </summary>
public static class Rows
{
    public const int AccountWidth = 8;
    public static string AccountColumns(string t = "accounts") =>
        $"{t}.id, {t}.name, {t}.join_code, {t}.custom_styles, {t}.settings, {t}.created_at, {t}.updated_at, " +
        $"EXISTS(SELECT 1 FROM active_storage_attachments asa WHERE asa.record_type = 'Account' AND asa.record_id = {t}.id AND asa.name = 'logo')";

    public static Account ReadAccount(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        Name: r.GetString(o + 1),
        JoinCode: r.GetString(o + 2),
        CustomStyles: r.IsDBNull(o + 3) ? null : r.GetString(o + 3),
        Settings: AccountSettingsJson.Parse(r.IsDBNull(o + 4) ? null : r.GetString(o + 4)),
        HasLogo: r.GetInt64(o + 7) == 1,
        CreatedAt: SqlTime.Get(r, o + 5),
        UpdatedAt: SqlTime.Get(r, o + 6));

    public const int UserWidth = 9;
    public static string UserColumns(string t = "users") =>
        $"{t}.id, {t}.name, {t}.email_address, {t}.role, {t}.status, {t}.bio, {t}.bot_token, {t}.created_at, {t}.updated_at";

    public static User ReadUser(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        Name: r.GetString(o + 1),
        EmailAddress: r.IsDBNull(o + 2) ? null : r.GetString(o + 2),
        Role: (UserRole)r.GetInt32(o + 3),
        Status: (UserStatus)r.GetInt32(o + 4),
        Bio: r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
        BotToken: r.IsDBNull(o + 6) ? null : r.GetString(o + 6),
        CreatedAt: SqlTime.Get(r, o + 7),
        UpdatedAt: SqlTime.Get(r, o + 8));

    public const int RoomWidth = 6;
    public static string RoomColumns(string t = "rooms") =>
        $"{t}.id, {t}.name, {t}.type, {t}.creator_id, {t}.created_at, {t}.updated_at";

    public static Room ReadRoom(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        Name: r.IsDBNull(o + 1) ? null : r.GetString(o + 1),
        Type: StoredValues.ParseRoomType(r.GetString(o + 2)),
        CreatorId: r.GetInt64(o + 3),
        CreatedAt: SqlTime.Get(r, o + 4),
        UpdatedAt: SqlTime.Get(r, o + 5));

    public const int MembershipWidth = 9;
    public static string MembershipColumns(string t = "memberships") =>
        $"{t}.id, {t}.room_id, {t}.user_id, {t}.involvement, {t}.unread_at, {t}.connected_at, {t}.connections, {t}.created_at, {t}.updated_at";

    public static Membership ReadMembership(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        RoomId: r.GetInt64(o + 1),
        UserId: r.GetInt64(o + 2),
        Involvement: StoredValues.ParseInvolvement(r.IsDBNull(o + 3) ? null : r.GetString(o + 3)),
        UnreadAt: SqlTime.GetNullable(r, o + 4),
        ConnectedAt: SqlTime.GetNullable(r, o + 5),
        Connections: r.GetInt32(o + 6),
        CreatedAt: SqlTime.Get(r, o + 7),
        UpdatedAt: SqlTime.Get(r, o + 8));

    public const int MessageWidth = 6;
    public static string MessageColumns(string t = "messages") =>
        $"{t}.id, {t}.room_id, {t}.creator_id, {t}.client_message_id, {t}.created_at, {t}.updated_at";

    public static Message ReadMessage(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        RoomId: r.GetInt64(o + 1),
        CreatorId: r.GetInt64(o + 2),
        ClientMessageId: r.GetString(o + 3),
        CreatedAt: SqlTime.Get(r, o + 4),
        UpdatedAt: SqlTime.Get(r, o + 5));

    public const int BoostWidth = 6;
    public static string BoostColumns(string t = "boosts") =>
        $"{t}.id, {t}.message_id, {t}.booster_id, {t}.content, {t}.created_at, {t}.updated_at";

    public static Boost ReadBoost(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        MessageId: r.GetInt64(o + 1),
        BoosterId: r.GetInt64(o + 2),
        Content: r.GetString(o + 3),
        CreatedAt: SqlTime.Get(r, o + 4),
        UpdatedAt: SqlTime.Get(r, o + 5));

    public const int SessionWidth = 8;
    public static string SessionColumns(string t = "sessions") =>
        $"{t}.id, {t}.user_id, {t}.token, {t}.ip_address, {t}.user_agent, {t}.last_active_at, {t}.created_at, {t}.updated_at";

    public static Session ReadSession(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        UserId: r.GetInt64(o + 1),
        Token: r.GetString(o + 2),
        IpAddress: r.IsDBNull(o + 3) ? null : r.GetString(o + 3),
        UserAgent: r.IsDBNull(o + 4) ? null : r.GetString(o + 4),
        LastActiveAt: SqlTime.Get(r, o + 5),
        CreatedAt: SqlTime.Get(r, o + 6),
        UpdatedAt: SqlTime.Get(r, o + 7));

    public const string SearchColumns = "searches.id, searches.user_id, searches.query, searches.created_at, searches.updated_at";

    public static Search ReadSearch(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.GetString(o + 2), SqlTime.Get(r, o + 3), SqlTime.Get(r, o + 4));

    public const string BanColumns = "bans.id, bans.user_id, bans.ip_address, bans.created_at, bans.updated_at";

    public static Ban ReadBan(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.GetString(o + 2), SqlTime.Get(r, o + 3), SqlTime.Get(r, o + 4));

    public const string WebhookColumns = "webhooks.id, webhooks.user_id, webhooks.url, webhooks.created_at, webhooks.updated_at";

    public static Webhook ReadWebhook(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.IsDBNull(o + 2) ? null : r.GetString(o + 2), SqlTime.Get(r, o + 3), SqlTime.Get(r, o + 4));

    public const string PushSubscriptionColumns =
        "push_subscriptions.id, push_subscriptions.user_id, push_subscriptions.endpoint, push_subscriptions.p256dh_key, " +
        "push_subscriptions.auth_key, push_subscriptions.user_agent, push_subscriptions.created_at, push_subscriptions.updated_at";

    public static PushSubscription ReadPushSubscription(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        UserId: r.GetInt64(o + 1),
        Endpoint: r.IsDBNull(o + 2) ? null : r.GetString(o + 2),
        P256dhKey: r.IsDBNull(o + 3) ? null : r.GetString(o + 3),
        AuthKey: r.IsDBNull(o + 4) ? null : r.GetString(o + 4),
        UserAgent: r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
        CreatedAt: SqlTime.Get(r, o + 6),
        UpdatedAt: SqlTime.Get(r, o + 7));

    public const int BlobWidth = 8;
    public static string BlobColumns(string t = "active_storage_blobs") =>
        $"{t}.id, {t}.key, {t}.filename, {t}.content_type, {t}.metadata, {t}.byte_size, {t}.checksum, {t}.created_at";

    public static Blob ReadBlob(SqliteDataReader r, int o = 0) => new(
        Id: r.GetInt64(o),
        Key: r.GetString(o + 1),
        Filename: r.GetString(o + 2),
        ContentType: r.IsDBNull(o + 3) ? null : r.GetString(o + 3),
        Metadata: BlobMetadataJson.Parse(r.IsDBNull(o + 4) ? null : r.GetString(o + 4)),
        ByteSize: r.GetInt64(o + 5),
        Checksum: r.IsDBNull(o + 6) ? null : r.GetString(o + 6),
        CreatedAt: SqlTime.Get(r, o + 7));

    public static string? NullableString(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
}
