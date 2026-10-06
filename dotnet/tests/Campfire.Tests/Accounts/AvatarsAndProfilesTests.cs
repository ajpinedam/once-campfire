using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Views;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Tests.AccountsAndPeople;

public sealed class AvatarsAndProfilesTests : IDisposable
{
    private readonly CampfireApp _app = new();
    private Fixtures Fixtures => _app.Fixtures;

    public void Dispose() => _app.Dispose();

    private async Task UploadAvatar(CampfireClient client, string fixture, string contentType)
    {
        var form = AccountsTestSupport.Multipart([("_method", "patch")], "user[avatar]", fixture, contentType);
        var response = await client.SendAsync(HttpMethod.Post, Paths.UserProfile, form);
        response.AssertRedirectTo(Paths.UserProfile);
    }

    [Fact]
    public async Task Avatar_shows_initials_without_an_image()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync(Paths.UserAvatar(Fixtures.Kevin.Id), accept: "image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Matches(@"<text[^>]*>\s*K\s*</text>", svg);
        Assert.Contains("public", response.Headers.CacheControl!.ToString());
        Assert.Equal(TimeSpan.FromMinutes(30), response.Headers.CacheControl!.MaxAge);
    }

    [Fact]
    public async Task Avatar_is_not_modified_while_the_user_is_unchanged()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var first = await client.GetAsync(Paths.UserAvatar(Fixtures.Kevin.Id), accept: "image/*");

        var request = new HttpRequestMessage(HttpMethod.Get, Paths.UserAvatar(Fixtures.Kevin.Id));
        request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var second = await client.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Bots_get_the_default_bot_avatar()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync(Paths.UserAvatar(Fixtures.Bender.Id), accept: "image/*");

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("<text", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Avatar_with_an_invalid_token_is_not_found()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/users/not-a-valid-token/avatar", accept: "image/*")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/users/{Fixtures.Kevin.Id}/avatar", accept: "image/*")).StatusCode);
    }

    [Fact]
    public async Task Avatar_shows_an_uploaded_image_as_webp()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        await UploadAvatar(client, "moon.jpg", "image/jpeg");

        var kevin = _app.Sql(sql => Q.Users.Find(sql, Fixtures.Kevin.Id))!;
        var response = await client.GetAsync(Paths.FreshUserAvatar(kevin), accept: "image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Avatar_falls_back_to_initials_when_the_image_cannot_be_resized()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        await UploadAvatar(client, "pixel.bmp", "image/bmp");

        var response = await client.GetAsync(Paths.UserAvatar(Fixtures.Kevin.Id), accept: "image/*");

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains(">", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Avatar_can_be_removed()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        await UploadAvatar(client, "moon.jpg", "image/jpeg");
        Assert.True(_app.Sql(sql => Q.Attachments.Exists(sql, "User", Fixtures.Kevin.Id, "avatar")));

        (await client.SubmitAsync("delete", $"/users/{Fixtures.Kevin.Id}/avatar")).AssertRedirectTo(Paths.UserProfile);

        Assert.False(_app.Sql(sql => Q.Attachments.Exists(sql, "User", Fixtures.Kevin.Id, "avatar")));
    }

    [Fact]
    public async Task Profile_shows_the_settings_and_rooms()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync(Paths.UserProfile);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.HtmlAsync();
        Assert.Equal("David", html.QuerySelector("input[name='user[name]']")!.GetAttribute("value"));
        Assert.NotNull(html.QuerySelector($"turbo-frame#involvement_room_{Fixtures.Designers.Id} form[action='/rooms/{Fixtures.Designers.Id}/involvement?involvement=everything']"));
        Assert.NotNull(html.QuerySelector("#session_transfer_url"));
        Assert.Contains(html.QuerySelectorAll(".membership-item strong"), item => item.TextContent == "Jason");
        Assert.NotNull(html.QuerySelector("form[action='/session'] input[name=push_subscription_endpoint]"));
    }

    [Fact]
    public async Task Profile_includes_install_instructions_for_edge()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetWithUserAgentAsync(Paths.UserProfile, AccountsTestSupport.EdgeUserAgent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await response.HtmlAsync()).QuerySelector(".pwa__instructions img[src*='install-edge']"));
    }

    [Fact]
    public async Task Profile_update_changes_name_and_bio_only()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.SubmitAsync("put", Paths.UserProfile, ("user[name]", "John Doe"), ("user[bio]", "Acrobat"));

        response.AssertRedirectTo(Paths.UserProfile);
        var david = _app.Sql(sql => Q.Users.Find(sql, Fixtures.David.Id))!;
        Assert.Equal("John Doe", david.Name);
        Assert.Equal("Acrobat", david.Bio);
        Assert.Equal("david@37signals.com", david.EmailAddress);
        Assert.NotNull(_app.Sql(sql => Q.Users.Authenticate(sql, "david@37signals.com", CampfireApp.Password)));
    }

    [Fact]
    public async Task Profile_updates_are_limited_to_the_current_user()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await client.SubmitAsync("put", $"/users/{Fixtures.Jason.Id}/profile", ("user[name]", "John Doe"));

        Assert.Equal("Jason", _app.Sql(sql => Q.Users.Find(sql, Fixtures.Jason.Id))!.Name);
        Assert.Equal("John Doe", _app.Sql(sql => Q.Users.Find(sql, Fixtures.David.Id))!.Name);
    }

    [Fact]
    public async Task Profile_update_shows_a_confirmation()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await client.SubmitAsync("patch", Paths.UserProfile, ("user[bio]", "Hello"));

        var html = await (await client.GetAsync(Paths.UserProfile)).HtmlAsync();
        Assert.Equal("✓", html.QuerySelector(".flash [role=alert]")!.TextContent);
    }
}
