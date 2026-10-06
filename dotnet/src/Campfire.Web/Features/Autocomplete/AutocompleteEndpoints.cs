using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Messages;
using Campfire.Web.Http;
using Campfire.Web.RichText;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Autocomplete;

/// <summary>autocompletable/users/_user.json: the name is HTML-escaped, as <c>h(user.name)</c> left it.</summary>
public sealed record AutocompletableUserJson(string Name, long Value, string AvatarUrl, string Sgid);

/// <summary>One <c>&lt;lexxy-prompt-item&gt;</c> for the editor's @mention prompt.</summary>
public sealed record PromptItem(User User, string Sgid, string MentionHtml);

/// <summary>Autocompletable::UsersController: people to @mention or to start a ping with.</summary>
public static class AutocompleteEndpoints
{
    private const int PerPage = 20;

    public static IEndpointRouteBuilder MapAutocomplete(this IEndpointRouteBuilder app)
    {
        app.MapGet("/autocompletable/users", (HttpContext context, Current current, Sql sql, RichTextService richText) => Index(context, current, sql, richText, json: false));
        app.MapGet("/autocompletable/users.json", (HttpContext context, Current current, Sql sql, RichTextService richText) => Index(context, current, sql, richText, json: true));
        return app;
    }

    private static IResult Index(HttpContext context, Current current, Sql sql, RichTextService richText, bool json)
    {
        var request = context.Request;

        // The rich text editor's mentions prompt filters with `filter`, the autocomplete inputs with `query`
        var query = request.Query["filter"].ToString() is { Length: > 0 } filter ? filter : request.Query["query"].ToString();

        long? roomId = null;
        if (long.TryParse(request.Query["room_id"], out var requestedRoomId))
        {
            if (Data.Queries.Rooms.FindForUser(sql, current.User.Id, requestedRoomId) is null)
            {
                return Results.NotFound(); // Current.user.rooms.find(params[:room_id])
            }
            roomId = requestedRoomId;
        }

        var page = int.TryParse(request.Query["page"], out var number) && number > 1 ? number : 1;
        var users = Data.Queries.Users.Autocompletable(sql, roomId, string.IsNullOrWhiteSpace(query) ? null : query, PerPage, (page - 1) * PerPage);

        if (json || request.WantsJson())
        {
            var baseUrl = request.BaseUrl();
            var payload = users.Select(user => new AutocompletableUserJson(
                MinimalHtmlEncoder.Escape(user.Name), user.Id, baseUrl + Paths.FreshUserAvatar(user), richText.UserSgid(user.Id))).ToList();
            return Results.Json(payload, MessagesJson.Options);
        }

        var items = users.Select(user => new PromptItem(user, richText.UserSgid(user.Id), richText.RenderMention(user))).ToList();
        return Views.Autocomplete.PromptItems.Create(items);
    }
}
