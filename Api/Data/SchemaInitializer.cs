namespace Api.Data;

// Runs the idempotent T-SQL schema script on startup, the same way the old
// Node server did in server/db/init.ts. SqlClient executes the whole
// multi-statement batch in one call, and the SET options at the top of the
// script apply to that batch.
public static class SchemaInitializer
{
    public static async Task RunAsync(Db db, string schemaSqlPath)
    {
        var sql = await File.ReadAllTextAsync(schemaSqlPath);
        await db.ExecuteAsync(sql);
    }
}
