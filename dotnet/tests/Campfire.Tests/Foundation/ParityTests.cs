namespace Campfire.Tests.Foundation;

using Campfire.Tests.Support;
using Campfire.Web.Platform;

/// <summary>Behaviors found by diffing the port's responses against the Rails app's on the same data.</summary>
public sealed class ParityTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    [Fact]
    public async Task Frame_endpoints_render_bare_frames_for_turbo_and_full_pages_otherwise()
    {
        using var client = app.SignedInAs(app.Fixtures.David);

        var request = new HttpRequestMessage(HttpMethod.Get, "/users/me/sidebar");
        request.Headers.Add("Turbo-Frame", "user_sidebar");
        var frame = await (await client.Http.SendAsync(request)).Content.ReadAsStringAsync();
        Assert.StartsWith("<turbo-frame id=\"user_sidebar\"", frame.TrimStart());

        // turbo-rails only uses its frame layout when Turbo asks; a plain request gets the application layout
        var page = await (await client.GetAsync("/users/me/sidebar")).BodyAsync();
        Assert.StartsWith("<!DOCTYPE html>", page.TrimStart());
        Assert.Contains("<turbo-frame id=\"user_sidebar\"", page);
        Assert.Contains("name=\"csrf-token\"", page);
    }

    [Fact]
    public async Task Sidebar_counts_the_viewer_twice_against_the_placeholder_limit_as_rails_does()
    {
        using var client = app.SignedInAs(app.Fixtures.David);
        var page = await (await client.GetAsync("/users/me/sidebar")).BodyAsync();

        // David has direct rooms with Jason and Kevin: the ids [David, Jason, Kevin] plus David again
        // leave 20 - 4 = 16 slots, more than the remaining active people (JZ and Bender).
        Assert.Equal(2, CountOccurrences(page, "Start a ping with"));
    }

    [Theory]
    [InlineData("curl/8.5.0", "Curl")]
    [InlineData("Ruby", "Ruby")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome")]
    public void Unknown_agents_are_named_by_their_first_product_token(string userAgent, string name) =>
        Assert.Equal(name, ApplicationPlatform.Parse(userAgent).BrowserDisplayName);

    [Fact]
    public void Non_browser_agents_are_never_blocked_as_unsupported()
    {
        Assert.False(ApplicationPlatform.Parse("curl/8.5.0").IsUnsupportedBrowser);
        Assert.False(ApplicationPlatform.Parse("Ruby").IsUnsupportedBrowser);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
