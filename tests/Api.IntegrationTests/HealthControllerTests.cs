using Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.IntegrationTests;

// Coverage for the health probes:
//   GET /api/health/live  — always 200 (no dependencies)
//   GET /api/health/ready — 200 {db:connected} when the DB is reachable,
//                           503 {db:disconnected} when it is not.
// The 503 branch is what orchestrators (k8s/compose readiness) depend on.
//
// HealthController is exercised directly rather than over HTTP: the 503 path needs an
// unreachable DB, and routing that through WebApplicationFactory would fail app
// STARTUP (schema-init/seed run against the same connection string before the server
// is ready), not the readiness probe. The controller builds its own probe connection
// string from ConnectionStrings:Default and pins a 3s ConnectTimeout, so the
// unreachable case returns quickly. Uses the same live test DB the fixture seeds.
//
// Joins the "api" collection (and takes the fixture) only to guarantee the seeded
// test DB exists before Ready_reports_connected runs — the controller itself is
// constructed directly, not pulled from the host.
[Collection("api")]
public class HealthControllerTests
{
    public HealthControllerTests(WebAppFixture fx) => _ = fx;

    private const string Password = "Csub_Local_Dev_2026!";
    private static string TestConn =>
        $"Server=localhost,1433;Database=csub_admissions_test;User Id=sa;Password={Password};TrustServerCertificate=True;Encrypt=False";

    // A port nothing listens on; the controller's 3s ConnectTimeout makes this fail fast.
    private static string UnreachableConn =>
        "Server=127.0.0.1,1;Database=unreachable;User Id=sa;Password=nope;TrustServerCertificate=True;Encrypt=False";

    private static HealthController Build(string connectionString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
            })
            .Build();
        return new HealthController(config, NullLogger<HealthController>.Instance);
    }

    private static T GetProp<T>(object body, string name)
    {
        var prop = body.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(body)!;
    }

    [Fact]
    public void Live_is_always_ok()
    {
        var result = Build(UnreachableConn).Live();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ok", GetProp<string>(ok.Value!, "status"));
    }

    [Fact]
    public async Task Ready_reports_connected_when_db_is_up()
    {
        var result = await Build(TestConn).Ready();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ready", GetProp<string>(ok.Value!, "status"));
        Assert.Equal("connected", GetProp<string>(ok.Value!, "db"));
    }

    [Fact]
    public async Task Ready_returns_503_disconnected_when_db_is_unreachable()
    {
        var result = await Build(UnreachableConn).Ready();
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, obj.StatusCode);
        Assert.Equal("not_ready", GetProp<string>(obj.Value!, "status"));
        Assert.Equal("disconnected", GetProp<string>(obj.Value!, "db"));
    }
}
