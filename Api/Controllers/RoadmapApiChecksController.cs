using Api.Auth;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

// Student-triggered API check runs, ported from server/routes/studentApiChecks.ts.
// Mounted under /api/roadmap and gated by the student JWT.
//
// Actions catch locally so the log line names the operation; the global handler in
// Program.cs would log it generically.
[ApiController]
[Route("api/roadmap")]
[StudentAuth]
public sealed class RoadmapApiChecksController : ControllerBase
{
    private readonly Db _db;
    private readonly ApiCheckRunner _runner;
    private readonly ILogger<RoadmapApiChecksController> _logger;

    public RoadmapApiChecksController(Db db, ApiCheckRunner runner, ILogger<RoadmapApiChecksController> logger)
    {
        _db = db;
        _runner = runner;
        _logger = logger;
    }

    // POST /api/roadmap/run-api-checks
    [HttpPost("run-api-checks")]
    public async Task<IActionResult> RunApiChecks()
    {
        try
        {
            var studentId = HttpContext.StudentId();
            var student = await _db.QueryOneAsync<StudentForApiCheck>(
                "SELECT id, email, emplid, term_id, last_api_check_at FROM students WHERE id = @studentId",
                new { studentId });

            if (student is null)
                return NotFound(new { error = "Student not found" });

            // 5-minute throttle.
            if (student.last_api_check_at.HasValue)
            {
                var elapsed = DateTime.UtcNow - student.last_api_check_at.Value;
                if (elapsed.TotalMilliseconds < 5 * 60 * 1000)
                    return Ok(new { status = "skipped" });
            }

            // Atomically claim the run slot — a get-then-set here would let two
            // concurrent requests both start background runs for the same student.
            var claimed = _runner.TryBeginRun(student.id, new ApiCheckRunner.RunState
            {
                status = "running",
                checkedSteps = new List<ApiCheckRunner.CheckedStep>(),
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            // A run is already in flight — report 'started' anyway so the client polls
            // check-status (same contract as the old server).
            if (!claimed)
                return Ok(new { status = "started" });

            var studentForCheck = new ApiCheckRunner.StudentForCheck
            {
                id = student.id,
                email = student.email ?? "",
                emplid = student.emplid,
                term_id = student.term_id,
            };

            // Fire-and-forget; the runner is a singleton so the work outlives the request.
            _ = Task.Run(async () =>
            {
                try
                {
                    var checkedSteps = await _runner.RunApiChecksForStudentAsync(studentForCheck);
                    _runner.SetRunState(student.id, new ApiCheckRunner.RunState
                    {
                        status = "complete",
                        checkedSteps = checkedSteps,
                        startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                }
                catch (Exception err)
                {
                    _logger.LogError(err, "Background API check run failed");
                    _runner.SetRunState(student.id, new ApiCheckRunner.RunState
                    {
                        status = "complete",
                        checkedSteps = new List<ApiCheckRunner.CheckedStep>(),
                        startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                }
            });

            return Ok(new { status = "started" });
        }
        catch (Exception err)
        {
            _logger.LogError(err, "run-api-checks failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // GET /api/roadmap/check-status
    [HttpGet("check-status")]
    public IActionResult CheckStatus()
    {
        try
        {
            var studentId = HttpContext.StudentId();
            var state = _runner.GetRunState(studentId ?? "");
            return Ok(new { status = state.status, checkedSteps = state.checkedSteps });
        }
        catch (Exception err)
        {
            _logger.LogError(err, "check-status failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    private sealed class StudentForApiCheck
    {
        public string id { get; set; } = "";
        public string? email { get; set; }
        public string? emplid { get; set; }
        public int? term_id { get; set; }
        public DateTime? last_api_check_at { get; set; }
    }
}
