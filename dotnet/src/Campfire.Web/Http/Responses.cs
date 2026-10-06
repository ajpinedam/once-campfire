using System.Text;

namespace Campfire.Web.Http;

public static class RequestExtensions
{
    public const string TurboStreamMediaType = "text/vnd.turbo-stream.html";

    /// <summary>True when Turbo asked for a stream response (Rails' <c>format.turbo_stream</c>).</summary>
    public static bool WantsTurboStream(this HttpRequest request) =>
        request.Headers.Accept.ToString().Contains(TurboStreamMediaType, StringComparison.OrdinalIgnoreCase);

    public static bool WantsJson(this HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
        request.Path.Value?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The frame a Turbo Frame navigation targets, if any.</summary>
    public static string? TurboFrame(this HttpRequest request) =>
        request.Headers["Turbo-Frame"] is { Count: > 0 } frame ? frame.ToString() : null;

    /// <summary><c>{scheme}://{host}</c> of this request, the base for absolute URLs (Rails' <c>*_url</c> helpers).</summary>
    public static string BaseUrl(this HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    public static string FullUrl(this HttpRequest request) => $"{request.BaseUrl()}{request.Path}{request.QueryString}";

    public static long? RouteLong(this HttpRequest request, string name) =>
        request.RouteValues[name] is string value && long.TryParse(value, out var id) ? id : null;
}

/// <summary>Results shaped like Rails controller responses.</summary>
public static class Respond
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// <c>redirect_to</c>. After a non-GET request this answers 303 See Other, so fetch (Turbo,
    /// request.js) always follows with a GET — a 302 would replay DELETE/PATCH requests.
    /// </summary>
    public static IResult Redirect(HttpContext context, string url, string? notice = null, string? alert = null)
    {
        if (notice is not null || alert is not null)
        {
            context.RequestServices.GetRequiredService<AppCookies>().SetFlash(context, notice, alert);
        }

        return HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            ? Results.Redirect(url)
            : new SeeOtherResult(url);
    }

    public static IResult TurboStream(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(html, RequestExtensions.TurboStreamMediaType, Utf8, statusCode);

    public static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(html, "text/html; charset=utf-8", Utf8, statusCode);

    /// <summary><c>head :status</c>.</summary>
    public static IResult Head(int statusCode) => Results.StatusCode(statusCode);

    private sealed class SeeOtherResult(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = url;
            return Task.CompletedTask;
        }
    }
}
