using Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

// GET /api/health — same shape as the old Express health check.
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly Db _db;

    public HealthController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        string dbStatus;
        try
        {
            await _db.QueryOneAsync<int>("SELECT 1");
            dbStatus = "connected";
        }
        catch
        {
            dbStatus = "disconnected";
        }

        return Ok(new
        {
            status = "ok",
            db = dbStatus,
            timestamp = DateTime.UtcNow.ToString("o"),
        });
    }
}
