using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Api.Controllers;

// Health probes:
//   GET /api/health/live   — liveness: process is up, no dependencies (always 200)
//   GET /api/health/ready  — readiness: probes the DB, returns 503 when it is unreachable
// Orchestrators (k8s/compose) should use /live for liveness and /ready for readiness.
//
// The DB probe deliberately bypasses Db's transient-retry layer and uses a short
// 3-second connect/command timeout: a probe that takes ~a minute of retries and
// full connect timeouts to report "down" defeats its purpose.
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly string _probeConnectionString;

    public HealthController(IConfiguration config)
    {
        var builder = new SqlConnectionStringBuilder(config.GetConnectionString("Default"))
        {
            ConnectTimeout = 3,
        };
        _probeConnectionString = builder.ConnectionString;
    }

    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "ok", timestamp = DateTime.UtcNow.ToString("o") });

    [HttpGet("ready")]
    public async Task<IActionResult> Ready()
    {
        var connected = await DbReachableAsync();
        var body = new { status = connected ? "ready" : "not_ready", db = connected ? "connected" : "disconnected", timestamp = DateTime.UtcNow.ToString("o") };
        return connected ? Ok(body) : StatusCode(503, body);
    }

    private async Task<bool> DbReachableAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_probeConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 3;
            await command.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
