using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Http;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Campfire.Web.Views.FirstRun;
using Microsoft.Data.Sqlite;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.FirstRun;

/// <summary>FirstRunsController and the FirstRun model: the wizard that creates the account and its administrator.</summary>
public static class FirstRunEndpoints
{
    public const string AccountName = "Campfire";
    public const string FirstRoomName = "All Talk";

    public static IEndpointRouteBuilder MapFirstRun(this IEndpointRouteBuilder app)
    {
        app.MapGet(Paths.FirstRun, Show).AllowUnauthenticated();
        app.MapPost(Paths.FirstRun, Create).AllowUnauthenticated();
        return app;
    }

    private static IResult Show(HttpContext context, Sql sql) =>
        Q.Accounts.Any(sql)
            ? Respond.Redirect(context, Paths.Root)
            : Campfire.Web.Views.FirstRun.Show.Create(new FirstRunModel(PageContext.For(context)));

    private static async Task<IResult> Create(HttpContext context, Sql sql, AccountCache accounts, Authentication authentication, BlobStore store)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = form["user[name]"].ToString();
        var emailAddress = form["user[email_address]"].ToString();
        var password = form["user[password]"].ToString();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(emailAddress) || string.IsNullOrEmpty(password))
        {
            return Q.Accounts.Any(sql) ? Respond.Redirect(context, Paths.Root) : Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        User administrator;
        try
        {
            administrator = Setup(sql, name, emailAddress, password);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: someone else got there first
        {
            return Respond.Redirect(context, Paths.Root);
        }

        accounts.Invalidate();

        if (Uploads.File(form, "user[avatar]") is { } avatar)
        {
            await Uploads.AttachAsync(sql, store, avatar, "User", administrator.Id, "avatar", context.RequestAborted);
            Q.Users.Touch(sql, administrator.Id);
        }

        authentication.StartNewSessionFor(context, sql, administrator);
        return Respond.Redirect(context, Paths.Root);
    }

    /// <summary>
    /// <c>FirstRun.create!</c>: the account, its first (open) room and its first administrator.
    /// The accounts table's singleton guard makes concurrent attempts fail rather than create two.
    /// </summary>
    public static User Setup(Sql sql, string name, string emailAddress, string password) => sql.Transaction(tx =>
    {
        if (Q.Accounts.Any(tx))
        {
            throw new SqliteException("An account already exists", 19);
        }

        Q.Accounts.Create(tx, AccountName);
        var administrator = Q.Users.Create(tx, name, emailAddress, password, UserRole.Administrator);
        var room = Q.Rooms.Create(tx, FirstRoomName, RoomType.Open, administrator.Id);
        Q.Memberships.GrantToAllActiveUsers(tx, room);
        return administrator;
    });
}
