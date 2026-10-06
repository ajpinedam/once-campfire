using System.Net;
using System.Text.RegularExpressions;
using Campfire.Web.Net;

namespace Campfire.Web.OpenGraph;

/// <summary>A URL checked for fetching: parsed as http(s), with the public address it resolves to.</summary>
public sealed record OpenGraphLocation(string? Url, Uri? ParsedUrl, IPAddress? ResolvedIp, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Rails' Opengraph::Location: validating a URL and reading it, failures logged and swallowed.</summary>
public sealed partial class OpenGraphLocations(PrivateNetworkGuard guard, OpenGraphFetch fetch, ILogger<OpenGraphLocations> logger)
{
    /// <summary>Parses and resolves a URL; <see cref="OpenGraphLocation.Errors"/> says why it can't be fetched.</summary>
    public async Task<OpenGraphLocation> CheckAsync(string? url, CancellationToken cancellationToken = default)
    {
        var parsed = Parse(url);
        var ip = parsed is null ? null : await guard.TryResolvePublicAsync(parsed.IdnHost, cancellationToken);

        var errors = new List<string>(2);
        if (parsed is null) errors.Add("is invalid");
        if (ip is null) errors.Add("is not public");
        return new OpenGraphLocation(url, parsed, ip, errors);
    }

    /// <summary>The page's HTML, or null when the URL is invalid, points at a file or media, or the fetch fails.</summary>
    public async Task<FetchedDocument?> ReadHtmlAsync(string? url, CancellationToken cancellationToken = default)
    {
        var location = await CheckAsync(url, cancellationToken);
        if (!location.IsValid || FilesAndMedia().IsMatch(url!))
        {
            return null;
        }

        try
        {
            return await fetch.FetchDocumentAsync(location.ParsedUrl!, location.ResolvedIp, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Failed to fetch {Host} at {Ip} ({Error})", location.ParsedUrl!.Host, location.ResolvedIp, error.GetType().Name);
            return null;
        }
    }

    /// <summary>The Content-Type a HEAD request reports, or null.</summary>
    public async Task<string?> FetchContentTypeAsync(string? url, CancellationToken cancellationToken = default)
    {
        var location = await CheckAsync(url, cancellationToken);
        if (!location.IsValid)
        {
            return null;
        }

        try
        {
            return await fetch.FetchContentTypeAsync(location.ParsedUrl!, location.ResolvedIp, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Failed to fetch {Host} at {Ip} ({Error})", location.ParsedUrl!.Host, location.ResolvedIp, error.GetType().Name);
            return null;
        }
    }

    /// <summary>Only absolute http(s) URLs with a host; anything else (relative, ftp:, "https/x") is invalid.</summary>
    public static Uri? Parse(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host))
        {
            return null;
        }

        return parsed;
    }

    [GeneratedRegex(@"\bhttps?://\S+\.(?:zip|tar|tar\.gz|tar\.bz2|tar\.xz|gz|bz2|rar|7z|dmg|exe|msi|pkg|deb|iso|jpg|jpeg|png|gif|bmp|mp4|mov|avi|mkv|wmv|flv|heic|heif|mp3|wav|ogg|aac|wma|webm|ogv|mpg|mpeg)\b")]
    private static partial Regex FilesAndMedia();
}
