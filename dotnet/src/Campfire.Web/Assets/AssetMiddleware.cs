namespace Campfire.Web.Assets;

/// <summary>
/// Serves <c>/assets/*</c> from memory. Digested paths are immutable for a year; logical
/// (undigested) paths are served too, with a short cache, for anything that hard-codes them.
/// </summary>
public sealed class AssetMiddleware(RequestDelegate next, AssetCatalog catalog)
{
    private const string Immutable = "public, max-age=31536000, immutable";
    private const string ShortLived = "public, max-age=60, stale-while-revalidate=300";

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith(AssetCatalog.Prefix, StringComparison.Ordinal) ||
            !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            return next(context);
        }

        var relative = Uri.UnescapeDataString(path[AssetCatalog.Prefix.Length..]);
        string cacheControl;
        if (catalog.TryGetByDigestedPath(relative, out var file))
        {
            cacheControl = Immutable;
        }
        else if (catalog.TryGetByLogicalPath(relative, out file))
        {
            cacheControl = ShortLived;
        }
        else
        {
            return next(context);
        }

        var response = context.Response;
        response.Headers.CacheControl = cacheControl;
        response.Headers.ETag = file.ETag;
        response.Headers.Vary = "Accept-Encoding";

        if (context.Request.Headers.IfNoneMatch == file.ETag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        response.ContentType = file.ContentType;
        var body = file.Content;
        if (file.Brotli is { } brotli && context.Request.Headers.AcceptEncoding.ToString().Contains("br", StringComparison.Ordinal))
        {
            response.Headers.ContentEncoding = "br";
            body = brotli;
        }

        response.ContentLength = body.Length;
        return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }
}
