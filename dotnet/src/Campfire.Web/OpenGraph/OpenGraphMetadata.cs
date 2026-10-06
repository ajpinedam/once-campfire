using System.Collections.Frozen;
using AngleSharp.Html.Parser;

namespace Campfire.Web.OpenGraph;

/// <summary>
/// Rails' Opengraph::Metadata after validation: title and description reduced to escaped plain
/// text; title, url and description required.
/// </summary>
public sealed record OpenGraphMetadata(string? Title, string? Url, string? Image, string? Description)
{
    public IReadOnlyList<string> Errors
    {
        get
        {
            var errors = new List<string>(3);
            if (string.IsNullOrWhiteSpace(Title)) errors.Add("Title can't be blank");
            if (string.IsNullOrWhiteSpace(Url)) errors.Add("Url can't be blank");
            if (string.IsNullOrWhiteSpace(Description)) errors.Add("Description can't be blank");
            return errors;
        }
    }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>Rails' <c>Opengraph::Metadata.from_url</c>: unfurls a link someone pasted into a message.</summary>
public sealed class OpenGraphUnfurler(OpenGraphLocations locations)
{
    private static readonly FrozenSet<string> TwitterHosts = FrozenSet.ToFrozenSet(["twitter.com", "www.twitter.com", "x.com", "www.x.com"], StringComparer.Ordinal);
    private const string FxTwitterHost = "fxtwitter.com";
    private static readonly FrozenSet<string> AllowedImageContentTypes = FrozenSet.ToFrozenSet(["image/jpeg", "image/png", "image/gif", "image/webp"], StringComparer.Ordinal);

    public async Task<OpenGraphMetadata> FromUrlAsync(string untrustedUrl, CancellationToken cancellationToken = default)
    {
        var attributes = await FetchAttributesAsync(untrustedUrl, cancellationToken);
        attributes.TryGetValue("title", out var title);
        attributes.TryGetValue("url", out var url);
        attributes.TryGetValue("image", out var image);
        attributes.TryGetValue("description", out var description);

        return new OpenGraphMetadata(
            Title: Sanitize(title),
            Url: await ValidCanonicalUrlAsync(url, untrustedUrl, cancellationToken),
            Image: await ValidImageAsync(image, cancellationToken),
            Description: Sanitize(description));
    }

    /// <summary>
    /// Rails' <c>sanitize(strip_tags(text))</c>: the text content (script text included, tags and
    /// decoded-entity tags dropped) with <c>&amp; &lt; &gt;</c> escaped.
    /// </summary>
    public static string? Sanitize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        using var document = new HtmlParser().ParseDocument("");
        document.Body!.InnerHtml = text;
        return EscapeText(document.Body.TextContent);
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchAttributesAsync(string untrustedUrl, CancellationToken cancellationToken)
    {
        if (IsTweetUrl(untrustedUrl))
        {
            // Twitter/X don't serve OpenGraph; fxtwitter.com does. Its HTML declares no character
            // set but is UTF-8, so it's read as declared UTF-8 (Rails forces the encoding here).
            var document = await locations.ReadHtmlAsync(ReplaceTwitterHost(untrustedUrl), cancellationToken);
            return OpenGraphDocument.OpenGraphAttributes(document?.Html, charsetDeclared: true);
        }

        var page = await locations.ReadHtmlAsync(untrustedUrl, cancellationToken);
        return OpenGraphDocument.OpenGraphAttributes(page?.Html, page?.CharsetDeclared ?? false);
    }

    private async Task<string?> ValidCanonicalUrlAsync(string? url, string fallback, CancellationToken cancellationToken) =>
        (await locations.CheckAsync(url, cancellationToken)).IsValid ? url : fallback;

    private async Task<string?> ValidImageAsync(string? image, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var contentType = (await locations.FetchContentTypeAsync(image, cancellationToken))?.ToLowerInvariant();
        return contentType is not null && AllowedImageContentTypes.Contains(contentType) ? image : null;
    }

    private static bool IsTweetUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && TwitterHosts.Contains(uri.Host) &&
        uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/";

    private static string? ReplaceTwitterHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return TwitterHosts.Contains(uri.Host) ? new UriBuilder(uri) { Host = FxTwitterHost }.Uri.AbsoluteUri : url;
    }

    private static string EscapeText(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
