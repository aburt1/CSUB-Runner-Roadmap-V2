using Dapper;
using Microsoft.Data.SqlClient;

namespace Api.Data;

// Thin wrapper over Dapper + SqlClient. This is the only place that opens
// database connections. It mirrors the old Node `server/db/pool.ts` helper
// (queryOne / queryAll / execute / transaction) so the ported code reads the same.
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
    private const int MaxAttempts = 4;

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

    private enum RetryKind { Read, Write }

    private readonly string _connectionString;

    // When this Db represents an open transaction, these are set and every
    // call runs on that single connection/transaction instead of a new one.
    private readonly SqlConnection? _txConnection;
    private readonly SqlTransaction? _transaction;

    public Db(string connectionString)
    {
        _connectionString = connectionString;
    }

    private Db(SqlConnection txConnection, SqlTransaction transaction)
    {
        _connectionString = txConnection.ConnectionString;
        _txConnection = txConnection;
        _transaction = transaction;
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
                var scoped = new Db(connection, transaction);
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
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)));
            }
        }
    }

    private static bool IsRetryable(Exception ex, RetryKind kind, bool opened)
    {
        // Failure before/while opening: no command was sent, retry on any transient error.
        if (!opened)
            return IsInSet(ex, SafeTransientErrorNumbers) || IsInSet(ex, AmbiguousTransientErrorNumbers) || ex is TimeoutException;

        // Reads can retry everything; writes only what is guaranteed not applied.
        return kind switch
        {
            RetryKind.Read => IsInSet(ex, SafeTransientErrorNumbers) || IsInSet(ex, AmbiguousTransientErrorNumbers) || ex is TimeoutException,
            _ => IsInSet(ex, SafeTransientErrorNumbers),
        };
    }

    private static bool IsInSet(Exception ex, HashSet<int> numbers)
    {
        if (ex is SqlException sql)
        {
            foreach (SqlError error in sql.Errors)
                if (numbers.Contains(error.Number))
                    return true;
        }
        return false;
    }
}
