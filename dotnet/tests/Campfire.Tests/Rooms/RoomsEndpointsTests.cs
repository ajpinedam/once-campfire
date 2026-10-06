namespace Campfire.Tests.Rooms;

using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Turbo;

public sealed class RoomsEndpointsTests : RoomsTest
{
    [Fact]
    public async Task Index_redirects_to_the_users_last_room()
    {
        using var client = SignIn(F.David);
        var last = App.Sql(sql => Web.Data.Queries.Rooms.LastForUser(sql, F.David.Id))!;

        var response = await client.GetAsync("/rooms");
        response.AssertRedirectTo($"/rooms/{last.Id}");
    }

    [Fact]
    public async Task Show_renders_the_room_and_remembers_it_as_the_last_room_visited()
    {
        using var client = SignIn(F.David);
        var message = CreateMessage(F.Designers, F.Jason, "Hello designers", "hello-designers");

        var response = await client.GetAsync($"/rooms/{F.Designers.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith($"last_room={F.Designers.Id}", StringComparison.Ordinal));

        var page = await ParseAsync(response);
        Assert.Equal("Designers", page.Title);
        Assert.Equal(F.Designers.Id.ToString(), page.QuerySelector("meta[name='current-room-id']")?.GetAttribute("content"));
        Assert.NotNull(page.QuerySelector($"#messages_room_{F.Designers.Id}.messages[data-controller='maintain-scroll refresh-room']"));
        Assert.NotNull(page.QuerySelector($"#message_{message.ClientMessageId}[data-message-id='{message.Id}']"));
        Assert.NotNull(page.QuerySelector("turbo-cable-stream-source[channel='RoomMessagesChannel'][signed-stream-name]"));
        Assert.NotNull(page.QuerySelector("form#composer[action='/rooms/" + F.Designers.Id + "/messages'] lexxy-editor[name='message[body]']"));
        Assert.NotNull(page.QuerySelector("script[type='text/template'][data-messages-target='template']"));
        Assert.NotNull(page.QuerySelector("turbo-frame#user_sidebar[src='/users/me/sidebar']"));
        Assert.NotNull(page.QuerySelector($"turbo-frame#involvement_room_{F.Designers.Id}"));
    }

    [Fact]
    public async Task Show_names_direct_rooms_after_the_other_participants()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.DavidAndJason.Id}"));
        Assert.Equal("Jason", page.Title);
    }

    [Fact]
    public async Task Show_around_a_message_renders_the_messages_on_either_side()
    {
        using var client = SignIn(F.David);
        var messages = CreateMessages(F.Designers, F.Jason, 100);
        var target = messages[50];

        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Designers.Id}/@{target.Id}"));

        Assert.NotNull(page.QuerySelector($"#message_{target.ClientMessageId}"));
        Assert.NotNull(page.QuerySelector($"#message_{messages[11].ClientMessageId}"));
        Assert.Null(page.QuerySelector($"#message_{messages[9].ClientMessageId}"));
        Assert.NotNull(page.QuerySelector($"#message_{messages[90].ClientMessageId}"));
        Assert.Null(page.QuerySelector($"#message_{messages[91].ClientMessageId}"));
    }

    [Fact]
    public async Task Show_redirects_home_when_the_room_is_out_of_reach()
    {
        using var client = SignIn(F.Kevin);
        var response = await client.GetAsync($"/rooms/{F.Watercooler.Id}");
        response.AssertRedirectTo("/");
    }

    [Fact]
    public async Task Show_includes_the_invitation_in_the_original_room_until_it_has_a_page_of_messages()
    {
        using var client = SignIn(F.David);
        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Pets.Id}"));
        Assert.NotNull(page.QuerySelector("#system_welcome #invite_url"));

        CreateMessages(F.Pets, F.David, Message.PageSize + 1);
        page = await ParseAsync(await client.GetAsync($"/rooms/{F.Pets.Id}"));
        Assert.Null(page.QuerySelector("#system_welcome"));
    }

    [Fact]
    public async Task Destroy_removes_the_room_and_broadcasts_its_removal()
    {
        using var client = SignIn(F.David);
        await using var rooms = await CableProbe.StreamAsync(App, F.David, StreamNames.Rooms);
        var count = RoomCount();

        var response = await client.SendAsync(HttpMethod.Delete, $"/rooms/{F.Designers.Id}");

        response.AssertRedirectTo("/");
        Assert.Equal(count - 1, RoomCount());
        var streams = await rooms.WaitForAsync();
        Assert.Single(streams);
        Assert.Contains($"action=\"remove\" target=\"list_room_{F.Designers.Id}\"", rooms.TurboStreams[0]);
    }

    [Fact]
    public async Task Destroy_is_only_allowed_for_creators_or_administrators()
    {
        using var jz = SignIn(F.Jz);

        var response = await jz.SendAsync(HttpMethod.Delete, $"/rooms/{F.Designers.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(Reload(F.Designers));

        App.Sql(sql => sql.Execute("UPDATE rooms SET creator_id = @jz WHERE id = @room", ("@jz", F.Jz.Id), ("@room", F.Designers.Id)));
        response = await jz.SendAsync(HttpMethod.Delete, $"/rooms/{F.Designers.Id}");
        response.AssertRedirectTo("/");
        Assert.Null(App.Sql(sql => Web.Data.Queries.Rooms.Find(sql, F.Designers.Id)));
    }

    [Fact]
    public async Task Destroy_takes_the_rooms_messages_with_it()
    {
        using var client = SignIn(F.David);
        var message = CreateMessage(F.Designers, F.Jason, "gone soon");
        App.Sql(sql => Web.Data.Queries.Boosts.Create(sql, message.Id, F.David.Id, "👍"));

        await client.SendAsync(HttpMethod.Delete, $"/rooms/{F.Designers.Id}");

        Assert.Null(App.Sql(sql => Messages.Find(sql, message.Id)));
        Assert.Empty(App.Sql(sql => Messages.Search(sql, F.David.Id, "gone")));
    }

    [Fact]
    public async Task Rendered_link_previews_drop_off_scheme_images_and_links()
    {
        using var client = SignIn(F.David);
        var body = "<div><action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" " +
                   "href=\"javascript:alert(1)\" url=\"data:image/svg+xml;base64,PHN2Zy8+\" filename=\"Free cookies\" caption=\"Cookies here\"></action-text-attachment></div>";

        var posted = await client.PostFormAsync($"/rooms/{F.Watercooler.Id}/messages",
            [KeyValuePair.Create("message[body]", body), KeyValuePair.Create("message[client_message_id]", "hand-written-preview")],
            accept: "text/vnd.turbo-stream.html");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var html = await (await client.GetAsync($"/rooms/{F.Watercooler.Id}")).BodyAsync();
        Assert.DoesNotContain("javascript:alert", html);
        Assert.DoesNotContain("data:image/svg", html);
        Assert.Contains("Free cookies", html);
    }
}
