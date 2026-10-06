using Campfire.Web.Domain;
using Campfire.Web.Views;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Features.Messages;

/// <summary>A boost as the boost partial shows it.</summary>
public sealed record BoostView(Boost Boost, User Booster, bool IsEmoji);

/// <summary>
/// Everything the message partial renders. Deliberately free of per-viewer data: the rendered
/// fragment is cached and shared by everyone (messages--me / --mentioned are applied client-side).
/// </summary>
public sealed record MessageView(
    Message Message,
    User Creator,
    Room Room,
    string RoomName,
    string BaseUrl,
    HtmlString Presentation,
    bool IsEmoji,
    Blob? Attachment,
    IReadOnlyList<BoostView> Boosts)
{
    public string Permalink => Paths.RoomAtMessage(Message.RoomId, Message.Id);

    /// <summary>Rails' <c>room_at_message_url</c>, for the copy-link button.</summary>
    public string PermalinkUrl => BaseUrl + Permalink;
}

/// <summary>Model for the boosts list partial (the <c>boosting</c> turbo frame).</summary>
public sealed record BoostsView(Message Message, IReadOnlyList<BoostView> Boosts);

/// <summary>Model for the presentation partial, replaced on its own when a message is edited.</summary>
public sealed record PresentationView(Message Message, HtmlString Presentation);

/// <summary>The quick boosts offered in the message menu (Rails' EmojiHelper::REACTIONS).</summary>
public static class Reactions
{
    public static readonly IReadOnlyList<(string Character, string Title)> All =
    [
        ("👍", "Thumbs up"),
        ("👏", "Clapping"),
        ("👋", "Waving hand"),
        ("💪", "Muscle"),
        ("❤️", "Red heart"),
        ("😂", "Face with tears of joy"),
        ("🎉", "Party popper"),
        ("🔥", "Fire")
    ];
}
