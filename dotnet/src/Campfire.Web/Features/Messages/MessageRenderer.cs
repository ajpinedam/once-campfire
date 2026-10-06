using System.Text;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.RichText;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Caching.Memory;

namespace Campfire.Web.Features.Messages;

/// <summary>
/// Renders the message partial, with Rails' fragment caching (<c>cache [ message, "presentation-v3" ]</c>
/// plus the collection cache): rendered UTF-8 fragments are kept in a bounded in-memory cache keyed
/// by the message's and its creator's versions, and presentation data (bodies, attachments, boosts)
/// is only loaded — in batches — for the messages that missed.
/// </summary>
public sealed class MessageRenderer(RichTextService richText, BlobStore blobs, ILogger<MessageRenderer> logger) : IDisposable
{
    /// <summary>Bump when the message partial or presentation filters change what they emit.</summary>
    private const string TemplateVersion = "presentation-v3";

    private static readonly byte[] Unrenderable = Encoding.UTF8.GetBytes(
        "<div class=\"message message--formatted message--failed center\"><div class=\"message__body\"><div class=\"message__body-content txt-align-center\">" +
        "Failed to load message content</div></div></div>");

    private readonly MemoryCache _fragments = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024, CompactionPercentage = 0.25 });

    private readonly record struct FragmentKey(long MessageId, long MessageVersion, long CreatorVersion, string BaseUrl, string Template);

    /// <summary>Rendered fragments for messages, in the same order.</summary>
    public async Task<IReadOnlyList<byte[]>> RenderAsync(Sql sql, IReadOnlyList<Message> messages, string baseUrl, CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var creators = Data.Queries.Users.FindMany(sql, messages.Select(message => message.CreatorId).Distinct()).ToDictionary(user => user.Id);
        var fragments = new byte[messages.Count][];
        var keys = new FragmentKey[messages.Count];
        var misses = new List<int>();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var creatorVersion = creators.TryGetValue(message.CreatorId, out var creator) ? creator.UpdatedAt.Ticks : 0;
            keys[i] = new FragmentKey(message.Id, message.UpdatedAt.Ticks, creatorVersion, baseUrl, TemplateVersion);

            if (_fragments.TryGetValue(keys[i], out byte[]? cached) && cached is not null)
            {
                fragments[i] = cached;
            }
            else
            {
                misses.Add(i);
            }
        }

        if (misses.Count > 0)
        {
            var views = LoadViews(sql, misses.Select(i => messages[i]).ToList(), creators, baseUrl);
            foreach (var i in misses)
            {
                var bytes = views.TryGetValue(messages[i].Id, out var view) ? await RenderOneAsync(view, cancellationToken) : Unrenderable;
                fragments[i] = bytes;
                if (!ReferenceEquals(bytes, Unrenderable))
                {
                    _fragments.Set(keys[i], bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromHours(6) });
                }
            }
        }

        return fragments;
    }

    /// <summary>
    /// A message that was just posted, rendered from what posting already holds (no boosts yet, a
    /// fresh creator and room) instead of reloading it, and cached for the create response to reuse.
    /// </summary>
    public async Task<string> RenderNewAsync(Sql sql, Message message, User creator, Room room, string? body, Blob? attachment, string plainText, string baseUrl, CancellationToken cancellationToken = default)
    {
        var view = new MessageView(
            Message: message,
            Creator: creator,
            Room: room,
            RoomName: room.IsDirect ? Data.Queries.Rooms.DisplayName(sql, room, forUser: null) : room.Name ?? "",
            BaseUrl: baseUrl,
            Presentation: Presentation(sql, message, body, attachment, plainText, baseUrl),
            IsEmoji: IsEmoji(plainText),
            Attachment: attachment,
            Boosts: []);

        var bytes = await RenderOneAsync(view, cancellationToken);
        if (!ReferenceEquals(bytes, Unrenderable))
        {
            var key = new FragmentKey(message.Id, message.UpdatedAt.Ticks, creator.UpdatedAt.Ticks, baseUrl, TemplateVersion);
            _fragments.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromHours(6) });
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>One message's fragment as a string (broadcasts, turbo stream responses).</summary>
    public async Task<string> RenderStringAsync(Sql sql, Message message, string baseUrl, CancellationToken cancellationToken = default)
    {
        var fragments = await RenderAsync(sql, [message], baseUrl, cancellationToken);
        return Encoding.UTF8.GetString(fragments[0]);
    }

    /// <summary>Several messages' fragments concatenated.</summary>
    public async Task<string> RenderConcatenatedAsync(Sql sql, IReadOnlyList<Message> messages, string baseUrl, CancellationToken cancellationToken = default)
    {
        var fragments = await RenderAsync(sql, messages, baseUrl, cancellationToken);
        var html = new StringBuilder();
        foreach (var fragment in fragments)
        {
            html.Append(Encoding.UTF8.GetString(fragment));
        }
        return html.ToString();
    }

    /// <summary>
    /// <c>message_presentation(message)</c>: attachment, sound or rich text. Rendering problems
    /// degrade to an empty presentation rather than breaking the page (as the Rails helper rescues).
    /// </summary>
    public HtmlString Presentation(Sql sql, Message message, string? body, Blob? attachment, string plainText, string baseUrl)
    {
        try
        {
            if (attachment is not null)
            {
                return AttachmentPresentation.Render(attachment, blobs);
            }

            if (Sound.FromPlainText(plainText) is { } sound)
            {
                return SoundPresentation.Render(sound);
            }

            return new HtmlString(body is null ? "" : richText.RenderPresentation(sql, body, HostOf(baseUrl)));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Exception while generating message representation for Message#{Id}", message.Id);
            return HtmlString.Empty;
        }
    }

    /// <summary>Rails' <c>plain_text_body</c>: the body as text, else the attachment's filename.</summary>
    public string PlainText(Sql sql, string? body, Blob? attachment)
    {
        var text = "";
        if (body is not null)
        {
            try
            {
                text = richText.ToPlainText(sql, body);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Couldn't convert a message body to plain text");
            }
        }

        return string.IsNullOrWhiteSpace(text) ? attachment?.Filename ?? "" : text;
    }

    public bool IsEmoji(string text)
    {
        try
        {
            return text.Length > 0 && RichTextService.IsAllEmoji(text);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Couldn't classify message text as emoji");
            return false;
        }
    }

    public void Dispose() => _fragments.Dispose();

    private Dictionary<long, MessageView> LoadViews(Sql sql, List<Message> messages, Dictionary<long, User> creators, string baseUrl)
    {
        var ids = messages.Select(message => message.Id).ToList();
        var bodies = RichTexts.ForMessages(sql, ids);
        var attachments = Attachments.ForRecords(sql, "Message", "attachment", ids);
        var boosts = Data.Queries.Boosts.ForMessages(sql, ids);
        var rooms = Data.Queries.Rooms.FindMany(sql, messages.Select(message => message.RoomId).Distinct()).ToDictionary(room => room.Id);
        var roomNames = Data.Queries.Rooms.DisplayNames(sql, rooms.Values, forUser: null);

        var views = new Dictionary<long, MessageView>(messages.Count);
        foreach (var message in messages)
        {
            if (!creators.TryGetValue(message.CreatorId, out var creator) || !rooms.TryGetValue(message.RoomId, out var room))
            {
                continue;
            }

            var body = bodies.GetValueOrDefault(message.Id);
            var attachment = attachments.GetValueOrDefault(message.Id);
            var plainText = PlainText(sql, body, attachment);

            views[message.Id] = new MessageView(
                Message: message,
                Creator: creator,
                Room: room,
                RoomName: roomNames.GetValueOrDefault(room.Id, ""),
                BaseUrl: baseUrl,
                Presentation: Presentation(sql, message, body, attachment, plainText, baseUrl),
                IsEmoji: IsEmoji(plainText),
                Attachment: attachment,
                Boosts: boosts.TryGetValue(message.Id, out var list)
                    ? list.Select(pair => new BoostView(pair.Boost, pair.Booster, IsEmoji(pair.Boost.Content))).ToList()
                    : []);
        }
        return views;
    }

    private async Task<byte[]> RenderOneAsync(MessageView view, CancellationToken cancellationToken)
    {
        try
        {
            return await Slices.RenderUtf8Async(Views.Messages.Message.Create(view), cancellationToken);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Exception while rendering message Message#{Id}", view.Message.Id);
            return Unrenderable;
        }
    }

    public static string HostOf(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
}
