using System.Text.RegularExpressions;

namespace Campfire.Web.Http;

/// <summary>
/// Bot requests carry the bot key as a path segment (/rooms/:room_id/:bot_key/...), which query
/// parameter filtering never touches. Redact it wherever a path is logged.
/// </summary>
public static partial class LogScrubbing
{
    public static string Path(PathString path) => Scrub(path.Value ?? "");

    public static string Scrub(string text) => BotKeyInPath().Replace(text, "$1[FILTERED]");

    [GeneratedRegex(@"(/rooms/\d+/)\d+-[A-Za-z0-9]+")]
    private static partial Regex BotKeyInPath();
}
