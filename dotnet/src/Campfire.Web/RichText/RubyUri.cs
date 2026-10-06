using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Web.RichText;

/// <summary>
/// The subset of Ruby's <c>URI.parse</c> (RFC 3986 parser) the embed checks rely on: strict
/// syntax (anything else is <c>URI::InvalidURIError</c>), and the host exactly as written — not
/// normalized, percent-escapes and brackets kept, so the checks can see them.
/// </summary>
internal static partial class RubyUri
{
    /// <summary>A parsed absolute URI; <see cref="ToString"/> rebuilds it as Ruby's <c>URI#to_s</c> does.</summary>
    public sealed record Components(
        string Scheme,
        string? Userinfo,
        string? Host,
        int? Port,
        string Path,
        string? Opaque,
        string? Query,
        string? Fragment)
    {
        public bool IsHttp => Scheme is "http" or "https";

        private int? DefaultPort => Scheme switch { "http" => 80, "https" => 443, "ftp" => 21, _ => null };

        public override string ToString()
        {
            var uri = new StringBuilder(Scheme).Append(':');
            if (Opaque is not null)
            {
                uri.Append(Opaque);
            }
            else
            {
                if (Host is not null) uri.Append("//");
                if (Userinfo is not null) uri.Append(Userinfo).Append('@');
                if (Host is not null) uri.Append(Host);
                if (Port is { } port && port != DefaultPort) uri.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
                if ((Host is not null || Port is not null) && Path.Length > 0 && !Path.StartsWith('/')) uri.Append('/');
                uri.Append(Path);
                if (Query is not null) uri.Append('?').Append(Query);
            }

            if (Fragment is not null) uri.Append('#').Append(Fragment);
            return uri.ToString();
        }
    }

    /// <summary>Null when Ruby would raise <c>URI::InvalidURIError</c>.</summary>
    public static Components? ParseComponents(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = Rfc3986().Match(value);
        if (!match.Success)
        {
            return null;
        }

        string? Group(string name) => match.Groups[name].Success ? match.Groups[name].Value : null;

        var hasAuthority = match.Groups["authority"].Success;
        int? port = int.TryParse(Group("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
        return new Components(
            Scheme: match.Groups["scheme"].Value.ToLowerInvariant(),
            Userinfo: Group("userinfo"),
            Host: hasAuthority ? match.Groups["host"].Value : null,
            Port: port,
            Path: Group("path") ?? Group("absolute") ?? "",
            Opaque: Group("opaque"),
            Query: Group("query"),
            Fragment: Group("fragment"));
    }

    /// <summary>The scheme and host as written (host null when the URI has no authority).</summary>
    public static (string Scheme, string? Host)? Parse(string? value) =>
        ParseComponents(value) is { } uri ? (uri.Scheme, uri.Host) : null;

    /// <summary><c>URI.parse(value).is_a?(URI::HTTP)</c> (URI::HTTPS is a subclass).</summary>
    public static bool IsHttp((string Scheme, string? Host) uri) => uri.Scheme is "http" or "https";

    // RFC 3986 absolute URI (Ruby's RFC3986_Parser::RFC3986_URI, ASCII only):
    //   scheme ":" ( "//" authority path-abempty / path-absolute / path-rootless / path-empty ) [ "?" query ] [ "#" fragment ]
    [GeneratedRegex("""
        \A
        (?<scheme>[A-Za-z][A-Za-z0-9+\-.]*):
        (?:
          //(?<authority>
              (?:(?<userinfo>(?:[A-Za-z0-9\-._~!$&'()*+,;=:]|%[0-9A-Fa-f]{2})*)@)?
              (?<host>
                \[(?:[0-9A-Fa-f:.]+|v[0-9A-Fa-f]+\.[A-Za-z0-9\-._~!$&'()*+,;=:]+)\]
                |(?:[A-Za-z0-9\-._~!$&'()*+,;=]|%[0-9A-Fa-f]{2})*
              )
              (?::(?<port>[0-9]*))?
            )
            (?<path>(?:/(?:[A-Za-z0-9\-._~!$&'()*+,;=:@]|%[0-9A-Fa-f]{2})*)*)
          |
            (?<absolute>/(?:(?:[A-Za-z0-9\-._~!$&'()*+,;=:@]|%[0-9A-Fa-f]{2})+(?:/(?:[A-Za-z0-9\-._~!$&'()*+,;=:@]|%[0-9A-Fa-f]{2})*)*)?)
          |
            (?<opaque>(?:[A-Za-z0-9\-._~!$&'()*+,;=:@]|%[0-9A-Fa-f]{2})+(?:/(?:[A-Za-z0-9\-._~!$&'()*+,;=:@]|%[0-9A-Fa-f]{2})*)*)
          |
        )
        (?:\?(?<query>(?:[A-Za-z0-9\-._~!$&'()*+,;=:@/?]|%[0-9A-Fa-f]{2})*))?
        (?:\#(?<fragment>(?:[A-Za-z0-9\-._~!$&'()*+,;=:@/?]|%[0-9A-Fa-f]{2})*))?
        \z
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture)]
    private static partial Regex Rfc3986();
}
