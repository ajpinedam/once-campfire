namespace Campfire.Tests.Rooms;

using System.Net;
using System.Text;
using System.Text.Json;
using Campfire.Tests.Support;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Turbo;

/// <summary>Messages::ByBotsController and Messages::Boosts::ByBotsController.</summary>
public sealed class BotApiTests : RoomsTest
{
    private string Url(Room room, string? botKey = null, string suffix = "") => $"/rooms/{room.Id}/{botKey ?? F.Bender.BotKey}/messages{suffix}";

    /// <summary>What <c>curl -d 'text'</c> sends: the raw text, labelled as a form.</summary>
    private static StringContent CurlData(string text) => new(text, Encoding.UTF8, "application/x-www-form-urlencoded");

    private Task<HttpResponseMessage> PostText(CampfireClient client, string url, string text) => client.SendAsync(HttpMethod.Post, url, CurlData(text), accept: "*/*");

    private string LastPlainText() => App.Sql(sql =>
        sql.ScalarString("SELECT body FROM message_search_index WHERE rowid = (SELECT MAX(id) FROM messages)"))!;

    [Fact]
    public async Task Create_posts_the_raw_body_as_a_message()
    {
        using var client = App.Anonymous();
        var response = await PostText(client, Url(F.Watercooler), "Hello Bot World!");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();
        Assert.Equal(F.Bender.Id, message.CreatorId);
        Assert.Equal($"http://localhost/messages/{message.Id}", response.Headers.Location?.ToString());
        Assert.Equal("Hello Bot World!", LastPlainText());
    }

    [Fact]
    public async Task Create_keeps_utf8_text_intact()
    {
        using var client = App.Anonymous();
        await PostText(client, Url(F.Watercooler), "Hello 👋!");
        Assert.Equal("Hello 👋!", LastPlainText());
    }

    [Fact]
    public async Task Create_with_a_file()
    {
        using var client = App.Anonymous();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Convert.FromBase64String(MessagesTests.PixelPng)) { Headers = { ContentType = new("image/png") } }, "attachment", "moon.png" }
        };

        var response = await client.SendAsync(HttpMethod.Post, Url(F.Watercooler), form, accept: "*/*");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();
        Assert.Equal("moon.png", App.Sql(sql => Attachments.Find(sql, "Message", message.Id, "attachment"))?.Filename);
    }

    [Fact]
    public async Task Create_without_a_body_or_attachment_is_unprocessable()
    {
        using var client = App.Anonymous();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(HttpMethod.Post, Url(F.Watercooler), accept: "*/*")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostText(client, Url(F.Watercooler), "   ")).StatusCode);
        Assert.Equal(0, MessageCount());
    }

    [Fact]
    public async Task A_users_id_without_a_token_isnt_a_bot_key()
    {
        using var client = App.Anonymous();
        var response = await PostText(client, Url(F.BenderAndKevin, $"{F.Kevin.Id}-"), "Hello 👋!");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(0, MessageCount());
    }

    [Fact]
    public async Task Create_and_index_are_not_found_outside_the_bots_rooms()
    {
        using var client = App.Anonymous();
        Assert.Equal(HttpStatusCode.NotFound, (await PostText(client, Url(F.Designers), "Hello!")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(F.Designers), accept: "application/json")).StatusCode);
        Assert.Equal(0, MessageCount());
    }

    [Fact]
    public async Task Index_lists_messages_in_order_with_their_details()
    {
        using var client = App.Anonymous();
        var older = CreateMessages(F.Watercooler, F.Jason, 3);
        await PostText(client, Url(F.Watercooler), "Hello from Bender!");

        var response = await client.GetAsync(Url(F.Watercooler), accept: "application/json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("4", response.Headers.GetValues("X-Total-Count").Single());
        using var json = JsonDocument.Parse(await response.BodyAsync());
        var messages = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(older.Select(m => m.Id), messages.Take(3).Select(m => m.GetProperty("id").GetInt64()));

        var last = messages[^1];
        var stored = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();
        Assert.Equal(stored.Id, last.GetProperty("id").GetInt64());
        Assert.Equal("Hello from Bender!", last.GetProperty("body").GetProperty("plain_text").GetString());
        Assert.Contains("Hello from Bender!", last.GetProperty("body").GetProperty("html").GetString());
        Assert.Equal(stored.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), last.GetProperty("created_at").GetString());
        Assert.Equal(F.Bender.Id, last.GetProperty("creator").GetProperty("id").GetInt64());
        Assert.Equal("Bender Bot", last.GetProperty("creator").GetProperty("name").GetString());
        Assert.Equal("bot", last.GetProperty("creator").GetProperty("role").GetString());
        Assert.StartsWith("http://localhost/users/", last.GetProperty("creator").GetProperty("avatar_url").GetString());
        Assert.Equal(F.Watercooler.Id, last.GetProperty("room").GetProperty("id").GetInt64());
        Assert.Equal($"http://localhost/rooms/{F.Watercooler.Id}/messages/{stored.Id}", last.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Index_pages_back_through_older_messages_with_the_link_header()
    {
        using var client = App.Anonymous();
        var messages = CreateMessages(F.Watercooler, F.Jason, Message.PageSize + 1);

        var response = await client.GetAsync(Url(F.Watercooler), accept: "application/json");
        using var page = JsonDocument.Parse(await response.BodyAsync());
        Assert.Equal(Message.PageSize, page.RootElement.GetArrayLength());
        Assert.Equal("41", response.Headers.GetValues("X-Total-Count").Single());

        var link = response.Headers.GetValues("Link").Single();
        var next = link[(link.IndexOf('<') + 1)..link.IndexOf('>')];
        Assert.EndsWith($"?before={messages[1].Id}", next);

        var older = await client.GetAsync(new Uri(next).PathAndQuery, accept: "application/json");
        using var olderPage = JsonDocument.Parse(await older.BodyAsync());
        Assert.Equal(new[] { messages[0].Id }, olderPage.RootElement.EnumerateArray().Select(m => m.GetProperty("id").GetInt64()));
        Assert.False(older.Headers.Contains("Link"));
    }

    [Fact]
    public async Task Index_pages_newer_messages_with_after()
    {
        using var client = App.Anonymous();
        var messages = CreateMessages(F.Watercooler, F.Jason, 5);

        var response = await client.GetAsync(Url(F.Watercooler, suffix: $"?after={messages[1].Id}"), accept: "application/json");

        using var json = JsonDocument.Parse(await response.BodyAsync());
        Assert.Equal(messages.Skip(2).Select(m => m.Id), json.RootElement.EnumerateArray().Select(m => m.GetProperty("id").GetInt64()));
        Assert.False(response.Headers.Contains("Link"));
    }

    [Fact]
    public async Task Index_in_an_empty_room()
    {
        using var client = App.Anonymous();
        var response = await client.GetAsync(Url(F.BenderAndKevin), accept: "application/json");

        Assert.Equal("[]", await response.BodyAsync());
        Assert.Equal("0", response.Headers.GetValues("X-Total-Count").Single());
    }

    [Fact]
    public async Task Index_requires_a_valid_bot_key_and_people_routes_stay_closed_to_bots()
    {
        using var client = App.Anonymous();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(Url(F.Watercooler, "invalid-bot-key"))).StatusCode);
    }

    [Fact]
    public async Task Update_edits_the_bots_own_message()
    {
        using var client = App.Anonymous();
        await PostText(client, Url(F.Watercooler), "Deploying...");
        var message = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();

        var response = await client.SendAsync(HttpMethod.Patch, Url(F.Watercooler, suffix: $"/{message.Id}"), CurlData("Deployed 🚀!"), accept: "*/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Deployed 🚀!", LastPlainText());
        using var json = JsonDocument.Parse(await response.BodyAsync());
        Assert.Equal(message.Id, json.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("Deployed 🚀!", json.RootElement.GetProperty("body").GetProperty("plain_text").GetString());
        Assert.Equal($"http://localhost/rooms/{F.Watercooler.Id}/messages/{message.Id}", json.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Update_and_destroy_cant_touch_other_peoples_messages()
    {
        using var client = App.Anonymous();
        var message = CreateMessage(F.Watercooler, F.Jason, "Original", "original");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Patch, Url(F.Watercooler, suffix: $"/{message.Id}"), CurlData("Hijacked!"), accept: "*/*")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Delete, Url(F.Watercooler, suffix: $"/{message.Id}"), accept: "*/*")).StatusCode);

        var outside = CreateMessage(F.Designers, F.Jason, "Outside", "outside");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Patch, Url(F.Designers, suffix: $"/{outside.Id}"), CurlData("Hijacked!"), accept: "*/*")).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await client.SendAsync(HttpMethod.Patch, Url(F.Watercooler, $"{F.Jz.Id}-", $"/{message.Id}"), CurlData("Hijacked!"), accept: "*/*")).StatusCode);
        Assert.Contains("Original", App.Sql(sql => RichTexts.Find(sql, "Message", message.Id)));
    }

    [Fact]
    public async Task Destroy_deletes_the_bots_own_message()
    {
        using var client = App.Anonymous();
        await PostText(client, Url(F.Watercooler), "Deploying...");
        var message = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();

        var response = await client.SendAsync(HttpMethod.Delete, Url(F.Watercooler, suffix: $"/{message.Id}"), accept: "*/*");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, MessageCount());
    }

    [Fact]
    public async Task Bots_boost_messages_and_get_the_boost_back()
    {
        var message = CreateMessage(F.Watercooler, F.Jason, "Ship it", "ship");
        using var client = App.Anonymous();
        await using var room = await CableProbe.StreamAsync(App, F.Jason, StreamNames.RoomMessages(F.Watercooler.Id), "RoomMessagesChannel");

        var response = await PostText(client, Url(F.Watercooler, suffix: $"/{message.Id}/boosts"), "👀");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.BodyAsync());
        Assert.Equal("👀", json.RootElement.GetProperty("content").GetString());
        Assert.Equal(F.Bender.Id, json.RootElement.GetProperty("booster").GetProperty("id").GetInt64());
        Assert.Equal(message.Id, json.RootElement.GetProperty("message").GetProperty("id").GetInt64());
        Assert.Equal($"http://localhost/rooms/{F.Watercooler.Id}/messages/{message.Id}", json.RootElement.GetProperty("message").GetProperty("url").GetString());

        await room.WaitForAsync();
        Assert.Contains("target=\"boosts_message_ship\"", Assert.Single(room.TurboStreams));

        var boostId = json.RootElement.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, Url(F.Watercooler, suffix: $"/{message.Id}/boosts/{boostId}"), accept: "*/*")).StatusCode);
    }

    [Fact]
    public async Task Boosting_needs_content_a_valid_key_and_a_message_in_the_bots_room()
    {
        var message = CreateMessage(F.Watercooler, F.Jason, "Ship it", "ship");
        var outside = CreateMessage(F.Designers, F.Jason, "Elsewhere", "elsewhere");
        using var client = App.Anonymous();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostText(client, Url(F.Watercooler, suffix: $"/{message.Id}/boosts"), "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostText(client, Url(F.Watercooler, "invalid-bot-key", $"/{message.Id}/boosts"), "👀")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostText(client, Url(F.Watercooler, $"{F.Kevin.Id}-", $"/{message.Id}/boosts"), "👀")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostText(client, Url(F.Designers, suffix: $"/{outside.Id}/boosts"), "👀")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostText(client, Url(F.Watercooler, suffix: $"/{outside.Id}/boosts"), "👀")).StatusCode);
        Assert.Equal(0, App.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM boosts")));
    }

    [Fact]
    public async Task People_routes_stay_closed_to_bot_keys()
    {
        // GET /rooms/:room_id/messages has no {bot_key}, so a bot key in the query authenticates nobody
        using var client = App.Anonymous();
        var response = await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages?bot_key={F.Bender.BotKey}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}

public sealed class BoostsTests : RoomsTest
{
    [Fact]
    public async Task Index_wires_the_new_boost_link_to_the_soft_keyboard()
    {
        var message = CreateMessage(F.Watercooler, F.Jason, "Hi", "hi");
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync($"/messages/{message.Id}/boosts"));

        Assert.NotNull(page.QuerySelector("turbo-frame#boosting_message_hi .message__boost-inline a.boost__action[data-action='soft-keyboard#open']"));
    }

    [Fact]
    public async Task Create_and_destroy_broadcast_to_the_room()
    {
        var message = CreateMessage(F.Watercooler, F.Jason, "Hi", "hi");
        using var client = SignIn(F.David);
        await using var room = await CableProbe.StreamAsync(App, F.Jason, StreamNames.RoomMessages(F.Watercooler.Id), "RoomMessagesChannel");

        var response = await client.PostFormAsync($"/messages/{message.Id}/boosts", ("boost[content]", "Morning!"));
        response.AssertRedirectTo($"/messages/{message.Id}/boosts");
        var boost = App.Sql(sql => Boosts.ForMessages(sql, [message.Id]))[message.Id].Single().Boost;
        Assert.Equal("Morning!", boost.Content);

        var deleted = await client.SendAsync(HttpMethod.Delete, $"/messages/{message.Id}/boosts/{boost.Id}");
        Assert.True(deleted.IsSuccessStatusCode);
        Assert.Empty(App.Sql(sql => Boosts.ForMessages(sql, [message.Id])));

        await room.WaitForAsync(2);
        Assert.Contains("action=\"append\" target=\"boosts_message_hi\"", room.TurboStreams[0]);
        Assert.Contains("Morning!", room.TurboStreams[0]);
        Assert.Contains($"action=\"remove\" target=\"boost_{boost.Id}\"", room.TurboStreams[1]);
    }

    [Fact]
    public async Task People_can_only_remove_their_own_boosts_on_messages_they_can_reach()
    {
        var message = CreateMessage(F.Designers, F.Jason, "Hi", "hi");
        var boost = App.Sql(sql => Boosts.Create(sql, message.Id, F.Jason.Id, "👍"));

        using var david = SignIn(F.David);
        Assert.Equal(HttpStatusCode.NotFound, (await david.SendAsync(HttpMethod.Delete, $"/messages/{message.Id}/boosts/{boost.Id}")).StatusCode);

        using var outsider = SignIn(F.Bender); // not in Designers
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/messages/{message.Id}/boosts")).StatusCode);
    }

    [Fact]
    public async Task New_renders_the_boost_form()
    {
        var message = CreateMessage(F.Watercooler, F.Jason, "Hi", "hi");
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync($"/messages/{message.Id}/boosts/new"));

        Assert.NotNull(page.QuerySelector($"turbo-frame#new_boost_message_hi form[action='/messages/{message.Id}/boosts'] input[name='boost[content]'][maxlength='16']"));
    }
}
