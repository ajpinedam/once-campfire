using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Features.Sessions;
using Campfire.Web.Http;
using Campfire.Web.Security;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Campfire.Web.Views.Users;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.People;

/// <summary>UsersController (joining with the account's join link; profiles of other people) and Users::BansController.</summary>
public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapPeople(this IEndpointRouteBuilder app)
    {
        app.MapGet("/join/{join_code}", New).RequireUnauthenticated();
        app.MapPost("/join/{join_code}", Create).RequireUnauthenticated();

        app.MapGet("/users/{id:long}", Show);

        app.MapPost("/users/{user_id:long}/ban", Ban);
        app.MapDelete("/users/{user_id:long}/ban", Unban);
        return app;
    }

    private static IResult New(HttpContext context, Sql sql, Current current, string join_code)
    {
        if (!ValidJoinCode(current, join_code))
        {
            return Results.NotFound();
        }

        return Join.Create(new JoinModel(PageContext.For(context), join_code, Q.Users.FirstAdministrator(sql)));
    }

    private static async Task<IResult> Create(HttpContext context, Sql sql, Current current, Authentication authentication, BlobStore store, string join_code)
    {
        if (!ValidJoinCode(current, join_code))
        {
            return Results.NotFound();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = form["user[name]"].ToString();
        var emailAddress = form["user[email_address]"].ToString();
        var password = form["user[password]"].ToString();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(emailAddress) || string.IsNullOrEmpty(password))
        {
            return Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        // RecordNotUnique: the address already has an account, so send them to sign in with it
        if (Q.Users.EmailTaken(sql, emailAddress))
        {
            return Respond.Redirect(context, $"{Paths.NewSession}?email_address={Uri.EscapeDataString(emailAddress)}");
        }

        var user = Q.Users.Create(sql, name, emailAddress, password);
        if (Uploads.File(form, "user[avatar]") is { } avatar)
        {
            await Uploads.AttachAsync(sql, store, avatar, "User", user.Id, "avatar", context.RequestAborted);
            Q.Users.Touch(sql, user.Id);
        }

        authentication.StartNewSessionFor(context, sql, user);
        return Respond.Redirect(context, Paths.Root);
    }

    private static IResult Show(HttpContext context, Sql sql, Current current, KeyRing keys, long id)
    {
        if (Q.Users.Find(sql, id) is not { } user)
        {
            return Results.NotFound();
        }

        var page = PageContext.For(context);
        var transfer = current.User.CanAdminister() && user.IsActive && !user.IsBot
            ? new TransferModel(page.Url(Paths.SessionTransfer(TransferIds.For(keys, user.Id))), ForSomeoneElse: user.Id != current.User.Id)
            : null;

        return Views.Users.Show.Create(new UserShowModel(page, user, transfer));
    }

    private static IResult Ban(HttpContext context, Sql sql, Current current, UserModeration moderation, long user_id)
    {
        if (!current.User.CanAdminister())
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        if (Q.Users.Find(sql, user_id) is not { } user)
        {
            return Results.NotFound();
        }

        moderation.Ban(sql, user);
        return Respond.Redirect(context, Paths.User(user.Id));
    }

    private static IResult Unban(HttpContext context, Sql sql, Current current, long user_id)
    {
        if (!current.User.CanAdminister())
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        if (Q.Users.Find(sql, user_id) is not { } user)
        {
            return Results.NotFound();
        }

        UserModeration.Unban(sql, user);
        return Respond.Redirect(context, Paths.User(user.Id));
    }

    private static bool ValidJoinCode(Current current, string joinCode) =>
        current.Account is { } account && string.Equals(account.JoinCode, joinCode, StringComparison.Ordinal);
}
