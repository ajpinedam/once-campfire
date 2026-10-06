using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Messages;
using Campfire.Web.RichText;
using Campfire.Web.Storage;
using Campfire.Web.Views;

namespace Campfire.Web.Webhooks;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>What became of a webhook delivery (for logs and tests).</summary>
public sealed record WebhookResult(int? StatusCode, WebhookReply Reply);

public enum WebhookReply
{
    None,
    Text,
    Attachment,
    TimedOut
}

/// <summary>
/// Rails' Webhook#deliver: POSTs the message to the bot's URL as JSON, and posts whatever the bot
/// answers with — text, or a file — back into the room as the bot.
/// </summary>
public sealed class WebhookDelivery : IDisposable
{
    public static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(7);

    // Mime::Type symbols Rails registers, which name the attachment a bot replies with.
    private static readonly FrozenDictionary<string, string> MimeSymbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["text/html"] = "html", ["application/xhtml+xml"] = "html", ["text/plain"] = "text", ["text/javascript"] = "js",
        ["application/javascript"] = "js", ["text/css"] = "css", ["text/calendar"] = "ics", ["text/csv"] = "csv",
        ["text/vcard"] = "vcf", ["text/vtt"] = "vtt", ["image/png"] = "png", ["image/jpeg"] = "jpeg", ["image/pjpeg"] = "jpeg",
        ["image/gif"] = "gif", ["image/bmp"] = "bmp", ["image/tiff"] = "tiff", ["image/svg+xml"] = "svg", ["image/webp"] = "webp",
        ["video/mpeg"] = "mpeg", ["audio/mpeg"] = "mp3", ["audio/ogg"] = "ogg", ["audio/aac"] = "m4a", ["video/webm"] = "webm",
        ["video/mp4"] = "mp4", ["font/otf"] = "otf", ["font/ttf"] = "ttf", ["font/woff"] = "woff", ["font/woff2"] = "woff2",
        ["application/xml"] = "xml", ["text/xml"] = "xml", ["application/rss+xml"] = "rss", ["application/atom+xml"] = "atom",
        ["application/x-yaml"] = "yaml", ["text/yaml"] = "yaml", ["application/json"] = "json", ["text/x-json"] = "json",
        ["application/pdf"] = "pdf", ["application/zip"] = "zip", ["application/gzip"] = "gzip"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly Database _database;
    private readonly RichTextService _richText;
    private readonly BlobStore _blobs;
    private readonly MessagePosting _posting;
    private readonly ILogger<WebhookDelivery> _logger;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public WebhookDelivery(Database database, RichTextService richText, BlobStore blobs, MessagePosting posting, ILogger<WebhookDelivery> logger)
        : this(database, richText, blobs, posting, logger, EndpointTimeout)
    {
    }

    /// <summary>Tests shorten the timeout rather than wait seven seconds.</summary>
    internal WebhookDelivery(Database database, RichTextService richText, BlobStore blobs, MessagePosting posting, ILogger<WebhookDelivery> logger, TimeSpan timeout)
    {
        _timeout = timeout;
        _database = database;
        _richText = richText;
        _blobs = blobs;
        _posting = posting;
        _logger = logger;

        // No PrivateNetworkGuard, unlike link unfurling: only an administrator sets this URL, and
        // operators legitimately point bots at their own internal services.
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            AllowAutoRedirect = false,
            UseCookies = false
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<WebhookResult?> DeliverAsync(long botId, long messageId, string baseUrl, CancellationToken cancellationToken)
    {
        using var sql = _database.Open();
        if (Users.Find(sql, botId) is not { } bot || Webhooks.ForUser(sql, botId) is not { Url: { Length: > 0 } url } ||
            Messages.Find(sql, messageId) is not { } message || Rooms.Find(sql, message.RoomId) is not { } room ||
            Users.Find(sql, message.CreatorId) is not { } creator)
        {
            return null;
        }

        var payload = Payload(bot, message, room, creator, RichTexts.Find(sql, "Message", message.Id), PlainTextBody(sql, message));

        // Net::HTTP's open and read timeouts, each seven seconds.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout * 2);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (Exception error) when (IsTimeout(error, cancellationToken))
        {
            await ReplyWithTextAsync(sql, room, bot, $"Failed to respond within {EndpointTimeout.TotalSeconds:0} seconds", baseUrl, cancellationToken);
            return new WebhookResult(null, WebhookReply.TimedOut);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (response.StatusCode != HttpStatusCode.OK || mediaType is null)
            {
                return new WebhookResult(status, WebhookReply.None);
            }

            try
            {
                if (mediaType is "text/html" or "text/plain")
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                    await ReplyWithTextAsync(sql, room, bot, Encoding.UTF8.GetString(bytes), baseUrl, cancellationToken);
                    return new WebhookResult(status, WebhookReply.Text);
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var filename = MimeSymbols.TryGetValue(mediaType, out var symbol) ? $"attachment.{symbol}" : "attachment";
                var blob = await _blobs.CreateAsync(sql, body, filename, mediaType, timeout.Token);
                await _posting.PostAsync(sql, room, bot, null, blob, null, baseUrl, deliverWebhooks: false, cancellationToken);
                return new WebhookResult(status, WebhookReply.Attachment);
            }
            catch (Exception error) when (IsTimeout(error, cancellationToken))
            {
                await ReplyWithTextAsync(sql, room, bot, $"Failed to respond within {EndpointTimeout.TotalSeconds:0} seconds", baseUrl, cancellationToken);
                return new WebhookResult(status, WebhookReply.TimedOut);
            }
        }
    }

    /// <summary>
    /// <c>{"user":{"id","name"},"room":{"id","name","path"},"message":{"id","body":{"html","plain"},"path"}}</c>.
    /// The html is the stored body (Action Text's <c>as_json</c>); the plain text drops mentions of
    /// the bot itself, so a bot reads "what's up?" rather than "@Bot what's up?".
    /// </summary>
    public static byte[] Payload(User bot, Message message, Room room, User creator, string? bodyHtml, string plainText)
    {
        using var buffer = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();

            json.WriteStartObject("user");
            json.WriteNumber("id", creator.Id);
            json.WriteString("name", creator.Name);
            json.WriteEndObject();

            json.WriteStartObject("room");
            json.WriteNumber("id", room.Id);
            json.WriteString("name", room.Name);
            json.WriteString("path", Paths.RoomBotMessages(room.Id, bot.BotKey));
            json.WriteEndObject();

            json.WriteStartObject("message");
            json.WriteNumber("id", message.Id);
            json.WriteStartObject("body");
            json.WriteString("html", bodyHtml);
            json.WriteString("plain", WithoutRecipientMentions(plainText, bot));
            json.WriteEndObject();
            json.WriteString("path", Paths.RoomAtMessage(room.Id, message.Id));
            json.WriteEndObject();

            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Removes <c>@BotName</c> and leading/trailing whitespace, Unicode spaces included.</summary>
    public static string WithoutRecipientMentions(string text, User bot) =>
        text.Replace(bot.MentionText, "", StringComparison.Ordinal).Trim();

    public void Dispose() => _http.Dispose();

    private Task<Message> ReplyWithTextAsync(Sql sql, Room room, User bot, string text, string baseUrl, CancellationToken cancellationToken) =>
        _posting.PostAsync(sql, room, bot, text, null, null, baseUrl, deliverWebhooks: false, cancellationToken);

    /// <summary>Rails' <c>plain_text_body</c>: the text, else the attachment's filename, else nothing.</summary>
    private string PlainTextBody(Sql sql, Message message)
    {
        var body = RichTexts.Find(sql, "Message", message.Id);
        var text = body is null ? "" : _richText.ToPlainText(sql, body);
        return !string.IsNullOrWhiteSpace(text) ? text : Attachments.Find(sql, "Message", message.Id, "attachment")?.Filename ?? "";
    }

    private static bool IsTimeout(Exception error, CancellationToken cancellationToken) =>
        error is OperationCanceledException && !cancellationToken.IsCancellationRequested ||
        error is HttpRequestException { InnerException: TimeoutException or OperationCanceledException } && !cancellationToken.IsCancellationRequested;
}
