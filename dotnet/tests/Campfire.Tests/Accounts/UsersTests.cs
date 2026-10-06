using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Features.People;
using Microsoft.Extensions.DependencyInjection;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Tests.AccountsAndPeople;

public sealed class UsersTests : IDisposable
{
    private readonly CampfireApp _app = new();
    private Fixtures Fixtures => _app.Fixtures;

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Show_renders_someone_s_profile()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.GetAsync($"/users/{Fixtures.Kevin.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.HtmlAsync();
        Assert.Equal("Kevin", html.QuerySelector("h1")!.TextContent.Trim());
        Assert.NotNull(html.QuerySelector($"form[action='/users/{Fixtures.Kevin.Id}/ban'] button[data-turbo-confirm]"));
        Assert.NotNull(html.QuerySelector("#session_transfer_url"));
    }

    [Fact]
    public async Task Show_hides_administration_from_members()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);
        var html = await (await client.GetAsync($"/users/{Fixtures.Jz.Id}")).HtmlAsync();

        Assert.Null(html.QuerySelector($"form[action='/users/{Fixtures.Jz.Id}/ban']"));
        Assert.Null(html.QuerySelector("#session_transfer_url"));
        Assert.Null(html.QuerySelector("a[href^='mailto:']"));
    }

    [Fact]
    public async Task Join_new_renders_with_a_valid_code()
    {
        using var client = _app.Anonymous();
        var response = await client.GetAsync($"/join/{Fixtures.Account.JoinCode}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await response.HtmlAsync()).QuerySelector($"form[action='/join/{Fixtures.Account.JoinCode}'] input[name='user[name]']"));
    }

    [Fact]
    public async Task Join_new_does_not_allow_a_signed_in_user()
    {
        using var client = _app.SignedInAs(Fixtures.David);
        (await client.GetAsync($"/join/{Fixtures.Account.JoinCode}")).AssertRedirectTo("/");
    }

    [Fact]
    public async Task Join_new_requires_the_join_code()
    {
        _ = Fixtures;
        using var client = _app.Anonymous();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/join/not", accept: "application/json")).StatusCode);
    }

    [Fact]
    public async Task Join_create_signs_up_into_the_open_rooms()
    {
        using var client = _app.Anonymous();
        var response = await client.PostFormAsync($"/join/{Fixtures.Account.JoinCode}",
            ("user[name]", "New Person"), ("user[email_address]", "new@37signals.com"), ("user[password]", "secret123456"));

        response.AssertRedirectTo("/");
        var user = _app.Sql(sql => Q.Users.Authenticate(sql, "new@37signals.com", "secret123456"))!;
        Assert.Equal(user.Id, _app.Sql(sql => Q.Sessions.FindWithUser(sql, _app.SessionToken(client)!))!.Value.User.Id);
        Assert.True(user.IsMember);

        var rooms = _app.Sql(sql => Q.Memberships.ForUserWithRooms(sql, user.Id, visibleOnly: false)).Select(entry => entry.Room.Id).Order();
        Assert.Equal(new[] { Fixtures.Pets.Id, Fixtures.Hq.Id }.Order(), rooms);
    }

    [Fact]
    public async Task Join_create_with_an_existing_email_sends_them_to_sign_in()
    {
        _ = Fixtures;
        var before = _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM users"));
        using var client = _app.Anonymous();

        var response = await client.PostFormAsync($"/join/{Fixtures.Account.JoinCode}",
            ("user[name]", "Another David"), ("user[email_address]", "david@37signals.com"), ("user[password]", "secret123456"));

        response.AssertRedirectTo("/session/new?email_address=david%4037signals.com");
        Assert.Equal(before, _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM users")));
    }

    // Bans

    private void AddSession(User user, string ip) => _app.Sql(sql => Q.Sessions.Start(sql, user.Id, "Test", ip));

    [Fact]
    public async Task Ban_bans_the_user_from_their_session_addresses()
    {
        AddSession(Fixtures.Kevin, "203.0.113.1");
        AddSession(Fixtures.Kevin, "203.0.113.2");
        AddSession(Fixtures.Kevin, "127.0.0.1");
        AddSession(Fixtures.Kevin, "192.168.1.20");
        using var client = _app.SignedInAs(Fixtures.David);

        var response = await client.PostFormAsync($"/users/{Fixtures.Kevin.Id}/ban");

        response.AssertRedirectTo($"/users/{Fixtures.Kevin.Id}");
        var bans = _app.Sql(sql => sql.Query("SELECT ip_address FROM bans WHERE user_id = @user ORDER BY ip_address", r => r.GetString(0), ("@user", Fixtures.Kevin.Id)));
        Assert.Equal(["203.0.113.1", "203.0.113.2"], bans.ToArray());
        Assert.True(_app.Sql(sql => Q.Users.Find(sql, Fixtures.Kevin.Id))!.IsBanned);
        Assert.Equal(0, _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM sessions WHERE user_id = @user", ("@user", Fixtures.Kevin.Id))));
    }

    [Fact]
    public async Task Banned_addresses_cannot_make_changes()
    {
        AddSession(Fixtures.Kevin, "203.0.113.9");
        using (var client = _app.SignedInAs(Fixtures.David))
        {
            await client.PostFormAsync($"/users/{Fixtures.Kevin.Id}/ban");
        }

        Assert.True(_app.Sql(sql => Q.Bans.IsBanned(sql, "203.0.113.9")));
    }

    [Fact]
    public async Task Removing_banned_content_deletes_their_messages()
    {
        _app.Sql(sql => Q.Messages.Create(sql, Fixtures.Hq.Id, Fixtures.Kevin.Id, "test-123", "Test message", "Test message"));

        await using var scope = _app.Services.CreateAsyncScope();
        await new RemoveBannedContentJob(Fixtures.Kevin.Id).ExecuteAsync(scope.ServiceProvider, CancellationToken.None);

        Assert.Empty(_app.Sql(sql => Q.Messages.ByCreator(sql, Fixtures.Kevin.Id)));
    }

    [Fact]
    public async Task Members_cannot_ban_or_unban()
    {
        using var client = _app.SignedInAs(Fixtures.Kevin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostFormAsync($"/users/{Fixtures.Jz.Id}/ban")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("delete", $"/users/{Fixtures.Jz.Id}/ban")).StatusCode);
        Assert.True(_app.Sql(sql => Q.Users.Find(sql, Fixtures.Jz.Id))!.IsActive);
    }

    [Fact]
    public async Task Unban_lifts_the_ban()
    {
        AddSession(Fixtures.Kevin, "203.0.113.1");
        _app.Sql(sql => _app.Service<UserModeration>().Ban(sql, Fixtures.Kevin));
        Assert.Equal(1, _app.Sql(sql => Q.Bans.CountForUser(sql, Fixtures.Kevin.Id)));

        using var client = _app.SignedInAs(Fixtures.David);
        var response = await client.SubmitAsync("delete", $"/users/{Fixtures.Kevin.Id}/ban");

        response.AssertRedirectTo($"/users/{Fixtures.Kevin.Id}");
        Assert.Equal(0, _app.Sql(sql => Q.Bans.CountForUser(sql, Fixtures.Kevin.Id)));
        Assert.True(_app.Sql(sql => Q.Users.Find(sql, Fixtures.Kevin.Id))!.IsActive);
    }

    [Theory]
    [InlineData("203.0.113.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.0.4", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("not an address", false)]
    public void Only_public_addresses_can_be_banned(string address, bool banable) =>
        Assert.Equal(banable, BanAddresses.IsPublic(address));

    // Model behavior (test/models/user_test.rb, user/bot_test.rb, user/role_test.rb)

    [Fact]
    public void Creating_users_grants_membership_to_the_open_rooms()
    {
        _ = Fixtures;
        var before = _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM memberships"));
        _app.Sql(sql => Q.Users.Create(sql, "User", "user@example.com", "secret123456"));
        Assert.Equal(before + 2, _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM memberships")));
    }

    [Fact]
    public void Creating_subsequent_users_makes_them_members()
    {
        _ = Fixtures;
        Assert.True(_app.Sql(sql => Q.Users.Create(sql, "User", "user@example.com", "secret123456")).IsMember);
    }

    [Fact]
    public void Very_long_passwords_are_accepted()
    {
        var password = string.Concat(Enumerable.Repeat("secret", 50));
        _app.Sql(sql => Q.Users.UpdateProfile(sql, Fixtures.David.Id, password: password));
        Assert.NotNull(_app.Sql(sql => Q.Users.Authenticate(sql, "david@37signals.com", password)));
    }

    [Fact]
    public void Deactivating_removes_shared_memberships_subscriptions_searches_and_sessions_and_frees_the_email()
    {
        var david = Fixtures.David;
        _app.Sql(sql =>
        {
            Q.PushSubscriptions.Create(sql, david.Id, "https://fcm.googleapis.com/fcm/send/x", "p", "a", null);
            Q.Searches.Record(sql, david.Id, "hello");
            Q.Sessions.Start(sql, david.Id, "Test", "203.0.113.1");
        });

        using (var sql = _app.Database.Open())
        {
            _app.Service<UserModeration>().Deactivate(sql, david);
        }

        var memberships = _app.Sql(sql => Q.Memberships.ForUserWithRooms(sql, david.Id, visibleOnly: false));
        Assert.All(memberships, entry => Assert.True(entry.Room.IsDirect));
        Assert.Equal(2, memberships.Count);
        Assert.Empty(_app.Sql(sql => Q.PushSubscriptions.ForUser(sql, david.Id)));
        Assert.Empty(_app.Sql(sql => Q.Searches.RecentForUser(sql, david.Id)));
        Assert.Equal(0, _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM sessions WHERE user_id = @user", ("@user", david.Id))));

        var deactivated = _app.Sql(sql => Q.Users.Find(sql, david.Id))!;
        Assert.True(deactivated.IsDeactivated);
        Assert.Matches("^david-deactivated-[0-9a-f-]{36}@37signals.com$", deactivated.EmailAddress);
    }

    [Fact]
    public void Bots_are_created_and_rekeyed()
    {
        var bot = _app.Sql(sql => Q.Users.CreateBot(sql, "Bender"));
        Assert.Matches($"^{bot.Id}-[A-Za-z0-9]{{12}}$", bot.BotKey);
        Assert.Equal(bot.Id, _app.Sql(sql => Q.Users.AuthenticateBot(sql, bot.BotKey))?.Id);

        _app.Sql(sql => Q.Users.ResetBotKey(sql, bot.Id));
        var rekeyed = _app.Sql(sql => Q.Users.Find(sql, bot.Id))!;
        Assert.NotEqual(bot.BotKey, rekeyed.BotKey);
        Assert.Null(_app.Sql(sql => Q.Users.AuthenticateBot(sql, bot.BotKey)));
    }

    [Fact]
    public void Administrators_and_creators_can_administer()
    {
        Assert.True(Fixtures.David.CanAdminister());
        Assert.False(Fixtures.Kevin.CanAdminister());
        Assert.True(Fixtures.Kevin.CanAdminister(creatorId: Fixtures.Kevin.Id));
        Assert.False(Fixtures.Kevin.CanAdminister(creatorId: Fixtures.Designers.CreatorId));
    }
}
