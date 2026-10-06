namespace Campfire.Web.Data;

/// <summary>
/// Serializes this process's writes to SQLite. SQLite allows one writer at a time; when writers
/// collide, Microsoft.Data.Sqlite retries SQLITE_BUSY with a fixed 150 ms sleep, which turns a
/// burst of concurrent posts into tail latency. Queuing writers here instead makes them wait only
/// as long as the write ahead of them actually takes. (Another process writing the same database,
/// such as an admin command, still falls back to the busy timeout.)
///
/// Reentrant along a call flow, so a write inside a transaction — or a nested transaction on a
/// second connection — doesn't deadlock on the gate its own caller holds.
/// </summary>
#pragma warning disable CA1001 // The semaphore never allocates a wait handle (no AvailableWaitHandle), so it needs no disposal.
public sealed class WriteGate
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<int> _depth = new();

    /// <summary>Blocks until this flow may write; dispose the result to let the next writer in.</summary>
    public Held Enter()
    {
        if (_depth.Value > 0)
        {
            _depth.Value++;
            return new Held(this);
        }

        _gate.Wait();
        _depth.Value = 1;
        return new Held(this);
    }

    private void Exit()
    {
        if (--_depth.Value == 0)
        {
            _gate.Release();
        }
    }

    public readonly struct Held(WriteGate gate) : IDisposable
    {
        public void Dispose() => gate.Exit();
    }
}
