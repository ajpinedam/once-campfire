using System.Threading.Channels;

namespace Campfire.Web.Data;

/// <summary>
/// Group commit for hot write paths (posting messages). SQLite commits one writer at a time, so
/// under concurrency each post queued for the write lock, then paid for its own commit. Here the
/// writes that arrive while a batch is committing are run together in the next transaction —
/// each inside its own savepoint, so one failing write doesn't undo the others — and every caller
/// is completed only after that commit. Callers await instead of blocking a thread on the lock.
/// </summary>
public sealed class WriteBatcher(Database database, ILogger<WriteBatcher> logger) : BackgroundService
{
    private const int MaxBatch = 64;

    private readonly Channel<IWrite> _queue = Channel.CreateUnbounded<IWrite>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    /// <summary>
    /// Runs <paramref name="work"/> in the next batch transaction and returns its result once
    /// committed. The work must only touch the database through the <see cref="Sql"/> it's given.
    /// </summary>
    public Task<T> RunAsync<T>(Func<Sql, T> work)
    {
        var write = new Write<T>(work);
        if (!_queue.Writer.TryWrite(write))
        {
            throw new InvalidOperationException("The write batcher has stopped");
        }
        return write.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sql = database.Open(); // kept for the writer's lifetime: warm statements and pages
        var batch = new List<IWrite>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var write))
                {
                    batch.Add(write);
                }

                Exception? commitFailure = null;
                try
                {
                    sql.Transaction(tx =>
                    {
                        foreach (var write in batch)
                        {
                            write.Run(tx);
                        }
                    });
                }
                catch (Exception error)
                {
                    logger.LogError(error, "A batch of {Count} writes failed to commit", batch.Count);
                    commitFailure = error;
                }

                foreach (var write in batch)
                {
                    write.Complete(commitFailure);
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _queue.Writer.TryComplete();
            var stopped = new OperationCanceledException("The application is shutting down");
            foreach (var write in batch)
            {
                write.Complete(stopped);
            }
            while (_queue.Reader.TryRead(out var write))
            {
                write.Complete(stopped);
            }
        }
    }

    private interface IWrite
    {
        void Run(Sql transaction);
        void Complete(Exception? commitFailure);
    }

    private sealed class Write<T>(Func<Sql, T> work) : IWrite
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T? _result;
        private Exception? _error;

        public Task<T> Task => _completion.Task;

        public void Run(Sql transaction)
        {
            try
            {
                _result = transaction.Savepoint(work);
            }
            catch (Exception error)
            {
                _error = error;
            }
        }

        public void Complete(Exception? commitFailure)
        {
            if ((commitFailure ?? _error) is { } failure)
            {
                _completion.TrySetException(failure);
            }
            else
            {
                _completion.TrySetResult(_result!);
            }
        }
    }
}
