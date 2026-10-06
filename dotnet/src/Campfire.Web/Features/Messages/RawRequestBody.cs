using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Web.Features.Messages;

/// <summary>
/// Rails' RawRequestBody concern. Bots post plain text the way <c>curl -d 'Hello!'</c> sends it:
/// as the raw body, labelled <c>application/x-www-form-urlencoded</c>. The method-override
/// middleware parses form bodies before routing (consuming the stream), so bot routes get their
/// body buffered first (<see cref="BufferingFilter"/>) and the endpoint rewinds and reads it whole.
/// </summary>
public static partial class RawRequestBody
{
    public static async Task<string> ReadAsync(HttpRequest request)
    {
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }
        return body;
    }

    /// <summary>
    /// Buffers the body of non-multipart requests to the bot API before anything reads it, so
    /// the raw text survives form parsing. Registered as a startup filter so it runs first.
    /// </summary>
    public sealed class BufferingFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var request = context.Request;
                if (!HttpMethods.IsGet(request.Method) && BotRoute().IsMatch(request.Path.Value ?? "") &&
                    request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) != true)
                {
                    request.EnableBuffering();
                }
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    [GeneratedRegex(@"^/rooms/\d+/[^/]+/messages(/|$)")]
    private static partial Regex BotRoute();
}
