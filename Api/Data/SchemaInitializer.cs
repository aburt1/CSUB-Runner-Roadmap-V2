using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Api.Data;

// Database bootstrap on startup, the same way the old Node server did in
// server/db/init.ts: make sure the database exists, then run the idempotent
// T-SQL schema script (SqlClient runs the whole multi-statement batch at once).
public static class SchemaInitializer
{
    // Bump when schema.sql changes; recorded (append-only) in dbo.schema_version so an
    // operator can see which schema versions a database has had applied.
    public const string CurrentSchemaVersion = "2026.06.10";

    // Connects to master and creates the target database if it doesn't exist.
    // Retries to tolerate SQL Server still warming up (e.g. in docker-compose).
    //
    // DEV/TEST ONLY: in production the database is provisioned by a DBA and the app's
    // login is not expected to hold server-level CREATE DATABASE rights. Program.cs
    // only calls this when Database:AutoCreate is enabled (default: non-Production).
    public static async Task EnsureDatabaseAsync(string connectionString, ILogger? logger = null)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var dbName = builder.InitialCatalog;
        if (string.IsNullOrEmpty(dbName)) return;
        if (!Regex.IsMatch(dbName, "^[A-Za-z0-9_]+$"))
            throw new InvalidOperationException($"Unsafe database name in connection string: {dbName}");

        builder.InitialCatalog = "master";
        var masterConnectionString = builder.ConnectionString;

        Exception? last = null;
        for (var attempt = 1; attempt <= 15; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(masterConnectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{dbName}') IS NULL CREATE DATABASE [{dbName}];";
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                // Without this, a cold start (SQL Server still warming up) is silent for up
                // to 45s before success or the final throw. Logging only.
                logger?.LogWarning(
                    "EnsureDatabase attempt {Attempt}/15 failed; retrying in 3s: {Message}",
                    attempt, ex.Message);
                await Task.Delay(3000);
            }
        }
        throw new InvalidOperationException("Could not connect to SQL Server to ensure the database exists.", last);
    }

    // Applies the idempotent schema, then records the current schema version
    // (append-only; never drops or alters existing data).
    public static async Task RunAsync(Db db, string schemaSqlPath)
    {
        var sql = await File.ReadAllTextAsync(schemaSqlPath);
        await db.ExecuteAsync(sql);

        await db.ExecuteAsync(
            @"IF NOT EXISTS (SELECT 1 FROM schema_version WHERE version = @version)
              INSERT INTO schema_version (version) VALUES (@version);",
            new { version = CurrentSchemaVersion });
    }
}
