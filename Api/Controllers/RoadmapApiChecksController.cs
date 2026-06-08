using Api.Auth;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

// Student-triggered API check runs, ported from server/routes/studentApiChecks.ts.
// Mounted under /api/roadmap and gated by the student JWT.
[ApiController]
[Route("api/roadmap")]
[StudentAuth]
public sealed class RoadmapApiChecksController : ControllerBase
{
    private readonly Db _db;
    private readonly ApiCheckRunner _runner;

    public RoadmapApiChecksController(Db db, ApiCheckRunner runner)
    {
        _db = db;
        _runner = runner;
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

            // Guard against concurrent runs for the same student.
            var currentRun = _runner.GetRunState(student.id);
            if (currentRun.status == "running")
                return Ok(new { status = "started" });

            // Start background check run.
            _runner.SetRunState(student.id, new ApiCheckRunner.RunState
            {
                status = "running",
                checkedSteps = new List<ApiCheckRunner.CheckedStep>(),
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

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
                    Console.Error.WriteLine($"[api-check-runner] {err}");
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
            Console.Error.WriteLine($"[run-api-checks] {err}");
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
            Console.Error.WriteLine($"[check-status] {err}");
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
