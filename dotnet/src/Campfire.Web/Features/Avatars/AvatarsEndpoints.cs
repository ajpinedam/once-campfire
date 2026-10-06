using Campfire.Web.Data;
using Campfire.Web.Http;
using Campfire.Web.Storage;
using Campfire.Web.Views;
using Microsoft.Net.Http.Headers;
using Q = Campfire.Web.Data.Queries;

namespace Campfire.Web.Features.Avatars;

/// <summary>Users::AvatarsController: avatars by signed token, so user ids can't be enumerated.</summary>
public static class AvatarsEndpoints
{
    private const string CacheControl = "max-age=1800, public, stale-while-revalidate=604800";

    public static IEndpointRouteBuilder MapAvatars(this IEndpointRouteBuilder app)
    {
        app.MapGet("/users/{user_id}/avatar", Show);
        app.MapDelete("/users/{user_id}/avatar", Destroy);
        return app;
    }

    private static async Task<IResult> Show(HttpContext context, Sql sql, BlobStore store, IWebHostEnvironment environment, string user_id)
    {
        if (Paths.UserIdFromAvatarToken(user_id) is not { } userId || Q.Users.Find(sql, userId) is not { } user)
        {
            return Results.NotFound();
        }

        // stale?(etag: @user): the avatar only changes when the user does
        var etag = $"W/\"user-{user.Id}-{user.UpdatedAt.Ticks}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = CacheControl;
        if (context.Request.Headers.IfNoneMatch.Contains(etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (Q.Attachments.Find(sql, "User", user.Id, "avatar") is { } avatar &&
            await store.VariantAsync(avatar, VariantKind.AvatarSquare, context.RequestAborted) is { } variant)
        {
            return Results.File(variant.Path, "image/webp");
        }

        if (user.IsBot)
        {
            return Results.File(Path.Combine(environment.WebRootPath, "assets", "default-bot-avatar.svg"), "image/svg+xml");
        }

        return Results.Text(InitialsAvatar.Render(user), "image/svg+xml; charset=utf-8");
    }

    private static IResult Destroy(HttpContext context, Sql sql, Current current, BlobStore store)
    {
        Uploads.Detach(sql, store, "User", current.User.Id, "avatar");
        Q.Users.Touch(sql, current.User.Id);
        return Respond.Redirect(context, Paths.UserProfile);
    }
}
