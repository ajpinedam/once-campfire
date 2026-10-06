using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Campfire.Tests.Support;
using Campfire.Web.Http;

namespace Campfire.Tests.AccountsAndPeople;

internal static class AccountsTestSupport
{
    private static readonly HtmlParser Parser = new();

    public const string SafariUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15";
    public const string OldFirefoxUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/114.0";
    public const string EdgeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.18363";

    public static async Task<IHtmlDocument> HtmlAsync(this HttpResponseMessage response) =>
        Parser.ParseDocument(await response.Content.ReadAsStringAsync());

    /// <summary>The (unsigned) session token the response set, if any.</summary>
    public static string? SessionToken(this CampfireApp app, CampfireClient client)
    {
        var cookie = client.Cookies.GetCookies(client.BaseAddress)[AppCookies.SessionToken];
        return cookie is null || cookie.Expired || cookie.Value.Length == 0 ? null : app.Service<AppCookies>().ReadSessionToken(new CookieCollectionView(cookie.Value));
    }

    public static bool IsRedirect(this HttpResponseMessage response) => (int)response.StatusCode is 301 or 302 or 303;

    /// <summary>A file from the Rails test fixtures (test/fixtures/files), found from the build output.</summary>
    public static string FixtureFile(string name, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "test", "fixtures", "files", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Fixture file {name} not found above {sourceFile}");
    }

    public static MultipartFormDataContent Multipart(IEnumerable<(string Name, string Value)> fields, string? fileField = null, string? fixtureName = null, string? contentType = null)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        if (fileField is not null && fixtureName is not null)
        {
            var file = new ByteArrayContent(File.ReadAllBytes(FixtureFile(fixtureName)));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType ?? "application/octet-stream");
            content.Add(file, fileField, fixtureName);
        }

        return content;
    }

    /// <summary>PNG width and height from the IHDR chunk.</summary>
    public static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', "Not a PNG");
        static int BigEndian(byte[] bytes, int offset) => bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
        return (BigEndian(png, 16), BigEndian(png, 20));
    }

    public static HttpRequestMessage WithUserAgent(this HttpRequestMessage request, string userAgent)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return request;
    }

    public static Task<HttpResponseMessage> GetWithUserAgentAsync(this CampfireClient client, string url, string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url).WithUserAgent(userAgent);
        request.Headers.Accept.ParseAdd("text/html");
        return client.Http.SendAsync(request);
    }

    private sealed class CookieCollectionView(string sessionCookie) : Microsoft.AspNetCore.Http.IRequestCookieCollection
    {
        public string? this[string key] => key == AppCookies.SessionToken ? sessionCookie : null;
        public int Count => 1;
        public ICollection<string> Keys => [AppCookies.SessionToken];
        public bool ContainsKey(string key) => key == AppCookies.SessionToken;
        public bool TryGetValue(string key, out string value) { value = sessionCookie; return key == AppCookies.SessionToken; }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() { yield return new(AppCookies.SessionToken, sessionCookie); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static HttpStatusCode Status(this HttpResponseMessage response) => response.StatusCode;
}
