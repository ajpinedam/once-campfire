using System.Buffers;
using System.Net;
using System.Text;
using Campfire.Web.Net;

namespace Campfire.Web.OpenGraph;

public sealed class TooManyRedirectsException() : Exception("Too many redirects");

public sealed class RedirectDeniedException(string? location) : Exception($"Redirect to a non-HTTP location denied: {location}");

/// <summary>A fetched HTML document: its text, and whether the response declared a character set.</summary>
public sealed record FetchedDocument(string Html, bool CharsetDeclared);

/// <summary>
/// Rails' Opengraph::Fetch: GET (or HEAD) a public URL, following up to ten redirects by hand so
/// every hop's host is resolved through the guard and the connection pinned to that address.
/// </summary>
public sealed class OpenGraphFetch(PrivateNetworkGuard guard, PinnedHttpClient http)
{
    public const string AllowedDocumentContentType = "text/html";
    public const int MaxBodySize = 5 * 1024 * 1024;
    public const int MaxRedirects = 10;

    /// <summary>Everything a fetch does — redirects, slow bodies included — must finish within this.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>The page's HTML when it's a 200 <c>text/html</c> response within the size limit; else null.</summary>
    public async Task<FetchedDocument?> FetchDocumentAsync(Uri url, IPAddress? ip = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        ip ??= await guard.ResolvePublicAsync(url.IdnHost, deadline.Token);
        using var response = await RequestAsync(url, HttpMethod.Get, ip, deadline.Token);
        return await BodyIfAcceptableAsync(response, deadline.Token);
    }

    /// <summary>The raw Content-Type header of a HEAD request (after redirects).</summary>
    public async Task<string?> FetchContentTypeAsync(Uri url, IPAddress? ip = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        ip ??= await guard.ResolvePublicAsync(url.IdnHost, deadline.Token);
        using var response = await RequestAsync(url, HttpMethod.Head, ip, deadline.Token);
        return response.Content.Headers.ContentType?.ToString();
    }

    private async Task<HttpResponseMessage> RequestAsync(Uri url, HttpMethod method, IPAddress ip, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.ParseAdd("*/*");
            var response = await http.SendAsync(request, ip, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location?.OriginalString;
                response.Dispose();
                (url, ip) = await ResolveRedirectAsync(location, cancellationToken);
                continue;
            }

            return response;
        }

        throw new TooManyRedirectsException();
    }

    // Only absolute http(s) locations are followed, and each is resolved through the guard anew.
    private async Task<(Uri, IPAddress)> ResolveRedirectAsync(string? location, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new RedirectDeniedException(location);
        }

        return (url, await guard.ResolvePublicAsync(url.IdnHost, cancellationToken));
    }

    private static async Task<FetchedDocument?> BodyIfAcceptableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var headers = response.Content.Headers;
        var valid = response.StatusCode == HttpStatusCode.OK &&
                    string.Equals(headers.ContentType?.MediaType, AllowedDocumentContentType, StringComparison.OrdinalIgnoreCase) &&
                    (headers.ContentLength ?? 0) <= MaxBodySize;
        if (!valid)
        {
            return null;
        }

        // The Content-Length was checked to avoid reading large bodies at all, but it can be wrong
        // or missing, so read in chunks and give up as soon as the limit is passed.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = new ArrayBufferWriter<byte>(64 * 1024);
        while (true)
        {
            var read = await stream.ReadAsync(body.GetMemory(16 * 1024), cancellationToken);
            if (read == 0)
            {
                break;
            }

            body.Advance(read);
            if (body.WrittenCount > MaxBodySize)
            {
                return null;
            }
        }

        var charset = headers.ContentType?.CharSet;
        return new FetchedDocument(Decode(body.WrittenSpan, charset), CharsetDeclared: charset is not null);
    }

    private static string Decode(ReadOnlySpan<byte> body, string? charset)
    {
        var encoding = OpenGraphDocument.EncodingFor(charset) ?? OpenGraphDocument.EncodingFor(OpenGraphDocument.SniffMetaCharset(body)) ?? Encoding.UTF8;
        return encoding.GetString(body);
    }
}
