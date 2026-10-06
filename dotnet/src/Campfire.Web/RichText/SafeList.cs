using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Campfire.Web.RichText;

/// <summary>
/// The tag and attribute safe lists of every sanitization layer a message passes through, as
/// rails-html-sanitizer, Action Text and Campfire's ContentFilters define them.
/// </summary>
internal static class SafeList
{
    /// <summary>rails-html-sanitizer's <c>SafeListSanitizer.allowed_tags</c>.</summary>
    public static readonly string[] DefaultTags =
    [
        "a", "abbr", "acronym", "address", "b", "big", "blockquote", "br", "cite", "code", "dd", "del", "dfn", "div",
        "dl", "dt", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "mark", "ol", "p",
        "pre", "samp", "small", "span", "strong", "sub", "sup", "time", "tt", "ul", "var"
    ];

    /// <summary>rails-html-sanitizer's <c>SafeListSanitizer.allowed_attributes</c>.</summary>
    public static readonly string[] DefaultAttributes =
        ["abbr", "alt", "cite", "class", "datetime", "height", "href", "lang", "name", "src", "title", "width", "xml:lang"];

    /// <summary><c>ContentFilters::EDITOR_FORMATTING_TAGS</c>: formatting Lexxy produces that Rails' lists lack.</summary>
    public static readonly string[] EditorFormattingTags = ["s", "u", "mark", "table", "thead", "tbody", "tfoot", "tr", "th", "td"];

    /// <summary><c>ContentFilters::EDITOR_FORMATTING_ATTRIBUTES</c>.</summary>
    public static readonly string[] EditorFormattingAttributes = ["data-language"];

    /// <summary><c>ActionText::Attachment::ATTRIBUTES</c>.</summary>
    public static readonly string[] AttachmentAttributes =
        ["sgid", "content-type", "url", "href", "filename", "filesize", "width", "height", "previewable", "presentation", "caption", "content"];

    /// <summary><c>ContentFilters::SanitizeTags::ALLOWED_TAGS</c> (note: no <c>img</c>).</summary>
    public static readonly FrozenSet<string> ContentFilterTags = Set(
        ["a", "abbr", "acronym", "address", "b", "big", "blockquote", "br", "cite", "code", "dd", "del", "dfn", "div", "dl", "dt",
         "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "ins", "kbd", "li", "ol", "p", "pre", "samp", "small", "span",
         "strong", "sub", "sup", "time", "tt", "ul", "var"],
        EditorFormattingTags,
        [HtmlDom.AttachmentTag, "figure", "figcaption"]);

    /// <summary>Action Text's allowed attributes as Campfire configures them (lib/rails_ext/action_text_allowed_tags.rb).</summary>
    public static readonly FrozenSet<string> ActionTextAttributes = Set(DefaultAttributes, AttachmentAttributes, EditorFormattingAttributes);

    /// <summary>Action Text's allowed tags as Campfire configures them.</summary>
    public static readonly FrozenSet<string> ActionTextTags = Set(DefaultTags, [HtmlDom.AttachmentTag, "figure", "figcaption"], EditorFormattingTags);

    /// <summary><c>ContentFilters::SanitizeAttributes</c>: Action Text's attributes plus <c>class</c>.</summary>
    public static readonly FrozenSet<string> ContentFilterAttributes = Set(ActionTextAttributes, ["class"]);

    /// <summary><c>MessagesHelper::AUTO_LINK_ALLOWED_TAGS</c>.</summary>
    public static readonly FrozenSet<string> AutoLinkTags = Set(DefaultTags, EditorFormattingTags);

    /// <summary><c>MessagesHelper::AUTO_LINK_ALLOWED_ATTRIBUTES</c>.</summary>
    public static readonly FrozenSet<string> AutoLinkAttributes = Set(DefaultAttributes, EditorFormattingAttributes);

    private static FrozenSet<string> Set(params IEnumerable<string>[] lists) =>
        lists.SelectMany(list => list).ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// rails-html-sanitizer's <c>PermitScrubber</c> (without pruning): walks bottom-up; elements not in
/// <c>tags</c> (and comments, doctypes...) are stripped — their children move up in their place —
/// and kept elements lose every attribute not in <c>attributes</c>, plus URI attributes whose
/// scheme Loofah wouldn't allow (javascript:, vbscript:, non-image data: ...). Immutable and
/// thread-safe; one instance per configuration.
/// </summary>
internal sealed partial class PermitScrubber(FrozenSet<string> tags, FrozenSet<string> attributes)
{
    /// <summary>Loofah's <c>ATTR_VAL_IS_URI</c>.</summary>
    private static readonly FrozenSet<string> UriAttributes =
        new[] { "href", "src", "cite", "action", "longdesc", "xlink:href", "lowsrc", "xml:base" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Loofah's <c>ALLOWED_PROTOCOLS</c>.</summary>
    private static readonly FrozenSet<string> AllowedProtocols =
        new[] { "afs", "aim", "callto", "data", "ed2k", "feed", "ftp", "gopher", "http", "https", "irc", "mailto", "news",
                "nntp", "rsync", "rtsp", "sftp", "ssh", "tag", "telnet", "urn", "webcal", "xmpp" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Loofah's <c>ALLOWED_URI_DATA_MEDIATYPES</c>.</summary>
    private static readonly FrozenSet<string> AllowedDataMediaTypes =
        new[] { "image/gif", "image/jpeg", "image/png", "text/css", "text/plain" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Scrubs everything below <paramref name="root"/> (the root itself is kept).</summary>
    public void Scrub(INode root)
    {
        foreach (var child in root.ChildNodes.ToArray())
        {
            ScrubNode(child);
        }
    }

    /// <summary>Parses, scrubs and serializes an HTML fragment.</summary>
    public string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html ?? "";
        }

        var root = HtmlDom.Parse(html);
        Scrub(root);
        return HtmlDom.Serialize(root);
    }

    private void ScrubNode(INode node)
    {
        foreach (var child in node.ChildNodes.ToArray())
        {
            ScrubNode(child);
        }

        switch (node)
        {
            case IText:
                return;
            case IElement element when tags.Contains(element.LocalName):
                ScrubAttributes(element);
                return;
            default:
                HtmlDom.Unwrap(node);
                return;
        }
    }

    private void ScrubAttributes(IElement element)
    {
        foreach (var attribute in element.Attributes.ToArray())
        {
            var name = attribute.Name;
            var remove = !attributes.Contains(name) ||
                         UriAttributes.Contains(name) && IsUnsafeUri(attribute.Value) ||
                         name == "src" && string.IsNullOrWhiteSpace(attribute.Value);

            if (remove)
            {
                if (attribute.NamespaceUri is { } ns)
                {
                    element.RemoveAttribute(ns, attribute.LocalName);
                }
                else
                {
                    element.RemoveAttribute(name);
                }
            }
        }
    }

    /// <summary>Loofah's <c>scrub_uri_attribute</c>.</summary>
    internal static bool IsUnsafeUri(string value)
    {
        var unescaped = WebUtility.HtmlDecode(ControlCharacters().Replace(value, "")).Replace('：', ':').ToLowerInvariant();
        var parts = ProtocolSeparator().Split(unescaped, 3);

        if (Scheme().IsMatch(unescaped) && !AllowedProtocols.Contains(parts[0]))
        {
            return true;
        }

        if (parts[0] == "data" && parts.Length > 1)
        {
            var mediaType = MediaTypeSeparator().Split(parts[1], 2)[0];
            return !AllowedDataMediaTypes.Contains(mediaType);
        }

        return false;
    }

    [GeneratedRegex("[`\u0000- \u007F\u0080-ā]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(":|(&#0*58)|(&#x70)|(&#x0*3a)|(%|&#37;)3A", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex ProtocolSeparator();

    [GeneratedRegex("^[a-z0-9][-+.a-z0-9]*:")]
    private static partial Regex Scheme();

    [GeneratedRegex("[;,]")]
    private static partial Regex MediaTypeSeparator();
}

internal static class Sanitizers
{
    /// <summary><c>ContentFilters::SanitizeAttributes</c>.</summary>
    public static readonly PermitScrubber ContentFilter = new(SafeList.ContentFilterTags, SafeList.ContentFilterAttributes);

    /// <summary>Action Text's render-time sanitizer (<c>sanitize_action_text_content</c>).</summary>
    public static readonly PermitScrubber ActionText = new(SafeList.ActionTextTags, SafeList.ActionTextAttributes);

    /// <summary><c>auto_link</c>'s re-sanitization in <c>message_presentation</c>.</summary>
    public static readonly PermitScrubber AutoLink = new(SafeList.AutoLinkTags, SafeList.AutoLinkAttributes);
}

/// <summary>Ruby string helpers whose semantics templates and filters rely on.</summary>
internal static class RubyText
{
    /// <summary>
    /// ActiveSupport's <c>String#truncate(length, omission:)</c>, counting code points as Ruby
    /// does (an emoji is one character, not two UTF-16 units).
    /// </summary>
    public static string Truncate(string text, int length, string omission = "…")
    {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= length)
        {
            return text;
        }

        var keep = Math.Max(0, length - omission.EnumerateRunes().Count());
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < keep; i++)
        {
            builder.Append(runes[i].ToString());
        }
        return builder.Append(omission).ToString();
    }

    /// <summary>Ruby's <c>String#chomp("")</c>: removes every trailing <c>\n</c> / <c>\r\n</c>.</summary>
    public static string ChompNewlines(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '\n')
        {
            end--;
            if (end > 0 && text[end - 1] == '\r')
            {
                end--;
            }
        }
        return end == text.Length ? text : text[..end];
    }

    /// <summary>Ruby's <c>String#strip</c> (ASCII whitespace and NUL).</summary>
    public static string Strip(string text) => text.Trim(' ', '\t', '\n', '\v', '\f', '\r', '\0');
}
