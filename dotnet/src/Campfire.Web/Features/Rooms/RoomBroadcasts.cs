using Campfire.Web.Cable;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Features.Sidebar;
using Campfire.Web.Turbo;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Rooms;

/// <summary>Keeps everyone's sidebar in step as rooms come, go and change (the controllers' broadcast_* helpers).</summary>
public sealed class RoomBroadcasts(CableServer cable)
{
    private static string ListTarget(Room room) => DomId.For(room, "list");

    /// <summary>Opens#create: a new open room appears in everyone's shared rooms list.</summary>
    public async Task OpenRoomCreatedAsync(Room room) =>
        cable.BroadcastTurboStream(StreamNames.Rooms, TurboStream.Prepend("shared_rooms", await SharedHtml(room)));

    /// <summary>Opens#update: renames (or converts) the room in everyone's list.</summary>
    public async Task OpenRoomUpdatedAsync(Room room) =>
        cable.BroadcastTurboStream(StreamNames.Rooms, TurboStream.Replace(ListTarget(room), await SharedHtml(room)));

    /// <summary>Closeds#create: only the room's members learn of it.</summary>
    public async Task ClosedRoomCreatedAsync(Sql sql, Room room)
    {
        // Rendered once and reused for every member, as the Rails controller does
        var html = TurboStream.Prepend("shared_rooms", await SharedHtml(room));
        foreach (var userId in Data.Queries.Rooms.UserIds(sql, room.Id))
        {
            cable.BroadcastTurboStream(StreamNames.UserRooms(userId), html);
        }
    }

    /// <summary>Closeds#update: replaces the room in its (remaining) members' lists.</summary>
    public async Task ClosedRoomUpdatedAsync(Sql sql, Room room)
    {
        var html = TurboStream.Replace(ListTarget(room), await SharedHtml(room));
        foreach (var userId in Data.Queries.Rooms.UserIds(sql, room.Id))
        {
            cable.BroadcastTurboStream(StreamNames.UserRooms(userId), html);
        }
    }

    /// <summary>Directs#create: each participant sees the conversation named after the others.</summary>
    public async Task DirectRoomCreatedAsync(Sql sql, Room room)
    {
        var memberships = Memberships.ForRoomWithUsers(sql, room.Id);
        foreach (var (membership, user) in memberships)
        {
            var others = memberships.Where(pair => pair.User.Id != user.Id).Select(pair => pair.User).ToList();
            var view = new DirectRoomView(room, membership.IsUnread, others.Count > 0 ? others : [user]);
            var html = await Slices.RenderAsync(Views.Sidebar.Direct.Create(view));
            cable.BroadcastTurboStream(StreamNames.UserRooms(user.Id), TurboStream.Prepend("direct_rooms", html));
        }
    }

    /// <summary>Rooms#destroy: drops the room from every sidebar.</summary>
    public void RoomRemoved(Room room) =>
        cable.BroadcastTurboStream(StreamNames.Rooms, TurboStream.Remove(ListTarget(room)));

    /// <summary>Involvements#update to invisible: hides the room from that user's list.</summary>
    public void HiddenFor(long userId, Room room) =>
        cable.BroadcastTurboStream(StreamNames.UserRooms(userId), TurboStream.Remove(ListTarget(room)));

    /// <summary>Involvements#update from invisible: puts the room back in that user's list.</summary>
    public async Task ShownForAsync(long userId, Room room) =>
        cable.BroadcastTurboStream(StreamNames.UserRooms(userId), TurboStream.Prepend("shared_rooms", await SharedHtml(room)));

    private static Task<string> SharedHtml(Room room) => Slices.RenderAsync(Views.Sidebar.Shared.Create(new SharedRoomView(room, Unread: false)));
}
