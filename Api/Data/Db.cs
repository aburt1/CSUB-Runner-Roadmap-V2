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
public sealed class Db
{
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

    // Returns the first row mapped to T, or null when there are no rows.
    public async Task<T?> QueryOneAsync<T>(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return await _txConnection.QueryFirstOrDefaultAsync<T>(sql, param, _transaction);

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<T>(sql, param);
    }

    // Returns every row mapped to T.
    public async Task<IReadOnlyList<T>> QueryAllAsync<T>(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return (await _txConnection.QueryAsync<T>(sql, param, _transaction)).AsList();

        await using var connection = new SqlConnection(_connectionString);
        return (await connection.QueryAsync<T>(sql, param)).AsList();
    }

    // Runs an INSERT/UPDATE/DELETE/DDL statement and returns rows affected.
    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        if (_txConnection is not null)
            return await _txConnection.ExecuteAsync(sql, param, _transaction);

        await using var connection = new SqlConnection(_connectionString);
        return await connection.ExecuteAsync(sql, param);
    }

    // Runs `work` inside a single transaction. The Db handed to `work` routes
    // every query through that transaction; commit on success, rollback on throw.
    public async Task<T> TransactionAsync<T>(Func<Db, Task<T>> work)
    {
        // Already inside a transaction — join it (the outer commit/rollback governs),
        // so callers can safely wrap a unit of work that may itself be nested.
        if (_txConnection is not null)
            return await work(this);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
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
            await transaction.RollbackAsync();
            throw;
        }
    }

    // Overload for transactions that don't return a value.
    public Task TransactionAsync(Func<Db, Task> work) =>
        TransactionAsync(async db => { await work(db); return true; });
}
