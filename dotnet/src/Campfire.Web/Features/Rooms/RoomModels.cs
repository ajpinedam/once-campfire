using Campfire.Web.Domain;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Rooms;

/// <summary>rooms/show.</summary>
public sealed record RoomShowModel(
    PageContext Page,
    Room Room,
    string DisplayName,
    IReadOnlyList<byte[]> Messages,
    bool ShowInvitation)
{
    public PwaHelpModel PwaHelp => new(Page.Platform, Page.Url(Paths.Root));
}

/// <summary>
/// rooms/opens and rooms/closeds new/edit. <see cref="Room"/> already carries the type being edited
/// towards (Rails' <c>becomes!</c>): editing an open room as closed converts it on save.
/// </summary>
public sealed record RoomFormModel(
    PageContext Page,
    Room Room,
    bool IsNew,
    IReadOnlyList<User> SelectedUsers,
    IReadOnlyList<User> UnselectedUsers,
    string TypeChangePath,
    string BackPath)
{
    public bool CanAdminister => IsNew || Page.CurrentUser.CanAdminister(Room.CreatorId);

    /// <summary>Open rooms list everyone; closed rooms split members from everyone else.</summary>
    public IReadOnlyList<User> Users => SelectedUsers;

    public string FormAction => (Room.Type, IsNew) switch
    {
        (RoomType.Open, true) => Paths.OpenRooms,
        (RoomType.Open, false) => Paths.OpenRoom(Room.Id),
        (_, true) => Paths.ClosedRooms,
        _ => Paths.ClosedRoom(Room.Id)
    };

    public bool ShowFilter => SelectedUsers.Count + UnselectedUsers.Count > 20;
}

/// <summary>rooms/directs/edit.</summary>
public sealed record DirectEditModel(PageContext Page, Room Room, string DisplayName, IReadOnlyList<User> Users, string BackPath);

/// <summary>rooms/involvements/show (the bell inside its turbo frame).</summary>
public sealed record InvolvementModel(PageContext Page, Room Room, Involvement Involvement);

/// <summary>The bell in a room's nav: notification settings plus help for when they're blocked.</summary>
public sealed record BellModel(Room Room, PwaHelpModel PwaHelp);
