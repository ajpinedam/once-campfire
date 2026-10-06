using Campfire.Web.Domain;
using Campfire.Web.Security;

namespace Campfire.Web.Data.Queries;

public static partial class Accounts
{
    public static Account? First(Sql sql) =>
        sql.First($"SELECT {Rows.AccountColumns()} FROM accounts ORDER BY id LIMIT 1", r => Rows.ReadAccount(r));

    public static bool Any(Sql sql) => sql.Exists("SELECT 1 FROM accounts");

    public static Account Create(Sql sql, string name)
    {
        var now = SqlTime.UtcNow();
        var joinCode = SecureTokens.JoinCode();
        var id = sql.Insert(
            "INSERT INTO accounts (name, join_code, settings, singleton_guard, created_at, updated_at) VALUES (@name, @code, @settings, 0, @now, @now)",
            ("@name", name), ("@code", joinCode), ("@settings", AccountSettingsJson.Serialize(AccountSettings.Default)), ("@now", now));
        return new Account(id, name, joinCode, null, AccountSettings.Default, false, now, now);
    }

    public static void ResetJoinCode(Sql sql, long accountId) =>
        sql.Execute("UPDATE accounts SET join_code = @code, updated_at = @now WHERE id = @id",
            ("@code", SecureTokens.JoinCode()), ("@now", SqlTime.UtcNow()), ("@id", accountId));

    public static void UpdateName(Sql sql, long accountId, string name) =>
        sql.Execute("UPDATE accounts SET name = @name, updated_at = @now WHERE id = @id",
            ("@name", name), ("@now", SqlTime.UtcNow()), ("@id", accountId));

    public static void UpdateCustomStyles(Sql sql, long accountId, string? customStyles) =>
        sql.Execute("UPDATE accounts SET custom_styles = @styles, updated_at = @now WHERE id = @id",
            ("@styles", customStyles), ("@now", SqlTime.UtcNow()), ("@id", accountId));

    public static void UpdateSettings(Sql sql, long accountId, AccountSettings settings) =>
        sql.Execute("UPDATE accounts SET settings = @settings, updated_at = @now WHERE id = @id",
            ("@settings", AccountSettingsJson.Serialize(settings)), ("@now", SqlTime.UtcNow()), ("@id", accountId));

    public static void Touch(Sql sql, long accountId) =>
        sql.Execute("UPDATE accounts SET updated_at = @now WHERE id = @id", ("@now", SqlTime.UtcNow()), ("@id", accountId));
}
