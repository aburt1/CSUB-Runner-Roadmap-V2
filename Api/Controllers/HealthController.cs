using Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

// Health probes:
//   GET /api/health        — legacy combined check (always 200, reports db status)
//   GET /api/health/live   — liveness: process is up, no dependencies (always 200)
//   GET /api/health/ready  — readiness: probes the DB, returns 503 when it is unreachable
// Orchestrators (k8s/compose) should use /live for liveness and /ready for readiness.
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly Db _db;

    public HealthController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var connected = await DbReachableAsync();
        return Ok(new
        {
            status = "ok",
            db = connected ? "connected" : "disconnected",
            timestamp = DateTime.UtcNow.ToString("o"),
        });
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
            await _db.QueryOneAsync<int>("SELECT 1");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
