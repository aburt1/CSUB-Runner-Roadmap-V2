using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Api.Data;

// Thin wrapper over Dapper + SqlClient. This is the only place that opens
// database connections, exposing a small surface (QueryOne / QueryAll /
// Execute / Transaction) that every caller goes through.
//
// Boring on purpose: every query is hand-written T-SQL passed in by the caller,
// parameters are passed as a plain anonymous object (e.g. new { id, term_id }).
// No repositories, no LINQ, no query builder.
//
// Transient faults are retried with exponential backoff, but retry safety depends on
// what the statement does and when the fault happened:
//   - Opening the connection failed: nothing was sent — always safe to retry.
//   - READ statements: always safe to retry, whatever the error.
//   - WRITE statements / transactions: retried ONLY for errors that guarantee the
//     statement did not take effect (deadlock victim, throttling, request rejected).
//     Connection drops and timeouts mid-command are AMBIGUOUS — the server may have
//     already committed — so retrying could double-apply the write. Those surface
//     as errors instead.
public sealed class Db
{
    static Db()
    {
        // The schema uses DATETIME2 everywhere, but Dapper binds DateTime parameters as
        // legacy 'datetime' by default (3.33ms precision, min year 1753) — comparisons
        // against stored values can silently mismatch. Bind as datetime2 to match.
        SqlMapper.AddTypeMap(typeof(DateTime), System.Data.DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), System.Data.DbType.DateTime2);
    }

    private const int MaxAttempts = 4;
    // Backoff sleeps between the 4 attempts are 200/400/800ms (~1.4s total). That is
    // only the idle wait BETWEEN tries — it is NOT the worst-case latency. The
    // connection string supplies no explicit Connect/Command timeout, so SqlClient
    // defaults apply (15s connect, 30s command), and each attempt can itself block on
    // those before failing. A stalled server can therefore hold a request for roughly
    // the backoff sleeps PLUS up to MaxAttempts × (connect + command) timeouts, far
    // longer than 1.4s. Deploy config (not this file) must set timeouts low enough to
    // stay under the client request timeout; HealthController pins its own short ones.
    private const int BaseRetryDelayMs = 200;

    // Errors that guarantee the statement/transaction did NOT take effect: the request
    // was rejected before running (throttling/resource limits) or was rolled back
    // (deadlock victim, in-memory OLTP validation). Safe to retry reads AND writes.
    private static readonly HashSet<int> SafeTransientErrorNumbers =
    [
        49920, 49919, 49918, 41839, 41325, 41305, 41302, 41301, 40501,
        10936, 10929, 10928, 1205,
    ];

    // Errors that can strike mid-command and leave the outcome unknown (failover,
    // network drop, client-side timeout — the server may have committed the work
    // before the client saw the failure). Safe to retry only for reads.
    private static readonly HashSet<int> AmbiguousTransientErrorNumbers =
    [
        40613, 40197, 10060, 10054, 10053, 4221, 233, 121, 64, 20, -2,
    ];

    internal enum RetryKind { Read, Write }

    private readonly string _connectionString;
    private readonly ILogger<Db>? _logger;

    // When this Db represents an open transaction, these are set and every
    // call runs on that single connection/transaction instead of a new one.
    private readonly SqlConnection? _txConnection;
    private readonly SqlTransaction? _transaction;

    public Db(string connectionString, ILogger<Db>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    private Db(SqlConnection txConnection, SqlTransaction transaction, ILogger<Db>? logger)
    {
        _connectionString = txConnection.ConnectionString;
        _txConnection = txConnection;
        _transaction = transaction;
        _logger = logger;
    }

    // Returns the first row mapped to T, or null when there are no rows. READ semantics:
    // do not pass INSERT/UPDATE/DELETE here (use InsertReturningAsync / ExecuteAsync),
    // because reads are retried even after ambiguous mid-command failures.
    public async Task<T?> QueryOneAsync<T>(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return await _txConnection.QueryFirstOrDefaultAsync<T>(sql, param, _transaction);

        return await RetryAsync(RetryKind.Read, connection =>
            connection.QueryFirstOrDefaultAsync<T>(sql, param));
    }

    // Returns every row mapped to T. READ semantics (see QueryOneAsync).
    public async Task<IReadOnlyList<T>> QueryAllAsync<T>(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return (await _txConnection.QueryAsync<T>(sql, param, _transaction)).AsList();

        return await RetryAsync<IReadOnlyList<T>>(RetryKind.Read, async connection =>
            (await connection.QueryAsync<T>(sql, param)).AsList());
    }

    // Runs an INSERT/UPDATE/DELETE/DDL statement and returns rows affected.
    // WRITE semantics: only definitely-not-applied transient errors are retried.
    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return await _txConnection.ExecuteAsync(sql, param, _transaction);

        return await RetryAsync(RetryKind.Write, connection =>
            connection.ExecuteAsync(sql, param));
    }

    // Runs a write that returns a row (e.g. INSERT ... SELECT SCOPE_IDENTITY()).
    // WRITE semantics — retrying an ambiguous failure here could insert twice.
    public async Task<T?> InsertReturningAsync<T>(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return await _txConnection.QueryFirstOrDefaultAsync<T>(sql, param, _transaction);

        return await RetryAsync(RetryKind.Write, connection =>
            connection.QueryFirstOrDefaultAsync<T>(sql, param));
    }

    // Runs `work` inside a single transaction. The Db handed to `work` routes
    // every query through that transaction; commit on success, rollback on throw.
    public async Task<T> TransactionAsync<T>(Func<Db, Task<T>> work)
    {
        // Already inside a transaction — join it (the outer commit/rollback governs),
        // so callers can safely wrap a unit of work that may itself be nested.
        if (_txConnection is not null)
            return await work(this);

        // WRITE semantics: the whole transaction re-runs only on errors that guarantee
        // it did not commit (e.g. deadlock victim — rolled back by the server). An
        // ambiguous failure during commit is NOT retried: the commit may have succeeded.
        return await RetryAsync(RetryKind.Write, async connection =>
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            try
            {
                var scoped = new Db(connection, transaction, _logger);
                var result = await work(scoped);
                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                // Rollback can itself throw when the connection/transaction is already
                // dead (zombied tx after a drop). Swallow that: the original exception
                // is the one that matters, and masking it would also defeat the
                // transient classification above.
                try { await transaction.RollbackAsync(); }
                catch { /* connection gone; server rolls back on its own */ }
                throw;
            }
        });
    }

    // Overload for transactions that don't return a value.
    public Task TransactionAsync(Func<Db, Task> work) =>
        TransactionAsync(async db => { await work(db); return true; });

    // Opens a fresh connection and runs `operation`, retrying with exponential backoff.
    // Open failures are always retryable (nothing was sent); after the connection is
    // open, what is retryable depends on the operation kind (see IsRetryable).
    private async Task<T> RetryAsync<T>(RetryKind kind, Func<SqlConnection, Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            var opened = false;
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                opened = true;
                return await operation(connection);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsRetryable(ex, kind, opened))
            {
                var delayMs = BaseRetryDelayMs * Math.Pow(2, attempt - 1);
                // A query that succeeds on a later attempt leaves no other trace, so log
                // here: an operator needs this signal to see an intermittently unhealthy DB
                // before retries exhaust. Logging only — no change to query semantics.
                _logger?.LogWarning(
                    ex,
                    "Transient DB fault ({Kind}) on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms: {Message}",
                    kind, attempt, MaxAttempts, delayMs, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs));
            }
        }
    }

    private static bool IsRetryable(Exception ex, RetryKind kind, bool opened)
    {
        // TimeoutException covers connection-pool exhaustion (SqlConnection.OpenAsync can
        // throw it directly when the pool wait times out, before any command is sent), so
        // it is retryable wherever a read is — i.e. on the open path or for any read.
        if (ex is TimeoutException)
            return !opened || kind == RetryKind.Read;

        // A SqlException can carry several SqlError entries; retry if ANY one of them is
        // classified retryable for this (kind, opened) — the per-number rule lives in
        // IsRetryableForNumber so it can be unit-tested without a SqlException.
        if (ex is SqlException sql)
        {
            foreach (SqlError error in sql.Errors)
                if (IsRetryableForNumber(error.Number, kind, opened))
                    return true;
        }
        return false;
    }

    // Core retry-classification invariant, isolated from SqlException so it can be tested
    // directly (SqlException is sealed with no public ctor). Behavior must match the
    // per-error decision IsRetryable made before this was extracted:
    //   - Failure before/while opening (!opened): nothing was sent — retry on any transient.
    //   - READ statements: always safe to retry, on any transient.
    //   - WRITE statements: retry ONLY errors guaranteed not to have taken effect.
    internal static bool IsRetryableForNumber(int number, RetryKind kind, bool opened)
    {
        if (!opened || kind == RetryKind.Read)
            return SafeTransientErrorNumbers.Contains(number) || AmbiguousTransientErrorNumbers.Contains(number);

        return SafeTransientErrorNumbers.Contains(number);
    }
}
