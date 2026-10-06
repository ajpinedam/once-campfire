using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Data.Queries;
using Campfire.Web.Features.Sessions;
using Campfire.Web.Security;

namespace Campfire.Tests.AccountsAndPeople;

public sealed class SessionsTests : IDisposable
{
    private readonly CampfireApp _app = new();
    private Fixtures Fixtures => _app.Fixtures;

    public void Dispose() => _app.Dispose();

    private long SessionCount() => _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM sessions"));

    [Fact]
    public async Task New_renders_the_sign_in_form()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        var response = await client.GetAsync("/session/new");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.HtmlAsync();
        Assert.NotNull(html.QuerySelector("form[action='/session'] input[name=email_address]"));
        Assert.NotNull(html.QuerySelector("meta[name=turbo-visit-control][content=reload]"));
        Assert.Contains("david@37signals.com", html.QuerySelector("a.btn.center")!.TextContent);
    }

    [Fact]
    public async Task New_redirects_to_first_run_when_no_users_exist()
    {
        using var client = _app.Anonymous();
        var response = await client.GetAsync("/session/new");
        response.AssertRedirectTo("/first_run");
    }

    [Fact]
    public async Task New_is_denied_to_incompatible_browsers()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        var html = await (await client.GetWithUserAgentAsync("/session/new", AccountsTestSupport.OldFirefoxUserAgent)).HtmlAsync();
        Assert.Contains("Upgrade to a supported web browser", html.QuerySelector("h1")!.TextContent);
    }

    [Fact]
    public async Task New_is_allowed_for_compatible_browsers()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        var html = await (await client.GetWithUserAgentAsync("/session/new", AccountsTestSupport.SafariUserAgent)).HtmlAsync();
        Assert.DoesNotContain(html.QuerySelectorAll("h1"), h1 => h1.TextContent.Contains("Upgrade to a supported web browser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_with_valid_credentials_starts_a_session()
    {
        _ = Fixtures;
        var before = SessionCount();
        using var client = _app.Anonymous();
        var response = await client.PostFormAsync("/session", ("email_address", "david@37signals.com"), ("password", CampfireApp.Password));

        response.AssertRedirectTo("/");
        Assert.Equal(before + 1, SessionCount());
        Assert.NotNull(_app.SessionToken(client));
    }

    [Fact]
    public async Task Create_returns_to_the_page_that_asked_for_sign_in()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        (await client.GetAsync("/account/edit")).AssertRedirectTo("/session/new");

        var response = await client.PostFormAsync("/session", ("email_address", "david@37signals.com"), ("password", CampfireApp.Password));
        response.AssertRedirectTo("/account/edit");
    }

    [Fact]
    public async Task Create_with_invalid_credentials_is_unauthorized()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        var response = await client.PostFormAsync("/session", ("email_address", "david@37signals.com"), ("password", "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_app.SessionToken(client));
        var html = await response.HtmlAsync();
        Assert.Contains("shake", html.QuerySelector(".panel")!.ClassName);
        Assert.Contains("Too many requests or unauthorized.", html.QuerySelector(".flash [role=alert]")!.TextContent);
    }

    [Fact]
    public async Task Create_is_rate_limited_per_address()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostFormAsync("/session", ("email_address", "david@37signals.com"), ("password", "wrong"))).StatusCode);
        }

        var limited = await client.PostFormAsync("/session", ("email_address", "david@37signals.com"), ("password", CampfireApp.Password));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Too many requests or unauthorized.", await limited.Content.ReadAsStringAsync());
        Assert.Null(_app.SessionToken(client));
    }

    [Fact]
    public async Task Destroy_ends_the_session()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var before = SessionCount();

        var response = await client.SubmitAsync("delete", "/session");

        response.AssertRedirectTo("/");
        Assert.Equal(before - 1, SessionCount());
        Assert.Null(_app.SessionToken(client));
    }

    [Fact]
    public async Task Destroy_removes_the_push_subscription_for_the_device()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        _app.Sql(sql => PushSubscriptions.Create(sql, Fixtures.David.Id, "https://fcm.googleapis.com/fcm/send/abc", "p256dh", "auth", null));

        var response = await client.SubmitAsync("delete", "/session", ("push_subscription_endpoint", "https://fcm.googleapis.com/fcm/send/abc"));

        response.AssertRedirectTo("/");
        Assert.Empty(_app.Sql(sql => PushSubscriptions.ForUser(sql, Fixtures.David.Id)));
    }

    [Fact]
    public async Task Transfer_show_renders_when_not_signed_in()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        var response = await client.GetAsync("/session/transfers/some-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = (await response.HtmlAsync()).QuerySelector("form[data-controller=auto-submit]")!;
        Assert.Equal("/session/transfers/some-token", form.GetAttribute("action"));
        Assert.NotNull(form.QuerySelector("input[name=_method][value=put]"));
    }

    [Fact]
    public async Task Transfer_update_establishes_a_session_when_the_code_is_valid()
    {
        var transferId = TransferIds.For(_app.Service<KeyRing>(), Fixtures.David.Id);
        using var client = _app.Anonymous();

        var response = await client.SubmitAsync("put", $"/session/transfers/{transferId}");

        response.AssertRedirectTo("/");
        Assert.NotNull(_app.SessionToken(client));
    }

    [Fact]
    public async Task Transfer_update_rejects_invalid_and_expired_codes()
    {
        var expired = _app.Service<KeyRing>().SignedIds.Generate(Fixtures.David.Id, TransferIds.Purpose, TimeSpan.FromSeconds(-1));
        var avatarToken = _app.Service<KeyRing>().SignedIds.Generate(Fixtures.David.Id, "avatar");
        using var client = _app.Anonymous();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SubmitAsync("put", "/session/transfers/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SubmitAsync("put", $"/session/transfers/{expired}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SubmitAsync("put", $"/session/transfers/{avatarToken}")).StatusCode);
        Assert.Null(_app.SessionToken(client));
    }
}
