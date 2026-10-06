using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Rooms;

/// <summary>Room lookups shared by the room controllers (Rails' RoomScoped / TrackedRoomVisit concerns).</summary>
public static class RoomAccess
{
    public const string NotFoundAlert = "Room not found or inaccessible";

    /// <summary>
    /// <c>last_room_visited</c>: the room in the <c>last_room</c> cookie if the user can still see it,
    /// otherwise their oldest room.
    /// </summary>
    public static Room? LastRoomVisited(Sql sql, HttpContext context, User user) =>
        (AppCookies.ReadLastRoom(context) is { } lastRoomId ? Data.Queries.Rooms.FindForUser(sql, user.Id, lastRoomId) : null)
        ?? Data.Queries.Rooms.OriginalForUser(sql, user.Id);

    /// <summary><c>remember_last_room_visited</c>.</summary>
    public static void RememberLastRoomVisited(HttpContext context, Room room) =>
        context.RequestServices.GetRequiredService<AppCookies>().WriteLastRoom(context, room.Id);

    /// <summary>RoomsController#set_room's failure: back to the start with an alert.</summary>
    public static IResult NotFound(HttpContext context) => Respond.Redirect(context, Paths.Root, alert: NotFoundAlert);

    /// <summary>
    /// <c>ensure_permission_to_create_rooms</c>: when the account restricts room creation, only
    /// administrators may create rooms.
    /// </summary>
    public static bool MayCreateRooms(Current current) =>
        current.User.IsAdministrator || current.Account?.Settings.RestrictRoomCreationToAdministrators != true;

    /// <summary>Rails' params merging: ids may come in the query (button_to with params) or the form.</summary>
    public static async Task<List<long>> UserIdsParam(HttpRequest request)
    {
        var values = new List<string?>(request.Query["user_ids[]"]);
        values.AddRange(request.Query["user_ids"]);
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            values.AddRange(form["user_ids[]"]);
            values.AddRange(form["user_ids"]);
        }

        return values.Select(value => long.TryParse(value, out var id) ? id : (long?)null).OfType<long>().Distinct().ToList();
    }

    /// <summary>A room parameter from Rails' nested params (<c>room[name]</c>).</summary>
    public static async Task<string?> FormValue(HttpRequest request, string name)
    {
        if (!request.HasFormContentType)
        {
            return null;
        }

        var form = await request.ReadFormAsync();
        return form.TryGetValue(name, out var value) ? value.ToString() : null;
    }
}
