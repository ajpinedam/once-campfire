using Campfire.Web.Domain;
using Campfire.Web.Security;

namespace Campfire.Web.Data.Queries;

public static partial class Sessions
{
    public static Session Start(Sql sql, long userId, string? userAgent, string? ipAddress)
    {
        var now = SqlTime.UtcNow();
        var token = SecureTokens.SessionToken();
        var id = sql.Insert("""
            INSERT INTO sessions (user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at)
            VALUES (@user, @token, @ip, @agent, @now, @now, @now)
            """, ("@user", userId), ("@token", token), ("@ip", ipAddress), ("@agent", userAgent), ("@now", now));
        return new Session(id, userId, token, ipAddress, userAgent, now, now, now);
    }

    /// <summary>The session for a cookie token, together with its user, in one query.</summary>
    public static (Session Session, User User)? FindWithUser(Sql sql, string token)
    {
        (Session, User)? result = null;
        sql.Each(
            $"SELECT {Rows.SessionColumns()}, {Rows.UserColumns()} FROM sessions JOIN users ON users.id = sessions.user_id WHERE sessions.token = @token",
            r => result = (Rows.ReadSession(r), Rows.ReadUser(r, Rows.SessionWidth)),
            ("@token", token));
        return result;
    }

    /// <summary>Rails' <c>Session#resume</c>: refreshes activity details at most once an hour.</summary>
    public static void Resume(Sql sql, Session session, string? userAgent, string? ipAddress)
    {
        var now = SqlTime.UtcNow();
        if (session.LastActiveAt < now - Session.ActivityRefreshRate)
        {
            sql.Execute("UPDATE sessions SET user_agent = @agent, ip_address = @ip, last_active_at = @now, updated_at = @now WHERE id = @id",
                ("@agent", userAgent), ("@ip", ipAddress), ("@now", now), ("@id", session.Id));
        }
    }

    public static void Delete(Sql sql, long sessionId) =>
        sql.Execute("DELETE FROM sessions WHERE id = @id", ("@id", sessionId));

    public static void DeleteAllForUser(Sql sql, long userId) =>
        sql.Execute("DELETE FROM sessions WHERE user_id = @id", ("@id", userId));

    public static List<string> DistinctIpAddresses(Sql sql, long userId) =>
        sql.Query("SELECT DISTINCT ip_address FROM sessions WHERE user_id = @id AND ip_address IS NOT NULL AND ip_address != ''",
            r => r.GetString(0), ("@id", userId));
}
