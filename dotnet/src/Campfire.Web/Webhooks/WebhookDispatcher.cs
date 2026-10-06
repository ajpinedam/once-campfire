using Campfire.Web.Jobs;

namespace Campfire.Web.Webhooks;

/// <summary>Rails' <c>User#deliver_webhook_later</c> → <c>Bot::WebhookJob</c> → <c>Webhook#deliver</c>.</summary>
public sealed class WebhookDispatcher(BackgroundQueue queue)
{
    /// <summary>
    /// Queues delivery of a message to a bot's webhook (no-op when the bot has none). The bot's
    /// text or file reply is posted back into the room. <paramref name="baseUrl"/> is the
    /// origin to build absolute URLs with (e.g. <c>https://chat.example.com</c>).
    /// </summary>
    public void DeliverLater(long botId, long messageId, string baseUrl) => queue.Enqueue(new DeliverWebhookJob(botId, messageId, baseUrl));
}

/// <summary>Rails' Bot::WebhookJob.</summary>
public sealed record DeliverWebhookJob(long BotId, long MessageId, string BaseUrl) : IBackgroundJob
{
    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<WebhookDelivery>().DeliverAsync(BotId, MessageId, BaseUrl, cancellationToken);
}
