using System.Text.RegularExpressions;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Features.Messages;
using Campfire.Web.Features.Rooms;
using Campfire.Web.Http;
using Campfire.Web.Views;

namespace Campfire.Web.Features.Searches;

public sealed record SearchesModel(
    PageContext Page,
    string? Query,
    IReadOnlyList<byte[]> Messages,
    IReadOnlyList<Search> RecentSearches,
    Room? ReturnToRoom,
    string? QueryParam);

/// <summary>SearchesController: full-text search over the messages a user can reach.</summary>
public static partial class SearchEndpoints
{
    private const int ResultsLimit = 100;

    public static IEndpointRouteBuilder MapSearches(this IEndpointRouteBuilder app)
    {
        app.MapGet("/searches", Index);
        app.MapPost("/searches", Create);
        app.MapDelete("/searches/clear", Clear);
        return app;
    }

    private static async Task<IResult> Index(string? q, HttpContext context, Current current, Sql sql, MessageRenderer renderer)
    {
        var user = current.User;
        var query = Scrub(q);
        var messages = string.IsNullOrWhiteSpace(query) ? [] : Data.Queries.Messages.Search(sql, user.Id, query, ResultsLimit);
        var fragments = await renderer.RenderAsync(sql, messages, context.Request.BaseUrl(), context.RequestAborted);

        return Views.Searches.Index.Create(new SearchesModel(
            PageContext.For(context),
            string.IsNullOrWhiteSpace(query) ? null : query,
            fragments,
            Data.Queries.Searches.RecentForUser(sql, user.Id),
            RoomAccess.LastRoomVisited(sql, context, user),
            q));
    }

    private static async Task<IResult> Create(HttpContext context, Current current, Sql sql)
    {
        var query = Scrub(await RoomAccess.FormValue(context.Request, "q") ?? context.Request.Query["q"].ToString());
        if (string.IsNullOrWhiteSpace(query))
        {
            return Respond.Redirect(context, Paths.Searches);
        }

        Data.Queries.Searches.Record(sql, current.User.Id, query);
        return Respond.Redirect(context, Paths.SearchesFor(query));
    }

    private static IResult Clear(HttpContext context, Current current, Sql sql)
    {
        Data.Queries.Searches.Clear(sql, current.User.Id);
        return Respond.Redirect(context, Paths.Searches);
    }

    /// <summary><c>params[:q]&amp;.gsub(/[^[:word:]]/, " ")</c>: only word characters are searched for.</summary>
    public static string? Scrub(string? query) => query is null ? null : NonWord().Replace(query, " ");

    [GeneratedRegex(@"[^\w]")]
    private static partial Regex NonWord();
}
