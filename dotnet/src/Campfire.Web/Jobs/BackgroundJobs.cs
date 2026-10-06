using System.Threading.Channels;

namespace Campfire.Web.Jobs;

/// <summary>
/// A unit of deferred work (Rails' ActiveJob). Jobs are immutable records carrying ids, not
/// entities, and reload what they need when they run — the record may have changed or gone.
/// </summary>
public interface IBackgroundJob
{
    Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken);
}

/// <summary>
/// In-process job queue replacing Resque + Redis: Campfire is a single-machine deployment, so a
/// bounded channel drained by a few workers is all the machinery needed. Jobs still pending at
/// shutdown are dropped, as with an in-memory queue — they're notifications, not ledger entries.
/// </summary>
public sealed class BackgroundQueue
{
    private readonly Channel<IBackgroundJob> _channel = Channel.CreateBounded<IBackgroundJob>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false
    });

    private readonly ILogger<BackgroundQueue> _logger;

    public BackgroundQueue(ILogger<BackgroundQueue> logger) => _logger = logger;

    /// <summary>Rails' <c>perform_later</c>.</summary>
    public void Enqueue(IBackgroundJob job)
    {
        if (!_channel.Writer.TryWrite(job))
        {
            _logger.LogWarning("Background queue full; dropped {Job}", job.GetType().Name);
        }
    }

    internal ChannelReader<IBackgroundJob> Reader => _channel.Reader;
}

/// <summary>Drains <see cref="BackgroundQueue"/> with a fixed number of concurrent workers.</summary>
public sealed class BackgroundWorker(BackgroundQueue queue, IServiceScopeFactory scopes, ILogger<BackgroundWorker> logger) : BackgroundService
{
    private const int Workers = 4;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Work(stoppingToken)));

    private async Task Work(CancellationToken stoppingToken)
    {
        try
        {
            await Drain(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task Drain(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await job.ExecuteAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Background job {Job} failed", job.GetType().Name);
            }
        }
    }
}
