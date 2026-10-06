using System.Threading.Channels;
using Campfire.Web.Data;

namespace Campfire.Web.Push;

// Inside the namespace, so sibling namespaces (Campfire.Web.Webhooks, Campfire.Web.Features.Rooms, ...) can't shadow query classes.
using Campfire.Web.Data.Queries;

/// <summary>
/// Rails' WebPush::Pool: deliveries are queued (up to 10,000; beyond that they're dropped, like the
/// executor's rejected tasks) and sent by up to 50 concurrent workers. Subscriptions the push
/// service reports gone are deleted one at a time on a separate worker.
/// </summary>
public sealed class WebPushPool(WebPushSender sender, Database database, ILogger<WebPushPool> logger) : BackgroundService
{
    private const int Concurrency = 50;

    private readonly Channel<PushDelivery> _deliveries = Channel.CreateBounded<PushDelivery>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private readonly Channel<long> _invalidations = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });

    private long _completedDeliveries;
    private long _completedInvalidations;

    /// <summary>Deliveries attempted so far, whatever their outcome (Rails' completed_task_count; for tests).</summary>
    public long CompletedDeliveries => Interlocked.Read(ref _completedDeliveries);

    /// <summary>Invalid subscriptions handled so far (for tests).</summary>
    public long CompletedInvalidations => Interlocked.Read(ref _completedInvalidations);

    public void Queue(PushDelivery delivery)
    {
        if (!_deliveries.Writer.TryWrite(delivery))
        {
            logger.LogWarning("Web push queue full; dropped a notification");
        }
    }

    public void Queue(IEnumerable<PushDelivery> deliveries)
    {
        foreach (var delivery in deliveries)
        {
            Queue(delivery);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => DeliverAsync(stoppingToken)).Append(InvalidateAsync(stoppingToken)));

    private async Task DeliverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in _deliveries.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (await sender.DeliverAsync(delivery, stoppingToken) == WebPushOutcome.Invalid)
                    {
                        _invalidations.Writer.TryWrite(delivery.SubscriptionId);
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("Error in web push delivery: {Error} {Message}", error.GetType().Name, error.Message);
                }
                finally
                {
                    Interlocked.Increment(ref _completedDeliveries);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task InvalidateAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var subscriptionId in _invalidations.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    logger.LogInformation("Destroying push subscription: {Id}", subscriptionId);
                    using var sql = database.Open();
                    PushSubscriptions.Delete(sql, subscriptionId);
                }
                catch (Exception error)
                {
                    logger.LogError("Error invalidating push subscription: {Error} {Message}", error.GetType().Name, error.Message);
                }
                finally
                {
                    Interlocked.Increment(ref _completedInvalidations);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
