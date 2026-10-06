using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Http;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Sidebar;

/// <summary>Users::SidebarsController.</summary>
public static class SidebarEndpoints
{
    private const int DirectPlaceholders = 20;

    public static IEndpointRouteBuilder MapSidebar(this IEndpointRouteBuilder app)
    {
        // resource :sidebar under users/:user_id with user_id defaulting to "me": always the signed-in user
        app.MapGet("/users/{user_id}/sidebar", Show);
        return app;
    }

    private static IResult Show(HttpContext context, Current current, Sql sql)
    {
        var user = current.User;
        var memberships = Memberships.ForUserWithRooms(sql, user.Id, visibleOnly: true);

        var directMemberships = memberships.Where(pair => pair.Room.IsDirect).OrderByDescending(pair => pair.Room.UpdatedAt).ToList();
        var members = Data.Queries.Rooms.UsersByRoom(sql, directMemberships.Select(pair => pair.Room.Id));
        var directs = directMemberships.Select(pair =>
        {
            var others = members.GetValueOrDefault(pair.Room.Id, []).Where(member => member.Id != user.Id).ToList();
            return new DirectRoomView(pair.Room, pair.Membership.IsUnread, others.Count > 0 ? others : [user]);
        }).ToList();

        var shared = memberships.Where(pair => !pair.Room.IsDirect)
            .Select(pair => new SharedRoomView(pair.Room, pair.Membership.IsUnread))
            .ToList();

        // People to start a ping with: those not already in a direct room with the user. The ids
        // already include the user when they have any direct rooms, and Rails' `.including(Current.user.id)`
        // appends them again — so the duplicate counts against the limit, as it does there.
        var excluded = Memberships.DirectPartnerIds(sql, user.Id).Append(user.Id).ToList();
        var placeholders = Data.Queries.Users.ActiveExcept(sql, excluded, Math.Max(DirectPlaceholders - excluded.Count, 0));

        var page = PageContext.For(context);
        return Frames.Respond(context, page, Views.Sidebar.Show.Create(new SidebarModel(page, directs, shared, placeholders)));
    }
}
