using System.Text;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Features.People;
using Campfire.Web.Http;
using Campfire.Web.Storage;
using Campfire.Web.Turbo;
using Campfire.Web.Views;
using Campfire.Web.Views.Accounts;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.AccountSettings;

/// <summary>
/// AccountsController, Accounts::UsersController, Accounts::JoinCodesController,
/// Accounts::LogosController and Accounts::CustomStylesController.
/// </summary>
public static class AccountsEndpoints
{
    /// <summary>geared_pagination's page size for the people list.</summary>
    public const int PeoplePerPage = 500;

    private const string LogoCacheControl = "max-age=300, public, stale-while-revalidate=604800";

    public static IEndpointRouteBuilder MapAccountSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet(Paths.EditAccount, Edit);
        app.MapMethods(Paths.Account, ["PATCH", "PUT"], Update);

        app.MapGet(Paths.AccountUsers, PeoplePage);
        app.MapGet($"{Paths.AccountUsers}.turbo_stream", PeoplePage);
        app.MapMethods("/account/users/{id:long}", ["PATCH", "PUT"], UpdatePerson);
        app.MapDelete("/account/users/{id:long}", RemovePerson);

        app.MapPost(Paths.AccountJoinCode, ResetJoinCode);

        app.MapGet(Paths.AccountLogo, ShowLogo).AllowUnauthenticated();
        app.MapDelete(Paths.AccountLogo, DestroyLogo);

        app.MapGet(Paths.EditAccountCustomStyles, EditCustomStyles);
        app.MapMethods(Paths.AccountCustomStyles, ["PATCH", "PUT"], UpdateCustomStyles);
        return app;
    }

    internal static IResult Forbidden() => Respond.Head(StatusCodes.Status403Forbidden);

    private static RazorSlices.RazorSlice<Views.Accounts.AccountEditModel> Edit(HttpContext context, Sql sql, Current current)
    {
        var user = current.User;
        var people = Q.Users.HumansPage(sql, includeBanned: user.CanAdminister(), page: 1, PeoplePerPage);
        var firstPage = people.Take(PeoplePerPage).ToList();

        return Views.Accounts.Edit.Create(new AccountEditModel(
            PageContext.For(context),
            current.RequiredAccount,
            Administrators: firstPage.Where(person => person.IsAdministrator).ToList(),
            Members: firstPage.Where(person => !person.IsAdministrator).ToList(),
            NextPage: people.Count > PeoplePerPage ? 2 : null,
            BackPath: LastRoomVisitedPath(context, sql, user)));
    }

    private static async Task<IResult> Update(HttpContext context, Sql sql, Current current, AccountCache accounts, BlobStore store)
    {
        if (!current.User.CanAdminister())
        {
            return Forbidden();
        }

        var account = current.RequiredAccount;
        var form = await context.Request.ReadFormAsync(context.RequestAborted);

        if (form.TryGetValue("account[name]", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            Q.Accounts.UpdateName(sql, account.Id, name.ToString().Trim());
        }

        if (form.TryGetValue("account[settings][restrict_room_creation_to_administrators]", out var restrict))
        {
            Q.Accounts.UpdateSettings(sql, account.Id, account.Settings with { RestrictRoomCreationToAdministrators = restrict.ToString() is "true" or "1" });
        }

        if (Uploads.File(form, "account[logo]") is { } logo)
        {
            await Uploads.AttachAsync(sql, store, logo, "Account", account.Id, "logo", context.RequestAborted);
            Q.Accounts.Touch(sql, account.Id);
        }

        accounts.Invalidate();
        return Respond.Redirect(context, Paths.EditAccount, notice: "✓");
    }

    // People

    /// <summary>The next page of people for the account page's lazy-loading frame (<c>accounts/users/index.turbo_stream</c>).</summary>
    private static async Task<IResult> PeoplePage(HttpContext context, Sql sql, int? page)
    {
        var number = Math.Max(page ?? 1, 1);
        var people = Q.Users.HumansPage(sql, includeBanned: false, number, PeoplePerPage);
        var pageContext = PageContext.For(context);

        var items = new StringBuilder();
        foreach (var person in people.Take(PeoplePerPage))
        {
            items.Append(await Slices.RenderAsync(UserItem.Create(new AccountUserModel(pageContext, person)), context.RequestAborted));
        }

        var streams = new StringBuilder(TurboStream.Replace("next_page_container", items.ToString()));
        if (people.Count > PeoplePerPage)
        {
            streams.Append(TurboStream.Append("account_users", PeopleHelpers.NextPageContainer(number + 1).ToString()));
        }

        return Respond.TurboStream(streams.ToString());
    }

    private static async Task<IResult> UpdatePerson(HttpContext context, Sql sql, Current current, long id)
    {
        if (!current.User.CanAdminister())
        {
            return Forbidden();
        }

        if (Q.Users.FindActive(sql, id) is not { } person)
        {
            return Results.NotFound();
        }

        // The role checkbox posts a hidden "member" then (when checked) "administrator"; the last one wins
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var role = form["user[role]"].LastOrDefault() == "administrator" ? UserRole.Administrator : UserRole.Member;
        if (!person.IsBot)
        {
            Q.Users.UpdateRole(sql, person.Id, role);
        }

        return Respond.Redirect(context, Paths.EditAccount);
    }

    private static IResult RemovePerson(HttpContext context, Sql sql, Current current, UserModeration moderation, long id)
    {
        if (!current.User.CanAdminister())
        {
            return Forbidden();
        }

        if (Q.Users.FindActive(sql, id) is not { } person)
        {
            return Results.NotFound();
        }

        moderation.Deactivate(sql, person);
        return Respond.Redirect(context, Paths.EditAccount);
    }

    private static IResult ResetJoinCode(HttpContext context, Sql sql, Current current, AccountCache accounts)
    {
        if (!current.User.CanAdminister())
        {
            return Forbidden();
        }

        Q.Accounts.ResetJoinCode(sql, current.RequiredAccount.Id);
        accounts.Invalidate();
        return Respond.Redirect(context, Paths.EditAccount);
    }

    // Logo

    private static async Task<IResult> ShowLogo(HttpContext context, Sql sql, Current current, BlobStore store, IWebHostEnvironment environment, string? size)
    {
        var account = current.Account;
        var small = size == "small";

        // stale?(etag: Current.account): a new logo changes the account's updated_at
        var etag = account is null ? "W/\"account\"" : $"W/\"account-{account.Id}-{account.UpdatedAt.Ticks}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = LogoCacheControl;
        if (context.Request.Headers.IfNoneMatch.Contains(etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (account is { HasLogo: true } &&
            Q.Attachments.Find(sql, "Account", account.Id, "logo") is { } logo &&
            await store.VariantAsync(logo, small ? VariantKind.LogoSmall : VariantKind.LogoLarge, context.RequestAborted) is { } variant)
        {
            return Results.File(variant.Path, "image/png");
        }

        return Results.File(Path.Combine(environment.WebRootPath, "assets", "logos", small ? "app-icon-192.png" : "app-icon.png"), "image/png");
    }

    private static IResult DestroyLogo(HttpContext context, Sql sql, Current current, AccountCache accounts, BlobStore store)
    {
        if (!current.User.CanAdminister())
        {
            return Forbidden();
        }

        var account = current.RequiredAccount;
        Uploads.Detach(sql, store, "Account", account.Id, "logo");
        Q.Accounts.Touch(sql, account.Id);
        accounts.Invalidate();
        return Respond.Redirect(context, Paths.EditAccount);
    }

    // Custom styles

    private static IResult EditCustomStyles(HttpContext context, Current current) =>
        current.User.CanAdminister()
            ? CustomStyles.Create(new CustomStylesModel(PageContext.For(context), current.RequiredAccount))
            : Forbidden();

    private static async Task<IResult> UpdateCustomStyles(HttpContext context, Sql sql, Current current, AccountCache accounts)
    {
        if (!current.User.CanAdminister())
        {
            return Forbidden();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form.TryGetValue("account[custom_styles]", out var styles))
        {
            Q.Accounts.UpdateCustomStyles(sql, current.RequiredAccount.Id, styles.ToString());
            accounts.Invalidate();
        }

        return Respond.Redirect(context, Paths.EditAccountCustomStyles, notice: "✓");
    }

    /// <summary>Rails' <c>link_back_to_last_room_visited</c>: the room in the last_room cookie, else the user's first room.</summary>
    internal static string LastRoomVisitedPath(HttpContext context, Sql sql, User user)
    {
        var room = (AppCookies.ReadLastRoom(context) is { } lastRoomId ? Q.Rooms.FindForUser(sql, user.Id, lastRoomId) : null)
                   ?? Q.Rooms.OriginalForUser(sql, user.Id);
        return room is null ? Paths.Root : Paths.Room(room.Id);
    }
}
