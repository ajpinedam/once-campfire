using System.Text.Json;
using Campfire.Web.Data;
using Campfire.Web.Domain;
using Campfire.Web.Http;
using Campfire.Web.Push;
using Campfire.Web.Views;
using Campfire.Web.Views.PushSubscriptions;
using IndexView = Campfire.Web.Views.PushSubscriptions.Index;

namespace Campfire.Web.Features.PushSubscriptions;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>
/// Rails' Users::PushSubscriptionsController and Users::PushSubscriptions::TestNotificationsController.
/// The browser registers its push subscription here (notifications_controller.js); the index page is
/// a development tool for inspecting and testing them.
/// </summary>
public static class PushSubscriptionsEndpoints
{
    private const string ParamRoot = "push_subscription";

    public static IEndpointRouteBuilder MapPushSubscriptions(this IEndpointRouteBuilder app)
    {
        // Rails: resources :push_subscriptions under users/:user_id, with user_id defaulting to "me".
        // The id segment is decoration; it's always the signed-in user's subscriptions.
        var group = app.MapGroup("/users/{user_id}/push_subscriptions");
        group.MapGet("", Index);
        group.MapPost("", Create);
        group.MapDelete("/{id:long}", Destroy);
        group.MapPost("/{push_subscription_id:long}/test_notifications", CreateTestNotification);
        return app;
    }

    private static RazorSlices.RazorSlice<PushSubscriptionsPage> Index(HttpContext context, Current current, Sql sql)
    {
        var user = current.User;
        var model = new PushSubscriptionsPage(
            PageContext.For(context),
            PushSubscriptions.ForUser(sql, user.Id),
            BackPath(context, sql, user));
        return IndexView.Create(model);
    }

    private static async Task<IResult> Create(HttpContext context, Current current, Sql sql, PushEndpoints endpoints)
    {
        if (await ReadParamsAsync(context.Request) is not { } submitted)
        {
            return Respond.Head(StatusCodes.Status400BadRequest); // params.require(:push_subscription)
        }

        var user = current.User;
        var (endpoint, p256dh, auth) = submitted;

        if (PushSubscriptions.FindBySubmitted(sql, user.Id, endpoint, p256dh, auth) is { } existing)
        {
            // Existing endpoints must pass current validations, or a sink planted before they
            // existed could be kept alive by re-registering it.
            if ((await endpoints.ValidateAsync(existing.Endpoint, context.RequestAborted)).Count > 0)
            {
                return Respond.Head(StatusCodes.Status422UnprocessableEntity);
            }

            PushSubscriptions.Touch(sql, existing.Id);
            return Respond.Head(StatusCodes.Status200OK);
        }

        if ((await endpoints.ValidateAsync(endpoint.Value, context.RequestAborted)).Count > 0)
        {
            return Respond.Head(StatusCodes.Status422UnprocessableEntity);
        }

        PushSubscriptions.Create(sql, user.Id, endpoint.Value!, p256dh.Value, auth.Value, current.UserAgent);
        return Respond.Head(StatusCodes.Status200OK);
    }

    private static IResult Destroy(HttpContext context, Current current, Sql sql, long id)
    {
        PushSubscriptions.DeleteForUser(sql, current.User.Id, id);
        return Respond.Redirect(context, Paths.UserPushSubscriptions);
    }

    private static async Task<IResult> CreateTestNotification(HttpContext context, Current current, Sql sql, WebPushSender sender, long push_subscription_id)
    {
        var user = current.User;
        if (PushSubscriptions.Find(sql, user.Id, push_subscription_id) is not { } subscription)
        {
            return Results.NotFound();
        }

        var message = new WebPushMessage(
            Title: "Campfire Test",
            Body: Guid.NewGuid().ToString(),
            Path: context.Request.BaseUrl() + Paths.UserPushSubscriptions,
            Badge: Memberships.UnreadCount(sql, user.Id),
            Icon: Paths.AccountLogo);

        await sender.DeliverAsync(new PushDelivery(subscription.Id, subscription.Endpoint, subscription.P256dhKey, subscription.AuthKey, message), context.RequestAborted);
        return Respond.Redirect(context, Paths.UserPushSubscriptions);
    }

    /// <summary><c>link_back_to_last_room_visited</c>: the remembered room if still theirs, else their first room.</summary>
    private static string BackPath(HttpContext context, Sql sql, User user)
    {
        var room = AppCookies.ReadLastRoom(context) is { } lastRoomId ? Rooms.FindForUser(sql, user.Id, lastRoomId) : null;
        room ??= Rooms.OriginalForUser(sql, user.Id);
        return room is null ? Paths.Root : Paths.Room(room.Id);
    }

    /// <summary>
    /// <c>params.require(:push_subscription).permit(:endpoint, :p256dh_key, :auth_key)</c> from the
    /// query string, a form, or the JSON the notifications controller posts
    /// (<c>{"push_subscription":{"endpoint":…,"p256dh_key":…,"auth_key":…}}</c>); the body wins.
    /// </summary>
    internal static async Task<(SubmittedValue Endpoint, SubmittedValue P256dh, SubmittedValue Auth)?> ReadParamsAsync(HttpRequest request)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var present = false;

        void Collect(Func<string, (bool Found, string? Value)> lookup)
        {
            foreach (var key in (string[])["endpoint", "p256dh_key", "auth_key"])
            {
                var (found, value) = lookup(key);
                if (found)
                {
                    values[key] = value;
                    present = true;
                }
            }
        }

        Collect(key => request.Query.TryGetValue($"{ParamRoot}[{key}]", out var value) ? (true, value.ToString()) : (false, null));

        if (request.HasJsonContentType())
        {
            try
            {
                using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty(ParamRoot, out var subscription) && subscription.ValueKind == JsonValueKind.Object)
                {
                    Collect(key => subscription.TryGetProperty(key, out var value)
                        ? (true, value.ValueKind == JsonValueKind.Null ? null : value.ToString())
                        : (false, null));
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
            Collect(key => form.TryGetValue($"{ParamRoot}[{key}]", out var value) ? (true, value.ToString()) : (false, null));
        }

        if (!present)
        {
            return null;
        }

        SubmittedValue Value(string key) => values.TryGetValue(key, out var value) ? new SubmittedValue(true, value) : SubmittedValue.Absent;
        return (Value("endpoint"), Value("p256dh_key"), Value("auth_key"));
    }
}
