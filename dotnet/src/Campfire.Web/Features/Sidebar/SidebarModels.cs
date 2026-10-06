using Campfire.Web.Domain;
using Campfire.Web.Views;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Features.Sidebar;

/// <summary>users/sidebars/rooms/_shared: a shared (open or closed) room in the sidebar.</summary>
public sealed record SharedRoomView(Room Room, bool Unread);

/// <summary>
/// users/sidebars/rooms/_direct: a direct room shown from one member's point of view —
/// <see cref="Members"/> are the other participants (or the viewer, alone in it).
/// </summary>
public sealed record DirectRoomView(Room Room, bool Unread, IReadOnlyList<User> Members)
{
    /// <summary>
    /// Several members read as initials ("DH, JF, and KB", two as "DH+JF"); one as their first name.
    /// </summary>
    public string Label => Members.Count > 1
        ? Data.Queries.Text.ToSentence(Members.Select(Initials).ToList(), twoWordsConnector: "+")
        : FirstName(Members.Count > 0 ? Members[0].Name : "");

    public static string FirstName(string name) => name.Split(' ')[0];

    // name.split(' ')[0, 3].map { |str| str[0].capitalize }.join
    private static string Initials(User user) =>
        string.Concat(user.Name.Split(' ').Take(3).Where(word => word.Length > 0).Select(word => char.ToUpperInvariant(word[0])));
}

public sealed record SidebarModel(
    PageContext Page,
    IReadOnlyList<DirectRoomView> Directs,
    IReadOnlyList<SharedRoomView> Shared,
    IReadOnlyList<User> Placeholders)
{
    public bool CanCreateRooms => Page.IsAdministrator || Page.Account?.Settings.RestrictRoomCreationToAdministrators != true;
}

public static class SidebarFrame
{
    /// <summary>
    /// Rails' <c>sidebar_turbo_frame_tag(src:)</c>: the permanent frame the sidebar loads into
    /// (empty here; filled from <paramref name="src"/>).
    /// </summary>
    public static HtmlString Tag(string? src = Paths.UserSidebar) => new($"<turbo-frame id=\"user_sidebar\"{(src is null ? "" : $" src=\"{ViewHelpers.Escape(src)}\"")} target=\"_top\" {Attributes}></turbo-frame>");

    public const string Attributes =
        "data-turbo-permanent=\"true\" data-controller=\"rooms-list read-rooms turbo-frame\" data-rooms-list-unread-class=\"unread\" " +
        "data-action=\"presence:present@window->rooms-list#read read-rooms:read->rooms-list#read turbo:frame-load->rooms-list#loaded refresh-room:visible@window->turbo-frame#reload\"";
}
