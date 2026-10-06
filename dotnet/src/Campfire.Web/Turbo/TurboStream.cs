using System.Text;
using System.Text.Encodings.Web;

namespace Campfire.Web.Turbo;

/// <summary>
/// Builds <c>&lt;turbo-stream&gt;</c> elements, as turbo-rails' <c>turbo_stream</c> tag builder does.
/// <paramref name="html"/> arguments must already be safe HTML (rendered templates).
/// </summary>
public static class TurboStream
{
    public static string Append(string target, string html, bool maintainScroll = false) => Action("append", target, html, maintainScroll);
    public static string Prepend(string target, string html, bool maintainScroll = false) => Action("prepend", target, html, maintainScroll);
    public static string Replace(string target, string html, bool maintainScroll = false) => Action("replace", target, html, maintainScroll);
    public static string Update(string target, string html, bool maintainScroll = false) => Action("update", target, html, maintainScroll);
    public static string Before(string target, string html) => Action("before", target, html, false);
    public static string After(string target, string html) => Action("after", target, html, false);

    public static string Remove(string target) =>
        $"<turbo-stream action=\"remove\" target=\"{Campfire.Web.Views.MinimalHtmlEncoder.Escape(target)}\"></turbo-stream>";

    private static string Action(string action, string target, string html, bool maintainScroll)
    {
        var builder = new StringBuilder(html.Length + 96);
        builder.Append("<turbo-stream");
        if (maintainScroll)
        {
            builder.Append(" maintain_scroll=\"true\"");
        }
        builder.Append(" action=\"").Append(action)
            .Append("\" target=\"").Append(Campfire.Web.Views.MinimalHtmlEncoder.Escape(target))
            .Append("\"><template>").Append(html).Append("</template></turbo-stream>");
        return builder.ToString();
    }
}

/// <summary>
/// Names of the pub/sub streams pages subscribe to. Turbo stream names are signed into
/// <c>&lt;turbo-cable-stream-source signed-stream-name&gt;</c> elements by <see cref="Campfire.Web.Views.ViewHelpers.TurboStreamFrom"/>.
/// </summary>
public static class StreamNames
{
    /// <summary><c>turbo_stream_from @room, :messages</c> — guarded by RoomMessagesChannel.</summary>
    public static string RoomMessages(long roomId) => $"rooms/{roomId}:messages";

    /// <summary><c>turbo_stream_from :rooms</c> — the shared rooms list everyone sees.</summary>
    public const string Rooms = "rooms";

    /// <summary><c>turbo_stream_from Current.user, :rooms</c>.</summary>
    public static string UserRooms(long userId) => $"users/{userId}:rooms";

    /// <summary>ReadRoomsChannel: rooms this user just read on another tab/device.</summary>
    public static string UserReads(long userId) => $"user_{userId}_reads";

    /// <summary>UnreadRoomsChannel: activity in this user's rooms.</summary>
    public static string UserUnreads(long userId) => $"user_{userId}_unreads";

    /// <summary>TypingNotificationsChannel's <c>broadcast_to @room</c>.</summary>
    public static string Typing(long roomId) => $"typing_notifications:rooms/{roomId}";

    /// <summary>The room id a messages stream name refers to, if it is one.</summary>
    public static long? RoomIdFromMessagesStream(string streamName)
    {
        const string prefix = "rooms/";
        const string suffix = ":messages";
        if (!streamName.StartsWith(prefix, StringComparison.Ordinal) || !streamName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        return long.TryParse(streamName.AsSpan(prefix.Length, streamName.Length - prefix.Length - suffix.Length), out var id) ? id : null;
    }

    /// <summary>True for any stream name RoomMessagesChannel guards (Rails' <c>guarded_stream?</c>).</summary>
    public static bool IsGuarded(string streamName)
    {
        var separator = streamName.IndexOf(':', StringComparison.Ordinal);
        return separator >= 0 && streamName[(separator + 1)..] == "messages";
    }
}
