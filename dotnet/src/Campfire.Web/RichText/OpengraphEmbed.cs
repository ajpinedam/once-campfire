using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Campfire.Web.Views;

namespace Campfire.Web.RichText;

/// <summary>
/// A link preview attachment (<c>ActionText::Attachment::OpengraphEmbed</c>). Trix serialized the
/// embed's details as attributes of the attachment node; Lexxy only serializes sgid, content and
/// content-type, so newer embeds carry their details in the content markup instead.
/// </summary>
public sealed partial record OpengraphEmbed(string? Href, string? Url, string? Filename, string? Description)
{
    public const string ContentType = "application/vnd.actiontext.opengraph-embed";
    private const string TwitterAvatarUrlPrefix = "https://pbs.twimg.com/profile_images";

    public bool IsTwitterAvatar => Url?.StartsWith(TwitterAvatarUrlPrefix, StringComparison.Ordinal) == true;

    /// <summary>Whether a node is an embed (Ruby's <c>content_type.match(OPENGRAPH_EMBED_CONTENT_TYPE)</c>).</summary>
    public static bool IsEmbedNode(IElement node) =>
        node.GetAttribute("content-type") is { } type && ContentTypePattern().IsMatch(type);

    /// <summary><c>OpengraphEmbed.from_node</c>: null unless the node is an embed.</summary>
    public static OpengraphEmbed? FromNode(IElement node, string requestHost)
    {
        if (!IsEmbedNode(node))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(node.GetAttribute("filename")))
        {
            return new OpengraphEmbed(
                Href: WebUrl(node.GetAttribute("href"), requestHost),
                Url: WebUrl(node.GetAttribute("url"), requestHost),
                Filename: node.GetAttribute("filename"),
                Description: node.GetAttribute("caption"));
        }

        return FromContent(node.GetAttribute("content") ?? "", requestHost);
    }

    private static OpengraphEmbed FromContent(string content, string requestHost)
    {
        var fragment = HtmlDom.Parse(content);
        var title = fragment.QuerySelector(".og-embed__title");
        var link = title?.QuerySelector("a");

        return new OpengraphEmbed(
            Href: WebUrl(link?.GetAttribute("href"), requestHost),
            Url: WebUrl(fragment.QuerySelector(".og-embed__image img")?.GetAttribute("src"), requestHost),
            Filename: (link ?? title) is { } named ? RubyText.Strip(named.TextContent) : null,
            Description: fragment.QuerySelector(".og-embed__description") is { } description ? RubyText.Strip(description.TextContent) : null);
    }

    /// <summary>
    /// A link preview points at what was unfurled: an absolute http or https URL on some other
    /// host. Anything else a message body asks for is dropped, so it can't aim the preview's link
    /// or its image at this Campfire and have every reader's browser fetch it with their session.
    /// </summary>
    internal static string? WebUrl(string? value, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RubyUri.Parse(value) is { } uri && RubyUri.IsHttp(uri) && IsElsewhere(uri.Host, requestHost) ? value : null;
    }

    // "https:/rooms/1" parses as HTTPS with no host at all, and a browser resolves both that and
    // our own hostname against the origin Campfire is served from. A percent-escape hides our
    // hostname from this comparison while a browser still unescapes it back to us, so an escaped
    // host is out too, and neither case is anything an unfurl could have produced.
    private static bool IsElsewhere(string? host, string requestHost) =>
        IsNamedHost(host) && CanonicalHost(host!) != CanonicalHost(requestHost);

    // A preview names a page on the public internet, so its host is a domain name, written
    // plainly. A bare address is not one, and a browser rewrites the many spellings of an address
    // ("2130706433", "0x7f.0.0.1") into a single one before it fetches, which is a race a
    // comparison here loses.
    private static bool IsNamedHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && !host.Contains('%') && host.Contains('.') && IsDomainEnding(LastLabel(host));

    // Ruby's host.split(".").last: trailing empty labels are dropped, so "example.com." ends in "com".
    private static string LastLabel(string host)
    {
        var trimmed = host.TrimEnd('.');
        return trimmed[(trimmed.LastIndexOf('.') + 1)..];
    }

    // What keeps a name from reading as an address is its last label, which is a word: never a
    // number, and never the hexadecimal spelling of one.
    private static bool IsDomainEnding(string label) =>
        AsciiLetter().IsMatch(label) && !label.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalHost(string host)
    {
        var lowered = host.ToLowerInvariant();
        return lowered.EndsWith('.') ? lowered[..^1] : lowered;
    }

    /// <summary>The <c>action_text/attachables/_opengraph_embed</c> partial.</summary>
    public string Render()
    {
        var html = new StringBuilder(512);
        html.Append("<figure class=\"attachment attachment--content attachment--og\">\n  <actiontext-opengraph-embed>\n")
            .Append("    <div class=\"og-embed gap ").Append(IsTwitterAvatar ? "og-embed--twitter-avatar" : "").Append("\">\n")
            .Append("      <div class=\"og-embed__content\">\n        <div class=\"og-embed__title\">\n          ");

        // link_to_if href.present?, truncate(filename, length: 280, omission: "…"), href, rel: "noreferrer", target: "_blank"
        var title = Filename is null ? null : MinimalHtmlEncoder.Escape(RubyText.Truncate(Filename, 280));
        if (!string.IsNullOrWhiteSpace(Href))
        {
            html.Append("<a rel=\"noreferrer\" target=\"_blank\" href=\"").Append(MinimalHtmlEncoder.Escape(Href)).Append("\">")
                .Append(title ?? MinimalHtmlEncoder.Escape(Href)).Append("</a>");
        }
        else
        {
            html.Append(title);
        }

        html.Append("\n        </div>\n        <div class=\"og-embed__description\">")
            .Append(MinimalHtmlEncoder.Escape(RubyText.Truncate(Description ?? "", 560)))
            .Append("</div>\n      </div>\n");

        if (Url is not null)
        {
            html.Append("        <div class=\"og-embed__image\">\n          <img src=\"").Append(MinimalHtmlEncoder.Escape(Url))
                .Append("\" class=\"image center\" alt=\"\">\n        </div>\n");
        }

        return html.Append("    </div>\n  </actiontext-opengraph-embed>\n</figure>").ToString();
    }

    [GeneratedRegex("application/vnd.actiontext.opengraph-embed")]
    private static partial Regex ContentTypePattern();

    [GeneratedRegex("[a-z]", RegexOptions.IgnoreCase)]
    private static partial Regex AsciiLetter();
}
