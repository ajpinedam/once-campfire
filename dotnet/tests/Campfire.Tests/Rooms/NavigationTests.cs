namespace Campfire.Tests.Rooms;

using System.Net;
using System.Text.Json;
using Campfire.Tests.Support;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;

public sealed class SearchesTests : RoomsTest
{
    public SearchesTests() => CreateMessage(F.Designers, F.David, "Hello world!", "search");

    [Fact]
    public async Task Index_starts_empty()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync("/searches"));

        Assert.Equal("Search", page.Title);
        Assert.Empty(page.QuerySelectorAll(".message"));
        Assert.NotNull(page.QuerySelector("form[action='/searches'] input#q[name='q']"));
    }

    [Fact]
    public async Task Finds_reachable_messages()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync("/searches?q=hello"));

        var result = Assert.Single(page.QuerySelectorAll("#search-results .message"));
        Assert.Contains("Hello world!", result.TextContent);
        Assert.Contains("“hello”", page.QuerySelector(".searches__query")!.TextContent);
    }

    [Fact]
    public async Task Operator_words_are_searched_for_as_words()
    {
        using var client = SignIn(F.David);

        foreach (var word in new[] { "AND", "OR", "NOT" })
        {
            var response = await client.GetAsync($"/searches?q={word}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await ParseAsync(response)).QuerySelectorAll("#search-results .message"));
        }

        CreateMessage(F.Designers, F.David, "Salt and pepper", "operators");
        var page = await ParseAsync(await client.GetAsync("/searches?q=salt%20AND"));
        Assert.Contains("Salt and pepper", Assert.Single(page.QuerySelectorAll("#search-results .message")).TextContent);
    }

    [Fact]
    public async Task Unreachable_messages_are_not_found()
    {
        App.Sql(sql => Memberships.RevokeFrom(sql, F.Designers.Id, [F.David.Id]));
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync("/searches?q=hello"));
        Assert.Empty(page.QuerySelectorAll("#search-results .message"));
    }

    [Fact]
    public async Task Create_records_the_search_and_clear_forgets_them()
    {
        using var client = SignIn(F.David);

        var response = await client.PostFormAsync("/searches", ("q", "hello"));
        response.AssertRedirectTo("/searches?q=hello");
        Assert.Equal("hello", Assert.Single(App.Sql(sql => Searches.RecentForUser(sql, F.David.Id))).Query);

        var page = await ParseAsync(await client.GetAsync("/searches"));
        Assert.NotNull(page.QuerySelector(".searches__recents a[href='/searches?q=hello']"));

        (await client.SendAsync(HttpMethod.Delete, "/searches/clear")).AssertRedirectTo("/searches");
        Assert.Empty(App.Sql(sql => Searches.RecentForUser(sql, F.David.Id)));
    }

    [Fact]
    public async Task Only_the_ten_latest_searches_are_kept()
    {
        using var client = SignIn(F.David);
        for (var i = 0; i < 12; i++)
        {
            await client.PostFormAsync("/searches", ("q", $"term{i}"));
        }

        Assert.Equal(Search.RecentLimit, App.Sql(sql => Searches.RecentForUser(sql, F.David.Id)).Count);
    }
}

public sealed class WelcomeTests : RoomsTest
{
    [Fact]
    public async Task Redirects_to_the_first_room_the_user_has()
    {
        using var client = SignIn(F.David);
        var original = App.Sql(sql => Web.Data.Queries.Rooms.OriginalForUser(sql, F.David.Id))!;
        (await client.GetAsync("/")).AssertRedirectTo($"/rooms/{original.Id}");
    }

    [Fact]
    public async Task Redirects_to_the_last_room_visited()
    {
        using var client = SignIn(F.David);
        client.Cookies.Add(client.BaseAddress, new Cookie("last_room", F.Watercooler.Id.ToString()));
        (await client.GetAsync("/")).AssertRedirectTo($"/rooms/{F.Watercooler.Id}");
    }

    [Fact]
    public async Task People_without_rooms_see_the_welcome_page()
    {
        var loner = App.Sql(sql => Users.Create(sql, "Loner", "loner@example.com", CampfireApp.Password));
        App.Sql(sql => sql.Execute("DELETE FROM memberships WHERE user_id = @id", ("@id", loner.Id))); // new people join open rooms
        using var client = SignIn(loner);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ParseAsync(response);
        Assert.Equal("No rooms yet", page.Title);
        Assert.NotNull(page.QuerySelector("turbo-frame#user_sidebar[src='/users/me/sidebar']"));
    }
}

public sealed class SidebarTests : RoomsTest
{
    [Fact]
    public async Task Shows_the_users_rooms()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync("/users/me/sidebar"));

        Assert.NotNull(page.QuerySelector("turbo-frame#user_sidebar"));
        foreach (var room in new[] { F.Pets, F.Hq, F.Watercooler, F.Designers })
        {
            Assert.Contains(room.Name!, page.QuerySelector($"#shared_rooms #list_room_{room.Id}")!.TextContent);
        }

        Assert.NotNull(page.QuerySelector($"#direct_rooms #list_room_{F.DavidAndJason.Id}"));
        Assert.Equal(2, page.QuerySelectorAll("turbo-cable-stream-source").Length);
    }

    [Fact]
    public async Task Marks_unread_direct_and_shared_rooms()
    {
        App.Sql(sql =>
        {
            Memberships.MarkUnread(sql, F.DavidAndJason.Id, F.Jason.Id, DateTime.UtcNow);
            Memberships.MarkUnread(sql, F.Watercooler.Id, F.Jason.Id, DateTime.UtcNow);
        });
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync("/users/me/sidebar"));

        Assert.Single(page.QuerySelectorAll("#direct_rooms .unread"));
        Assert.Single(page.QuerySelectorAll("#shared_rooms .unread"));
        Assert.NotNull(page.QuerySelector($"#list_room_{F.Watercooler.Id}.unread"));
    }

    [Fact]
    public async Task Offers_people_without_a_direct_room_yet_as_placeholders()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync("/users/me/sidebar"));

        // David already pings Jason and Kevin; JZ and Bender remain
        var placeholders = page.QuerySelectorAll("form.button_to[action^='/rooms/directs?user_ids']").Select(form => form.GetAttribute("action")).ToList();
        Assert.Contains($"/rooms/directs?user_ids%5B%5D={F.Jz.Id}", placeholders);
        Assert.Contains($"/rooms/directs?user_ids%5B%5D={F.Bender.Id}", placeholders);
        Assert.DoesNotContain($"/rooms/directs?user_ids%5B%5D={F.Jason.Id}", placeholders);
    }

    [Fact]
    public async Task Hides_invisible_rooms_and_the_new_room_button_when_restricted()
    {
        App.Sql(sql => Memberships.UpdateInvolvement(sql, Memberships.Find(sql, F.Hq.Id, F.Kevin.Id)!.Id, Involvement.Invisible));
        App.Sql(sql => Accounts.UpdateSettings(sql, F.Account.Id, new AccountSettings(RestrictRoomCreationToAdministrators: true)));
        App.Service<Web.Http.AccountCache>().Invalidate();
        using var client = SignIn(F.Kevin);

        var page = await ParseAsync(await client.GetAsync("/users/me/sidebar"));

        Assert.Null(page.QuerySelector($"#list_room_{F.Hq.Id}"));
        Assert.Null(page.QuerySelector(".rooms__new-btn"));
    }
}

public sealed class AutocompleteTests : RoomsTest
{
    private static readonly string[] PromptNames = ["Jason", "JZ"];

    [Fact]
    public async Task Json_search_returns_matching_people_with_escaped_names()
    {
        App.Sql(sql => Users.UpdateProfile(sql, F.David.Id, name: "David <script>alert(123)</script>"));
        using var client = SignIn(F.Jason);

        using var json = JsonDocument.Parse(await (await client.GetAsync("/autocompletable/users.json?query=da")).BodyAsync());

        var first = json.RootElement[0];
        Assert.Equal("David &lt;script&gt;alert(123)&lt;/script&gt;", first.GetProperty("name").GetString());
        Assert.Equal(F.David.Id, first.GetProperty("value").GetInt64());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("sgid").GetString()));
    }

    [Fact]
    public async Task Room_search_is_scoped_to_its_members_and_to_the_users_rooms()
    {
        using var david = SignIn(F.David);
        using var json = JsonDocument.Parse(await (await david.GetAsync($"/autocompletable/users?room_id={F.Hq.Id}&query=da", accept: "application/json")).BodyAsync());
        Assert.Equal("David", json.RootElement[0].GetProperty("name").GetString());

        using var kevin = SignIn(F.Kevin);
        Assert.Equal(HttpStatusCode.NotFound, (await kevin.GetAsync($"/autocompletable/users?room_id={F.Watercooler.Id}&query=da", accept: "application/json")).StatusCode);
    }

    [Fact]
    public async Task Html_answers_with_prompt_items_for_the_mentions_prompt()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync($"/autocompletable/users?room_id={F.Designers.Id}&filter=j"));

        var items = page.QuerySelectorAll("lexxy-prompt-item");
        Assert.Equal(PromptNames, items.Select(item => item.GetAttribute("search") ?? "").ToArray());
        Assert.All(items, item => Assert.False(string.IsNullOrEmpty(item.GetAttribute("sgid"))));
    }
}

/// <summary>Room, membership and pagination behavior (test/models/room*, membership, message).</summary>
public sealed class RoomModelTests : RoomsTest
{
    [Fact]
    public void Grant_revoke_and_create_for_users()
    {
        App.Sql(sql =>
        {
            Memberships.GrantTo(sql, F.Watercooler, [F.Kevin.Id]);
            Memberships.RevokeFrom(sql, F.Watercooler.Id, [F.David.Id]);
        });
        Assert.Contains(F.Kevin.Id, MemberIds(F.Watercooler));
        Assert.DoesNotContain(F.David.Id, MemberIds(F.Watercooler));

        var room = App.Sql(sql =>
        {
            var created = Web.Data.Queries.Rooms.Create(sql, "Hello!", RoomType.Closed, F.David.Id);
            Memberships.GrantTo(sql, created, [F.Kevin.Id, F.David.Id, F.Kevin.Id]);
            return created;
        });
        Assert.Equal(new[] { F.David.Id, F.Kevin.Id }.Order(), MemberIds(room));
        Assert.All(App.Sql(sql => Memberships.ForRoomWithUsers(sql, room.Id)), pair => Assert.Equal(Involvement.Mentions, pair.Membership.Involvement));
    }

    [Fact]
    public void Direct_rooms_are_found_by_their_exact_set_of_people()
    {
        Assert.Equal(F.DavidAndKevin.Id, App.Sql(sql => Web.Data.Queries.Rooms.FindDirectWithExactly(sql, [F.Kevin.Id, F.David.Id]))?.Id);
        Assert.Null(App.Sql(sql => Web.Data.Queries.Rooms.FindDirectWithExactly(sql, [F.Kevin.Id, F.David.Id, F.Jason.Id])));
        Assert.Null(App.Sql(sql => Web.Data.Queries.Rooms.FindDirectWithExactly(sql, [F.David.Id])));
    }

    [Fact]
    public void Membership_connections_count_up_down_and_expire()
    {
        Membership Load() => App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.David.Id))!;

        App.Sql(sql => Memberships.Present(sql, Load()));
        Assert.True(Load().IsConnected(DateTime.UtcNow));
        Assert.Equal(1, Load().Connections);

        App.Sql(sql => Memberships.Present(sql, Load()));
        Assert.Equal(2, Load().Connections);

        App.Sql(sql => Memberships.Disconnected(sql, Load()));
        Assert.True(Load().IsConnected(DateTime.UtcNow));
        Assert.Equal(1, Load().Connections);

        App.Sql(sql => Memberships.Disconnected(sql, Load()));
        Assert.False(Load().IsConnected(DateTime.UtcNow));
        Assert.Equal(0, Load().Connections);

        App.Sql(sql => Memberships.Present(sql, Load()));
        Assert.False(Load().IsConnected(DateTime.UtcNow + Membership.ConnectionTtl + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Connected_members_dont_get_marked_unread()
    {
        App.Sql(sql => Memberships.Present(sql, Memberships.Find(sql, F.Watercooler.Id, F.Jason.Id)!));
        App.Sql(sql => Memberships.MarkUnread(sql, F.Watercooler.Id, F.Bender.Id, DateTime.UtcNow));

        Assert.False(App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.Jason.Id))!.IsUnread);
        Assert.True(App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.David.Id))!.IsUnread);
        Assert.False(App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.Bender.Id))!.IsUnread);
    }

    [Fact]
    public void Pagination_keeps_order_when_timestamps_tie()
    {
        var messages = CreateMessages(F.Watercooler, F.Jason, Message.PageSize + 5);
        App.Sql(sql => sql.Execute("UPDATE messages SET created_at = @now", ("@now", DateTime.UtcNow)));

        Assert.Equal(messages.TakeLast(Message.PageSize).Select(m => m.Id), App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Select(m => m.Id));
    }

    [Fact]
    public void Paged_tells_whether_a_room_has_more_than_a_page()
    {
        CreateMessages(F.Watercooler, F.Jason, Message.PageSize);
        Assert.False(App.Sql(sql => Messages.Paged(sql, F.Watercooler.Id)));

        CreateMessage(F.Watercooler, F.Jason, "One more");
        Assert.True(App.Sql(sql => Messages.Paged(sql, F.Watercooler.Id)));
    }

    [Fact]
    public void Search_reads_quotes_and_operator_words_as_text()
    {
        var message = CreateMessage(F.Designers, F.David, "Say hi, NOT bye");
        Assert.Equal(new[] { message.Id }, App.Sql(sql => Messages.Search(sql, F.David.Id, "say \"hi NOT")).Select(m => m.Id));
    }
}
