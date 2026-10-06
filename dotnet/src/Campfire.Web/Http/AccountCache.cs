using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;

namespace Campfire.Web.Http;

/// <summary>
/// The account is a singleton row read by nearly every request (layout styles, logo, settings),
/// so it's held in memory as an immutable record and swapped atomically after any change.
/// </summary>
public sealed class AccountCache(Database database)
{
    private volatile Account? _account;

    public Account? Get()
    {
        if (_account is { } cached)
        {
            return cached;
        }

        using var sql = database.Open();
        return _account = Accounts.First(sql);
    }

    public Account? Get(Sql sql) => _account ??= Accounts.First(sql);

    /// <summary>Call after writing to the accounts table (or attaching/removing the logo).</summary>
    public void Invalidate() => _account = null;
}
