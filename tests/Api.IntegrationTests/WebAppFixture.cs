using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace Api.IntegrationTests;

// Hosts the real API in-process (WebApplicationFactory) against a dedicated test
// database on the local SQL Server container. The DB is dropped before the run so
// the app recreates the schema and re-seeds deterministically on startup.
//
// Requires the SQL Server container to be up:  docker compose up -d sqlserver
public sealed class WebAppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Password = "Csub_Local_Dev_2026!";
    private static string MasterConn => $"Server=localhost,1433;Database=master;User Id=sa;Password={Password};TrustServerCertificate=True;Encrypt=False";
    private static string TestConn => $"Server=localhost,1433;Database=csub_admissions_test;User Id=sa;Password={Password};TrustServerCertificate=True;Encrypt=False";

    public string AdminToken { get; private set; } = "";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so the 50-student sample seed runs (it is deterministic).
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", TestConn);
        builder.UseSetting("Jwt:Secret", "integration-test-secret-0123456789-abcdefghijklmnopqrstuvwxyz");
        builder.UseSetting("RateLimiting:Disabled", "true");
        builder.UseSetting("Admin:DefaultEmail", "admin@csub.edu");
        builder.UseSetting("Admin:DefaultPassword", "admin123");
        builder.UseSetting("LocalLogin:Username", "localadmin");
        builder.UseSetting("LocalLogin:Password", "Local_Admin_2026!");
        builder.UseSetting("Integration:DefaultName", "PeopleSoft Dev");
        builder.UseSetting("Integration:DefaultKey", "dev-integration-key");
    }

    public async Task InitializeAsync()
    {
        // Drop the test DB so startup rebuilds it fresh.
        await using (var conn = new SqlConnection(MasterConn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "IF DB_ID('csub_admissions_test') IS NOT NULL " +
                "BEGIN ALTER DATABASE csub_admissions_test SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                "DROP DATABASE csub_admissions_test; END;";
            await cmd.ExecuteNonQueryAsync();
        }

        // First client build triggers EnsureDatabase + schema + seed. Then cache an admin token.
        using var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = "admin@csub.edu", password = "admin123" });
        res.EnsureSuccessStatusCode();
        AdminToken = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    // ---- helpers for test classes ----

    public HttpClient Anonymous() => CreateClient();

    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    public HttpClient Integration()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Integration-Key", "dev-integration-key");
        return client;
    }

    // Creates (or reuses) a student via dev-login and returns an authed client + token.
    public async Task<(HttpClient Client, string Token)> StudentAsync(string name, string email)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/dev-login", new { name, email });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<WebAppFixture>;
