using System.Net;
using System.Net.Http.Json;
using Campfire.Tests.Integrations.Support;
using Campfire.Tests.Support;
using Campfire.Web.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Tests.Integrations;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>Ports of test/models/push/subscription_test.rb and test/controllers/users/push_subscriptions_controller_test.rb.</summary>
public sealed class PushSubscriptionsTests : IClassFixture<CampfireApp>, IDisposable
{
    private const string PublicTestIp = "142.250.185.206";
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";

    private readonly CampfireApp _app;
    private readonly FakeHostResolver _dns = new() { Default = [IPAddress.Parse(PublicTestIp)] };
    private readonly RecordingPushTransport _transport = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public PushSubscriptionsTests(CampfireApp app)
    {
        _app = app;
        _ = app.Fixtures;
        _factory = IntegrationsApp.With(app, _dns, _transport);
    }

    public void Dispose() => _factory.Dispose();

    // Endpoint validation (Push::Subscription)

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/token123")]
    [InlineData("https://jmt17.google.com/fcm/send/token123")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/token123")]
    [InlineData("https://web.push.apple.com/QaBC123")]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc123")]
    public async Task Accepts_every_permitted_push_service(string endpoint) =>
        Assert.Empty(await Endpoints().ValidateAsync(endpoint));

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc123", "must use HTTPS")]
    [InlineData("https://attacker.example.com/webhook", "is not a permitted push service")]
    [InlineData("https://evilfcm.googleapis.com.attacker.example/webhook", "is not a permitted push service")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc123", "must use the default HTTPS port")]
    [InlineData("", "can't be blank")]
    public async Task Rejects_endpoints_that_break_the_rules(string endpoint, string error) =>
        Assert.Contains(error, await Endpoints().ValidateAsync(endpoint));

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task Rejects_endpoints_resolving_to_private_addresses(string address)
    {
        var endpoints = new PushEndpoints(new Campfire.Web.Net.PrivateNetworkGuard(new FakeHostResolver().Answer("fcm.googleapis.com", address)));
        Assert.Contains("resolves to a private or invalid IP address", await endpoints.ValidateAsync(Endpoint));
    }

    [Fact]
    public async Task Rejects_endpoints_that_resolve_to_nothing_without_raising()
    {
        var endpoints = new PushEndpoints(new Campfire.Web.Net.PrivateNetworkGuard(new FakeHostResolver()));
        Assert.Null(await endpoints.ResolvedEndpointIpAsync(Endpoint));
        Assert.Contains("resolves to a private or invalid IP address", await endpoints.ValidateAsync(Endpoint));
    }

    [Theory]
    [InlineData("https://attacker.example.com/collect")]
    [InlineData("https://fcm.googleapis.com:22/fcm/send/abc123")]
    public async Task Delivery_is_skipped_for_endpoints_that_no_longer_pass(string endpoint)
    {
        using var browser = new BrowserPushKeys();
        var sender = _factory.Services.GetRequiredService<WebPushSender>();

        var outcome = await sender.DeliverAsync(new PushDelivery(1, endpoint, browser.P256dh, browser.Auth, Message()), CancellationToken.None);

        Assert.Equal(WebPushOutcome.Skipped, outcome);
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task Delivery_resolves_on_the_worker_and_pins_the_connection()
    {
        using var browser = new BrowserPushKeys();
        var sender = _factory.Services.GetRequiredService<WebPushSender>();
        var lookups = _dns.Lookups;

        var outcome = await sender.DeliverAsync(new PushDelivery(1, Endpoint, browser.P256dh, browser.Auth, Message()), CancellationToken.None);

        Assert.Equal(WebPushOutcome.Delivered, outcome);
        Assert.True(_dns.Lookups > lookups);
        var (request, address) = Assert.Single(_transport.Sent);
        Assert.Equal(IPAddress.Parse(PublicTestIp), address);
        Assert.Equal("high", request.Urgency);
        Assert.StartsWith("vapid t=", request.Authorization, StringComparison.Ordinal);
        Assert.Equal("""{"title":"t","options":{"body":"b","icon":"/account/logo","data":{"path":"/","badge":0}}}""",
            System.Text.Encoding.UTF8.GetString(browser.Decrypt(request.Body)));
    }

    [Fact]
    public async Task Delivery_is_skipped_when_web_push_is_not_configured()
    {
        using var browser = new BrowserPushKeys();
        var settings = _app.Service<Campfire.Web.Configuration.CampfireSettings>() with { VapidPrivateKey = null };
        using var sender = new WebPushSender(settings, Endpoints(), _transport, NullLogger<WebPushSender>.Instance);

        Assert.Equal(WebPushOutcome.Skipped, await sender.DeliverAsync(new PushDelivery(1, Endpoint, browser.P256dh, browser.Auth, Message()), CancellationToken.None));
        Assert.Empty(_transport.Sent);
    }

    // Controller

    [Fact]
    public async Task Creates_a_subscription_from_the_notifications_controller_json()
    {
        var david = _app.Fixtures.David;
        using var client = IntegrationsApp.SignedIn(_factory, david);

        var response = await client.PostAsJsonAsync("/users/me/push_subscriptions",
            new { push_subscription = new { endpoint = "https://fcm.googleapis.com/fcm/send/json1", p256dh_key = "123", auth_key = "456" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = _app.Sql(sql => PushSubscriptions.ForUser(sql, david.Id)).Last();
        Assert.Equal(("https://fcm.googleapis.com/fcm/send/json1", "123", "456"), (created.Endpoint, created.P256dhKey, created.AuthKey));
        Assert.Equal("Mozilla/5.0", created.UserAgent);
    }

    [Fact]
    public async Task Creates_a_subscription_from_form_params()
    {
        var jz = _app.Fixtures.Jz;
        using var client = IntegrationsApp.SignedIn(_factory, jz);

        var response = await client.PostAsync("/users/me/push_subscriptions", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["push_subscription[endpoint]"] = "https://fcm.googleapis.com/fcm/send/form1",
            ["push_subscription[p256dh_key]"] = "123",
            ["push_subscription[auth_key]"] = "456"
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_app.Sql(sql => PushSubscriptions.ForUser(sql, jz.Id)), subscription => subscription.Endpoint == "https://fcm.googleapis.com/fcm/send/form1");
    }

    [Fact]
    public async Task Touches_an_existing_subscription()
    {
        var jason = _app.Fixtures.Jason;
        var existing = _app.Sql(sql => PushSubscriptions.Create(sql, jason.Id, "https://fcm.googleapis.com/fcm/send/567", "456-key", "xxx-auth", "Chrome"));
        using var client = IntegrationsApp.SignedIn(_factory, jason);
        var count = _app.Sql(sql => PushSubscriptions.ForUser(sql, jason.Id)).Count;
        await Task.Delay(5);

        var response = await client.PostAsync(
            "/users/me/push_subscriptions?push_subscription%5Bendpoint%5D=https%3A%2F%2Ffcm.googleapis.com%2Ffcm%2Fsend%2F567&push_subscription%5Bp256dh_key%5D=456-key&push_subscription%5Bauth_key%5D=xxx-auth", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var subscriptions = _app.Sql(sql => PushSubscriptions.ForUser(sql, jason.Id));
        Assert.Equal(count, subscriptions.Count);
        Assert.True(subscriptions.Single(subscription => subscription.Id == existing.Id).UpdatedAt > existing.UpdatedAt);
    }

    [Fact]
    public async Task Rejects_a_non_permitted_endpoint()
    {
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.Kevin);
        var before = CountAll();

        var response = await client.PostAsJsonAsync("/users/me/push_subscriptions",
            new { push_subscription = new { endpoint = "https://attacker.example.com/steal", p256dh_key = "123", auth_key = "456" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, CountAll());
    }

    [Fact]
    public async Task Rejects_an_endpoint_resolving_to_a_private_address()
    {
        _dns.Answer("fcm.googleapis.com", "169.254.169.254");
        try
        {
            using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.Kevin);
            var before = CountAll();

            var response = await client.PostAsJsonAsync("/users/me/push_subscriptions",
                new { push_subscription = new { endpoint = "https://fcm.googleapis.com/fcm/send/private", p256dh_key = "123", auth_key = "456" } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(before, CountAll());
        }
        finally
        {
            _dns.Answer("fcm.googleapis.com", PublicTestIp);
        }
    }

    [Fact]
    public async Task Re_registering_a_legacy_invalid_subscription_is_rejected()
    {
        var kevin = _app.Fixtures.Kevin;
        _app.Sql(sql => PushSubscriptions.Create(sql, kevin.Id, "https://attacker.example.com/legacy", "123", "456", null));
        using var client = IntegrationsApp.SignedIn(_factory, kevin);
        var before = CountAll();

        var response = await client.PostAsJsonAsync("/users/me/push_subscriptions",
            new { push_subscription = new { endpoint = "https://attacker.example.com/legacy", p256dh_key = "123", auth_key = "456" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, CountAll());
    }

    [Fact]
    public async Task Requires_the_push_subscription_param()
    {
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.Kevin);
        var response = await client.PostAsJsonAsync("/users/me/push_subscriptions", new { something = "else" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Destroys_a_subscription()
    {
        var david = _app.Fixtures.David;
        var subscription = _app.Sql(sql => PushSubscriptions.Create(sql, david.Id, "https://fcm.googleapis.com/fcm/send/doomed", "1", "2", null));
        using var client = IntegrationsApp.SignedIn(_factory, david);

        var response = await client.PostAsync($"/users/me/push_subscriptions/{subscription.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["_method"] = "delete" }));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/users/me/push_subscriptions", response.Headers.Location?.OriginalString);
        Assert.Null(_app.Sql(sql => PushSubscriptions.Find(sql, david.Id, subscription.Id)));
    }

    [Fact]
    public async Task Lists_subscriptions_and_sends_test_notifications()
    {
        using var browser = new BrowserPushKeys();
        var jz = _app.Fixtures.Jz;
        var subscription = _app.Sql(sql => PushSubscriptions.Create(sql, jz.Id, "https://fcm.googleapis.com/fcm/send/testing", browser.P256dh, browser.Auth,
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/113.0.0.0 Safari/537.36"));
        using var client = IntegrationsApp.SignedIn(_factory, jz);

        var page = await client.GetAsync("/users/me/push_subscriptions");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Push Notification Subscriptions", html, StringComparison.Ordinal);
        Assert.Contains("Chrome 113.0 on Macintosh", html, StringComparison.Ordinal);
        Assert.Contains($"/users/me/push_subscriptions/{subscription.Id}/test_notifications", html, StringComparison.Ordinal);

        var sent = _transport.Sent.Count;
        var response = await client.PostAsync($"/users/me/push_subscriptions/{subscription.Id}/test_notifications", null);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(sent + 1, _transport.Sent.Count);

        var test = _transport.Sent.Last().Request;
        Assert.Contains("\"title\":\"Campfire Test\"", System.Text.Encoding.UTF8.GetString(browser.Decrypt(test.Body)), StringComparison.Ordinal);
    }

    private PushEndpoints Endpoints() => _factory.Services.GetRequiredService<PushEndpoints>();

    private long CountAll() => _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM push_subscriptions"));

    private static WebPushMessage Message() => new("t", "b", "/", 0, "/account/logo");
}
