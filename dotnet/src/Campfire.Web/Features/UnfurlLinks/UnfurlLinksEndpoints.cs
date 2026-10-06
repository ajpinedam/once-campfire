using System.Text.Json;
using Campfire.Web.Http;
using Campfire.Web.OpenGraph;
using Campfire.Web.Views;

namespace Campfire.Web.Features.UnfurlLinks;

/// <summary>What the unfurl controller (unfurl_controller.js) builds a link preview from.</summary>
public sealed record UnfurledLink(string? Title, string? Url, string? Image, string? Description);

/// <summary>Rails' UnfurlLinksController: the composer asks for a pasted link's OpenGraph preview.</summary>
public static class UnfurlLinksEndpoints
{
    public static IEndpointRouteBuilder MapUnfurlLinks(this IEndpointRouteBuilder app)
    {
        app.MapPost(Paths.UnfurlLink, Create);
        return app;
    }

    private static async Task<IResult> Create(HttpContext context, OpenGraphUnfurler unfurler)
    {
        if (await ReadUrlAsync(context.Request) is not { Length: > 0 } url)
        {
            return Respond.Head(StatusCodes.Status400BadRequest); // params.require(:url)
        }

        var metadata = await unfurler.FromUrlAsync(url, context.RequestAborted);
        return metadata.IsValid
            ? Results.Json(new UnfurledLink(metadata.Title, metadata.Url, metadata.Image, metadata.Description), UnfurlLinksJsonContext.Default.UnfurledLink)
            : Respond.Head(StatusCodes.Status204NoContent);
    }

    /// <summary><c>params[:url]</c> from a JSON body (what the composer sends), a form, or the query string.</summary>
    private static async Task<string?> ReadUrlAsync(HttpRequest request)
    {
        if (request.HasJsonContentType())
        {
            try
            {
                using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    return url.GetString()?.Trim();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            if (form.TryGetValue("url", out var formUrl))
            {
                return formUrl.ToString().Trim();
            }
        }

        return request.Query.TryGetValue("url", out var queryUrl) ? queryUrl.ToString().Trim() : null;
    }
}
