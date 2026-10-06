using Campfire.Web.Cable;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Messages;
using Campfire.Web.Http;
using Campfire.Web.Turbo;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Boosts;

/// <summary>Messages::BoostsController: reactions on messages.</summary>
public static class BoostEndpoints
{
    public static IEndpointRouteBuilder MapBoosts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/messages/{message_id:long}/boosts", Index);
        app.MapGet("/messages/{message_id:long}/boosts/new", New);
        app.MapPost("/messages/{message_id:long}/boosts", Create);
        app.MapDelete("/messages/{message_id:long}/boosts/{id:long}", Destroy);
        return app;
    }

    /// <summary>The message's boosts frame (after a boost, or when cancelling a new one).</summary>
    private static IResult Index(long message_id, HttpContext context, Current current, Sql sql, MessageRenderer renderer)
    {
        if (Data.Queries.Messages.FindReachable(sql, current.User.Id, message_id) is not { } message)
        {
            return Results.NotFound();
        }

        var boosts = Data.Queries.Boosts.ForMessages(sql, [message.Id]).GetValueOrDefault(message.Id, [])
            .Select(pair => new BoostView(pair.Boost, pair.Booster, renderer.IsEmoji(pair.Boost.Content)))
            .ToList();
        return Frames.Respond(context, PageContext.For(context), Views.Boosts.Boosts.Create(new BoostsView(message, boosts)));
    }

    private static IResult New(long message_id, HttpContext context, Current current, Sql sql) =>
        Data.Queries.Messages.FindReachable(sql, current.User.Id, message_id) is { } message
            ? NewBoostPage(context, message)
            : Results.NotFound();

    private static IResult NewBoostPage(HttpContext context, Domain.Message message)
    {
        var page = PageContext.For(context);
        return Frames.Respond(context, page, Views.Boosts.New.Create(new NewBoostModel(page, message)));
    }

    private static async Task<IResult> Create(long message_id, HttpContext context, Current current, Sql sql, BoostBroadcasts broadcasts)
    {
        if (Data.Queries.Messages.FindReachable(sql, current.User.Id, message_id) is not { } message)
        {
            return Results.NotFound();
        }

        var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(context.RequestAborted) : null;
        if (form?["boost[content]"] is not { Count: > 0 } content)
        {
            return Respond.Head(StatusCodes.Status400BadRequest); // params.require(:boost)
        }

        var boost = Data.Queries.Boosts.Create(sql, message.Id, current.User.Id, content.ToString());
        await broadcasts.CreatedAsync(message, boost, current.User);
        return Respond.Redirect(context, Paths.MessageBoosts(message.Id));
    }

    private static IResult Destroy(long message_id, long id, Current current, Sql sql, BoostBroadcasts broadcasts)
    {
        if (Data.Queries.Messages.FindReachable(sql, current.User.Id, message_id) is not { } message ||
            Data.Queries.Boosts.FindForBooster(sql, message.Id, id, current.User.Id) is not { } boost)
        {
            return Results.NotFound();
        }

        Data.Queries.Boosts.Delete(sql, boost);
        broadcasts.Removed(message, boost);
        return Results.NoContent();
    }
}

/// <summary>Boost broadcasts on the room's messages stream.</summary>
public sealed class BoostBroadcasts(CableServer cable, MessageRenderer renderer)
{
    /// <summary>Appends the boost to the message's boosts list, keeping readers' scroll position.</summary>
    public async Task CreatedAsync(Message message, Boost boost, User booster)
    {
        var html = await Slices.RenderAsync(Views.Boosts.Boost.Create(new BoostView(boost, booster, renderer.IsEmoji(boost.Content))));
        cable.BroadcastTurboStream(StreamNames.RoomMessages(message.RoomId),
            TurboStream.Append(DomId.For(message, "boosts"), html, maintainScroll: true));
    }

    public void Removed(Message message, Boost boost) =>
        cable.BroadcastTurboStream(StreamNames.RoomMessages(message.RoomId), TurboStream.Remove(DomId.For(boost)));
}
