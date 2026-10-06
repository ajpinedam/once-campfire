using System.Text.RegularExpressions;

namespace Campfire.Web.Platform;

/// <summary>
/// What the views need to know about the visitor's browser and OS (Rails' ApplicationPlatform,
/// built on the useragent gem). Parsed once per request.
/// </summary>
public sealed partial record ApplicationPlatform(string UserAgent, string Browser, Version? BrowserVersion, string PlatformName, bool IsBot)
{
    /// <summary>Minimum versions from Rails' <c>allow_browser</c> (null = always blocked).</summary>
    public static readonly IReadOnlyList<(string Key, string Name, Version? Minimum)> SupportedBrowsers =
    [
        ("safari", "Safari", new Version(17, 2)),
        ("chrome", "Chrome", new Version(120, 0)),
        ("firefox", "Firefox", new Version(121, 0)),
        ("opera", "Opera", new Version(104, 0)),
        ("ie", "Ie", null)
    ];

    public static ApplicationPlatform Parse(string? userAgent)
    {
        var ua = userAgent ?? "";
        var (browser, version) = DetectBrowser(ua);
        return new ApplicationPlatform(ua, browser, version, DetectPlatform(ua), BotPattern().IsMatch(ua));
    }

    public bool IsIos => IosPattern().IsMatch(UserAgent);
    public bool IsAndroid => UserAgent.Contains("Android", StringComparison.Ordinal);
    public bool IsMac => UserAgent.Contains("Macintosh", StringComparison.Ordinal);
    public bool IsChrome => Browser == "Chrome";
    public bool IsFirefox => Browser == "Firefox";
    public bool IsSafari => Browser == "Safari";
    public bool IsEdge => Browser == "Edge";
    public bool IsMobile => IsIos || IsAndroid;
    public bool IsDesktop => !IsMobile;
    public bool IsWindows => OperatingSystem == "Windows";

    /// <summary>Apple Messages link previews spoof Facebook's and Twitter's crawlers at once.</summary>
    public bool IsAppleMessages =>
        UserAgent.Contains("facebookexternalhit", StringComparison.OrdinalIgnoreCase) &&
        UserAgent.Contains("Twitterbot", StringComparison.OrdinalIgnoreCase);

    public string OperatingSystem => PlatformName switch
    {
        "Android" => "Android",
        "iPad" => "iPad",
        "iPhone" => "iPhone",
        "Macintosh" => "macOS",
        "Windows" => "Windows",
        "ChromeOS" => "ChromeOS",
        _ => UserAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux" : PlatformName
    };

    /// <summary>Ruby's <c>String#capitalize</c> of the browser name, as the help texts print it.</summary>
    public string BrowserDisplayName => Browser.Length == 0 ? Browser : char.ToUpperInvariant(Browser[0]) + Browser[1..].ToLowerInvariant();

    /// <summary>Rails' <c>allow_browser versions: { safari: 17.2, chrome: 120, firefox: 121, opera: 104, ie: false }</c>.</summary>
    public bool IsUnsupportedBrowser
    {
        get
        {
            if (UserAgent.Length == 0 || BrowserVersion is null || IsBot)
            {
                return false;
            }

            var key = Browser switch
            {
                "Internet Explorer" => "ie",
                _ => Browser.ToLowerInvariant()
            };

            foreach (var (supportedKey, _, minimum) in SupportedBrowsers)
            {
                if (supportedKey == key)
                {
                    return minimum is null || BrowserVersion < minimum;
                }
            }

            return false;
        }
    }

    private static (string Browser, Version? Version) DetectBrowser(string ua)
    {
        Match match;
        if ((match = EdgePattern().Match(ua)).Success) return ("Edge", ParseVersion(match.Groups[1].Value));
        if ((match = OperaPattern().Match(ua)).Success) return ("Opera", ParseVersion(match.Groups[1].Value));
        if ((match = FirefoxPattern().Match(ua)).Success) return ("Firefox", ParseVersion(match.Groups[1].Value));
        if ((match = ChromePattern().Match(ua)).Success) return ("Chrome", ParseVersion(match.Groups[1].Value));
        if ((match = SafariPattern().Match(ua)).Success) return ("Safari", ParseVersion(match.Groups[1].Value));
        if ((match = IePattern().Match(ua)).Success) return ("Internet Explorer", ParseVersion(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value));

        // Anything else is named by its first product token, as the useragent gem does ("curl/8.5" → "curl")
        if ((match = ProductPattern().Match(ua)).Success) return (match.Groups[1].Value, ParseVersion(match.Groups[2].Value));
        return ("", null);
    }

    private static string DetectPlatform(string ua) =>
        ua.Contains("Android", StringComparison.Ordinal) ? "Android" :
        ua.Contains("iPad", StringComparison.Ordinal) ? "iPad" :
        ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone" :
        ua.Contains("Macintosh", StringComparison.Ordinal) ? "Macintosh" :
        ua.Contains("Windows", StringComparison.Ordinal) ? "Windows" :
        ua.Contains("CrOS", StringComparison.Ordinal) ? "ChromeOS" :
        ua.Contains("Linux", StringComparison.Ordinal) ? "Linux" : "";

    private static Version? ParseVersion(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
        {
            return null;
        }
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        return new Version(major, minor);
    }

    [GeneratedRegex(@"iPhone|iPad")] private static partial Regex IosPattern();
    [GeneratedRegex(@"Edg(?:e|A|iOS)?/([\d.]+)")] private static partial Regex EdgePattern();
    [GeneratedRegex(@"(?:OPR|Opera)/([\d.]+)")] private static partial Regex OperaPattern();
    [GeneratedRegex(@"(?:Firefox|FxiOS)/([\d.]+)")] private static partial Regex FirefoxPattern();
    [GeneratedRegex(@"(?:Chrome|CriOS)/([\d.]+)")] private static partial Regex ChromePattern();
    [GeneratedRegex(@"Version/([\d.]+).*Safari/")] private static partial Regex SafariPattern();
    [GeneratedRegex(@"MSIE ([\d.]+)|Trident/.*rv:([\d.]+)")] private static partial Regex IePattern();
    [GeneratedRegex(@"bot|crawl|spider|slurp|facebookexternalhit|preview", RegexOptions.IgnoreCase)] private static partial Regex BotPattern();
    [GeneratedRegex(@"\A([A-Za-z][\w.-]*)(?:/([\d.]+))?")] private static partial Regex ProductPattern();
}
