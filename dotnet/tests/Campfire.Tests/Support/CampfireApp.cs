using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Tests.Support;

// Inside the namespace, so sibling test namespaces (Campfire.Tests.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Security;

/// <summary>
/// A fully isolated Campfire instance: its own temporary storage directory (database + files)
/// and a fixed secret. Seeded with <see cref="Fixtures"/> mirroring test/fixtures/*.yml.
/// Use as an xUnit class fixture (<c>IClassFixture&lt;CampfireApp&gt;</c>), or create one per test
/// when a test mutates data that others read.
/// </summary>
public sealed class CampfireApp : WebApplicationFactory<Program>
{
    public const string Password = "secret123456";

    private readonly string _storage = Path.Combine(Path.GetTempPath(), "campfire-tests", Guid.NewGuid().ToString("N"));
    private Fixtures? _fixtures;

    static CampfireApp() => Passwords.WorkFactor = 4;

    public CampfireApp()
    {
        Directory.CreateDirectory(_storage);
        Environment.SetEnvironmentVariable("STORAGE_PATH", null);
    }

    public string StoragePath => _storage;

    /// <summary>Seeded records (created on first access, after the app has migrated the database).</summary>
    public Fixtures Fixtures => _fixtures ??= Fixtures.Seed(this);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("STORAGE_PATH", _storage);
        builder.UseSetting("SECRET_KEY_BASE", "test-secret-key-base-0123456789abcdef0123456789abcdef");
        builder.UseSetting("VAPID_PUBLIC_KEY", "BPh2h8Xb4K2L8hQh0N1cN2xS8D3zv0gZt7lQ6n0XmS9a3pQ6S1X3n0l1Jb8tW2y5X7c9V0b1n2m3k4j5h6g7f8e");
        builder.UseSetting("VAPID_PRIVATE_KEY", "test-vapid-private-key");
    }

    public Database Database => Services.GetRequiredService<Database>();

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Runs a query against the app's database.</summary>
    public T Sql<T>(Func<Sql, T> work)
    {
        using var sql = Database.Open();
        return work(sql);
    }

    public void Sql(Action<Sql> work)
    {
        using var sql = Database.Open();
        work(sql);
    }

    /// <summary>An anonymous browser: keeps cookies, doesn't follow redirects, sends CSRF tokens.</summary>
    public CampfireClient Anonymous() => new(this);

    /// <summary>A browser signed in as <paramref name="user"/> (session created directly, as sign_in does).</summary>
    public CampfireClient SignedInAs(User user)
    {
        var client = new CampfireClient(this);
        var session = Sql(sql => Web.Data.Queries.Sessions.Start(sql, user.Id, "Mozilla/5.0 (Macintosh) Chrome/130.0 Safari/537.36", "127.0.0.1"));
        client.Cookies.Add(client.BaseAddress, new Cookie(AppCookies.SessionToken, Service<KeyRing>().Cookies.Sign(session.Token)));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                Directory.Delete(_storage, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// An HttpClient with a cookie jar and automatic CSRF: unsafe requests carry X-CSRF-Token derived
/// from the jar's seed cookie (issued on first use), exactly as Turbo sends the meta tag token.
/// Redirects are not followed, so tests can assert on them.
/// </summary>
public sealed partial class CampfireClient : IDisposable
{
    private readonly CampfireApp _app;
    private readonly HttpClient _client;
    private readonly string _csrfSeed = Guid.NewGuid().ToString("N");

    public CampfireClient(CampfireApp app)
    {
        _app = app;
        Cookies = new CookieContainer();
        _client = app.CreateDefaultClient(new CookieHandler(Cookies), new CsrfHandler(this));
        Cookies.Add(BaseAddress, new Cookie(Csrf.CookieName, _csrfSeed));
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    }

    public CookieContainer Cookies { get; }
    public Uri BaseAddress => _client.BaseAddress!;
    public HttpClient Http => _client;

    /// <summary>When false, unsafe requests go out without a CSRF token.</summary>
    public bool SendCsrfToken { get; set; } = true;

    public string CsrfToken() => _app.Service<Csrf>().MaskedToken(_csrfSeed);

    public Task<HttpResponseMessage> GetAsync(string url, string? accept = null) => SendAsync(HttpMethod.Get, url, null, accept);

    public Task<HttpResponseMessage> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, string? accept = null) =>
        SendAsync(HttpMethod.Post, url, new FormUrlEncodedContent(form), accept);

    public Task<HttpResponseMessage> PostFormAsync(string url, params (string Key, string Value)[] form) =>
        PostFormAsync(url, form.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));

    /// <summary>A Rails-style form submission with <c>_method</c> override (PATCH/PUT/DELETE via POST).</summary>
    public Task<HttpResponseMessage> SubmitAsync(string method, string url, params (string Key, string Value)[] form) =>
        PostFormAsync(url, form.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)).Append(KeyValuePair.Create("_method", method)));

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null, string? accept = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Accept.ParseAdd(accept ?? "text/html, application/xhtml+xml");
        return _client.SendAsync(request);
    }

    public void Dispose() => _client.Dispose();

    private sealed class CookieHandler(CookieContainer cookies) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var header = cookies.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0)
            {
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    cookies.SetCookies(request.RequestUri!, setCookie);
                }
            }
            return response;
        }
    }

    private sealed class CsrfHandler(CampfireClient client) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (client.SendCsrfToken && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                request.Headers.Add(Csrf.HeaderName, client.CsrfToken());
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public static partial class ResponseAssertions
{
    public static async Task<string> BodyAsync(this HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    public static string? RedirectLocation(this HttpResponseMessage response) => response.Headers.Location?.OriginalString;

    public static void AssertRedirectTo(this HttpResponseMessage response, string location)
    {
        Assert.True((int)response.StatusCode is 301 or 302 or 303, $"Expected a redirect but got {(int)response.StatusCode}");
        Assert.Equal(location, response.RedirectLocation());
    }
}
