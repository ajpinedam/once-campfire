using System.Collections.Frozen;
using Campfire.Web.Domain;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Views;

/// <summary>Rails' Rooms::InvolvementsHelper: the bell button that cycles a room's notification level.</summary>
public static class Involvements
{
    private static readonly FrozenDictionary<Involvement, string> Humanized = new Dictionary<Involvement, string>
    {
        [Involvement.Mentions] = "Notifying about @ mentions",
        [Involvement.Everything] = "Notifying about all messages",
        [Involvement.Nothing] = "Notifications are off",
        [Involvement.Invisible] = "Notifications are off and room invisible in sidebar"
    }.ToFrozenDictionary();

    private static readonly Involvement[] SharedOrder = [Involvement.Mentions, Involvement.Everything, Involvement.Nothing, Involvement.Invisible];
    private static readonly Involvement[] DirectOrder = [Involvement.Everything, Involvement.Nothing];

    /// <summary>The level a click moves to: shared rooms cycle through four, direct rooms through two.</summary>
    public static Involvement Next(Room room, Involvement current)
    {
        var order = room.IsDirect ? DirectOrder : SharedOrder;
        var index = Array.IndexOf(order, current);
        return index < 0 || index + 1 >= order.Length ? order[0] : order[index + 1];
    }

    /// <summary><c>button_to_change_involvement(room, involvement)</c>.</summary>
    public static HtmlString ButtonToChange(Room room, Involvement involvement, string? csrfToken)
    {
        var stored = involvement.ToStored();
        var labelId = DomId.For(room, "involvement_label");
        var content = ViewHelpers.ImageTag($"notification-bell-{stored}.svg", new Img(Size: 20, AriaHidden: true)).Value +
                      $"<span class=\"for-screen-reader\" id=\"{labelId}\">{ViewHelpers.Escape(Humanized[involvement])}</span>";

        return ViewHelpers.ButtonTo(
            Paths.RoomInvolvement(room.Id, Next(room, involvement)),
            new HtmlString(content),
            method: "put",
            cssClass: $"btn {stored}",
            csrfToken: csrfToken,
            buttonAttributes: $"role=\"checkbox\" aria-checked=\"true\" aria-labelledby=\"{labelId}\" tabindex=\"0\"");
    }
}
