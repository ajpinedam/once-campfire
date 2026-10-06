namespace Campfire.Tests.Rooms;

using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Turbo;

public sealed class OpenRoomsTests : RoomsTest
{
    [Fact]
    public async Task Show_redirects_to_the_general_room_page()
    {
        using var client = SignIn(F.David);
        (await client.GetAsync($"/rooms/opens/{F.Hq.Id}")).AssertRedirectTo($"/rooms/{F.Hq.Id}");
        // Closed rooms are in reach too: open and closed rooms convert into each other
        (await client.GetAsync($"/rooms/closeds/{F.Designers.Id}")).AssertRedirectTo($"/rooms/{F.Designers.Id}");
    }

    [Fact]
    public async Task New_renders_a_form_listing_everyone()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync("/rooms/opens/new"));

        Assert.Equal("New chat room", page.Title);
        Assert.Equal("New room", page.QuerySelector("form[action='/rooms/opens'] input#room_name")?.GetAttribute("value"));
        Assert.NotNull(page.QuerySelector("a[href='/rooms/closeds/new'] input#room_type[checked]"));
        Assert.Equal((int)UserCount(), page.QuerySelectorAll("[data-filter-target='list'] li").Length);
    }

    [Fact]
    public async Task Create_grants_everyone_access_and_broadcasts_to_every_sidebar()
    {
        using var client = SignIn(F.David);
        await using var rooms = await CableProbe.StreamAsync(App, F.Kevin, StreamNames.Rooms);

        var response = await client.PostFormAsync("/rooms/opens", ("room[name]", "My New Room"));

        var room = App.Sql(sql => sql.First($"SELECT {Rows.RoomColumns()} FROM rooms ORDER BY id DESC LIMIT 1", r => Rows.ReadRoom(r)))!;
        response.AssertRedirectTo($"/rooms/{room.Id}");
        Assert.Equal(RoomType.Open, room.Type);
        Assert.Equal(UserCount(), MemberIds(room).Count);

        await rooms.WaitForAsync();
        Assert.Single(rooms.TurboStreams);
        Assert.Contains("action=\"prepend\" target=\"shared_rooms\"", rooms.TurboStreams[0]);
        Assert.Contains("My New Room", rooms.TurboStreams[0]);
    }

    [Fact]
    public async Task Create_is_forbidden_to_members_when_the_account_restricts_room_creation()
    {
        RestrictRoomCreation();
        using var client = SignIn(F.Jz);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostFormAsync("/rooms/opens", ("room[name]", "My New Room"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/rooms/opens/new")).StatusCode);
    }

    [Fact]
    public async Task Only_administrators_or_creators_can_update()
    {
        using var client = SignIn(F.Jz);
        await using var rooms = await CableProbe.StreamAsync(App, F.Jz, StreamNames.Rooms);

        var response = await client.SubmitAsync("put", $"/rooms/opens/{F.Hq.Id}", ("room[name]", "New Name"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("HQ", Reload(F.Hq).Name);
        Assert.Empty(await rooms.SettleAsync());
    }

    [Fact]
    public async Task Update_renames_and_broadcasts()
    {
        using var client = SignIn(F.David);
        await using var rooms = await CableProbe.StreamAsync(App, F.David, StreamNames.Rooms);

        var response = await client.SubmitAsync("put", $"/rooms/opens/{F.Pets.Id}", ("room[name]", "New Name"));

        response.AssertRedirectTo($"/rooms/{F.Pets.Id}");
        Assert.Equal("New Name", Reload(F.Pets).Name);
        await rooms.WaitForAsync();
        Assert.Contains($"action=\"replace\" target=\"list_room_{F.Pets.Id}\"", Assert.Single(rooms.TurboStreams));
    }

    [Fact]
    public async Task Updating_a_closed_room_as_open_opens_it_to_everyone()
    {
        using var client = SignIn(F.David);

        await client.SubmitAsync("put", $"/rooms/opens/{F.Designers.Id}", ("room[name]", "Doesn't matter"));

        Assert.Equal(RoomType.Open, Reload(F.Designers).Type);
        Assert.Equal(UserCount(), MemberIds(F.Designers).Count);
    }

    [Fact]
    public async Task A_direct_room_cant_be_promoted_to_open_by_anyone()
    {
        using var kevin = SignIn(F.Kevin);
        await kevin.SubmitAsync("put", $"/rooms/opens/{F.BenderAndKevin.Id}", ("room[name]", "Watercooler"));
        Assert.Equal(RoomType.Direct, Reload(F.BenderAndKevin).Type);
        Assert.Equal(new[] { F.Kevin.Id, F.Bender.Id }.Order(), MemberIds(F.BenderAndKevin));

        using var david = SignIn(F.David);
        await david.SubmitAsync("put", $"/rooms/opens/{F.DavidAndKevin.Id}", ("room[name]", "Watercooler"));
        Assert.Equal(RoomType.Direct, Reload(F.DavidAndKevin).Type);
        Assert.Equal(new[] { F.David.Id, F.Kevin.Id }.Order(), MemberIds(F.DavidAndKevin));
    }

    [Fact]
    public async Task Edit_offers_to_turn_the_room_closed()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync($"/rooms/opens/{F.Pets.Id}/edit"));

        Assert.Equal("Edit settings for All Pets", page.Title);
        Assert.NotNull(page.QuerySelector($"form[action='/rooms/opens/{F.Pets.Id}'] input[name='_method'][value='patch']"));
        Assert.NotNull(page.QuerySelector($"a[href='/rooms/closeds/{F.Pets.Id}/edit'] input#room_type[checked]"));
        Assert.NotNull(page.QuerySelector($"form[action$='/rooms/{F.Pets.Id}'] input[name='_method'][value='delete']"));
    }

    private void RestrictRoomCreation()
    {
        App.Sql(sql => Accounts.UpdateSettings(sql, F.Account.Id, new AccountSettings(RestrictRoomCreationToAdministrators: true)));
        App.Service<Web.Http.AccountCache>().Invalidate();
    }
}

public sealed class ClosedRoomsTests : RoomsTest
{
    [Fact]
    public async Task New_lists_everyone_with_the_creator_preselected()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync("/rooms/closeds/new"));

        Assert.NotNull(page.QuerySelector($"input[type='hidden'][name='user_ids[]'][value='{F.David.Id}']"));
        Assert.NotNull(page.QuerySelector($"input[type='checkbox'][name='user_ids[]'][value='{F.Kevin.Id}']:not([checked])"));
        Assert.NotNull(page.QuerySelector("a[href='/rooms/opens/new'] input#room_type:not([checked])"));
    }

    [Fact]
    public async Task Create_grants_the_chosen_people_and_tells_only_them()
    {
        using var client = SignIn(F.David);
        await using var davidRooms = await CableProbe.StreamAsync(App, F.David, StreamNames.UserRooms(F.David.Id));
        await using var kevinRooms = await CableProbe.StreamAsync(App, F.Kevin, StreamNames.UserRooms(F.Kevin.Id));
        await using var jzRooms = await CableProbe.StreamAsync(App, F.Jz, StreamNames.UserRooms(F.Jz.Id));

        var response = await client.PostFormAsync("/rooms/closeds", new[]
        {
            KeyValuePair.Create("room[name]", "My New Room"),
            KeyValuePair.Create("user_ids[]", F.David.Id.ToString()),
            KeyValuePair.Create("user_ids[]", F.Kevin.Id.ToString()),
            KeyValuePair.Create("user_ids[]", F.Jason.Id.ToString())
        });

        var room = App.Sql(sql => sql.First($"SELECT {Rows.RoomColumns()} FROM rooms ORDER BY id DESC LIMIT 1", r => Rows.ReadRoom(r)))!;
        response.AssertRedirectTo($"/rooms/{room.Id}");
        Assert.Equal(RoomType.Closed, room.Type);
        Assert.Equal(3, MemberIds(room).Count);

        Assert.Single(await davidRooms.WaitForAsync());
        Assert.Single(await kevinRooms.WaitForAsync());
        Assert.Empty(await jzRooms.SettleAsync());
        Assert.Contains("target=\"shared_rooms\"", davidRooms.TurboStreams[0]);
    }

    [Fact]
    public async Task Update_revises_memberships()
    {
        using var client = SignIn(F.David);
        var keep = MemberIds(F.Designers).Where(id => id != F.Jason.Id).ToList();

        var response = await client.PostFormAsync($"/rooms/closeds/{F.Designers.Id}",
            keep.Select(id => KeyValuePair.Create("user_ids[]", id.ToString()))
                .Append(KeyValuePair.Create("room[name]", "New Name"))
                .Append(KeyValuePair.Create("_method", "put")));

        response.AssertRedirectTo($"/rooms/{F.Designers.Id}");
        Assert.Equal("New Name", Reload(F.Designers).Name);
        Assert.Equal(keep.Order(), MemberIds(F.Designers));
    }

    [Fact]
    public async Task Updating_an_open_room_as_closed_keeps_only_the_chosen_people()
    {
        using var client = SignIn(F.David);

        await client.PostFormAsync($"/rooms/closeds/{F.Pets.Id}", new[]
        {
            KeyValuePair.Create("_method", "put"),
            KeyValuePair.Create("room[name]", "Doesn't matter"),
            KeyValuePair.Create("user_ids[]", F.David.Id.ToString()),
            KeyValuePair.Create("user_ids[]", F.Jason.Id.ToString())
        });

        Assert.Equal(RoomType.Closed, Reload(F.Pets).Type);
        Assert.Equal(2, MemberIds(F.Pets).Count);
    }

    [Fact]
    public async Task Only_administrators_or_creators_can_update()
    {
        using var client = SignIn(F.Jz);
        var response = await client.SubmitAsync("put", $"/rooms/closeds/{F.Designers.Id}", ("room[name]", "New Name"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Designers", Reload(F.Designers).Name);
    }

    [Fact]
    public async Task A_direct_room_cant_be_converted_to_closed_to_revise_its_participants()
    {
        using var client = SignIn(F.Kevin);

        await client.PostFormAsync($"/rooms/closeds/{F.BenderAndKevin.Id}", new[]
        {
            KeyValuePair.Create("_method", "put"),
            KeyValuePair.Create("room[name]", "Watercooler"),
            KeyValuePair.Create("user_ids[]", F.Kevin.Id.ToString()),
            KeyValuePair.Create("user_ids[]", F.Jz.Id.ToString())
        });

        Assert.Equal(RoomType.Direct, Reload(F.BenderAndKevin).Type);
        Assert.Equal(new[] { F.Kevin.Id, F.Bender.Id }.Order(), MemberIds(F.BenderAndKevin));
    }

    [Fact]
    public async Task Removing_yourself_leaves_the_room_out_of_reach()
    {
        using var client = SignIn(F.David);

        var response = await client.PostFormAsync($"/rooms/closeds/{F.Designers.Id}", new[]
        {
            KeyValuePair.Create("_method", "put"),
            KeyValuePair.Create("room[name]", "Designers"),
            KeyValuePair.Create("user_ids[]", F.Jason.Id.ToString()),
            KeyValuePair.Create("user_ids[]", F.Jz.Id.ToString())
        });

        response.AssertRedirectTo($"/rooms/{F.Designers.Id}");
        (await client.GetAsync($"/rooms/{F.Designers.Id}")).AssertRedirectTo("/");
    }

    [Fact]
    public async Task Edit_splits_members_from_everyone_else()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync($"/rooms/closeds/{F.Designers.Id}/edit"));

        Assert.NotNull(page.QuerySelector($"input[name='user_ids[]'][value='{F.Jz.Id}'][checked]"));
        Assert.NotNull(page.QuerySelector($"input[name='user_ids[]'][value='{F.Bender.Id}']:not([checked])"));
    }
}

public sealed class DirectRoomsTests : RoomsTest
{
    [Fact]
    public async Task Create_starts_a_ping_with_the_chosen_people()
    {
        using var client = SignIn(F.David);
        await using var jzRooms = await CableProbe.StreamAsync(App, F.Jz, StreamNames.UserRooms(F.Jz.Id));

        var response = await client.PostFormAsync("/rooms/directs", ("user_ids[]", F.Jz.Id.ToString()));

        var room = App.Sql(sql => sql.First($"SELECT {Rows.RoomColumns()} FROM rooms ORDER BY id DESC LIMIT 1", r => Rows.ReadRoom(r)))!;
        response.AssertRedirectTo($"/rooms/{room.Id}");
        Assert.Equal(RoomType.Direct, room.Type);
        Assert.Equal(new[] { F.David.Id, F.Jz.Id }.Order(), MemberIds(room));

        await jzRooms.WaitForAsync();
        var stream = Assert.Single(jzRooms.TurboStreams);
        Assert.Contains("target=\"direct_rooms\"", stream);
        Assert.Contains("David", stream);
    }

    [Fact]
    public async Task Create_takes_people_from_the_query_as_button_to_sends_them()
    {
        using var client = SignIn(F.David);
        var response = await client.PostFormAsync($"/rooms/directs?user_ids%5B%5D={F.Jason.Id}");
        response.AssertRedirectTo($"/rooms/{F.DavidAndJason.Id}");
    }

    [Fact]
    public async Task Create_only_once_per_set_of_people()
    {
        using var client = SignIn(F.David);
        var count = RoomCount();

        await client.PostFormAsync("/rooms/directs", ("user_ids[]", F.Jz.Id.ToString()));
        await client.PostFormAsync("/rooms/directs", ("user_ids[]", F.Jz.Id.ToString()));

        Assert.Equal(count + 1, RoomCount());
    }

    [Fact]
    public async Task Any_participant_can_delete_a_direct_room()
    {
        using var client = SignIn(F.Kevin);
        var count = RoomCount();

        var response = await client.SendAsync(HttpMethod.Delete, $"/rooms/directs/{F.DavidAndKevin.Id}");

        response.AssertRedirectTo("/");
        Assert.Equal(count - 1, RoomCount());
    }

    [Theory]
    [InlineData("Kevin", "Designers")]
    [InlineData("Kevin", "Hq")]
    [InlineData("Jz", "DavidAndKevin")]
    public async Task Destroy_cant_reach_rooms_beyond_the_participants_own_direct_rooms(string user, string room)
    {
        var who = (User)typeof(Fixtures).GetProperty(user)!.GetValue(F)!;
        var target = (Room)typeof(Fixtures).GetProperty(room)!.GetValue(F)!;
        using var client = SignIn(who);
        var count = RoomCount();

        await client.SendAsync(HttpMethod.Delete, $"/rooms/directs/{target.Id}");

        Assert.Equal(count, RoomCount());
    }

    [Fact]
    public async Task New_and_edit_render()
    {
        using var client = SignIn(F.David);

        var picker = await ParseAsync(await client.GetAsync("/rooms/directs/new"));
        Assert.NotNull(picker.QuerySelector("turbo-frame#direct_rooms_control form[action='/rooms/directs'] select[name='user_ids[]']"));

        var edit = await ParseAsync(await client.GetAsync($"/rooms/directs/{F.DavidAndKevin.Id}/edit"));
        Assert.Equal("Edit settings for Kevin", edit.Title);
        Assert.NotNull(edit.QuerySelector($"form[action$='/rooms/directs/{F.DavidAndKevin.Id}'] input[name='_method'][value='delete']"));
    }
}

public sealed class InvolvementsTests : RoomsTest
{
    [Fact]
    public async Task Show_renders_the_bell_in_its_frame()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Designers.Id}/involvement"));

        // David is involved in mentions in Designers; a click moves on to everything
        Assert.NotNull(page.QuerySelector($"turbo-frame#involvement_room_{F.Designers.Id} form[action='/rooms/{F.Designers.Id}/involvement?involvement=everything'] button.mentions"));
    }

    [Fact]
    public async Task Going_invisible_and_back_updates_the_users_sidebar()
    {
        using var client = SignIn(F.David);
        await using var rooms = await CableProbe.StreamAsync(App, F.David, StreamNames.UserRooms(F.David.Id));

        var response = await client.SubmitAsync("put", $"/rooms/{F.Watercooler.Id}/involvement?involvement=invisible");
        response.AssertRedirectTo($"/rooms/{F.Watercooler.Id}/involvement");
        Assert.Equal(Involvement.Invisible, WatercoolerInvolvement());
        await rooms.WaitForAsync();
        Assert.Contains($"action=\"remove\" target=\"list_room_{F.Watercooler.Id}\"", Assert.Single(rooms.TurboStreams));

        await client.SubmitAsync("put", $"/rooms/{F.Watercooler.Id}/involvement", ("involvement", "everything"));
        Assert.Equal(Involvement.Everything, WatercoolerInvolvement());
        await rooms.WaitForAsync(2);
        Assert.Contains("action=\"prepend\" target=\"shared_rooms\"", rooms.TurboStreams[1]);
    }

    [Fact]
    public async Task Changes_between_visible_levels_and_in_direct_rooms_broadcast_nothing()
    {
        using var client = SignIn(F.David);
        await using var rooms = await CableProbe.StreamAsync(App, F.David, StreamNames.UserRooms(F.David.Id));

        await client.SubmitAsync("put", $"/rooms/{F.Watercooler.Id}/involvement?involvement=mentions");
        await client.SubmitAsync("put", $"/rooms/{F.DavidAndJason.Id}/involvement?involvement=nothing");

        Assert.Equal(Involvement.Mentions, WatercoolerInvolvement());
        Assert.Empty(await rooms.SettleAsync());
    }

    [Fact]
    public async Task Members_can_change_their_own_involvement()
    {
        using var client = SignIn(F.Jz);
        var response = await client.SubmitAsync("put", $"/rooms/{F.Designers.Id}/involvement?involvement=mentions");

        response.AssertRedirectTo($"/rooms/{F.Designers.Id}/involvement");
        Assert.Equal(Involvement.Mentions, App.Sql(sql => Memberships.Find(sql, F.Designers.Id, F.Jz.Id))!.Involvement);
    }

    private Involvement WatercoolerInvolvement() => App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.David.Id))!.Involvement;
}

public sealed class RefreshesTests : RoomsTest
{
    [Fact]
    public async Task Refresh_appends_new_messages_and_replaces_updated_ones()
    {
        using var client = SignIn(F.David);
        var old = CreateMessage(F.Hq, F.Jason, "Old message", "old", DateTime.UtcNow.AddDays(-1));
        var recent = CreateMessage(F.Hq, F.Jason, "New message", "new", DateTime.UtcNow.AddMinutes(-1));
        App.Sql(sql => Messages.Touch(sql, old.Id));

        var since = SqlTime.ToEpochMilliseconds(DateTime.UtcNow.AddMinutes(-10));
        var response = await client.GetAsync($"/rooms/{F.Hq.Id}/refresh?since={since}", accept: "text/vnd.turbo-stream.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vnd.turbo-stream.html", response.Content.Headers.ContentType?.MediaType);

        var streams = await ParseAsync(response);
        var append = Assert.Single(streams.QuerySelectorAll("turbo-stream[action='append']"));
        Assert.Equal($"messages_room_{F.Hq.Id}", append.GetAttribute("target"));
        Assert.Contains($"id=\"message_{recent.ClientMessageId}\"", append.InnerHtml);

        var replace = Assert.Single(streams.QuerySelectorAll("turbo-stream[action='replace']"));
        Assert.Equal($"message_{old.ClientMessageId}", replace.GetAttribute("target"));
    }
}
