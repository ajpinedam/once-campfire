using Campfire.Web.Cable;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Features.Messages;
using Campfire.Web.Http;
using Campfire.Web.Storage;
using Campfire.Web.Turbo;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Rooms;

/// <summary>
/// RoomsController and its subclasses for each room type (Rooms::OpensController,
/// Rooms::ClosedsController, Rooms::DirectsController), plus involvements and refreshes.
/// Each type acts only within its own <c>room_scope</c>, so one namespace can't reach another's rooms.
/// </summary>
public static class RoomEndpoints
{
    private const string DefaultRoomName = "New room";

    public static IEndpointRouteBuilder MapRooms(this IEndpointRouteBuilder app)
    {
        app.MapGet("/rooms", Index);
        app.MapGet("/rooms/{id:long}", (long id, long? message_id, HttpContext context, Current current, Sql sql, MessageRenderer renderer) =>
            Show(id, message_id, context, current, sql, renderer));
        app.MapGet("/rooms/{room_id:long}/@{message_id:long}", (long room_id, long message_id, HttpContext context, Current current, Sql sql, MessageRenderer renderer) =>
            Show(room_id, message_id, context, current, sql, renderer));
        app.MapDelete("/rooms/{id:long}", (long id, HttpContext context, Current current, Sql sql, BlobStore blobs, RoomBroadcasts broadcasts) =>
            Destroy(id, RoomTypes.All, context, current, sql, blobs, broadcasts));

        // Rooms::OpensController
        app.MapGet("/rooms/opens", Index);
        app.MapGet("/rooms/opens/new", NewOpen);
        app.MapPost("/rooms/opens", CreateOpen);
        app.MapGet("/rooms/opens/{id:long}", (long id, HttpContext context, Current current, Sql sql) => RedirectToShow(id, RoomTypes.WithoutDirects, context, current, sql));
        app.MapGet("/rooms/opens/{id:long}/edit", EditOpen);
        app.MapMethods("/rooms/opens/{id:long}", [HttpMethods.Patch, HttpMethods.Put], UpdateOpen);
        app.MapDelete("/rooms/opens/{id:long}", (long id, HttpContext context, Current current, Sql sql, BlobStore blobs, RoomBroadcasts broadcasts) =>
            Destroy(id, RoomTypes.WithoutDirects, context, current, sql, blobs, broadcasts));

        // Rooms::ClosedsController
        app.MapGet("/rooms/closeds", Index);
        app.MapGet("/rooms/closeds/new", NewClosed);
        app.MapPost("/rooms/closeds", CreateClosed);
        app.MapGet("/rooms/closeds/{id:long}", (long id, HttpContext context, Current current, Sql sql) => RedirectToShow(id, RoomTypes.WithoutDirects, context, current, sql));
        app.MapGet("/rooms/closeds/{id:long}/edit", EditClosed);
        app.MapMethods("/rooms/closeds/{id:long}", [HttpMethods.Patch, HttpMethods.Put], UpdateClosed);
        app.MapDelete("/rooms/closeds/{id:long}", (long id, HttpContext context, Current current, Sql sql, BlobStore blobs, RoomBroadcasts broadcasts) =>
            Destroy(id, RoomTypes.WithoutDirects, context, current, sql, blobs, broadcasts));

        // Rooms::DirectsController
        app.MapGet("/rooms/directs", Index);
        app.MapGet("/rooms/directs/new", NewDirect);
        app.MapPost("/rooms/directs", CreateDirect);
        app.MapGet("/rooms/directs/{id:long}", (long id, HttpContext context, Current current, Sql sql) => RedirectToShow(id, RoomTypes.DirectsOnly, context, current, sql));
        app.MapGet("/rooms/directs/{id:long}/edit", EditDirect);
        // All members of a direct room can administer it — only direct rooms, though, which is why
        // this scope must keep every other type out of reach.
        app.MapDelete("/rooms/directs/{id:long}", (long id, HttpContext context, Current current, Sql sql, BlobStore blobs, RoomBroadcasts broadcasts) =>
            Destroy(id, RoomTypes.DirectsOnly, context, current, sql, blobs, broadcasts));

        // Rooms::InvolvementsController
        app.MapGet("/rooms/{room_id:long}/involvement", ShowInvolvement);
        app.MapMethods("/rooms/{room_id:long}/involvement", [HttpMethods.Patch, HttpMethods.Put], UpdateInvolvement);

        // Rooms::RefreshesController
        app.MapGet("/rooms/{room_id:long}/refresh", Refresh);

        return app;
    }

    // RoomsController

    private static IResult Index(HttpContext context, Current current, Sql sql) =>
        Data.Queries.Rooms.LastForUser(sql, current.User.Id) is { } room
            ? Respond.Redirect(context, Paths.Room(room.Id))
            : Respond.Redirect(context, Paths.Root);

    private static async Task<IResult> Show(long roomId, long? messageId, HttpContext context, Current current, Sql sql, MessageRenderer renderer)
    {
        var user = current.User;
        if (Data.Queries.Rooms.FindForUser(sql, user.Id, roomId) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        RoomAccess.RememberLastRoomVisited(context, room);

        var messages = messageId is { } id && Data.Queries.Messages.FindInRoom(sql, room.Id, id) is { } around
            ? Data.Queries.Messages.PageAround(sql, room.Id, around)
            : Data.Queries.Messages.LastPage(sql, room.Id);

        var fragments = await renderer.RenderAsync(sql, messages, context.Request.BaseUrl(), context.RequestAborted);
        var showInvitation = Data.Queries.Rooms.Original(sql)?.Id == room.Id && !Data.Queries.Messages.Paged(sql, room.Id);

        return Views.Rooms.Show.Create(new RoomShowModel(
            PageContext.For(context), room, Data.Queries.Rooms.DisplayName(sql, room, user), fragments, showInvitation));
    }

    private static IResult RedirectToShow(long id, RoomTypes scope, HttpContext context, Current current, Sql sql)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, id, scope) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        RoomAccess.RememberLastRoomVisited(context, room);
        return Respond.Redirect(context, Paths.Room(room.Id));
    }

    private static IResult Destroy(long id, RoomTypes scope, HttpContext context, Current current, Sql sql, BlobStore blobs, RoomBroadcasts broadcasts)
    {
        var user = current.User;
        if (Data.Queries.Rooms.FindForUser(sql, user.Id, id, scope) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        if (scope != RoomTypes.DirectsOnly && !user.CanAdminister(room.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        blobs.Purge(Data.Queries.Rooms.Delete(sql, room.Id));
        broadcasts.RoomRemoved(room);
        return Respond.Redirect(context, Paths.Root);
    }

    // Rooms::OpensController

    private static IResult NewOpen(HttpContext context, Current current, Sql sql)
    {
        if (!RoomAccess.MayCreateRooms(current))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var room = NewRoom(RoomType.Open, current.User);
        return Views.Rooms.Form.Create(new RoomFormModel(PageContext.For(context), room, IsNew: true,
            Data.Queries.Users.ActiveOrdered(sql), [], Paths.NewClosedRoom, BackPath(sql, context, current.User)));
    }

    private static async Task<IResult> CreateOpen(HttpContext context, Current current, Sql sql, RoomBroadcasts broadcasts)
    {
        if (!RoomAccess.MayCreateRooms(current))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var name = await RoomAccess.FormValue(context.Request, "room[name]");
        if (name is null)
        {
            return Respond.Head(StatusCodes.Status400BadRequest);
        }

        var room = sql.Transaction(tx =>
        {
            var created = Data.Queries.Rooms.Create(tx, name, RoomType.Open, current.User.Id);
            Memberships.GrantToAllActiveUsers(tx, created);
            Memberships.GrantTo(tx, created, [current.User.Id]);
            return created;
        });

        await broadcasts.OpenRoomCreatedAsync(room);
        return Respond.Redirect(context, Paths.Room(room.Id));
    }

    private static IResult EditOpen(long id, HttpContext context, Current current, Sql sql)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, id, RoomTypes.WithoutDirects) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        // Editing a closed room here turns it into an open one on saving (force_room_type)
        return Views.Rooms.Form.Create(new RoomFormModel(PageContext.For(context), room with { Type = RoomType.Open }, IsNew: false,
            Data.Queries.Users.ActiveOrdered(sql), [], Paths.EditClosedRoom(room.Id), BackPath(sql, context, current.User)));
    }

    private static async Task<IResult> UpdateOpen(long id, HttpContext context, Current current, Sql sql, RoomBroadcasts broadcasts)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, id, RoomTypes.WithoutDirects) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        if (!current.User.CanAdminister(room.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var name = await RoomAccess.FormValue(context.Request, "room[name]");
        if (name is null)
        {
            return Respond.Head(StatusCodes.Status400BadRequest);
        }

        var updated = sql.Transaction(tx =>
        {
            var saved = Data.Queries.Rooms.Update(tx, room, name, RoomType.Open);
            if (!room.IsOpen)
            {
                Memberships.GrantToAllActiveUsers(tx, saved); // a room becoming open is open to everyone
            }
            return saved;
        });

        await broadcasts.OpenRoomUpdatedAsync(updated);
        return Respond.Redirect(context, Paths.Room(updated.Id));
    }

    // Rooms::ClosedsController

    private static IResult NewClosed(HttpContext context, Current current, Sql sql)
    {
        if (!RoomAccess.MayCreateRooms(current))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var room = NewRoom(RoomType.Closed, current.User);
        return Views.Rooms.Form.Create(new RoomFormModel(PageContext.For(context), room, IsNew: true,
            [], Data.Queries.Users.ActiveOrdered(sql), Paths.NewOpenRoom, BackPath(sql, context, current.User)));
    }

    private static async Task<IResult> CreateClosed(HttpContext context, Current current, Sql sql, RoomBroadcasts broadcasts)
    {
        if (!RoomAccess.MayCreateRooms(current))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var name = await RoomAccess.FormValue(context.Request, "room[name]");
        if (name is null)
        {
            return Respond.Head(StatusCodes.Status400BadRequest);
        }

        var grantees = Data.Queries.Users.ExistingIds(sql, await RoomAccess.UserIdsParam(context.Request));
        var room = sql.Transaction(tx =>
        {
            var created = Data.Queries.Rooms.Create(tx, name, RoomType.Closed, current.User.Id);
            Memberships.GrantTo(tx, created, grantees);
            return created;
        });

        await broadcasts.ClosedRoomCreatedAsync(sql, room);
        return Respond.Redirect(context, Paths.Room(room.Id));
    }

    private static IResult EditClosed(long id, HttpContext context, Current current, Sql sql)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, id, RoomTypes.WithoutDirects) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        var members = Data.Queries.Rooms.UserIds(sql, room.Id).ToHashSet();
        var users = Data.Queries.Users.ActiveOrdered(sql);

        // Editing an open room here turns it into a closed one on saving (force_room_type)
        return Views.Rooms.Form.Create(new RoomFormModel(PageContext.For(context), room with { Type = RoomType.Closed }, IsNew: false,
            users.Where(user => members.Contains(user.Id)).ToList(),
            users.Where(user => !members.Contains(user.Id)).ToList(),
            Paths.EditOpenRoom(room.Id), BackPath(sql, context, current.User)));
    }

    private static async Task<IResult> UpdateClosed(long id, HttpContext context, Current current, Sql sql, RoomBroadcasts broadcasts, CableServer cable)
    {
        if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, id, RoomTypes.WithoutDirects) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        if (!current.User.CanAdminister(room.CreatorId))
        {
            return Respond.Head(StatusCodes.Status403Forbidden);
        }

        var name = await RoomAccess.FormValue(context.Request, "room[name]");
        if (name is null)
        {
            return Respond.Head(StatusCodes.Status400BadRequest);
        }

        var granteeIds = await RoomAccess.UserIdsParam(context.Request);
        var (updated, revoked) = sql.Transaction(tx =>
        {
            var saved = Data.Queries.Rooms.Update(tx, room, name, RoomType.Closed);
            var grantees = Data.Queries.Users.ExistingIds(tx, granteeIds);
            var revokees = Data.Queries.Rooms.UserIds(tx, room.Id).Except(granteeIds).ToList();

            // memberships.revise(granted:, revoked:)
            Memberships.GrantTo(tx, saved, grantees);
            Memberships.RevokeFrom(tx, room.Id, revokees);
            return (saved, revokees);
        });

        // A revoked membership resets the user's connections, so their streams re-authorize
        foreach (var userId in revoked)
        {
            cable.DisconnectUser(userId, reconnect: true);
        }

        await broadcasts.ClosedRoomUpdatedAsync(sql, updated);
        return Respond.Redirect(context, Paths.Room(updated.Id));
    }

    // Rooms::DirectsController

    private static IResult NewDirect(HttpContext context)
    {
        var page = PageContext.For(context);
        return Frames.Respond(context, page, Views.Rooms.DirectNew.Create(page));
    }

    private static async Task<IResult> CreateDirect(HttpContext context, Current current, Sql sql, RoomBroadcasts broadcasts)
    {
        var userIds = Data.Queries.Users.ExistingIds(sql, [.. await RoomAccess.UserIdsParam(context.Request), current.User.Id]);

        var (room, created) = sql.Transaction(tx =>
        {
            if (Data.Queries.Rooms.FindDirectWithExactly(tx, userIds) is { } existing)
            {
                return (existing, false);
            }

            var direct = Data.Queries.Rooms.Create(tx, null, RoomType.Direct, current.User.Id);
            Memberships.GrantTo(tx, direct, userIds);
            return (direct, true);
        });

        if (created)
        {
            await broadcasts.DirectRoomCreatedAsync(sql, room);
        }

        return Respond.Redirect(context, Paths.Room(room.Id));
    }

    private static IResult EditDirect(long id, HttpContext context, Current current, Sql sql)
    {
        var user = current.User;
        if (Data.Queries.Rooms.FindForUser(sql, user.Id, id, RoomTypes.DirectsOnly) is not { } room)
        {
            return RoomAccess.NotFound(context);
        }

        var users = Data.Queries.Rooms.UsersByRoom(sql, [room.Id]).GetValueOrDefault(room.Id, []);
        var shown = users.Count > 1 ? users.Where(member => member.Id != user.Id).ToList() : users;
        return Views.Rooms.DirectEdit.Create(new DirectEditModel(PageContext.For(context), room, Data.Queries.Rooms.DisplayName(sql, room, user), shown, BackPath(sql, context, user)));
    }

    // Rooms::InvolvementsController

    private static IResult ShowInvolvement(long room_id, Current current, Sql sql) =>
        Memberships.FindWithRoom(sql, current.User.Id, room_id) is { } found
            ? InvolvementPage(current.HttpContext, found.Room, found.Membership.Involvement)
            : Results.NotFound();

    private static IResult InvolvementPage(HttpContext context, Domain.Room room, Domain.Involvement involvement)
    {
        var page = PageContext.For(context);
        return Frames.Respond(context, page, Views.Rooms.Involvement.Create(new InvolvementModel(page, room, involvement)));
    }

    private static async Task<IResult> UpdateInvolvement(long room_id, HttpContext context, Current current, Sql sql, RoomBroadcasts broadcasts)
    {
        if (Memberships.FindWithRoom(sql, current.User.Id, room_id) is not { } found)
        {
            return Results.NotFound();
        }

        var requested = context.Request.Query["involvement"].ToString();
        if (string.IsNullOrEmpty(requested))
        {
            requested = await RoomAccess.FormValue(context.Request, "involvement") ?? "";
        }

        if (!StoredValues.TryParseInvolvement(requested, out var involvement))
        {
            return Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        var (membership, room) = found;
        Memberships.UpdateInvolvement(sql, membership.Id, involvement);

        // broadcast_visibility_changes
        if (!room.IsDirect)
        {
            if (involvement == Involvement.Invisible)
            {
                broadcasts.HiddenFor(membership.UserId, room);
            }
            else if (membership.Involvement == Involvement.Invisible)
            {
                await broadcasts.ShownForAsync(membership.UserId, room);
            }
        }

        return Respond.Redirect(context, Paths.RoomInvolvement(room.Id));
    }

    // Rooms::RefreshesController

    private static async Task<IResult> Refresh(long room_id, long? since, HttpContext context, Current current, Sql sql, MessageRenderer renderer)
    {
        if (Memberships.FindWithRoom(sql, current.User.Id, room_id) is not { } found)
        {
            return Results.NotFound();
        }

        var room = found.Room;
        var lastUpdatedAt = SqlTime.FromEpochMilliseconds(since ?? 0);
        var created = Data.Queries.Messages.PageCreatedSince(sql, room.Id, lastUpdatedAt);
        var updated = Data.Queries.Messages.PageUpdatedSince(sql, room.Id, lastUpdatedAt, created.Select(message => message.Id));
        var baseUrl = context.Request.BaseUrl();

        var streams = new System.Text.StringBuilder();
        if (created.Count > 0)
        {
            streams.Append(TurboStream.Append(DomId.For(room, "messages"), await renderer.RenderConcatenatedAsync(sql, created, baseUrl, context.RequestAborted)));
        }

        var fragments = await renderer.RenderAsync(sql, updated, baseUrl, context.RequestAborted);
        for (var i = 0; i < updated.Count; i++)
        {
            streams.Append(TurboStream.Replace(DomId.For(updated[i]), System.Text.Encoding.UTF8.GetString(fragments[i])));
        }

        return Respond.TurboStream(streams.ToString());
    }

    /// <summary><c>link_back_to_last_room_visited</c>.</summary>
    public static string BackPath(Sql sql, HttpContext context, User user) =>
        RoomAccess.LastRoomVisited(sql, context, user) is { } room ? Paths.Room(room.Id) : Paths.Root;

    private static Room NewRoom(RoomType type, User creator)
    {
        var now = SqlTime.UtcNow();
        return new Room(0, DefaultRoomName, type, creator.Id, now, now);
    }
}
