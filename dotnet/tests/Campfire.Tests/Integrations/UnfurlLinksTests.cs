using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Campfire.Tests.Integrations.Support;
using Campfire.Tests.Support;
using Campfire.Web.Net;
using Campfire.Web.OpenGraph;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Tests.Integrations;

/// <summary>Port of test/controllers/unfurl_links_controller_test.rb.</summary>
public sealed class UnfurlLinksTests : IClassFixture<CampfireApp>, IAsyncLifetime
{
    private readonly CampfireApp _app;
    private readonly FakeHostResolver _dns = new() { Default = [IPAddress.Parse("93.184.216.34")] };
    private StubWeb _web = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public UnfurlLinksTests(CampfireApp app)
    {
        _app = app;
        _ = app.Fixtures;
    }

    public async Task InitializeAsync()
    {
        _web = await StubWeb.StartAsync();
        var dialer = _web.Dialer(new ConcurrentQueue<IPEndPoint>());
        _factory = IntegrationsApp.With(_app, _dns, services: services =>
            services.AddSingleton(provider => new OpenGraphFetch(
                provider.GetRequiredService<PrivateNetworkGuard>(),
                new PinnedHttpClient(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), int.MaxValue, dialer))));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _web.DisposeAsync();
    }

    [Fact]
    public async Task Unfurls_a_link_from_the_composer_json()
    {
        StubSuccessfulRequest();
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.David);

        var response = await client.PostAsJsonAsync("/unfurl_link", new { url = "http://www.example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hey!", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("https://example.com", json.RootElement.GetProperty("url").GetString());
        Assert.Equal("http://example.com/image.png", json.RootElement.GetProperty("image").GetString());
        Assert.Equal("desc..", json.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Unfurls_a_link_from_form_params()
    {
        StubSuccessfulRequest();
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.David);

        var response = await client.PostAsync("/unfurl_link", new FormUrlEncodedContent(new Dictionary<string, string> { ["url"] = "http://www.example.com" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Strips_markup_from_the_title_and_description()
    {
        const string imageTag = "&#x3c;&#x69;&#x6d;&#x67;&#x20;&#x73;&#x72;&#x63;&#x3d;&#x61;&#x20;&#x6f;&#x6e;&#x65;&#x72;&#x72;&#x6f;&#x72;&#x3d;&#x70;&#x72;&#x6f;&#x6d;&#x70;&#x74;&#x28;&#x31;&#x29;&#x3e;";
        StubSuccessfulRequest(title: imageTag + "Hey!", description: imageTag + "desc..");
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.David);

        using var json = JsonDocument.Parse(await (await client.PostAsJsonAsync("/unfurl_link", new { url = "http://www.example.com" })).Content.ReadAsStringAsync());
        Assert.Equal("Hey!", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("desc..", json.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Answers_no_content_without_opengraph_tags()
    {
        _web.Stub("GET", "http://empty.example.com/", StubResponse.Html("<html><head></head></html>"));
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.David);

        var response = await client.PostAsJsonAsync("/unfurl_link", new { url = "http://empty.example.com" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Requires_a_url()
    {
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.David);
        var response = await client.PostAsJsonAsync("/unfurl_link", new { url = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unfurls_tweets_through_fxtwitter()
    {
        StubSuccessfulRequest(url: "http://fxtwitter.com/dhh/status/834146806594433025");
        using var client = IntegrationsApp.SignedIn(_factory, _app.Fixtures.David);

        var response = await client.PostAsJsonAsync("/unfurl_link", new { url = "http://x.com/dhh/status/834146806594433025" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        using var client = _factory.CreateDefaultClient();
        var response = await client.PostAsJsonAsync("/unfurl_link", new { url = "http://www.example.com" });
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private void StubSuccessfulRequest(string url = "http://www.example.com/", string title = "Hey!", string description = "desc..")
    {
        _web.Stub("GET", url, StubResponse.Html(
            $"<html><head><meta property=\"og:url\" content=\"https://example.com\"><meta property=\"og:title\" content=\"{title}\"><meta property=\"og:description\" content=\"{description}\"><meta property=\"og:image\" content=\"http://example.com/image.png\"></head></html>"));
        _web.Stub("HEAD", "http://example.com/image.png", new StubResponse(200, "image/png"));
    }
}
