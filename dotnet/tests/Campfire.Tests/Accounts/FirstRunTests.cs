using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Features.FirstRun;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Tests.AccountsAndPeople;

/// <summary>A brand-new installation: no account, users or rooms yet.</summary>
public sealed class FirstRunTests : IDisposable
{
    private readonly CampfireApp _app = new();

    public void Dispose() => _app.Dispose();

    private long Count(string table) => _app.Sql(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {table}"));

    [Fact]
    public async Task Show_is_permitted_when_there_is_no_account()
    {
        using var client = _app.Anonymous();
        var response = await client.GetAsync("/first_run");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await response.HtmlAsync()).QuerySelector("form[action='/first_run'][enctype='multipart/form-data'] input[name='user[email_address]']"));
    }

    [Fact]
    public async Task Show_is_not_permitted_once_an_account_exists()
    {
        _app.Sql(sql => Q.Accounts.Create(sql, "Chat"));
        using var client = _app.Anonymous();

        (await client.GetAsync("/first_run")).AssertRedirectTo("/");
    }

    [Fact]
    public async Task Create_sets_up_the_account_room_and_administrator()
    {
        using var client = _app.Anonymous();
        var response = await client.PostFormAsync("/first_run",
            ("user[name]", "New Person"), ("user[email_address]", "new@37signals.com"), ("user[password]", "secret123456"));

        response.AssertRedirectTo("/");
        Assert.NotNull(_app.SessionToken(client));
        Assert.Equal(1, Count("accounts"));
        Assert.Equal(1, Count("rooms"));
        Assert.Equal(1, Count("users"));

        var user = _app.Sql(sql => Q.Users.Authenticate(sql, "new@37signals.com", "secret123456"))!;
        var room = _app.Sql(sql => Q.Rooms.Original(sql))!;
        Assert.True(user.IsAdministrator);
        Assert.Equal(RoomType.Open, room.Type);
        Assert.Equal("All Talk", room.Name);
        Assert.Equal(new[] { user.Id }, _app.Sql(sql => Q.Rooms.UserIds(sql, room.Id)));
        Assert.Equal("Campfire", _app.Sql(sql => Q.Accounts.First(sql))!.Name);
    }

    [Fact]
    public async Task Create_is_not_vulnerable_to_race_conditions()
    {
        var attempts = Enumerable.Range(0, 5).Select(async i =>
        {
            using var client = _app.Anonymous();
            return await client.PostFormAsync("/first_run",
                ("user[name]", $"Attacker{i}"), ("user[email_address]", $"attacker{i}@example.com"), ("user[password]", "password123"));
        });

        await Task.WhenAll(attempts);

        Assert.Equal(1, Count("accounts"));
        Assert.Equal(1, _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM users WHERE role = 1")));
    }

    [Fact]
    public void Setup_makes_the_first_user_an_administrator_of_one_open_room()
    {
        var user = _app.Sql(sql => FirstRunEndpoints.Setup(sql, "User", "user@example.com", "secret123456"));

        Assert.True(user.IsAdministrator);
        Assert.Single(_app.Sql(sql => Q.Memberships.ForUserWithRooms(sql, user.Id, visibleOnly: false)));
        Assert.True(_app.Sql(sql => Q.Rooms.Original(sql))!.IsOpen);
    }
}
