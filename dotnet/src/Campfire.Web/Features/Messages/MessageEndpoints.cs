using System.Security.Cryptography;
using System.Text;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.RichText;
using Campfire.Web.Storage;
using Campfire.Web.Turbo;
using Campfire.Web.Views;
using Microsoft.Extensions.Primitives;

namespace Campfire.Web.Features.Messages;

/// <summary>MessagesController: the room transcript's pages, posting, editing and deleting messages.</summary>
public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessages(this IEndpointRouteBuilder app)
    {
        app.MapGet("/rooms/{room_id:long}/messages", Index);
        app.MapPost("/rooms/{room_id:long}/messages", Create);
        app.MapGet("/rooms/{room_id:long}/messages/{id:long}", Show);
        app.MapGet("/rooms/{room_id:long}/messages/{id:long}/edit", Edit);
        app.MapMethods("/rooms/{room_id:long}/messages/{id:long}", [HttpMethods.Patch, HttpMethods.Put], Update);
        app.MapDelete("/rooms/{room_id:long}/messages/{id:long}", Destroy);
        return app;
    }

    /// <summary>A page of the transcript (before/after a message, or the latest), for the paginator.</summary>
    private static async Task<IResult> Index(long room_id, long? before, long? after, HttpContext context, Current current, Sql sql, MessageRenderer renderer)
    {
        if (Memberships.FindWithRoom(sql, current.User.Id, room_id) is not { } found)
        {
            return Results.NotFound();
        }

        if (MessagePages.Find(sql, found.Room.Id, before, after) is not { } messages)
        {
            return Results.NotFound();
        }

        if (messages.Count == 0)
        {
            return Results.NoContent();
        }

        // fresh_when @messages
        var etag = MessagePages.ETag(messages);
        if (context.Request.Headers.IfNoneMatch.Contains(etag))
        {
            context.Response.Headers.ETag = etag;
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        context.Response.Headers.ETag = etag;
        return new FragmentsResult(await renderer.RenderAsync(sql, messages, context.Request.BaseUrl(), context.RequestAborted));
    }

    private static async Task<IResult> Create(long room_id, HttpContext context, Current current, Sql sql, MessagePosting posting, MessageRenderer renderer, BlobStore blobs)
    {
        if (Memberships.FindWithRoom(sql, current.User.Id, room_id) is not { } found)
        {
            // The room went away while the composer was open
            return Views.Messages.RoomNotFound.Create();
        }

        var room = found.Room;
        var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(context.RequestAborted) : null;
        var body = form?["message[body]"] is { Count: > 0 } bodyValues ? bodyValues.ToString() : null;
        var clientMessageId = form?["message[client_message_id]"].ToString();

        Blob? attachment = null;
        if (form?.Files.GetFile("message[attachment]") is { } file)
        {
            await using var stream = file.OpenReadStream();
            attachment = await blobs.CreateAsync(sql, stream, file.FileName, file.ContentType, context.RequestAborted);
        }

        var baseUrl = context.Request.BaseUrl();
        var message = await posting.PostAsync(sql, room, current.User, attachment is null ? body ?? "" : body, attachment, clientMessageId, baseUrl, deliverWebhooks: true, context.RequestAborted);

        var html = await renderer.RenderStringAsync(sql, message, baseUrl, context.RequestAborted);
        return Respond.TurboStream(TurboStream.Append(DomId.For(room, "messages"), html));
    }

    private static async Task<IResult> Show(long room_id, long id, HttpContext context, Current current, Sql sql, MessageRenderer renderer)
    {
        if (FindMessage(sql, current.User, room_id, id) is not { } message)
        {
            return Results.NotFound();
        }

        return new FragmentsResult(await renderer.RenderAsync(sql, [message], context.Request.BaseUrl(), context.RequestAborted));
    }

    private static IResult Edit(long room_id, long id, HttpContext context, Current current, Sql sql, RichTextService richText, BlobStore blobs)
    {
        if (FindMessage(sql, current.User, room_id, id) is not { } message)
        {
            return Results.NotFound();
        }

        if (!current.User.CanAdminister(message.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var model = Attachments.Find(sql, "Message", message.Id, "attachment") is { } attachment
            ? new EditMessageModel(PageContext.For(context), message, AttachmentPresentation.Render(attachment, blobs), null)
            : new EditMessageModel(PageContext.For(context), message, null,
                richText.EditableBody(sql, RichTexts.Find(sql, "Message", message.Id) ?? "", MessageRenderer.HostOf(context.Request.BaseUrl())));

        return Frames.Respond(context, model.Page, Views.Messages.Edit.Create(model));
    }

    private static async Task<IResult> Update(long room_id, long id, HttpContext context, Current current, Sql sql, RichTextService richText, MessageRenderer renderer, MessageBroadcasts broadcasts)
    {
        if (FindMessage(sql, current.User, room_id, id) is not { } message)
        {
            return Results.NotFound();
        }

        if (!current.User.CanAdminister(message.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(context.RequestAborted) : null;
        if (form?["message[body]"] is not { Count: > 0 } body)
        {
            return Respond.Head(StatusCodes.Status400BadRequest); // params.require(:message)
        }

        var updated = await MessageEdits.UpdateBodyAsync(sql, message, body.ToString(), context.Request.BaseUrl(), richText, renderer, broadcasts, context.RequestAborted);

        return context.Request.WantsJson()
            ? Results.Json(await MessagesJson.MessageAsync(sql, updated, context.Request.BaseUrl(), richText, renderer), MessagesJson.MessageInfo)
            : Respond.Redirect(context, Paths.RoomMessage(room_id, updated.Id));
    }

    private static IResult Destroy(long room_id, long id, Current current, Sql sql, BlobStore blobs, MessageBroadcasts broadcasts)
    {
        if (FindMessage(sql, current.User, room_id, id) is not { } message)
        {
            return Results.NotFound();
        }

        if (!current.User.CanAdminister(message.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        MessageEdits.Destroy(sql, message, blobs, broadcasts);
        return Respond.TurboStream(TurboStream.Remove(DomId.For(message)));
    }

    /// <summary>RoomScoped#set_room then <c>@room.messages.find(params[:id])</c>.</summary>
    private static Message? FindMessage(Sql sql, User user, long roomId, long id) =>
        Memberships.FindWithRoom(sql, user.Id, roomId) is { } found ? Data.Queries.Messages.FindInRoom(sql, found.Room.Id, id) : null;
}

/// <summary>Edits and deletions shared by people and bots.</summary>
public static class MessageEdits
{
    public static async Task<Message> UpdateBodyAsync(Sql sql, Message message, string bodyHtml, string baseUrl,
        RichTextService richText, MessageRenderer renderer, MessageBroadcasts broadcasts, CancellationToken cancellationToken)
    {
        var body = richText.Canonicalize(bodyHtml);
        var attachment = Attachments.Find(sql, "Message", message.Id, "attachment");
        var updated = Data.Queries.Messages.UpdateBody(sql, message, body, renderer.PlainText(sql, body, attachment));
        await broadcasts.UpdatedAsync(sql, updated, baseUrl, cancellationToken);
        return updated;
    }

    public static void Destroy(Sql sql, Message message, BlobStore blobs, MessageBroadcasts broadcasts)
    {
        blobs.Purge(Data.Queries.Messages.Delete(sql, message.Id));
        broadcasts.Removed(message);
    }
}

/// <summary>Message::Pagination as the controllers use it (<c>find_paged_messages</c>).</summary>
public static class MessagePages
{
    /// <summary>The page before/after a message, or the last page; null when the anchor message doesn't exist.</summary>
    public static List<Message>? Find(Sql sql, long roomId, long? before, long? after)
    {
        if (before is { } beforeId)
        {
            return Data.Queries.Messages.FindInRoom(sql, roomId, beforeId) is { } anchor ? Data.Queries.Messages.PageBefore(sql, roomId, anchor) : null;
        }

        if (after is { } afterId)
        {
            return Data.Queries.Messages.FindInRoom(sql, roomId, afterId) is { } anchor ? Data.Queries.Messages.PageAfter(sql, roomId, anchor) : null;
        }

        return Data.Queries.Messages.LastPage(sql, roomId);
    }

    /// <summary>A weak ETag over the page's messages and their versions (Rails' collection cache key).</summary>
    public static string ETag(IReadOnlyList<Message> messages)
    {
        var key = new StringBuilder("messages-v1");
        foreach (var message in messages)
        {
            key.Append('/').Append(message.Id).Append('-').Append(message.UpdatedAt.Ticks);
        }
        return $"W/\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..32]}\"";
    }
}

/// <summary>Writes pre-rendered UTF-8 HTML fragments one after another, without copying them together.</summary>
public sealed class FragmentsResult(IReadOnlyList<byte[]> fragments, int statusCode = StatusCodes.Status200OK) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = fragments.Sum(fragment => (long)fragment.Length);
        foreach (var fragment in fragments)
        {
            await response.Body.WriteAsync(fragment, httpContext.RequestAborted);
        }
    }
}

internal static class HeaderValues
{
    public static bool Contains(this StringValues values, string value) =>
        values.Any(header => header is not null && header.Split(',').Any(candidate => candidate.Trim() == value));
}
