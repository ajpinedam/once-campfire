using System.Net;
using System.Text.Json;
using Campfire.Tests.Support;
using Campfire.Web.Cable;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Security;
using Campfire.Web.Turbo;
using static Campfire.Tests.Cable.CableTestClient;

namespace Campfire.Tests.Cable;

/// <summary>Connection-level behavior: handshake, authentication, heartbeat, remote disconnects.</summary>
public sealed class CableConnectionTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    [Fact]
    public async Task Welcomes_signed_in_users_and_pings_every_few_seconds()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.David);
        await cable.ExpectWelcomeAsync();

        var ping = await cable.ReceiveAsync(skipPings: false);
        Assert.Equal("ping", Type(ping!.Value));
        Assert.Equal(JsonValueKind.Number, ping.Value.GetProperty("message").ValueKind);
        Assert.Equal("actioncable-v1-json", cable.Socket.SubProtocol);
    }

    [Fact]
    public async Task Rejects_connections_without_a_session()
    {
        _ = app.Fixtures;
        await using var cable = await ConnectAsync(app, user: null);

        var message = await cable.ExpectAsync();
        Assert.Equal("disconnect", Type(message));
        Assert.Equal("unauthorized", message.GetProperty("reason").GetString());
        Assert.False(message.GetProperty("reconnect").GetBoolean());
        Assert.Null(await cable.ReceiveAsync());
    }

    [Fact]
    public async Task Rejects_connections_with_a_forged_session_cookie()
    {
        _ = app.Fixtures;
        var client = app.Server.CreateWebSocketClient();
        client.SubProtocols.Add("actioncable-v1-json");
        client.ConfigureRequest = request =>
        {
            request.Headers.Origin = "http://localhost";
            request.Headers.Cookie = "session_token=forged--signature";
        };
        await using var cable = Wrap(await client.ConnectAsync(new Uri(app.Server.BaseAddress, "cable"), CancellationToken.None));

        Assert.Equal("unauthorized", (await cable.ExpectAsync()).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Refuses_cross_site_handshakes()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(app, app.Fixtures.David, origin: "https://evil.example"));
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_plain_http_requests_with_upgrade_required()
    {
        using var client = app.Anonymous();
        var response = await client.GetAsync("/cable", accept: "*/*");
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task Disconnects_a_users_connections_remotely_and_only_theirs()
    {
        var fixtures = app.Fixtures;
        await using var david = await ConnectAsync(app, fixtures.David);
        await using var kevin = await ConnectAsync(app, fixtures.Kevin);
        await david.ExpectWelcomeAsync();
        await kevin.ExpectWelcomeAsync();

        var heartbeat = Identifier("HeartbeatChannel");
        await kevin.SubscribeAndExpectAsync(heartbeat, "confirm_subscription");

        app.Service<CableServer>().DisconnectUser(fixtures.David.Id, reconnect: true);

        var notice = await david.ExpectAsync();
        Assert.Equal("disconnect", Type(notice));
        Assert.Equal("remote", notice.GetProperty("reason").GetString());
        Assert.True(notice.GetProperty("reconnect").GetBoolean());
        Assert.Null(await david.ReceiveAsync());

        // Kevin's connection is untouched and still talking
        await kevin.UnsubscribeAsync(heartbeat);
        await kevin.SubscribeAndExpectAsync(heartbeat, "confirm_subscription");
    }

    [Fact]
    public async Task Ignores_repeated_subscribes_to_the_same_identifier()
    {
        var fixtures = app.Fixtures;
        await using var cable = await ConnectAsync(app, fixtures.Jz);
        await cable.ExpectWelcomeAsync();

        var unreads = Identifier("UnreadRoomsChannel");
        await cable.SubscribeAndExpectAsync(unreads, "confirm_subscription");
        await cable.SubscribeAsync(unreads);

        app.Service<CableServer>().Broadcast(StreamNames.UserUnreads(fixtures.Jz.Id), """{"roomId":1}""");

        var message = await cable.ExpectAsync();
        Assert.Equal(1, message.GetProperty("message").GetProperty("roomId").GetInt32());
        Assert.Equal(1, app.Service<CableServer>().SubscriberCount(StreamNames.UserUnreads(fixtures.Jz.Id)));
    }

    [Fact]
    public async Task Rejects_unknown_channels_and_malformed_identifiers()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.David);
        await cable.ExpectWelcomeAsync();

        await cable.SubscribeAndExpectAsync(Identifier("ApplicationCable::Channel"), "reject_subscription");
        await cable.SubscribeAndExpectAsync("""{"room_id":1}""", "reject_subscription");
        await cable.SubscribeAndExpectAsync("not json", "reject_subscription");
    }

    [Fact]
    public async Task Drops_subscriptions_when_the_socket_closes()
    {
        var fixtures = app.Fixtures;
        var stream = StreamNames.UserReads(fixtures.Jason.Id);
        var server = app.Service<CableServer>();

        var cable = await ConnectAsync(app, fixtures.Jason);
        await cable.ExpectWelcomeAsync();
        await cable.SubscribeAndExpectAsync(Identifier("ReadRoomsChannel"), "confirm_subscription");
        Assert.Equal(1, server.SubscriberCount(stream));

        await cable.DisposeAsync();

        await Eventually(() => server.SubscriberCount(stream) == 0);
    }

    internal static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }
}

/// <summary>Turbo::StreamsChannel and RoomMessagesChannel: who may stream what.</summary>
public sealed class CableStreamTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    private string Signed(string stream) => app.Service<KeyRing>().StreamNames.Sign(stream);

    private string TurboIdentifier(string signedStreamName) =>
        Identifier("Turbo::StreamsChannel", new { signed_stream_name = signedStreamName });

    private string RoomMessagesIdentifier(string signedStreamName) =>
        Identifier("RoomMessagesChannel", new { signed_stream_name = signedStreamName });

    [Fact]
    public async Task Stock_turbo_channel_serves_the_rooms_list()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.Kevin);
        await cable.ExpectWelcomeAsync();
        await cable.SubscribeAndExpectAsync(TurboIdentifier(Signed(StreamNames.Rooms)), "confirm_subscription");
    }

    [Fact]
    public async Task Stock_turbo_channel_rejects_tampered_unsigned_and_missing_names()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.Kevin);
        await cable.ExpectWelcomeAsync();

        var signed = Signed(StreamNames.UserRooms(app.Fixtures.David.Id));
        await cable.SubscribeAndExpectAsync(TurboIdentifier(signed[..^2] + "xx"), "reject_subscription");
        await cable.SubscribeAndExpectAsync(TurboIdentifier(StreamNames.Rooms), "reject_subscription");
        await cable.SubscribeAndExpectAsync(Identifier("Turbo::StreamsChannel"), "reject_subscription");
    }

    [Fact]
    public async Task Stock_turbo_channel_refuses_to_serve_a_room_message_stream()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.Kevin);
        await cable.ExpectWelcomeAsync();
        await cable.SubscribeAndExpectAsync(TurboIdentifier(Signed(StreamNames.RoomMessages(app.Fixtures.Designers.Id))), "reject_subscription");
    }

    [Fact]
    public async Task A_member_may_subscribe_to_a_rooms_message_stream()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.Kevin);
        await cable.ExpectWelcomeAsync();
        await cable.SubscribeAndExpectAsync(RoomMessagesIdentifier(Signed(StreamNames.RoomMessages(app.Fixtures.Designers.Id))), "confirm_subscription");
    }

    [Fact]
    public async Task Non_members_unsigned_and_missing_names_are_rejected_by_room_messages()
    {
        var fixtures = app.Fixtures;
        await using var bender = await ConnectAsync(app, fixtures.Bender);
        await bender.ExpectWelcomeAsync();

        await bender.SubscribeAndExpectAsync(RoomMessagesIdentifier(Signed(StreamNames.RoomMessages(fixtures.Designers.Id))), "reject_subscription");
        await bender.SubscribeAndExpectAsync(RoomMessagesIdentifier(Signed(StreamNames.RoomMessages(fixtures.Hq.Id))), "reject_subscription");
        await bender.SubscribeAndExpectAsync(RoomMessagesIdentifier(StreamNames.RoomMessages(fixtures.Watercooler.Id)), "reject_subscription");
        await bender.SubscribeAndExpectAsync(Identifier("RoomMessagesChannel"), "reject_subscription");
        await bender.SubscribeAndExpectAsync(RoomMessagesIdentifier(Signed(StreamNames.Rooms)), "reject_subscription");
    }

    [Fact]
    public async Task A_revoked_member_cannot_resubscribe_with_a_harvested_stream_name()
    {
        using var isolated = new CampfireApp();
        var fixtures = isolated.Fixtures;
        var signed = isolated.Service<KeyRing>().StreamNames.Sign(StreamNames.RoomMessages(fixtures.Designers.Id));
        var identifier = RoomMessagesIdentifier(signed);

        await using (var before = await ConnectAsync(isolated, fixtures.Kevin))
        {
            await before.ExpectWelcomeAsync();
            await before.SubscribeAndExpectAsync(identifier, "confirm_subscription");
        }

        isolated.Sql(sql => Memberships.RevokeFrom(sql, fixtures.Designers.Id, [fixtures.Kevin.Id]));

        await using var after = await ConnectAsync(isolated, fixtures.Kevin);
        await after.ExpectWelcomeAsync();
        await after.SubscribeAndExpectAsync(identifier, "reject_subscription");
    }

    [Fact]
    public async Task Broadcasts_reach_only_the_streams_subscribers_in_order()
    {
        var fixtures = app.Fixtures;
        var server = app.Service<CableServer>();
        await using var david = await ConnectAsync(app, fixtures.David);
        await using var kevin = await ConnectAsync(app, fixtures.Kevin);
        await david.ExpectWelcomeAsync();
        await kevin.ExpectWelcomeAsync();

        var rooms = TurboIdentifier(Signed(StreamNames.Rooms));
        var davidsRooms = TurboIdentifier(Signed(StreamNames.UserRooms(fixtures.David.Id)));
        await david.SubscribeAndExpectAsync(rooms, "confirm_subscription");
        await david.SubscribeAndExpectAsync(davidsRooms, "confirm_subscription");
        await kevin.SubscribeAndExpectAsync(rooms, "confirm_subscription");

        var personal = TurboStream.Prepend("direct_rooms", "<a href=\"/rooms/9\">Ping with Jason & “Kevin” ✓</a>");
        var shared = TurboStream.Remove("list_room_3");
        server.BroadcastTurboStream(StreamNames.UserRooms(fixtures.David.Id), personal);
        server.BroadcastTurboStream(StreamNames.Rooms, shared);

        var first = await david.ExpectAsync();
        Assert.Equal(davidsRooms, first.GetProperty("identifier").GetString());
        Assert.Equal(personal, first.GetProperty("message").GetString());
        Assert.Equal(shared, (await david.ExpectAsync()).GetProperty("message").GetString());

        var kevins = await kevin.ExpectAsync();
        Assert.Equal(rooms, kevins.GetProperty("identifier").GetString());
        Assert.Equal(shared, kevins.GetProperty("message").GetString());
    }
}

/// <summary>PresenceChannel, TypingNotificationsChannel, ReadRoomsChannel and UnreadRoomsChannel.</summary>
public sealed class CableRoomChannelTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    private Membership MembershipOf(User user, Room room) => app.Sql(sql => Memberships.Find(sql, room.Id, user.Id))!;

    [Fact]
    public async Task Presence_marks_the_membership_connected_and_read_and_tells_the_users_other_tabs()
    {
        var fixtures = app.Fixtures;
        var designers = fixtures.Designers;
        app.Sql(sql => sql.Execute("UPDATE memberships SET unread_at = @now, connected_at = NULL, connections = 0 WHERE id = @id",
            ("@now", DateTime.UtcNow), ("@id", MembershipOf(fixtures.David, designers).Id)));
        Assert.True(MembershipOf(fixtures.David, designers).IsUnread);

        await using var cable = await ConnectAsync(app, fixtures.David);
        await cable.ExpectWelcomeAsync();
        var reads = Identifier("ReadRoomsChannel");
        await cable.SubscribeAndExpectAsync(reads, "confirm_subscription");

        var presence = Identifier("PresenceChannel", new { room_id = designers.Id });
        await cable.SubscribeAsync(presence);

        var read = await cable.ExpectAsync(m => m.TryGetProperty("message", out _), "expected the read broadcast");
        Assert.Equal(reads, read.GetProperty("identifier").GetString());
        Assert.Equal(designers.Id, read.GetProperty("message").GetProperty("room_id").GetInt64());
        await cable.ExpectAsync(m => Type(m) == "confirm_subscription", "expected the presence confirmation");

        var membership = MembershipOf(fixtures.David, designers);
        Assert.True(membership.IsConnected(DateTime.UtcNow));
        Assert.False(membership.IsUnread);
        Assert.Equal(1, membership.Connections);

        await cable.PerformAsync(presence, "absent");
        await CableConnectionTests.Eventually(() => !MembershipOf(fixtures.David, designers).IsConnected(DateTime.UtcNow));

        await cable.PerformAsync(presence, "present");
        await CableConnectionTests.Eventually(() => MembershipOf(fixtures.David, designers).IsConnected(DateTime.UtcNow));

        await cable.UnsubscribeAsync(presence);
        await CableConnectionTests.Eventually(() => !MembershipOf(fixtures.David, designers).IsConnected(DateTime.UtcNow));
    }

    [Fact]
    public async Task Closing_the_socket_marks_presence_disconnected()
    {
        var fixtures = app.Fixtures;
        var hq = fixtures.Hq;

        var cable = await ConnectAsync(app, fixtures.Jz);
        await cable.ExpectWelcomeAsync();
        await cable.SubscribeAndExpectAsync(Identifier("PresenceChannel", new { room_id = hq.Id }), "confirm_subscription");
        Assert.True(MembershipOf(fixtures.Jz, hq).IsConnected(DateTime.UtcNow));

        await cable.DisposeAsync();
        await CableConnectionTests.Eventually(() => !MembershipOf(fixtures.Jz, hq).IsConnected(DateTime.UtcNow));
    }

    [Fact]
    public async Task Presence_and_typing_reject_rooms_the_user_isnt_in()
    {
        var fixtures = app.Fixtures;
        await using var cable = await ConnectAsync(app, fixtures.Kevin);
        await cable.ExpectWelcomeAsync();

        await cable.SubscribeAndExpectAsync(Identifier("PresenceChannel", new { room_id = fixtures.Watercooler.Id }), "reject_subscription");
        await cable.SubscribeAndExpectAsync(Identifier("PresenceChannel", new { room_id = -1 }), "reject_subscription");
        await cable.SubscribeAndExpectAsync(Identifier("PresenceChannel"), "reject_subscription");
        await cable.SubscribeAndExpectAsync(Identifier("TypingNotificationsChannel", new { room_id = fixtures.Watercooler.Id }), "reject_subscription");
    }

    [Fact]
    public async Task Typing_notifications_reach_everyone_in_the_room()
    {
        var fixtures = app.Fixtures;
        var typing = Identifier("TypingNotificationsChannel", new { room_id = fixtures.Designers.Id });
        await using var david = await ConnectAsync(app, fixtures.David);
        await using var jason = await ConnectAsync(app, fixtures.Jason);
        await david.ExpectWelcomeAsync();
        await jason.ExpectWelcomeAsync();
        await david.SubscribeAndExpectAsync(typing, "confirm_subscription");
        await jason.SubscribeAndExpectAsync(typing, "confirm_subscription");

        await david.PerformAsync(typing, "start");

        foreach (var cable in new[] { jason, david })
        {
            var message = (await cable.ExpectAsync()).GetProperty("message");
            Assert.Equal("start", message.GetProperty("action").GetString());
            Assert.Equal(fixtures.David.Id, message.GetProperty("user").GetProperty("id").GetInt64());
            Assert.Equal("David", message.GetProperty("user").GetProperty("name").GetString());
        }

        await david.PerformAsync(typing, "stop");
        Assert.Equal("stop", (await jason.ExpectAsync()).GetProperty("message").GetProperty("action").GetString());
    }

    [Fact]
    public async Task Unread_rooms_stream_only_the_subscribers_own_activity()
    {
        var fixtures = app.Fixtures;
        var server = app.Service<CableServer>();
        await using var kevin = await ConnectAsync(app, fixtures.Kevin);
        await using var jz = await ConnectAsync(app, fixtures.Jz);
        await kevin.ExpectWelcomeAsync();
        await jz.ExpectWelcomeAsync();

        var unreads = Identifier("UnreadRoomsChannel");
        await kevin.SubscribeAndExpectAsync(unreads, "confirm_subscription");
        await jz.SubscribeAndExpectAsync(unreads, "confirm_subscription");

        server.Broadcast(StreamNames.UserUnreads(fixtures.Kevin.Id), $$"""{"roomId":{{fixtures.BenderAndKevin.Id}}}""");
        server.Broadcast(StreamNames.UserUnreads(fixtures.Jz.Id), $$"""{"roomId":{{fixtures.Hq.Id}}}""");

        Assert.Equal(fixtures.BenderAndKevin.Id, (await kevin.ExpectAsync()).GetProperty("message").GetProperty("roomId").GetInt64());
        Assert.Equal(fixtures.Hq.Id, (await jz.ExpectAsync()).GetProperty("message").GetProperty("roomId").GetInt64());
    }

    [Fact]
    public async Task Heartbeat_confirms_without_streaming()
    {
        await using var cable = await ConnectAsync(app, app.Fixtures.Kevin);
        await cable.ExpectWelcomeAsync();
        await cable.SubscribeAndExpectAsync(Identifier("HeartbeatChannel"), "confirm_subscription");
    }
}
