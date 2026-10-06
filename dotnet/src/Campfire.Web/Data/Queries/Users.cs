using Campfire.Web.Domain;
using Campfire.Web.Security;

namespace Campfire.Web.Data.Queries;

public static partial class Users
{
    private static readonly string Select = $"SELECT {Rows.UserColumns()} FROM users";

    public static User? Find(Sql sql, long id) =>
        sql.First($"{Select} WHERE id = @id", r => Rows.ReadUser(r), ("@id", id));

    public static User? FindActive(Sql sql, long id) =>
        sql.First($"{Select} WHERE id = @id AND status = 0", r => Rows.ReadUser(r), ("@id", id));

    public static List<User> FindMany(Sql sql, IEnumerable<long> ids) =>
        sql.Query($"{Select} WHERE id IN (SELECT value FROM json_each(@ids))", r => Rows.ReadUser(r), ("@ids", IdList.Json(ids)));

    public static bool None(Sql sql) => !sql.Exists("SELECT 1 FROM users");

    /// <summary><c>User.active.ordered</c> (bots included, as in Rails).</summary>
    public static List<User> ActiveOrdered(Sql sql) =>
        sql.Query($"{Select} WHERE status = 0 ORDER BY LOWER(name)", r => Rows.ReadUser(r));

    /// <summary><c>User.active.ordered.without_bots</c>.</summary>
    public static List<User> ActiveHumansOrdered(Sql sql) =>
        sql.Query($"{Select} WHERE status = 0 AND role != 2 ORDER BY LOWER(name)", r => Rows.ReadUser(r));

    /// <summary><c>User.administrator.first</c>, the help contact shown on the sign-in page.</summary>
    public static User? FirstAdministrator(Sql sql) =>
        sql.First($"{Select} WHERE role = 1 ORDER BY id LIMIT 1", r => Rows.ReadUser(r));

    public static bool EmailTaken(Sql sql, string emailAddress, long? exceptUserId = null) =>
        sql.Exists("SELECT 1 FROM users WHERE email_address = @email AND id != @except",
            ("@email", emailAddress), ("@except", exceptUserId ?? 0));

    /// <summary>
    /// <c>User.active.authenticate_by(email_address:, password:)</c>. Runs bcrypt even when no user
    /// matches, so response timing doesn't reveal which addresses have accounts.
    /// </summary>
    public static User? Authenticate(Sql sql, string? emailAddress, string? password)
    {
        if (string.IsNullOrEmpty(emailAddress) || string.IsNullOrEmpty(password))
        {
            Passwords.VerifyAgainstDummy(password ?? "");
            return null;
        }

        User? user = null;
        string? digest = null;
        sql.Each($"SELECT {Rows.UserColumns()}, password_digest FROM users WHERE email_address = @email AND status = 0",
            r => { user = Rows.ReadUser(r); digest = Rows.NullableString(r, Rows.UserWidth); },
            ("@email", emailAddress));

        if (user is null || digest is null)
        {
            Passwords.VerifyAgainstDummy(password);
            return null;
        }

        return Passwords.Verify(password, digest) ? user : null;
    }

    /// <summary>Creates a user and grants them membership of every open room (Rails' after_create_commit).</summary>
    public static User Create(Sql sql, string name, string? emailAddress, string? password, UserRole role = UserRole.Member, string? bio = null, string? botToken = null) =>
        sql.Transaction(tx =>
        {
            var now = SqlTime.UtcNow();
            var id = tx.Insert("""
                INSERT INTO users (name, email_address, password_digest, role, status, bio, bot_token, created_at, updated_at)
                VALUES (@name, @email, @digest, @role, 0, @bio, @bot, @now, @now)
                """,
                ("@name", name), ("@email", emailAddress), ("@digest", password is null ? null : Passwords.Hash(password)),
                ("@role", (long)role), ("@bio", bio), ("@bot", botToken), ("@now", now));

            tx.Execute("""
                INSERT INTO memberships (room_id, user_id, involvement, connections, created_at, updated_at)
                SELECT id, @user, 'mentions', 0, @now, @now FROM rooms WHERE type = 'Rooms::Open'
                """, ("@user", id), ("@now", now));

            return new User(id, name, emailAddress, role, UserStatus.Active, bio, botToken, now, now);
        });

    public static void Touch(Sql sql, long userId) =>
        sql.Execute("UPDATE users SET updated_at = @now WHERE id = @id", ("@now", SqlTime.UtcNow()), ("@id", userId));

    public static void UpdateRole(Sql sql, long userId, UserRole role) =>
        sql.Execute("UPDATE users SET role = @role, updated_at = @now WHERE id = @id",
            ("@role", (long)role), ("@now", SqlTime.UtcNow()), ("@id", userId));

    /// <summary>Profile changes; null arguments leave the column untouched (Rails' <c>permit(...).compact</c>).</summary>
    public static void UpdateProfile(Sql sql, long userId, string? name = null, string? emailAddress = null, string? password = null, string? bio = null)
    {
        sql.Execute("""
            UPDATE users SET
              name = COALESCE(@name, name),
              email_address = COALESCE(@email, email_address),
              password_digest = COALESCE(@digest, password_digest),
              bio = COALESCE(@bio, bio),
              updated_at = @now
            WHERE id = @id
            """,
            ("@name", string.IsNullOrWhiteSpace(name) ? null : name),
            ("@email", string.IsNullOrWhiteSpace(emailAddress) ? null : emailAddress),
            ("@digest", string.IsNullOrEmpty(password) ? null : Passwords.Hash(password)),
            ("@bio", bio),
            ("@now", SqlTime.UtcNow()),
            ("@id", userId));
    }

    /// <summary>
    /// Rails' <c>User#deactivate</c> minus closing cable connections (the caller does that):
    /// drops shared-room memberships, push subscriptions, searches and sessions, and frees the email.
    /// </summary>
    public static void Deactivate(Sql sql, long userId) => sql.Transaction(tx =>
    {
        tx.Execute("""
            DELETE FROM memberships WHERE user_id = @id
              AND room_id IN (SELECT id FROM rooms WHERE type != 'Rooms::Direct')
            """, ("@id", userId));
        tx.Execute("DELETE FROM push_subscriptions WHERE user_id = @id", ("@id", userId));
        tx.Execute("DELETE FROM searches WHERE user_id = @id", ("@id", userId));
        tx.Execute("DELETE FROM sessions WHERE user_id = @id", ("@id", userId));
        tx.Execute("""
            UPDATE users SET status = 1, updated_at = @now,
              email_address = CASE WHEN email_address IS NULL THEN NULL
                ELSE replace(email_address, '@', '-deactivated-' || @uuid || '@') END
            WHERE id = @id
            """, ("@now", SqlTime.UtcNow()), ("@uuid", Guid.NewGuid().ToString()), ("@id", userId));
    });

    public static void SetStatus(Sql sql, long userId, UserStatus status) =>
        sql.Execute("UPDATE users SET status = @status, updated_at = @now WHERE id = @id",
            ("@status", (long)status), ("@now", SqlTime.UtcNow()), ("@id", userId));

    // Bots

    /// <summary><c>User.active_bots.find_by(id:, bot_token:)</c> for a <c>"{id}-{token}"</c> bot key.</summary>
    public static User? AuthenticateBot(Sql sql, string botKey)
    {
        var separator = botKey.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 || !long.TryParse(botKey.AsSpan(0, separator), out var id))
        {
            return null;
        }

        var token = botKey[(separator + 1)..];
        var dash = token.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            token = token[..dash]; // Ruby's split("-") keeps only the first two parts
        }

        return string.IsNullOrEmpty(token) ? null : sql.First(
            $"{Select} WHERE id = @id AND bot_token = @token AND role = 2 AND status = 0",
            r => Rows.ReadUser(r), ("@id", id), ("@token", token));
    }

    public static List<User> ActiveBotsOrdered(Sql sql) =>
        sql.Query($"{Select} WHERE role = 2 AND status = 0 ORDER BY LOWER(name)", r => Rows.ReadUser(r));

    public static User? FindActiveBot(Sql sql, long id) =>
        sql.First($"{Select} WHERE id = @id AND role = 2 AND status = 0", r => Rows.ReadUser(r), ("@id", id));

    public static void ResetBotKey(Sql sql, long userId) =>
        sql.Execute("UPDATE users SET bot_token = @token, updated_at = @now WHERE id = @id",
            ("@token", SecureTokens.BotToken()), ("@now", SqlTime.UtcNow()), ("@id", userId));
}

/// <summary>Builds the JSON array parameter for <c>IN (SELECT value FROM json_each(@ids))</c>.</summary>
public static class IdList
{
    public static string Json(IEnumerable<long> ids) => $"[{string.Join(',', ids)}]";
}
