using System.Collections.Frozen;
using AngleSharp.Dom;

namespace Campfire.Web.RichText;

/// <summary>
/// Campfire's presentation filters over a message's stored content (app/helpers/content_filters).
/// A message passes through three sanitization layers on its way to the screen: SanitizeTags strips
/// disallowed markup from the body, Action Text sanitizes the rendered content, and auto_link
/// re-sanitizes the final HTML.
/// </summary>
internal static class ContentFilters
{
    private const string EmbedSelector = $"{HtmlDom.AttachmentTag}[content-type=\"{OpengraphEmbed.ContentType}\"]";

    private static readonly FrozenDictionary<string, string> TwitterDomainMapping =
        new Dictionary<string, string> { ["x.com"] = "twitter.com" }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] TwitterDomains = ["x.com", "twitter.com"];

    /// <summary>
    /// <c>RemoveSoloUnfurledLinkText</c>: when a message is nothing but a link and its preview,
    /// drop the link text and keep just the preview. Trix bodies wrap everything in a div, whose
    /// content becomes the embed; Lexxy bodies drop every paragraph that holds no attachment.
    /// </summary>
    public static void RemoveSoloUnfurledLinkText(IElement root, Attachables attachables)
    {
        var embeds = root.QuerySelectorAll(EmbedSelector);
        if (embeds.Length != 1)
        {
            return;
        }

        var soloUrl = OpengraphEmbed.FromNode(embeds[0], attachables.RequestHost)?.Href;
        if (NormalizeTweetUrl(soloUrl) != NormalizeTweetUrl(PlainText.Convert(root, attachables.PlainText)))
        {
            return;
        }

        if (root.QuerySelector("div") is not null)
        {
            var embedHtml = embeds[0].OuterHtml;
            foreach (var div in root.QuerySelectorAll("div").ToArray())
            {
                if (root.Contains(div))
                {
                    HtmlDom.SetInnerHtml(div, embedHtml);
                }
            }
        }
        else
        {
            foreach (var paragraph in root.QuerySelectorAll("p").ToArray())
            {
                if (root.Contains(paragraph) && paragraph.QuerySelector(HtmlDom.AttachmentTag) is null)
                {
                    HtmlDom.Remove(paragraph);
                }
            }
        }
    }

    /// <summary><c>SanitizeTags</c>: removes every element outside ALLOWED_TAGS, contents and all.</summary>
    public static void SanitizeTags(IElement root)
    {
        foreach (var element in root.QuerySelectorAll("*").ToArray())
        {
            if (!SafeList.ContentFilterTags.Contains(element.LocalName) && root.Contains(element))
            {
                HtmlDom.Remove(element);
            }
        }
    }

    /// <summary>x.com and twitter.com links to the same tweet compare equal, query string aside.</summary>
    internal static string? NormalizeTweetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !TwitterDomains.Any(domain => RubyText.Strip(url).Contains(domain, StringComparison.Ordinal)))
        {
            return url;
        }

        if (RubyUri.ParseComponents(url) is not { } uri)
        {
            return url;
        }

        var host = uri.Host is null ? null : TwitterDomainMapping.GetValueOrDefault(uri.Host.ToLowerInvariant(), uri.Host);
        return (uri with { Host = host, Query = null }).ToString();
    }
}
