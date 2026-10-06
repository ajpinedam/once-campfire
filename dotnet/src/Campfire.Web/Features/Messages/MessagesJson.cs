using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Campfire.Web.Data;
using Campfire.Web.Data.Queries;
using Campfire.Web.Domain;
using Campfire.Web.RichText;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Messages;

// The jbuilder shapes the bot API returns: users/_user, messages/_message, messages/boosts/_boost.

public sealed record UserJson(long Id, string Name, string Role, string AvatarUrl);

public sealed record MessageBodyJson(string PlainText, string Html);

public sealed record RoomReferenceJson(long Id);

public sealed record MessageJson(long Id, string CreatedAt, MessageBodyJson Body, UserJson Creator, RoomReferenceJson Room, string Url);

public sealed record BoostMessageJson(long Id, string Url);

public sealed record BoostJson(long Id, string Content, string CreatedAt, UserJson Booster, BoostMessageJson Message);

public static class MessagesJson
{
    /// <summary>Snake case like jbuilder; non-ASCII text (emoji, names) written as itself.</summary>
    public static readonly JsonSerializerOptions Options = new(RoomsJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonTypeInfo<MessageJson> MessageInfo => (JsonTypeInfo<MessageJson>)Options.GetTypeInfo(typeof(MessageJson));
    public static JsonTypeInfo<List<MessageJson>> MessageListInfo => (JsonTypeInfo<List<MessageJson>>)Options.GetTypeInfo(typeof(List<MessageJson>));
    public static JsonTypeInfo<BoostJson> BoostInfo => (JsonTypeInfo<BoostJson>)Options.GetTypeInfo(typeof(BoostJson));

    /// <summary>Times as Rails encodes them in JSON: UTC ISO 8601 with milliseconds.</summary>
    public static string Time(DateTime time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static UserJson User(User user, string baseUrl) =>
        new(user.Id, user.Name, user.Role.ToStored(), baseUrl + Paths.FreshUserAvatar(user));

    public static async Task<MessageJson> MessageAsync(Sql sql, Message message, string baseUrl, RichTextService richText, MessageRenderer renderer) =>
        (await MessagesAsync(sql, [message], baseUrl, richText, renderer))[0];

    /// <summary>messages/_message.json for a page of messages, loading bodies and creators in batches.</summary>
    public static Task<List<MessageJson>> MessagesAsync(Sql sql, IReadOnlyList<Message> messages, string baseUrl, RichTextService richText, MessageRenderer renderer)
    {
        var ids = messages.Select(message => message.Id).ToList();
        var bodies = RichTexts.ForMessages(sql, ids);
        var attachments = Attachments.ForRecords(sql, "Message", "attachment", ids);
        var creators = Data.Queries.Users.FindMany(sql, messages.Select(message => message.CreatorId).Distinct()).ToDictionary(user => user.Id);
        var host = MessageRenderer.HostOf(baseUrl);

        var json = new List<MessageJson>(messages.Count);
        foreach (var message in messages)
        {
            var body = bodies.GetValueOrDefault(message.Id);
            var html = body is null ? "" : richText.RenderBody(sql, body, host);
            var creator = creators[message.CreatorId];

            json.Add(new MessageJson(
                message.Id,
                Time(message.CreatedAt),
                new MessageBodyJson(renderer.PlainText(sql, body, attachments.GetValueOrDefault(message.Id)), html),
                User(creator, baseUrl),
                new RoomReferenceJson(message.RoomId),
                baseUrl + Paths.RoomMessage(message.RoomId, message.Id)));
        }
        return Task.FromResult(json);
    }

    public static BoostJson Boost(Boost boost, User booster, Message message, string baseUrl) =>
        new(boost.Id, boost.Content, Time(boost.CreatedAt), User(booster, baseUrl),
            new BoostMessageJson(message.Id, baseUrl + Paths.RoomMessage(message.RoomId, message.Id)));
}
