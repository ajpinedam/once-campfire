using System.Text.Json;
using Campfire.Tests.Integrations.Support;
using Campfire.Tests.Support;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Messages;
using Campfire.Web.RichText;
using Campfire.Web.Storage;
using Campfire.Web.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Tests.Integrations;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>Port of test/models/webhook_test.rb, against a real HTTP server on loopback.</summary>
public sealed class WebhookTests : IAsyncLifetime
{
    private readonly CampfireApp _app = new();
    private StubWeb _bot = null!;
    private Message _first = null!;

    public async Task InitializeAsync()
    {
        _bot = await StubWeb.StartAsync();
        var fixtures = _app.Fixtures;
        _app.Sql(sql => Webhooks.Set(sql, fixtures.Bender.Id, _bot.Url("/bender")));
        _first = _app.Sql(sql => Messages.Create(sql, fixtures.Designers.Id, fixtures.Jason.Id, "0001", "First post!", "First post!"));
    }

    public async Task DisposeAsync()
    {
        await _bot.DisposeAsync();
        _app.Dispose();
    }

    private Fixtures F => _app.Fixtures;

    [Fact]
    public async Task Posts_the_rails_payload()
    {
        _bot.Stub("POST", _bot.Url("/bender"), new StubResponse(200));

        var result = await Delivery().DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None);

        Assert.Equal(200, result?.StatusCode);
        var (_, _, body, contentType) = Assert.Single(_bot.Requests);
        Assert.Equal("application/json", contentType);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(["user", "room", "message"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(F.Jason.Id, root.GetProperty("user").GetProperty("id").GetInt64());
        Assert.Equal("Jason", root.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal(F.Designers.Id, root.GetProperty("room").GetProperty("id").GetInt64());
        Assert.Equal("Designers", root.GetProperty("room").GetProperty("name").GetString());
        Assert.Equal($"/rooms/{F.Designers.Id}/{F.Bender.BotKey}/messages", root.GetProperty("room").GetProperty("path").GetString());
        Assert.Equal(_first.Id, root.GetProperty("message").GetProperty("id").GetInt64());
        Assert.Equal("First post!", root.GetProperty("message").GetProperty("body").GetProperty("html").GetString());
        Assert.Equal("First post!", root.GetProperty("message").GetProperty("body").GetProperty("plain").GetString());
        Assert.Equal($"/rooms/{F.Designers.Id}/@{_first.Id}", root.GetProperty("message").GetProperty("path").GetString());
    }

    [Fact]
    public void Plain_text_drops_mentions_of_the_bot_and_surrounding_unicode_space()
    {
        Assert.Equal("what's up?", WebhookDelivery.WithoutRecipientMentions(" @Bender Bot what's up? ", F.Bender));
    }

    [Fact]
    public async Task Posts_a_text_reply_back_into_the_room()
    {
        _bot.Stub("POST", _bot.Url("/bender"), new StubResponse(200, "text/plain", "Hello back!"u8.ToArray()));

        var result = await Delivery().DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None);

        Assert.Equal(WebhookReply.Text, result?.Reply);
        var reply = LastMessage();
        Assert.Equal(F.Bender.Id, reply.CreatorId);
        Assert.Equal("Hello back!", _app.Sql(sql => _app.Service<RichTextService>().ToPlainText(sql, RichTexts.Find(sql, "Message", reply.Id)!)));
    }

    [Fact]
    public async Task Posts_an_attachment_reply_back_into_the_room()
    {
        var moon = await File.ReadAllBytesAsync(FixtureFile("moon.jpg"));
        _bot.Stub("POST", _bot.Url("/bender"), new StubResponse(200, "image/jpeg", moon));

        var result = await Delivery().DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None);

        Assert.Equal(WebhookReply.Attachment, result?.Reply);
        var attachment = _app.Sql(sql => Attachments.Find(sql, "Message", LastMessage().Id, "attachment"));
        Assert.NotNull(attachment);
        Assert.Equal("attachment.jpeg", attachment.Filename);
        Assert.Equal("image/jpeg", attachment.ContentType);
    }

    [Fact]
    public async Task Error_replies_post_nothing()
    {
        _bot.Stub("POST", _bot.Url("/bender"), new StubResponse(500, "text/html", "<h1>Internal Error!</h1>"u8.ToArray()));
        var count = MessageCount();

        var result = await Delivery().DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None);

        Assert.Equal((500, WebhookReply.None), (result?.StatusCode, result?.Reply));
        Assert.Equal(count, MessageCount());
    }

    [Fact]
    public async Task Empty_replies_without_a_content_type_post_nothing()
    {
        _bot.Stub("POST", _bot.Url("/bender"), new StubResponse(200));
        var count = MessageCount();

        await Delivery().DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None);

        Assert.Equal(count, MessageCount());
    }

    [Fact]
    public async Task Slow_bots_get_a_timeout_message()
    {
        _bot.Stub("POST", _bot.Url("/bender"), new StubResponse(200, "text/plain", "too late"u8.ToArray(), Delay: TimeSpan.FromSeconds(3)));

        var result = await Delivery(TimeSpan.FromMilliseconds(300)).DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None);

        Assert.Equal(WebhookReply.TimedOut, result?.Reply);
        Assert.Equal("Failed to respond within 7 seconds",
            _app.Sql(sql => _app.Service<RichTextService>().ToPlainText(sql, RichTexts.Find(sql, "Message", LastMessage().Id)!)));
    }

    [Fact]
    public async Task Bots_without_a_webhook_get_nothing()
    {
        _app.Sql(sql => Webhooks.Set(sql, F.Bender.Id, null));
        Assert.Null(await Delivery().DeliverAsync(F.Bender.Id, _first.Id, "http://campfire.test", CancellationToken.None));
        Assert.Empty(_bot.Requests);
    }

    private WebhookDelivery Delivery(TimeSpan? timeout = null) => new(
        _app.Service<Database>(), _app.Service<RichTextService>(), _app.Service<BlobStore>(), _app.Service<MessagePosting>(),
        NullLogger<WebhookDelivery>.Instance, timeout ?? WebhookDelivery.EndpointTimeout);

    private Message LastMessage() => _app.Sql(sql => sql.First($"SELECT {Rows.MessageColumns()} FROM messages ORDER BY id DESC LIMIT 1", r => Rows.ReadMessage(r)))!;

    private long MessageCount() => _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM messages"));

    // The Rails app's own fixtures, found from this source file (build output may live anywhere).
    private static string FixtureFile(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..", "test", "fixtures", "files", name));
}
