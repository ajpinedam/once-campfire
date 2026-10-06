using Campfire.Web.Domain;

namespace Campfire.Web.Data.Queries;

public static partial class Searches
{
    public static List<Search> RecentForUser(Sql sql, long userId) =>
        sql.Query($"SELECT {Rows.SearchColumns} FROM searches WHERE user_id = @user ORDER BY updated_at DESC, id DESC",
            r => Rows.ReadSearch(r), ("@user", userId));

    /// <summary><c>Search.record(query)</c>: find-or-create then touch, keeping only the ten most recent.</summary>
    public static void Record(Sql sql, long userId, string query) => sql.Transaction(tx =>
    {
        var now = SqlTime.UtcNow();
        var updated = tx.Execute("UPDATE searches SET updated_at = @now WHERE user_id = @user AND query = @query",
            ("@now", now), ("@user", userId), ("@query", query));

        if (updated == 0)
        {
            tx.Execute("INSERT INTO searches (user_id, query, created_at, updated_at) VALUES (@user, @query, @now, @now)",
                ("@user", userId), ("@query", query), ("@now", now));
            tx.Execute($"""
                DELETE FROM searches WHERE user_id = @user AND id NOT IN (
                  SELECT id FROM searches WHERE user_id = @user ORDER BY updated_at DESC, id DESC LIMIT {Search.RecentLimit})
                """, ("@user", userId));
        }
    });

    public static void Clear(Sql sql, long userId) =>
        sql.Execute("DELETE FROM searches WHERE user_id = @user", ("@user", userId));
}

public static partial class Bans
{
    public static bool IsBanned(Sql sql, string? ipAddress) =>
        ipAddress is not null && sql.Exists("SELECT 1 FROM bans WHERE ip_address = @ip", ("@ip", ipAddress));

    public static void Create(Sql sql, long userId, string ipAddress)
    {
        var now = SqlTime.UtcNow();
        sql.Execute("INSERT INTO bans (user_id, ip_address, created_at, updated_at) VALUES (@user, @ip, @now, @now)",
            ("@user", userId), ("@ip", ipAddress), ("@now", now));
    }

    public static void DeleteForUser(Sql sql, long userId) =>
        sql.Execute("DELETE FROM bans WHERE user_id = @user", ("@user", userId));
}

public static partial class Webhooks
{
    public static Webhook? ForUser(Sql sql, long userId) =>
        sql.First($"SELECT {Rows.WebhookColumns} FROM webhooks WHERE user_id = @user ORDER BY id LIMIT 1", r => Rows.ReadWebhook(r), ("@user", userId));

    /// <summary>Sets a bot's webhook URL, creating it if needed; a blank URL removes it.</summary>
    public static void Set(Sql sql, long userId, string? url) => sql.Transaction(tx =>
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            tx.Execute("DELETE FROM webhooks WHERE user_id = @user", ("@user", userId));
            return;
        }

        var now = SqlTime.UtcNow();
        if (tx.Execute("UPDATE webhooks SET url = @url, updated_at = @now WHERE user_id = @user", ("@url", url), ("@now", now), ("@user", userId)) == 0)
        {
            tx.Execute("INSERT INTO webhooks (user_id, url, created_at, updated_at) VALUES (@user, @url, @now, @now)",
                ("@user", userId), ("@url", url), ("@now", now));
        }
    });
}

public static partial class PushSubscriptions
{
    public static List<PushSubscription> ForUser(Sql sql, long userId) =>
        sql.Query($"SELECT {Rows.PushSubscriptionColumns} FROM push_subscriptions WHERE user_id = @user ORDER BY id",
            r => Rows.ReadPushSubscription(r), ("@user", userId));

    public static PushSubscription? Find(Sql sql, long userId, long id) =>
        sql.First($"SELECT {Rows.PushSubscriptionColumns} FROM push_subscriptions WHERE user_id = @user AND id = @id",
            r => Rows.ReadPushSubscription(r), ("@user", userId), ("@id", id));

    public static PushSubscription? FindByKeys(Sql sql, long userId, string? endpoint, string? p256dhKey, string? authKey) =>
        sql.First($"""
            SELECT {Rows.PushSubscriptionColumns} FROM push_subscriptions
            WHERE user_id = @user AND endpoint IS @endpoint AND p256dh_key IS @p256dh AND auth_key IS @auth
            """, r => Rows.ReadPushSubscription(r), ("@user", userId), ("@endpoint", endpoint), ("@p256dh", p256dhKey), ("@auth", authKey));

    public static PushSubscription Create(Sql sql, long userId, string endpoint, string? p256dhKey, string? authKey, string? userAgent)
    {
        var now = SqlTime.UtcNow();
        var id = sql.Insert("""
            INSERT INTO push_subscriptions (user_id, endpoint, p256dh_key, auth_key, user_agent, created_at, updated_at)
            VALUES (@user, @endpoint, @p256dh, @auth, @agent, @now, @now)
            """, ("@user", userId), ("@endpoint", endpoint), ("@p256dh", p256dhKey), ("@auth", authKey), ("@agent", userAgent), ("@now", now));
        return new PushSubscription(id, userId, endpoint, p256dhKey, authKey, userAgent, now, now);
    }

    public static void Touch(Sql sql, long id) =>
        sql.Execute("UPDATE push_subscriptions SET updated_at = @now WHERE id = @id", ("@now", SqlTime.UtcNow()), ("@id", id));

    public static void Delete(Sql sql, long id) =>
        sql.Execute("DELETE FROM push_subscriptions WHERE id = @id", ("@id", id));

    public static void DeleteForUser(Sql sql, long userId, long id) =>
        sql.Execute("DELETE FROM push_subscriptions WHERE id = @id AND user_id = @user", ("@id", id), ("@user", userId));

    public static void DeleteByEndpoint(Sql sql, long userId, string endpoint) =>
        sql.Execute("DELETE FROM push_subscriptions WHERE endpoint = @endpoint AND user_id = @user", ("@endpoint", endpoint), ("@user", userId));
}
