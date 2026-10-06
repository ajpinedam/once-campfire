using Campfire.Web.Data;
using Campfire.Web.Features.Avatars;
using Campfire.Web.Features.People;
using Campfire.Web.Http;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Campfire.Web.Views.Accounts.Bots;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.AccountSettings;

/// <summary>Accounts::BotsController and Accounts::Bots::KeysController (administrators only).</summary>
public static class BotsEndpoints
{
    public static IEndpointRouteBuilder MapBots(this IEndpointRouteBuilder app)
    {
        app.MapGet(Paths.AccountBots, Index);
        app.MapGet(Paths.NewAccountBot, New);
        app.MapPost(Paths.AccountBots, Create);
        app.MapGet("/account/bots/{id:long}/edit", Edit);
        app.MapMethods("/account/bots/{id:long}", ["PATCH", "PUT"], Update);
        app.MapDelete("/account/bots/{id:long}", Destroy);
        app.MapMethods("/account/bots/{bot_id:long}/key", ["PATCH", "PUT"], ResetKey);
        return app;
    }

    private static IResult Index(HttpContext context, Sql sql, Current current)
    {
        if (!current.User.CanAdminister())
        {
            return AccountsEndpoints.Forbidden();
        }

        var page = PageContext.For(context);
        var bots = Q.Users.ActiveBotsOrdered(sql)
            .Select(bot => new BotEntry(page, bot, Q.Rooms.SharedForUserOrdered(sql, bot.Id)))
            .ToList();
        return Views.Accounts.Bots.Index.Create(new BotsIndexModel(page, bots));
    }

    private static IResult New(HttpContext context, Current current) =>
        current.User.CanAdminister()
            ? Views.Accounts.Bots.New.Create(new BotFormModel(PageContext.For(context), null, null, HasAvatar: false))
            : AccountsEndpoints.Forbidden();

    private static async Task<IResult> Create(HttpContext context, Sql sql, Current current, BlobStore store)
    {
        if (!current.User.CanAdminister())
        {
            return AccountsEndpoints.Forbidden();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = form["user[name]"].ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        var bot = Q.Users.CreateBot(sql, name.Trim());
        Q.Webhooks.Set(sql, bot.Id, form["user[webhook_url]"].ToString());

        if (Uploads.File(form, "user[avatar]") is { } avatar)
        {
            await Uploads.AttachAsync(sql, store, avatar, "User", bot.Id, "avatar", context.RequestAborted);
            Q.Users.Touch(sql, bot.Id);
        }

        return Respond.Redirect(context, Paths.AccountBots);
    }

    private static IResult Edit(HttpContext context, Sql sql, Current current, long id)
    {
        if (!current.User.CanAdminister())
        {
            return AccountsEndpoints.Forbidden();
        }

        if (Q.Users.FindActiveBot(sql, id) is not { } bot)
        {
            return Results.NotFound();
        }

        return Views.Accounts.Bots.Edit.Create(new BotFormModel(
            PageContext.For(context), bot, Q.Webhooks.ForUser(sql, bot.Id)?.Url, Q.Attachments.Exists(sql, "User", bot.Id, "avatar")));
    }

    /// <summary><c>update_bot!</c>: a blank (or missing) webhook URL removes the webhook.</summary>
    private static async Task<IResult> Update(HttpContext context, Sql sql, Current current, BlobStore store, long id)
    {
        if (!current.User.CanAdminister())
        {
            return AccountsEndpoints.Forbidden();
        }

        if (Q.Users.FindActiveBot(sql, id) is not { } bot)
        {
            return Results.NotFound();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        sql.Transaction(tx =>
        {
            Q.Webhooks.Set(tx, bot.Id, form["user[webhook_url]"].ToString());
            if (form["user[name]"].ToString() is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name))
            {
                Q.Users.Rename(tx, bot.Id, name.Trim());
            }
        });

        if (Uploads.File(form, "user[avatar]") is { } avatar)
        {
            await Uploads.AttachAsync(sql, store, avatar, "User", bot.Id, "avatar", context.RequestAborted);
            Q.Users.Touch(sql, bot.Id);
        }

        return Respond.Redirect(context, Paths.AccountBots);
    }

    private static IResult Destroy(HttpContext context, Sql sql, Current current, UserModeration moderation, long id)
    {
        if (!current.User.CanAdminister())
        {
            return AccountsEndpoints.Forbidden();
        }

        if (Q.Users.FindActiveBot(sql, id) is not { } bot)
        {
            return Results.NotFound();
        }

        moderation.Deactivate(sql, bot);
        return Respond.Redirect(context, Paths.AccountBots);
    }

    private static IResult ResetKey(HttpContext context, Sql sql, Current current, long bot_id)
    {
        if (!current.User.CanAdminister())
        {
            return AccountsEndpoints.Forbidden();
        }

        if (Q.Users.FindActiveBot(sql, bot_id) is not { } bot)
        {
            return Results.NotFound();
        }

        Q.Users.ResetBotKey(sql, bot.Id);
        return Respond.Redirect(context, Paths.AccountBots);
    }
}
