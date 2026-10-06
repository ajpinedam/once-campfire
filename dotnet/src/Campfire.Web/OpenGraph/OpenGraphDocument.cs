using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Campfire.Web.OpenGraph;

/// <summary>Rails' Opengraph::Document: the og: meta tags of an HTML page.</summary>
public static partial class OpenGraphDocument
{
    public static readonly IReadOnlyList<string> Attributes = ["title", "url", "image", "description"];

    static OpenGraphDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// The og:title/url/image/description values. Tags may use <c>property</c> or <c>name</c>;
    /// later tags win. When the page declares no character set (no meta charset and, when
    /// fetched, no charset header) non-ASCII is dropped, as the Rails app does, rather than
    /// guessing an encoding and producing mojibake.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OpenGraphAttributes(string? html, bool charsetDeclared = false)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(html))
        {
            return attributes;
        }

        using var document = new HtmlParser().ParseDocument(html);
        var declared = charsetDeclared || HasMetaEncoding(document);

        foreach (var meta in document.QuerySelectorAll("meta"))
        {
            var property = meta.GetAttribute("property");
            var name = meta.GetAttribute("name");
            if (property?.StartsWith("og:", StringComparison.Ordinal) != true && name?.StartsWith("og:", StringComparison.Ordinal) != true)
            {
                continue;
            }

            var content = meta.GetAttribute("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var key = (meta.HasAttribute("property") ? property! : name!).Replace("og:", "", StringComparison.Ordinal);
            if (Attributes.Contains(key))
            {
                attributes[key] = declared ? content : AsciiOnly(content);
            }
        }

        return attributes;
    }

    /// <summary>The encoding for a charset label, or null when unknown/absent.</summary>
    public static Encoding? EncodingFor(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A <c>&lt;meta charset&gt;</c> (or http-equiv Content-Type) label in the first bytes of a page.</summary>
    public static string? SniffMetaCharset(ReadOnlySpan<byte> body)
    {
        var head = Encoding.ASCII.GetString(body[..Math.Min(body.Length, 2048)]);
        var match = MetaCharset().Match(head);
        return match.Success ? match.Groups["charset"].Value : null;
    }

    private static bool HasMetaEncoding(AngleSharp.Dom.IDocument document) =>
        document.QuerySelector("meta[charset]") is not null ||
        document.QuerySelectorAll("meta[http-equiv]").Any(meta =>
            string.Equals(meta.GetAttribute("http-equiv"), "content-type", StringComparison.OrdinalIgnoreCase) &&
            meta.GetAttribute("content")?.Contains("charset=", StringComparison.OrdinalIgnoreCase) == true);

    private static string AsciiOnly(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character < 0x80)
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    [GeneratedRegex("""<meta[^>]+charset\s*=\s*["']?(?<charset>[A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
