using System.Net;
using System.Text.Json;
using Campfire.Tests.Integrations.Support;
using Campfire.Tests.Support;
using Campfire.Web.Domain;
using Campfire.Web.Push;
using Campfire.Web.RichText;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Tests.Integrations;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>Port of test/models/room/push_test.rb. Each test gets its own app: they count deliveries.</summary>
public sealed class MessagePusherTests : IDisposable
{
    private readonly CampfireApp _app = new();
    private readonly RecordingPushTransport _transport = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Dictionary<long, BrowserPushKeys> _browsers = [];

    public MessagePusherTests()
    {
        var fixtures = _app.Fixtures;
        _factory = IntegrationsApp.With(_app, new FakeHostResolver { Default = [IPAddress.Parse("142.250.185.206")] }, _transport);
        _ = _factory.Services; // start the host now: its boot-time Membership.disconnect_all must not undo a test's setup

        foreach (var user in new[] { fixtures.David, fixtures.Jason, fixtures.Jz, fixtures.Kevin })
        {
            var browser = new BrowserPushKeys();
            _browsers[user.Id] = browser;
            _app.Sql(sql => PushSubscriptions.Create(sql, user.Id, $"https://fcm.googleapis.com/fcm/send/{user.Id}", browser.P256dh, browser.Auth, "Chrome"));
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _app.Dispose();
        foreach (var browser in _browsers.Values) browser.Dispose();
    }

    private Fixtures F => _app.Fixtures;
    private WebPushPool Pool => _factory.Services.GetRequiredService<WebPushPool>();

    [Fact]
    public async Task Delivers_a_new_message_to_the_other_members_with_subscriptions()
    {
        _app.Sql(sql => Memberships.UpdateInvolvement(sql, Memberships.Find(sql, F.Hq.Id, F.Kevin.Id)!.Id, Involvement.Everything));

        await PostAndPush(F.Hq, F.David, "This is from earth");

        await WaitForDeliveries(3);
        var payload = Payload(F.Jason);
        Assert.Equal("HQ", payload.GetProperty("title").GetString());
        Assert.Equal("David: This is from earth", payload.GetProperty("options").GetProperty("body").GetString());
        Assert.Equal($"/rooms/{F.Hq.Id}", payload.GetProperty("options").GetProperty("data").GetProperty("path").GetString());
        Assert.Equal("/account/logo", payload.GetProperty("options").GetProperty("icon").GetString());
    }

    [Fact]
    public async Task Notifies_users_involved_in_everything_and_mentioned_users_involved_in_mentions()
    {
        await PostAndPush(F.Designers, F.David, "This is from earth");
        await WaitForDeliveries(2); // Jason and JZ want everything; Kevin only mentions

        var mention = Mention(F.Kevin);
        await PostAndPush(F.Designers, F.David, $"Hey {mention}");
        await WaitForDeliveries(5);

        Assert.Equal(2, _transport.Sent.Count(push => push.Request.Endpoint.AbsolutePath.EndsWith($"/{F.Jason.Id}", StringComparison.Ordinal)));
        Assert.Single(_transport.Sent, push => push.Request.Endpoint.AbsolutePath.EndsWith($"/{F.Kevin.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_not_notify_members_connected_to_the_room()
    {
        _app.Sql(sql => Memberships.Present(sql, Memberships.Find(sql, F.Designers.Id, F.Kevin.Id)!));

        await PostAndPush(F.Designers, F.David, $"Hey {Mention(F.Kevin)}");

        await WaitForDeliveries(2);
        Assert.DoesNotContain(_transport.Sent, push => push.Request.Endpoint.AbsolutePath.EndsWith($"/{F.Kevin.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_not_notify_for_invisible_rooms()
    {
        _app.Sql(sql => Memberships.UpdateInvolvement(sql, Memberships.Find(sql, F.Designers.Id, F.Kevin.Id)!.Id, Involvement.Invisible));

        await PostAndPush(F.Designers, F.David, $"Hey {Mention(F.Kevin)}");

        await WaitForDeliveries(2);
        Assert.DoesNotContain(_transport.Sent, push => push.Request.Endpoint.AbsolutePath.EndsWith($"/{F.Kevin.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Destroys_subscriptions_the_push_service_reports_gone()
    {
        _transport.Status = 410;
        var before = _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM push_subscriptions"));

        await PostAndPush(F.Designers, F.David, "This is from earth");

        await WaitForDeliveries(2);
        await IntegrationsApp.WaitUntil(() => Pool.CompletedInvalidations >= 2);
        Assert.Equal(before - 2, _app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM push_subscriptions")));
    }

    [Fact]
    public async Task Direct_rooms_are_titled_with_the_author()
    {
        await PostAndPush(F.DavidAndKevin, F.David, "psst");

        await WaitForDeliveries(1);
        var payload = Payload(F.Kevin);
        Assert.Equal("David", payload.GetProperty("title").GetString());
        Assert.Equal("psst", payload.GetProperty("options").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Badges_count_the_recipients_unread_rooms()
    {
        _app.Sql(sql => Memberships.MarkUnread(sql, F.Hq.Id, F.David.Id, DateTime.UtcNow));
        _app.Sql(sql => Memberships.MarkUnread(sql, F.Pets.Id, F.David.Id, DateTime.UtcNow));

        await PostAndPush(F.DavidAndJason, F.David, "badge me");

        await WaitForDeliveries(1);
        Assert.Equal(2, Payload(F.Jason).GetProperty("options").GetProperty("data").GetProperty("badge").GetInt64());
    }

    private Task PostAndPush(Room room, User creator, string body)
    {
        var richText = _factory.Services.GetRequiredService<RichTextService>();
        var message = _app.Sql(sql => Messages.Create(sql, room.Id, creator.Id, null, body, richText.ToPlainText(sql, body)));
        _factory.Services.GetRequiredService<MessagePusher>().Push(room.Id, message.Id);
        return Task.CompletedTask;
    }

    private string Mention(User user)
    {
        var sgid = _factory.Services.GetRequiredService<RichTextService>().UserSgid(user.Id);
        return $"<action-text-attachment sgid=\"{sgid}\" content-type=\"application/vnd.campfire.mention\"></action-text-attachment>";
    }

    private async Task WaitForDeliveries(int count)
    {
        await IntegrationsApp.WaitUntil(() => Pool.CompletedDeliveries >= count);
        await Task.Delay(50); // nothing beyond the expected count should follow
        Assert.Equal(count, Pool.CompletedDeliveries);
    }

    private JsonElement Payload(User recipient)
    {
        var push = _transport.Sent.Last(push => push.Request.Endpoint.AbsolutePath.EndsWith($"/{recipient.Id}", StringComparison.Ordinal));
        return JsonDocument.Parse(_browsers[recipient.Id].Decrypt(push.Request.Body)).RootElement.Clone();
    }
}
