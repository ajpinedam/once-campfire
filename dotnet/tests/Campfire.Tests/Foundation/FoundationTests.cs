using Campfire.Tests.Support;

namespace Campfire.Tests.Foundation;

// Inside the namespace, so sibling test namespaces (Campfire.Tests.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.Security;
using Campfire.Web.Views;

public sealed class FoundationTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    [Fact]
    public void Fixtures_seed_into_an_isolated_database()
    {
        var fixtures = app.Fixtures;
        Assert.StartsWith(app.StoragePath, app.Database.Path);
        Assert.Equal("David", app.Sql(sql => Users.Find(sql, fixtures.David.Id))!.Name);
        Assert.Equal(4, app.Sql(sql => Rooms.UserIds(sql, fixtures.Designers.Id)).Count);
    }

    [Fact]
    public void Authenticates_with_bcrypt_and_rejects_wrong_passwords()
    {
        var fixtures = app.Fixtures;
        Assert.Equal(fixtures.David.Id, app.Sql(sql => Users.Authenticate(sql, "david@37signals.com", CampfireApp.Password))?.Id);
        Assert.Null(app.Sql(sql => Users.Authenticate(sql, "david@37signals.com", "wrong")));
        Assert.Null(app.Sql(sql => Users.Authenticate(sql, "nobody@example.com", CampfireApp.Password)));
    }

    [Fact]
    public void Authenticates_bots_by_key()
    {
        var bender = app.Fixtures.Bender;
        Assert.Equal(bender.Id, app.Sql(sql => Users.AuthenticateBot(sql, bender.BotKey))?.Id);
        Assert.Null(app.Sql(sql => Users.AuthenticateBot(sql, $"{bender.Id}-wrong")));
    }

    [Fact]
    public void Times_round_trip_in_rails_storage_format()
    {
        var time = new DateTime(2026, 10, 5, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);
        Assert.Equal("2026-10-05 12:34:56.123456", SqlTime.Format(time));
        Assert.Equal(time, SqlTime.Parse("2026-10-05 12:34:56.123456"));
        Assert.Equal(time.AddTicks(-1234560), SqlTime.Parse("2026-10-05 12:34:56"));
    }

    [Fact]
    public void Signed_messages_always_round_trip_even_when_base64url_contains_double_dashes()
    {
        // base64url uses '-', so payloads and digests can contain "--"; verification must not care.
        var signer = new MessageSigner(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        for (var i = 0; i < 10_000; i++)
        {
            var value = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(i % 40 + 1)) + "--x-";
            var signed = signer.Sign(value);
            Assert.True(signer.TryVerify(signed, out var verified), $"failed to verify round {i}");
            Assert.Equal(value, verified);
        }

        var good = signer.Sign("token");
        Assert.False(signer.TryVerify(good[..^1] + (good[^1] == 'A' ? 'B' : 'A'), out _));
        Assert.False(signer.TryVerify("x" + good, out _));
        Assert.False(signer.TryVerify("--", out _));
        Assert.False(signer.TryVerify("", out _));
    }

    [Fact]
    public void Signed_ids_are_purpose_bound_and_expire()
    {
        var ids = app.Service<KeyRing>().SignedIds;
        var token = ids.Generate(42, "avatar");
        Assert.Equal(42, ids.Find(token, "avatar"));
        Assert.Null(ids.Find(token, "transfer"));
        Assert.Null(ids.Find(token + "x", "avatar"));
        Assert.Null(ids.Find(ids.Generate(42, "transfer", TimeSpan.FromSeconds(-1)), "transfer"));
    }

    [Fact]
    public void Full_text_search_finds_messages_in_reachable_rooms_only()
    {
        var fixtures = app.Fixtures;
        app.Sql(sql =>
        {
            Messages.Create(sql, fixtures.Designers.Id, fixtures.David.Id, null, "<p>Ship the redesign AND party</p>", "Ship the redesign AND party");
            Messages.Create(sql, fixtures.Pets.Id, fixtures.David.Id, null, "<p>redesign the dog house</p>", "redesign the dog house");
        });

        Assert.Single(app.Sql(sql => Messages.Search(sql, fixtures.Kevin.Id, "redesign")));
        Assert.Equal(2, app.Sql(sql => Messages.Search(sql, fixtures.David.Id, "redesign")).Count);
        Assert.Single(app.Sql(sql => Messages.Search(sql, fixtures.David.Id, "AND party")));
    }

    [Fact]
    public void Concurrent_writers_queue_instead_of_hitting_sqlite_busy_sleeps()
    {
        var fixtures = app.Fixtures;
        var started = System.Diagnostics.Stopwatch.StartNew();

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => app.Sql(sql =>
            sql.Transaction(tx =>
            {
                // Messages.Create opens a nested transaction and writes inside it: the gate must be reentrant.
                Messages.Create(tx, fixtures.Hq.Id, fixtures.David.Id, $"gate-{i}", "<p>gate</p>", "gate");
                Users.Touch(tx, fixtures.David.Id);
            })));

        Assert.Equal(64, app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM messages WHERE client_message_id LIKE 'gate-%'")));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"64 queued writes took {started.Elapsed}");
    }

    [Fact]
    public void Html_encoding_escapes_like_erb_and_keeps_unicode()
    {
        Assert.Equal("José &lt;b&gt; &amp; &quot;🔥&quot; &#39;", MinimalHtmlEncoder.Escape("José <b> & \"🔥\" '"));
    }

    [Fact]
    public async Task Unknown_paths_are_not_found()
    {
        using var client = app.Anonymous();
        var response = await client.GetAsync("/definitely-not-a-route", accept: "application/json");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Assets_are_served_digested_and_immutable()
    {
        using var client = app.Anonymous();
        var path = Campfire.Web.Assets.AssetCatalog.Current.PathFor("check.svg");
        var response = await client.GetAsync(path, accept: "*/*");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
    }
}
