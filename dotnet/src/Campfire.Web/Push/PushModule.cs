using Campfire.Web.Features.PushSubscriptions;
using Campfire.Web.Features.UnfurlLinks;
using Campfire.Web.Jobs;
using Campfire.Web.Net;
using Campfire.Web.OpenGraph;
using Campfire.Web.Webhooks;

namespace Campfire.Web.Push;

/// <summary>
/// The Integrations workstream: web push, bot webhooks, link unfurling, and the SSRF guard
/// behind the requests they make to the outside world.
/// </summary>
public static class PushModule
{
    public static IServiceCollection AddPushModule(this IServiceCollection services)
    {
        services.AddSingleton<IHostResolver, SystemHostResolver>();
        services.AddSingleton<PrivateNetworkGuard>();

        services.AddSingleton(provider => new OpenGraphFetch(
            provider.GetRequiredService<PrivateNetworkGuard>(),
            new PinnedHttpClient(connectTimeout: TimeSpan.FromSeconds(5), requestTimeout: TimeSpan.FromSeconds(15))));
        services.AddSingleton<OpenGraphLocations>();
        services.AddSingleton<OpenGraphUnfurler>();

        services.AddSingleton<PushEndpoints>();
        services.AddSingleton<IWebPushTransport, HttpWebPushTransport>();
        services.AddSingleton<WebPushSender>();
        services.AddSingleton<WebPushPool>();
        services.AddHostedService(provider => provider.GetRequiredService<WebPushPool>());
        services.AddSingleton<MessagePusher>();
        services.AddSingleton<MessagePushes>();

        services.AddSingleton<WebhookDispatcher>();
        services.AddSingleton<WebhookDelivery>();
        return services;
    }

    /// <summary>Push subscription endpoints (/users/me/push_subscriptions...) and POST /unfurl_link.</summary>
    public static IEndpointRouteBuilder MapPushModule(this IEndpointRouteBuilder app)
    {
        app.MapPushSubscriptions();
        app.MapUnfurlLinks();
        return app;
    }
}

/// <summary>Rails' <c>Room#push_later</c> → <c>Room::PushMessageJob</c> → <c>Room::MessagePusher</c>.</summary>
public sealed class MessagePushes(BackgroundQueue queue)
{
    /// <summary>Queues web push notifications for a new message (runs in the background).</summary>
    public void PushLater(long roomId, long messageId) => queue.Enqueue(new PushMessageJob(roomId, messageId));
}
