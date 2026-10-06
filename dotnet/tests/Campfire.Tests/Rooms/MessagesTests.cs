namespace Campfire.Tests.Rooms;

using System.Net;
using Campfire.Tests.Support;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Jobs;
using Campfire.Web.Turbo;

public sealed class MessagesTests : RoomsTest
{
    private const string TurboStream = "text/vnd.turbo-stream.html";

    /// <summary>A 4x3 red PNG.</summary>
    internal const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAEElEQVR4nGP4z8AARww4OQD1MQv1NXv7ggAAAABJRU5ErkJggg==";

    [Fact]
    public async Task Index_returns_the_last_page_by_default()
    {
        using var client = SignIn(F.David);
        var messages = CreateMessages(F.Watercooler, F.Jason, 5);

        var response = await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ParseAsync(response);
        Assert.All(messages, message => Assert.NotNull(page.QuerySelector($"#message_{message.ClientMessageId}")));
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Index_pages_before_and_after_a_message()
    {
        using var client = SignIn(F.David);
        var messages = CreateMessages(F.Watercooler, F.Jason, 5);

        var before = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages?before={messages[2].Id}"));
        Assert.Equal(new[] { messages[0].ClientMessageId, messages[1].ClientMessageId }, Ids(before));

        var after = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages?after={messages[2].Id}"));
        Assert.Equal(new[] { messages[3].ClientMessageId, messages[4].ClientMessageId }, Ids(after));
    }

    [Fact]
    public async Task Index_answers_no_content_when_there_are_no_messages_and_not_modified_when_unchanged()
    {
        using var client = SignIn(F.David);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages")).StatusCode);

        CreateMessages(F.Watercooler, F.Jason, 2);
        var first = await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages");
        var request = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{F.Watercooler.Id}/messages");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Cached_pages_are_identical_and_show_edits_immediately()
    {
        using var client = SignIn(F.David);
        var messages = CreateMessages(F.Watercooler, F.Jason, 5);

        var original = await (await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages")).BodyAsync();
        Assert.Equal(original, await (await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages")).BodyAsync());

        App.Sql(sql => Messages.UpdateBody(sql, messages[3], "<div>Updated cached message</div>", "Updated cached message"));
        var updated = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages"));
        Assert.Contains("Updated cached message", updated.QuerySelector($"#message_{messages[3].ClientMessageId}")!.TextContent);
    }

    [Fact]
    public async Task Show_renders_a_single_message()
    {
        using var client = SignIn(F.David);
        var message = CreateMessage(F.Watercooler, F.David, "Mine", "mine");

        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages/{message.Id}"));
        Assert.NotNull(page.QuerySelector($"#message_mine turbo-frame#edit_message_mine"));
    }

    [Fact]
    public async Task Create_broadcasts_the_message_to_the_room_with_its_permalink()
    {
        using var client = SignIn(F.David);
        await using var room = await CableProbe.StreamAsync(App, F.Jason, StreamNames.RoomMessages(F.Watercooler.Id), "RoomMessagesChannel");

        var response = await client.PostFormAsync($"/rooms/{F.Watercooler.Id}/messages",
            [KeyValuePair.Create("message[body]", "<p>New one</p>"), KeyValuePair.Create("message[client_message_id]", "999")], accept: TurboStream);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();
        Assert.Equal("999", created.ClientMessageId);

        // The response appends the message for the sender (the file uploader renders it as a stream)
        var reply = await ParseAsync(response);
        Assert.Equal($"messages_room_{F.Watercooler.Id}", reply.QuerySelector("turbo-stream[action='append']")?.GetAttribute("target"));

        await room.WaitForAsync();
        var broadcast = Assert.Single(room.TurboStreams);
        Assert.StartsWith($"<turbo-stream action=\"append\" target=\"messages_room_{F.Watercooler.Id}\"><template>", broadcast);
        Assert.Contains("New one", broadcast);
        Assert.Contains($"id=\"message_999\"", broadcast);
        Assert.Contains($"data-copy-to-clipboard-content-value=\"http://localhost/rooms/{F.Watercooler.Id}/@{created.Id}\"", broadcast);
    }

    [Fact]
    public async Task Create_pings_members_unread_streams_and_nobody_elses()
    {
        using var client = SignIn(F.David);
        await using var jason = await CableProbe.ChannelAsync(App, F.Jason, "UnreadRoomsChannel");
        await using var kevin = await CableProbe.ChannelAsync(App, F.Kevin, "UnreadRoomsChannel");

        await client.PostFormAsync($"/rooms/{F.Watercooler.Id}/messages",
            [KeyValuePair.Create("message[body]", "<p>Hi</p>"), KeyValuePair.Create("message[client_message_id]", "1")], accept: TurboStream);

        var ping = Assert.Single(await jason.WaitForAsync());
        Assert.Equal(F.Watercooler.Id, ping.GetProperty("roomId").GetInt64());
        Assert.Empty(await kevin.SettleAsync());
    }

    [Fact]
    public async Task Create_marks_the_room_unread_for_disconnected_members_and_queues_pushes()
    {
        using var client = SignIn(F.David);

        await client.PostFormAsync($"/rooms/{F.Watercooler.Id}/messages",
            [KeyValuePair.Create("message[body]", "<p>Hi</p>"), KeyValuePair.Create("message[client_message_id]", "1")], accept: TurboStream);

        Assert.True(App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.Jason.Id))!.IsUnread);
        Assert.False(App.Sql(sql => Memberships.Find(sql, F.Watercooler.Id, F.David.Id))!.IsUnread);
    }

    [Fact]
    public async Task Create_in_a_room_that_went_away_answers_with_the_room_not_found_frame()
    {
        using var client = SignIn(F.Kevin);

        var response = await client.PostFormAsync($"/rooms/{F.Watercooler.Id}/messages",
            [KeyValuePair.Create("message[body]", "<p>Hi</p>")], accept: TurboStream);

        Assert.Contains("This room was deleted.", await response.BodyAsync());
        Assert.Equal(0, MessageCount());
    }

    [Fact]
    public async Task Update_changes_the_body_for_its_author_and_administrators()
    {
        var own = CreateMessage(F.Watercooler, F.David, "Mine", "mine");
        var others = CreateMessage(F.Watercooler, F.Jason, "Jason's", "jasons");
        using var client = SignIn(F.David);
        await using var room = await CableProbe.StreamAsync(App, F.Jason, StreamNames.RoomMessages(F.Watercooler.Id), "RoomMessagesChannel");

        var response = await client.SubmitAsync("put", $"/rooms/{F.Watercooler.Id}/messages/{own.Id}", ("message[body]", "<p>Updated body</p>"));
        response.AssertRedirectTo($"/rooms/{F.Watercooler.Id}/messages/{own.Id}");
        Assert.Contains("Updated body", App.Sql(sql => RichTexts.Find(sql, "Message", own.Id)));

        await client.SubmitAsync("put", $"/rooms/{F.Watercooler.Id}/messages/{others.Id}", ("message[body]", "<p>Admin edit</p>"));
        Assert.Contains("Admin edit", App.Sql(sql => RichTexts.Find(sql, "Message", others.Id)));

        await room.WaitForAsync(2);
        Assert.Contains("action=\"replace\" target=\"presentation_message_mine\"", room.TurboStreams[0]);
        Assert.Contains("maintain_scroll", room.TurboStreams[0]);
    }

    [Fact]
    public async Task Members_cant_edit_or_delete_other_peoples_messages()
    {
        var message = CreateMessage(F.Designers, F.Jason, "Jason's", "jasons");
        using var client = SignIn(F.Jz);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SubmitAsync("put", $"/rooms/{F.Designers.Id}/messages/{message.Id}", ("message[body]", "Hijacked"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Delete, $"/rooms/{F.Designers.Id}/messages/{message.Id}", accept: TurboStream)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/rooms/{F.Designers.Id}/messages/{message.Id}/edit")).StatusCode);
        Assert.NotNull(App.Sql(sql => Messages.Find(sql, message.Id)));
    }

    [Fact]
    public async Task Destroy_removes_the_message_and_broadcasts_its_removal()
    {
        var message = CreateMessage(F.Watercooler, F.Jason, "Jason's", "jasons");
        using var client = SignIn(F.David);
        await using var room = await CableProbe.StreamAsync(App, F.Jason, StreamNames.RoomMessages(F.Watercooler.Id), "RoomMessagesChannel");

        var response = await client.SendAsync(HttpMethod.Delete, $"/rooms/{F.Watercooler.Id}/messages/{message.Id}", accept: TurboStream);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"remove\" target=\"message_jasons\"", await response.BodyAsync());
        Assert.Null(App.Sql(sql => Messages.Find(sql, message.Id)));
        await room.WaitForAsync();
        Assert.Contains("action=\"remove\" target=\"message_jasons\"", Assert.Single(room.TurboStreams));
    }

    [Fact]
    public async Task Edit_renders_the_editor_with_the_message_body()
    {
        var message = CreateMessage(F.Watercooler, F.David, "Draft words", "draft");
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}/messages/{message.Id}/edit"));

        var editor = page.QuerySelector("turbo-frame#edit_message_draft form#form_message_draft lexxy-editor[name='message[body]']");
        Assert.NotNull(editor);
        Assert.Contains("Draft words", editor!.GetAttribute("value"));
        Assert.NotNull(page.QuerySelector("form#delete_form_message_draft input[name='_method'][value='delete']"));
    }

    [Fact]
    public async Task Mentioning_a_bot_queues_its_webhook()
    {
        App.Sql(sql => Web.Data.Queries.Webhooks.Set(sql, F.Bender.Id, "https://example.com/bender"));
        using var client = SignIn(F.David);
        var sgid = App.Service<Web.RichText.RichTextService>().UserSgid(F.Bender.Id);
        var mention = $"<action-text-attachment sgid=\"{sgid}\" content-type=\"application/vnd.campfire.mention\"></action-text-attachment>";

        var response = await client.PostFormAsync($"/rooms/{F.Watercooler.Id}/messages",
            [KeyValuePair.Create("message[body]", $"<div>Hey {mention}</div>"), KeyValuePair.Create("message[client_message_id]", "999")], accept: TurboStream);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { F.Bender.Id }, App.Service<Web.RichText.RichTextService>().MentionedUserIds(
            App.Sql(sql => RichTexts.Find(sql, "Message", Messages.LastPage(sql, F.Watercooler.Id).Last().Id))!));
    }

    [Fact]
    public async Task Attachments_upload_as_their_own_messages()
    {
        using var client = SignIn(F.David);
        var image = Convert.FromBase64String(PixelPng);
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(image) { Headers = { ContentType = new("image/png") } }, "message[attachment]", "pixel.png" },
            { new StringContent("upload-1"), "message[client_message_id]" }
        };

        var response = await client.SendAsync(HttpMethod.Post, $"/rooms/{F.Watercooler.Id}/messages", form, accept: "*/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = App.Sql(sql => Messages.LastPage(sql, F.Watercooler.Id)).Last();
        Assert.Equal("upload-1", message.ClientMessageId);
        var blob = App.Sql(sql => Attachments.Find(sql, "Message", message.Id, "attachment"));
        Assert.Equal("pixel.png", blob?.Filename);
        Assert.Single(App.Sql(sql => Messages.Search(sql, F.David.Id, "pixel")));

        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}"));
        Assert.NotNull(page.QuerySelector("#message_upload-1 a[data-action='lightbox#open'] img.message__attachment"));
    }

    [Fact]
    public async Task Sounds_render_their_player()
    {
        CreateMessage(F.Watercooler, F.David, "/play tada", "tada");
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}"));

        var sound = page.QuerySelector("#message_tada .sound[data-controller='sound']");
        Assert.NotNull(sound);
        Assert.Contains("plays a fanfare", sound!.TextContent);
    }

    [Fact]
    public async Task Emoji_only_messages_are_marked()
    {
        CreateMessage(F.Watercooler, F.David, "😄🤘", "emoji");
        CreateMessage(F.Watercooler, F.David, "Haha! 😄🤘", "words");
        using var client = SignIn(F.David);

        var page = await ParseAsync(await client.GetAsync($"/rooms/{F.Watercooler.Id}"));

        Assert.NotNull(page.QuerySelector("#message_emoji.message--emoji"));
        Assert.Null(page.QuerySelector("#message_words.message--emoji"));
    }

    private static string[] Ids(AngleSharp.Html.Dom.IHtmlDocument page) =>
        page.QuerySelectorAll(".message[data-message-id]").Select(element => element.Id!["message_".Length..]).ToArray();
}
