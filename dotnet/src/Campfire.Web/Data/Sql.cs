using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Data;

/// <summary>A named SQL parameter. Tuples convert implicitly: <c>("@id", 5L)</c>.</summary>
public readonly record struct SqlParam(string Name, object? Value)
{
    public static implicit operator SqlParam((string Name, object? Value) tuple) => new(tuple.Name, tuple.Value);
}

/// <summary>
/// One pooled SQLite connection plus its (optional) active transaction. Query classes in
/// <see cref="Campfire.Web.Data.Queries"/> are static functions over this type, so the same
/// code runs inside or outside a transaction and every command is bound to it correctly.
/// Not thread-safe: a request (or cable connection, or job) owns its own instance.
/// </summary>
public sealed class Sql : IDisposable
{
    private readonly Database _database;
    private PooledConnection? _connection;
    private SqliteTransaction? _transaction;

    /// <summary>The connection is taken from the pool on the first command, not before.</summary>
    internal Sql(Database database) => _database = database;

    public bool InTransaction => _transaction is not null;

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params ReadOnlySpan<SqlParam> parameters)
    {
        var command = Command(sql, parameters);
        using var reader = Run(sql, command, static command => command.ExecuteReader());
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }
        return results;
    }

    public T? First<T>(string sql, Func<SqliteDataReader, T> map, params ReadOnlySpan<SqlParam> parameters) where T : class
    {
        var command = Command(sql, parameters);
        using var reader = Run(sql, command, static command => command.ExecuteReader());
        return reader.Read() ? map(reader) : null;
    }

    public T? FirstValue<T>(string sql, Func<SqliteDataReader, T> map, params ReadOnlySpan<SqlParam> parameters) where T : struct
    {
        var command = Command(sql, parameters);
        using var reader = Run(sql, command, static command => command.ExecuteReader());
        return reader.Read() ? map(reader) : null;
    }

    public void Each(string sql, Action<SqliteDataReader> each, params ReadOnlySpan<SqlParam> parameters)
    {
        var command = Command(sql, parameters);
        using var reader = Run(sql, command, static command => command.ExecuteReader());
        while (reader.Read())
        {
            each(reader);
        }
    }

    /// <summary>Runs a write (or other non-query) statement; writes take the process write gate.</summary>
    public int Execute(string sql, params ReadOnlySpan<SqlParam> parameters)
    {
        using var write = _database.Writes.Enter();
        return Run(sql, Command(sql, parameters), static command => command.ExecuteNonQuery());
    }

    /// <summary>Runs an INSERT and returns the new row id.</summary>
    public long Insert(string sql, params ReadOnlySpan<SqlParam> parameters)
    {
        using var write = _database.Writes.Enter();
        var text = sql + " RETURNING id";
        return (long)Run(text, Command(text, parameters), static command => command.ExecuteScalar())!;
    }

    public long ScalarLong(string sql, params ReadOnlySpan<SqlParam> parameters) =>
        Run(sql, Command(sql, parameters), static command => command.ExecuteScalar()) switch
        {
            null or DBNull => 0,
            long value => value,
            var other => Convert.ToInt64(other, System.Globalization.CultureInfo.InvariantCulture)
        };

    public string? ScalarString(string sql, params ReadOnlySpan<SqlParam> parameters) =>
        Run(sql, Command(sql, parameters), static command => command.ExecuteScalar()) as string;

    public bool Exists(string sql, params ReadOnlySpan<SqlParam> parameters)
    {
        var text = $"SELECT EXISTS({sql})";
        return (long)Run(text, Command(text, parameters), static command => command.ExecuteScalar())! == 1;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in an IMMEDIATE transaction (matching the Rails app's
    /// <c>default_transaction_mode: immediate</c>). Nested calls join the outer transaction.
    /// </summary>
    public T Transaction<T>(Func<Sql, T> work)
    {
        if (_transaction is not null)
        {
            return work(this);
        }

        using var write = _database.Writes.Enter();
        _transaction = Connection.Sqlite.BeginTransaction(deferred: false);
        try
        {
            var result = work(this);
            _transaction.Commit();
            return result;
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public void Transaction(Action<Sql> work) => Transaction(sql => { work(sql); return true; });

    /// <summary>
    /// A savepoint inside the current transaction: <paramref name="work"/>'s writes are undone on
    /// failure without abandoning the rest of the transaction (used by group commit).
    /// </summary>
    internal T Savepoint<T>(Func<Sql, T> work)
    {
        if (_transaction is null)
        {
            throw new InvalidOperationException("Savepoints need an open transaction");
        }

        Execute("SAVEPOINT item");
        try
        {
            var result = work(this);
            Execute("RELEASE item");
            return result;
        }
        catch
        {
            Execute("ROLLBACK TO item");
            Execute("RELEASE item");
            throw;
        }
    }

    public void Dispose()
    {
        if (_transaction is not null)
        {
            _transaction.Rollback();
            _transaction.Dispose();
            _transaction = null;
        }

        if (_connection is not null)
        {
            _database.Return(_connection);
            _connection = null;
        }
    }

    private PooledConnection Connection => _connection ??= _database.Rent();

    private SqliteCommand Command(string sql, ReadOnlySpan<SqlParam> parameters)
    {
        var command = Connection.Prepared(sql);
        command.Transaction = _transaction;
        command.Parameters.Clear();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, ToDbValue(parameter.Value));
        }
        return command;
    }

    // A statement that failed mid-execution is discarded rather than reused in an unknown state.
    private TResult Run<TResult>(string sql, SqliteCommand command, Func<SqliteCommand, TResult> execute)
    {
        try
        {
            return execute(command);
        }
        catch
        {
            Connection.Evict(sql);
            throw;
        }
    }

    private static object ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime time => SqlTime.Format(time),
        bool flag => flag ? 1L : 0L,
        Enum => throw new ArgumentException("Convert enums to their stored representation before binding"),
        _ => value
    };
}

/// <summary>
/// A long-lived SQLite connection with its prepared statements. Microsoft.Data.Sqlite prepares a
/// command's SQL on every execution and finalizes it on dispose; keeping commands per connection,
/// keyed by their text, makes each statement a one-time cost — that was a third of a read request.
/// </summary>
internal sealed class PooledConnection : IDisposable
{
    private const int MaxCachedStatements = 512;
    private readonly Dictionary<string, SqliteCommand> _statements = new(StringComparer.Ordinal);

    public PooledConnection(string connectionString)
    {
        Sqlite = new SqliteConnection(connectionString);
        Sqlite.Open();
        using var pragma = Sqlite.CreateCommand();
        // Per-connection settings, applied once. journal_mode=WAL is persistent (set by the migrator).
        // mmap lets SQLite read pages straight from the OS page cache, without a read() per page.
        pragma.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA mmap_size = 268435456;";
        pragma.ExecuteNonQuery();
    }

    public SqliteConnection Sqlite { get; }

    public SqliteCommand Prepared(string sql)
    {
        if (_statements.TryGetValue(sql, out var command))
        {
            return command;
        }

        command = Sqlite.CreateCommand();
        command.CommandText = sql;
        if (_statements.Count < MaxCachedStatements)
        {
            _statements[sql] = command;
        }
        return command;
    }

    public void Evict(string sql)
    {
        if (_statements.Remove(sql, out var command))
        {
            command.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var command in _statements.Values)
        {
            command.Dispose();
        }
        _statements.Clear();
        Sqlite.Dispose();
    }
}

/// <summary>
/// The application database: a pool of long-lived connections (most recently used first, so the
/// warmest page and statement caches serve the next request) and the process write gate.
/// </summary>
public sealed class Database : IDisposable
{
    private const int MaxIdleConnections = 32;

    private readonly string _connectionString;
    private readonly ConcurrentStack<PooledConnection> _idle = new();
    private int _idleCount;
    private bool _disposed;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false, // pooled here, with statement caches
            ForeignKeys = true,
            DefaultTimeout = 5 // seconds; Microsoft.Data.Sqlite retries SQLITE_BUSY until this elapses
        }.ToString();

        Path = path;
    }

    public string Path { get; }

    internal WriteGate Writes { get; } = new();

    /// <summary>
    /// A unit of database work. The connection is taken from the pool on the first command — so a
    /// request (or WebSocket) that never queries never holds one — and returned on dispose.
    /// </summary>
    public Sql Open() => new(this);

    internal PooledConnection Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_idle.TryPop(out var connection))
        {
            Interlocked.Decrement(ref _idleCount);
            return connection;
        }
        return new PooledConnection(_connectionString);
    }

    internal void Return(PooledConnection connection)
    {
        if (_disposed || Interlocked.Increment(ref _idleCount) > MaxIdleConnections)
        {
            Interlocked.Decrement(ref _idleCount);
            connection.Dispose();
            return;
        }
        _idle.Push(connection);
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryPop(out var connection))
        {
            connection.Dispose();
        }
    }
}
