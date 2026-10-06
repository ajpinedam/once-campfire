namespace Campfire.Tests.Foundation;

using Campfire.Tests.Support;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.RichText;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Group commit, the statement cache, single-parse posting and buffered pages.</summary>
public sealed class PerformanceInfrastructureTests(CampfireApp app) : IClassFixture<CampfireApp>
{
    [Fact]
    public async Task Concurrent_writes_are_group_committed_and_each_caller_gets_its_own_result()
    {
        var fixtures = app.Fixtures;
        var writes = app.Service<WriteBatcher>();

        var created = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => writes.RunAsync(tx =>
            Messages.Create(tx, fixtures.Pets.Id, fixtures.David.Id, $"batch-{i}", "<p>batched</p>", "batched")))));

        Assert.Equal(200, created.Select(message => message.Id).Distinct().Count());
        Assert.All(created.Select((message, i) => (message, i)), pair => Assert.Equal($"batch-{pair.i}", pair.message.ClientMessageId));
        Assert.Equal(200, app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM messages WHERE client_message_id LIKE 'batch-%'")));
    }

    [Fact]
    public async Task A_failing_write_is_rolled_back_alone_and_its_caller_sees_the_error()
    {
        var fixtures = app.Fixtures;
        var writes = app.Service<WriteBatcher>();

        var good = writes.RunAsync(tx => Messages.Create(tx, fixtures.Hq.Id, fixtures.David.Id, "isolated-good", "<p>ok</p>", "ok"));
        var bad = writes.RunAsync<long>(tx =>
        {
            Messages.Create(tx, fixtures.Hq.Id, fixtures.David.Id, "isolated-bad", "<p>no</p>", "no");
            return tx.Insert("INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at) VALUES (-1, -1, 'fk', @now, @now)",
                ("@now", SqlTime.UtcNow())); // violates the foreign keys
        });

        await good;
        await Assert.ThrowsAsync<SqliteException>(() => bad);
        Assert.Equal(1, app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM messages WHERE client_message_id = 'isolated-good'")));
        Assert.Equal(0, app.Sql(sql => sql.ScalarLong("SELECT COUNT(*) FROM messages WHERE client_message_id IN ('isolated-bad', 'fk')")));
    }

    [Fact]
    public void A_statement_that_failed_is_prepared_afresh_and_works_again()
    {
        var fixtures = app.Fixtures;
        app.Sql(sql =>
        {
            const string insert = "INSERT INTO bans (user_id, ip_address, created_at, updated_at) VALUES (@user, @ip, @now, @now)";
            Assert.Throws<SqliteException>(() => sql.Execute(insert, ("@user", -1L), ("@ip", "203.0.113.9"), ("@now", SqlTime.UtcNow())));
            Assert.Equal(1, sql.Execute(insert, ("@user", fixtures.Kevin.Id), ("@ip", "203.0.113.9"), ("@now", SqlTime.UtcNow())));
            Assert.True(Bans.IsBanned(sql, "203.0.113.9"));
            sql.Execute("DELETE FROM bans WHERE ip_address = '203.0.113.9'");
        });
    }

    [Theory]
    [InlineData("<p>Hello <strong>world</strong></p><ul><li>one</li><li>two</li></ul>")]
    [InlineData("<div>Line one<br>Line two</div><blockquote>quoted</blockquote>")]
    [InlineData("plain text from a bot")]
    [InlineData("")]
    public void Prepare_matches_canonicalize_then_flatten(string html) => AssertPrepareMatches(html);

    [Fact]
    public void Prepare_matches_for_mentions_too()
    {
        var richText = app.Service<RichTextService>();
        var jason = app.Fixtures.Jason;
        var mention = $"<action-text-attachment sgid=\"{richText.UserSgid(jason.Id)}\" content-type=\"application/vnd.campfire.mention\"><span>ignored</span></action-text-attachment>";
        AssertPrepareMatches($"<p>Hey {mention}, lunch?</p>");
        Assert.Equal([jason.Id], app.Sql(sql => richText.Prepare(sql, $"<p>{mention}</p>")).MentionedUserIds);
    }

    [Fact]
    public async Task A_posted_message_renders_the_same_from_posting_data_as_from_the_database()
    {
        var fixtures = app.Fixtures;
        var richText = app.Service<RichTextService>();
        var mention = $"<action-text-attachment sgid=\"{richText.UserSgid(fixtures.Jason.Id)}\" content-type=\"application/vnd.campfire.mention\"></action-text-attachment>";
        const string baseUrl = "http://localhost";

        await using var scope = app.Services.CreateAsyncScope();
        using var sql = app.Database.Open();
        var posting = scope.ServiceProvider.GetRequiredService<Campfire.Web.Features.Messages.MessagePosting>();
        var message = await posting.PostAsync(sql, fixtures.DavidAndJason, fixtures.David,
            $"<p>Hey {mention}, see https://example.com/notes 🎉</p>", null, "render-parity", baseUrl, deliverWebhooks: false);

        // The posting path rendered (and cached) it; a fresh renderer has to load everything back
        var fresh = ActivatorUtilities.CreateInstance<Campfire.Web.Features.Messages.MessageRenderer>(app.Services);
        var fromDatabase = await fresh.RenderStringAsync(sql, message, baseUrl);
        var fromPosting = await app.Service<Campfire.Web.Features.Messages.MessageRenderer>().RenderStringAsync(sql, message, baseUrl);

        Assert.Contains("https://example.com/notes", fromDatabase);
        Assert.Equal(fromDatabase, fromPosting);
    }

    [Fact]
    public async Task Pages_are_sent_in_one_buffered_write_with_a_content_length()
    {
        using var client = app.SignedInAs(app.Fixtures.David);
        var response = await client.GetAsync($"/rooms/{app.Fixtures.Designers.Id}");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Large_pages_span_several_buffer_segments_and_arrive_intact()
    {
        var fixtures = app.Fixtures;
        var text = string.Concat(Enumerable.Repeat("segmented page filler ", 120));
        app.Sql(sql =>
        {
            for (var i = 0; i < 40; i++)
            {
                Messages.Create(sql, fixtures.Watercooler.Id, fixtures.Jason.Id, $"segment-{i}", $"<p>{i} {text}</p>", $"{i} {text}");
            }
        });

        using var client = app.SignedInAs(fixtures.David);
        var response = await client.GetAsync($"/rooms/{fixtures.Watercooler.Id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(body.Length > 3 * 64 * 1024, $"expected a multi-segment page, got {body.Length} bytes");
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        Assert.EndsWith("</html>", body.TrimEnd());
        for (var i = 0; i < 40; i++)
        {
            Assert.Contains($"segment-{i}", body); // every message, in a page assembled from segments
        }
        Assert.True(body.IndexOf("segment-5\"", StringComparison.Ordinal) < body.IndexOf("segment-35\"", StringComparison.Ordinal));
    }

    private void AssertPrepareMatches(string html)
    {
        var richText = app.Service<RichTextService>();
        app.Sql(sql =>
        {
            var prepared = richText.Prepare(sql, html);
            var canonical = richText.Canonicalize(html);
            Assert.Equal(canonical, prepared.Canonical);
            Assert.Equal(richText.ToPlainText(sql, canonical), prepared.PlainText);
            Assert.Equal(richText.MentionedUserIds(canonical), prepared.MentionedUserIds);
        });
    }
}
