using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Api.Data;

// Database bootstrap on startup, the same way the old Node server did in
// server/db/init.ts: make sure the database exists, then run the idempotent
// T-SQL schema script (SqlClient runs the whole multi-statement batch at once).
public static class SchemaInitializer
{
    // Connects to master and creates the target database if it doesn't exist.
    // Retries to tolerate SQL Server still warming up (e.g. in docker-compose).
    public static async Task EnsureDatabaseAsync(string connectionString)
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
                await Task.Delay(3000);
            }
        }
        throw new InvalidOperationException("Could not connect to SQL Server to ensure the database exists.", last);
    }

    public static async Task RunAsync(Db db, string schemaSqlPath)
    {
        var sql = await File.ReadAllTextAsync(schemaSqlPath);
        await db.ExecuteAsync(sql);
    }
}
