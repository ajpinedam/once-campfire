using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Features.Boosts;
using Campfire.Web.Http;
using Campfire.Web.RichText;
using Campfire.Web.Storage;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Messages;

/// <summary>
/// Messages::ByBotsController and Messages::Boosts::ByBotsController: the bot API under
/// <c>/rooms/:room_id/:bot_key/messages</c>, authenticated by the bot key in the path.
/// Bodies are the raw request body (text or HTML), or a multipart <c>attachment</c>.
/// </summary>
public static class BotMessageEndpoints
{
    private const string Messages = "/rooms/{room_id:long}/{bot_key}/messages";

    public static IEndpointRouteBuilder MapBotMessages(this IEndpointRouteBuilder app)
    {
        app.MapGet(Messages, Index).AllowBots();
        app.MapPost(Messages, Create).AllowBots();
        app.MapMethods(Messages + "/{id:long}", [HttpMethods.Patch, HttpMethods.Put], Update).AllowBots();
        app.MapDelete(Messages + "/{id:long}", Destroy).AllowBots();
        app.MapPost(Messages + "/{message_id:long}/boosts", CreateBoost).AllowBots();
        app.MapDelete(Messages + "/{message_id:long}/boosts/{id:long}", DestroyBoost).AllowBots();
        return app;
    }

    private static async Task<IResult> Index(long room_id, string bot_key, long? before, long? after, HttpContext context, Current current, Sql sql,
        RichTextService richText, MessageRenderer renderer)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, room_id) is not { } room)
        {
            return Results.NotFound();
        }

        if (MessagePages.Find(sql, room.Id, before, after) is not { } messages)
        {
            return Results.NotFound();
        }

        var baseUrl = context.Request.BaseUrl();
        context.Response.Headers["X-Total-Count"] = Data.Queries.Messages.CountInRoom(sql, room.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (NextPageQuery(sql, room.Id, messages, after is not null) is { } next)
        {
            context.Response.Headers.Link = $"<{baseUrl}{Paths.RoomBotMessages(room.Id, bot_key.Trim())}?{next}>; rel=\"next\"";
        }

        return Results.Json(await MessagesJson.MessagesAsync(sql, messages, baseUrl, richText, renderer), MessagesJson.MessageListInfo);
    }

    private static async Task<IResult> Create(long room_id, HttpContext context, Current current, Sql sql, MessagePosting posting, BlobStore blobs)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, room_id) is not { } room)
        {
            return Results.NotFound();
        }

        var request = context.Request;
        var file = request.HasFormContentType ? (await request.ReadFormAsync(context.RequestAborted)).Files.GetFile("attachment") : null;
        var body = file is null ? await RawRequestBody.ReadAsync(request) : null;

        if (file is null && string.IsNullOrWhiteSpace(body))
        {
            return Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        Blob? attachment = null;
        if (file is not null)
        {
            await using var stream = file.OpenReadStream();
            attachment = await blobs.CreateAsync(sql, stream, file.FileName, file.ContentType, context.RequestAborted);
        }

        var baseUrl = request.BaseUrl();
        var message = await posting.PostAsync(sql, room, current.User, body, attachment, null, baseUrl, deliverWebhooks: true, context.RequestAborted);

        // head :created, location: message_url(@message)
        context.Response.Headers.Location = $"{baseUrl}/messages/{message.Id}";
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private static async Task<IResult> Update(long room_id, long id, HttpContext context, Current current, Sql sql,
        RichTextService richText, MessageRenderer renderer, MessageBroadcasts broadcasts)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, room_id) is not { } room)
        {
            return Results.NotFound();
        }

        if (Data.Queries.Messages.FindInRoom(sql, room.Id, id) is not { } message)
        {
            return Results.NotFound();
        }

        if (!current.User.CanAdminister(message.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var baseUrl = context.Request.BaseUrl();
        var updated = await MessageEdits.UpdateBodyAsync(sql, message, await RawRequestBody.ReadAsync(context.Request), baseUrl, richText, renderer, broadcasts, context.RequestAborted);
        return Results.Json(await MessagesJson.MessageAsync(sql, updated, baseUrl, richText, renderer), MessagesJson.MessageInfo);
    }

    private static IResult Destroy(long room_id, long id, Current current, Sql sql, BlobStore blobs, MessageBroadcasts broadcasts)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, room_id) is not { } room)
        {
            return Results.NotFound();
        }

        if (Data.Queries.Messages.FindInRoom(sql, room.Id, id) is not { } message)
        {
            return Results.NotFound();
        }

        if (!current.User.CanAdminister(message.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        MessageEdits.Destroy(sql, message, blobs, broadcasts);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateBoost(long room_id, long message_id, HttpContext context, Current current, Sql sql, BoostBroadcasts broadcasts)
    {
        if (FindMessage(sql, current.User, room_id, message_id) is not { } message)
        {
            return Results.NotFound();
        }

        var content = await RawRequestBody.ReadAsync(context.Request);
        if (string.IsNullOrWhiteSpace(content))
        {
            return Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        var boost = Data.Queries.Boosts.Create(sql, message.Id, current.User.Id, content);
        await broadcasts.CreatedAsync(message, boost, current.User);
        return Results.Json(MessagesJson.Boost(boost, current.User, message, context.Request.BaseUrl()), MessagesJson.BoostInfo, statusCode: StatusCodes.Status201Created);
    }

    private static IResult DestroyBoost(long room_id, long message_id, long id, Current current, Sql sql, BoostBroadcasts broadcasts)
    {
        if (FindMessage(sql, current.User, room_id, message_id) is not { } message ||
            Data.Queries.Boosts.FindForBooster(sql, message.Id, id, current.User.Id) is not { } boost)
        {
            return Results.NotFound();
        }

        Data.Queries.Boosts.Delete(sql, boost);
        broadcasts.Removed(message, boost);
        return Results.NoContent();
    }

    private static Message? FindMessage(Sql sql, User bot, long roomId, long messageId) =>
        Data.Queries.Rooms.FindForUser(sql, bot.Id, roomId) is { } room ? Data.Queries.Messages.FindInRoom(sql, room.Id, messageId) : null;

    /// <summary>
    /// The Link header's next page: further back in time by default, or further forward when
    /// paging with <c>after</c> — only while more messages exist in that direction.
    /// </summary>
    private static string? NextPageQuery(Sql sql, long roomId, List<Message> messages, bool forward)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        if (forward)
        {
            var last = messages[^1];
            return Data.Queries.Messages.ExistsAfter(sql, roomId, last) ? $"after={last.Id}" : null;
        }

        var first = messages[0];
        return Data.Queries.Messages.ExistsBefore(sql, roomId, first) ? $"before={first.Id}" : null;
    }
}
