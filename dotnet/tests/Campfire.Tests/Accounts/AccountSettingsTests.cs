using System.Net;
using System.Text.Json;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Views;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Tests.AccountsAndPeople;

public sealed class AccountSettingsTests : IDisposable
{
    private readonly CampfireApp _app = new();
    private Fixtures Fixtures => _app.Fixtures;

    public void Dispose() => _app.Dispose();

    private Account Account() => _app.Sql(sql => Q.Accounts.First(sql))!;

    [Fact]
    public async Task Edit_lists_administrators_before_members_with_a_divider()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync(Paths.EditAccount);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var html = await response.HtmlAsync();
        Assert.NotNull(html.QuerySelector("turbo-frame#account_users hr.separator.full-width"));

        var divider = body.IndexOf("<hr class=\"separator full-width\" style=\"--border-style: solid\">", body.IndexOf("account_users", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(divider > 0);
        foreach (var name in new[] { "David", "Jason" })
        {
            Assert.InRange(body.IndexOf($"<strong>{name}</strong>", StringComparison.Ordinal), 1, divider);
        }
        foreach (var name in new[] { "JZ", "Kevin" })
        {
            Assert.True(body.IndexOf($"<strong>{name}</strong>", StringComparison.Ordinal) > divider);
        }

        Assert.Null(html.QuerySelector("#next_page_container"));
        Assert.Contains(Fixtures.Account.JoinCode, html.QuerySelector("#invite_url")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Edit_is_read_only_for_members()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        var html = await (await client.GetAsync(Paths.EditAccount)).HtmlAsync();

        Assert.Equal("37signals", html.QuerySelector("h1")!.TextContent.Trim());
        Assert.Null(html.QuerySelector("input[name='account[name]']"));
        Assert.Null(html.QuerySelector("form[action^='/account/users/'][method=post] input[name=_method][value=delete]"));
    }

    [Fact]
    public async Task Update_renames_the_account()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.SubmitAsync("put", Paths.Account, ("account[name]", "Different"));

        response.AssertRedirectTo(Paths.EditAccount);
        Assert.Equal("Different", Account().Name);
    }

    [Fact]
    public async Task Update_toggles_restricting_room_creation()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await client.SubmitAsync("put", Paths.Account, ("account[settings][restrict_room_creation_to_administrators]", "true"));
        Assert.True(Account().Settings.RestrictRoomCreationToAdministrators);

        await client.SubmitAsync("put", Paths.Account, ("account[settings][restrict_room_creation_to_administrators]", "false"));
        Assert.False(Account().Settings.RestrictRoomCreationToAdministrators);
    }

    [Fact]
    public async Task Members_cannot_update_the_account()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("put", Paths.Account, ("account[name]", "Different"))).StatusCode);
        Assert.Equal("37signals", Account().Name);
    }

    [Fact]
    public void Settings_accept_every_stored_spelling()
    {
        Assert.True(Q.AccountSettingsJson.Parse("""{"restrict_room_creation_to_administrators":true}""").RestrictRoomCreationToAdministrators);
        Assert.True(Q.AccountSettingsJson.Parse("""{"restrict_room_creation_to_administrators":"true"}""").RestrictRoomCreationToAdministrators);
        Assert.False(Q.AccountSettingsJson.Parse("""{"restrict_room_creation_to_administrators":"false"}""").RestrictRoomCreationToAdministrators);
        Assert.False(Q.AccountSettingsJson.Parse(null).RestrictRoomCreationToAdministrators);
    }

    // People

    [Fact]
    public async Task People_can_be_promoted_and_demoted()
    {
        using var client = _app.SignedInAs(Fixtures.David);

        var promote = await client.SubmitAsync("patch", Paths.AccountUser(Fixtures.Kevin.Id), ("user[role]", "member"), ("user[role]", "administrator"));
        promote.AssertRedirectTo(Paths.EditAccount);
        Assert.True(_app.Sql(sql => Q.Users.Find(sql, Fixtures.Kevin.Id))!.IsAdministrator);

        await client.SubmitAsync("patch", Paths.AccountUser(Fixtures.Kevin.Id), ("user[role]", "member"));
        Assert.True(_app.Sql(sql => Q.Users.Find(sql, Fixtures.Kevin.Id))!.IsMember);
    }

    [Fact]
    public async Task People_can_be_removed()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.SubmitAsync("delete", Paths.AccountUser(Fixtures.Kevin.Id));

        response.AssertRedirectTo(Paths.EditAccount);
        Assert.Null(_app.Sql(sql => Q.Users.FindActive(sql, Fixtures.Kevin.Id)));
    }

    [Fact]
    public async Task Members_cannot_manage_people()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("put", Paths.AccountUser(Fixtures.David.Id), ("user[role]", "administrator"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("delete", Paths.AccountUser(Fixtures.David.Id))).StatusCode);
    }

    [Fact]
    public async Task People_pages_are_turbo_streams()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        var response = await client.GetAsync($"{Paths.AccountUsers}.turbo_stream?page=1");

        Assert.Equal("text/vnd.turbo-stream.html", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<turbo-stream action=\"replace\" target=\"next_page_container\">", body, StringComparison.Ordinal);
        Assert.Contains("<strong>Kevin</strong>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bender", body, StringComparison.Ordinal);
    }

    // Join codes

    [Fact]
    public async Task Administrators_can_reset_the_join_code()
    {
        var before = Fixtures.Account.JoinCode;
        using var client = _app.SignedInAs(Fixtures.David);

        (await client.PostFormAsync(Paths.AccountJoinCode)).AssertRedirectTo(Paths.EditAccount);

        Assert.NotEqual(before, Account().JoinCode);
        Assert.Matches(@"^\w{4}-\w{4}-\w{4}$", Account().JoinCode);
    }

    [Fact]
    public async Task Members_cannot_reset_the_join_code()
    {
        using var client = _app.SignedInAs(Fixtures.Jz);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostFormAsync(Paths.AccountJoinCode)).StatusCode);
    }

    // Custom styles

    [Fact]
    public async Task Custom_styles_can_be_edited_by_administrators()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Paths.EditAccountCustomStyles)).StatusCode);

        var response = await client.SubmitAsync("put", Paths.AccountCustomStyles, ("account[custom_styles]", ":root { --color-text: red; }"));

        response.AssertRedirectTo(Paths.EditAccountCustomStyles);
        Assert.Equal(":root { --color-text: red; }", Account().CustomStyles);

        var page = await (await client.GetAsync(Paths.EditAccountCustomStyles)).Content.ReadAsStringAsync();
        Assert.Contains("<style data-turbo-track=\"reload\">:root { --color-text: red; }</style>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_cannot_change_custom_styles()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("put", Paths.AccountCustomStyles, ("account[custom_styles]", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Paths.EditAccountCustomStyles)).StatusCode);
    }

    // Logo

    [Theory]
    [InlineData(null, 512)]
    [InlineData("small", 192)]
    public async Task Logo_is_the_stock_icon_without_a_custom_logo(string? size, int pixels)
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync(size is null ? Paths.AccountLogo : $"{Paths.AccountLogo}?size={size}", accept: "image/*");

        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal((pixels, pixels), AccountsTestSupport.PngSize(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl!.MaxAge);
    }

    [Fact]
    public async Task Logo_is_served_to_signed_out_visitors()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Paths.AccountLogo, accept: "image/*")).StatusCode);
    }

    private async Task UploadLogo(CampfireClient client, string fixture, string contentType)
    {
        var form = AccountsTestSupport.Multipart([("_method", "patch")], "account[logo]", fixture, contentType);
        (await client.SendAsync(HttpMethod.Post, Paths.Account, form)).AssertRedirectTo(Paths.EditAccount);
    }

    [Theory]
    [InlineData(null, 512)]
    [InlineData("small", 192)]
    public async Task Logo_is_a_resized_custom_logo(string? size, int pixels)
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await UploadLogo(client, "moon.jpg", "image/jpeg");
        Assert.True(Account().HasLogo);

        var response = await client.GetAsync(Paths.FreshAccountLogo(Account(), size), accept: "image/*");

        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal((pixels, pixels), AccountsTestSupport.PngSize(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Logo_falls_back_to_stock_when_it_cannot_be_resized()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await UploadLogo(client, "pixel.bmp", "image/bmp");

        var response = await client.GetAsync(Paths.FreshAccountLogo(Account()), accept: "image/*");

        Assert.Equal((512, 512), AccountsTestSupport.PngSize(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Logo_can_be_removed()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await UploadLogo(client, "moon.jpg", "image/jpeg");

        (await client.SubmitAsync("delete", Paths.AccountLogo)).AssertRedirectTo(Paths.EditAccount);

        Assert.False(Account().HasLogo);
    }

    // Bots

    [Fact]
    public async Task Bots_index_lists_bots_with_their_room_commands()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync(Paths.AccountBots);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.HtmlAsync();
        Assert.Contains("Bender Bot", html.QuerySelector("menu li strong")!.TextContent);
        var curl = html.QuerySelector("input[aria-label='curl command for posting messages']")!.GetAttribute("value")!;
        Assert.EndsWith($"/rooms/{Fixtures.Watercooler.Id}/{Fixtures.Bender.BotKey}/messages", curl);
        Assert.StartsWith("curl -d 'Hello!' http", curl);
    }

    [Fact]
    public async Task Bots_can_be_created()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Paths.NewAccountBot)).StatusCode);

        var response = await client.PostFormAsync(Paths.AccountBots, ("user[name]", "Bender's Friend"), ("user[webhook_url]", "https://example.com/hook"));

        response.AssertRedirectTo(Paths.AccountBots);
        var bot = _app.Sql(sql => Q.Users.ActiveBotsOrdered(sql)).Single(user => user.Name == "Bender's Friend");
        Assert.Equal("https://example.com/hook", _app.Sql(sql => Q.Webhooks.ForUser(sql, bot.Id))?.Url);
    }

    [Fact]
    public async Task Bots_can_be_updated()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Paths.EditAccountBot(Fixtures.Bender.Id))).StatusCode);

        var response = await client.SubmitAsync("put", Paths.AccountBot(Fixtures.Bender.Id), ("user[name]", "Bender's New Friend"), ("user[webhook_url]", "https://example.com/new"));

        response.AssertRedirectTo(Paths.AccountBots);
        Assert.Equal("Bender's New Friend", _app.Sql(sql => Q.Users.Find(sql, Fixtures.Bender.Id))!.Name);
        Assert.Equal("https://example.com/new", _app.Sql(sql => Q.Webhooks.ForUser(sql, Fixtures.Bender.Id))?.Url);
    }

    [Fact]
    public async Task Updating_a_bot_without_a_webhook_url_removes_the_webhook()
    {
        _app.Sql(sql => Q.Webhooks.Set(sql, Fixtures.Bender.Id, "https://example.com/hook"));
        using var client = _app.SignedInAs(Fixtures.David);

        await client.SubmitAsync("put", Paths.AccountBot(Fixtures.Bender.Id), ("user[name]", "Bender's New Friend"), ("user[webook_url]", ""));

        Assert.Null(_app.Sql(sql => Q.Webhooks.ForUser(sql, Fixtures.Bender.Id)));
    }

    [Fact]
    public async Task Bots_can_be_removed()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        await client.SubmitAsync("delete", Paths.AccountBot(Fixtures.Bender.Id));

        Assert.True(_app.Sql(sql => Q.Users.Find(sql, Fixtures.Bender.Id))!.IsDeactivated);
        Assert.Empty(_app.Sql(sql => Q.Users.ActiveBotsOrdered(sql)));
    }

    [Fact]
    public async Task Bot_keys_can_be_reset()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        (await client.SubmitAsync("put", Paths.AccountBotKey(Fixtures.Bender.Id))).AssertRedirectTo(Paths.AccountBots);

        Assert.NotEqual(Fixtures.Bender.BotToken, _app.Sql(sql => Q.Users.Find(sql, Fixtures.Bender.Id))!.BotToken);
    }

    [Fact]
    public async Task Members_cannot_manage_bots()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Paths.AccountBots)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("delete", Paths.AccountBot(Fixtures.Bender.Id))).StatusCode);
    }
}

public sealed class PublicEndpointsTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    [Fact]
    public async Task Qr_codes_are_cacheable_svg_images()
    {
        using var client = app.Anonymous();
        var response = await client.GetAsync(QrCodes.Path("http://example.com"), accept: "image/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl!.MaxAge);
        Assert.True(response.Headers.CacheControl.Public);
        Assert.Contains("viewBox", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qr_code_ids_are_rails_urlsafe_base64()
    {
        Assert.Equal("aHR0cDovL2V4YW1wbGUuY29t", QrCodes.Id("http://example.com"));
        Assert.Equal("http://example.com/?a=1&b=>", QrCodes.UrlFromId(QrCodes.Id("http://example.com/?a=1&b=>")));

        using var client = app.Anonymous();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/qr_code/!!!", accept: "image/*")).StatusCode);
    }

    [Fact]
    public async Task Manifest_names_the_account_and_its_icons()
    {
        _ = app.Fixtures;
        using var client = app.Anonymous();
        var response = await client.GetAsync("/webmanifest.json", accept: "application/json");

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("37signals", manifest.RootElement.GetProperty("name").GetString());
        Assert.StartsWith("/account/logo?v=", manifest.RootElement.GetProperty("icons")[0].GetProperty("src").GetString());
        Assert.EndsWith("&size=small", manifest.RootElement.GetProperty("icons")[0].GetProperty("src").GetString());
        Assert.StartsWith("http", manifest.RootElement.GetProperty("shortcuts")[0].GetProperty("icons")[0].GetProperty("src").GetString());
    }

    [Fact]
    public async Task Service_worker_is_served_from_the_root()
    {
        using var client = app.Anonymous();
        var response = await client.GetAsync("/service-worker", accept: "*/*");

        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("self.addEventListener(\"push\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_check_is_up()
    {
        using var client = app.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/up")).StatusCode);
    }
}
