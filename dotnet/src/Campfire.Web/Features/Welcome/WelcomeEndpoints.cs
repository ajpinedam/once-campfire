using Campfire.Web.Data;
using Campfire.Web.Features.Rooms;
using Campfire.Web.Http;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Welcome;

/// <summary>WelcomeController: the root sends people to the room they were last in.</summary>
public static class WelcomeEndpoints
{
    public static IEndpointRouteBuilder MapWelcome(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", Show);
        return app;
    }

    private static IResult Show(HttpContext context, Current current, Sql sql)
    {
        if (Data.Queries.Rooms.AnyForUser(sql, current.User.Id) && RoomAccess.LastRoomVisited(sql, context, current.User) is { } room)
        {
            return Respond.Redirect(context, Paths.Room(room.Id));
        }

        return Views.Welcome.Show.Create(PageContext.For(context));
    }
}
