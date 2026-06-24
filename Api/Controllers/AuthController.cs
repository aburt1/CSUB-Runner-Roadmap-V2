using Api.Auth;
using Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

// Student authentication, ported from server/routes/auth.ts.
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly Db _db;
    private readonly JwtService _jwt;
    private readonly AzureAdTokenValidator _azure;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AuthController> _logger;

    public AuthController(Db db, JwtService jwt, AzureAdTokenValidator azure, IHostEnvironment env, ILogger<AuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _azure = azure;
        _env = env;
        _logger = logger;
    }

    public sealed record DevLoginRequest(string? Name, string? Email);
    public sealed record SsoRequest(string? IdToken);

    // POST /api/auth/dev-login — dev/POC login (disabled in production).
    [HttpPost("dev-login")]
    public async Task<IActionResult> DevLogin([FromBody] DevLoginRequest? body)
    {
        if (_env.IsProduction())
            return NotFound(new { error = "Not found" });

        var name = body?.Name;
        var email = body?.Email;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
            return BadRequest(new { error = "Name and email are required" });

        var student = await _db.QueryOneAsync<StudentLite>(
            "SELECT id, display_name, email FROM students WHERE email = @email", new { email });

        if (student is null)
        {
            var studentId = Guid.NewGuid().ToString();
            var termId = await _db.QueryOneAsync<int?>("SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC");
            await _db.ExecuteAsync(
                "INSERT INTO students (id, display_name, email, term_id) VALUES (@studentId, @name, @email, @termId)",
                new { studentId, name, email, termId });
            await AutoCompleteAcceptedStepAsync(studentId, termId);
            student = new StudentLite { id = studentId, display_name = name, email = email };
        }

        var token = _jwt.IssueStudentToken(student.id, student.email ?? "");
        return Ok(new { token, student = new { id = student.id, displayName = student.display_name, email = student.email } });
    }

    // POST /api/auth/sso — Azure AD SSO login.
    [HttpPost("sso")]
    public async Task<IActionResult> Sso([FromBody] SsoRequest? body)
    {
        if (!_azure.IsConfigured)
            return StatusCode(501, new { error = "Azure AD SSO is not configured" });
        if (string.IsNullOrEmpty(body?.IdToken))
            return BadRequest(new { error = "idToken is required" });

        string oid, email, name;
        try
        {
            var claims = await _azure.ValidateAsync(body.IdToken);
            oid = claims.oid;
            email = claims.email ?? "";
            name = claims.name ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Student SSO token validation failed from {RemoteIp}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Invalid or expired token" });
        }

        var student = await _db.QueryOneAsync<StudentLite>(
            "SELECT id, display_name, email FROM students WHERE azure_id = @oid", new { oid });

        if (student is not null)
        {
            await _db.ExecuteAsync(
                "UPDATE students SET display_name = @name, email = @email WHERE id = @id",
                new { name, email, id = student.id });
            student.display_name = name;
            student.email = email;
        }
        else
        {
            var studentId = Guid.NewGuid().ToString();
            var termId = await _db.QueryOneAsync<int?>("SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC");
            await _db.ExecuteAsync(
                "INSERT INTO students (id, display_name, email, azure_id, term_id) VALUES (@studentId, @name, @email, @oid, @termId)",
                new { studentId, name, email, oid, termId });
            await AutoCompleteAcceptedStepAsync(studentId, termId);
            student = new StudentLite { id = studentId, display_name = name, email = email };
        }

        var token = _jwt.IssueStudentToken(student.id, student.email ?? "");
        return Ok(new { token, student = new { id = student.id, displayName = student.display_name, email = student.email } });
    }

    // GET /api/auth/me — current student session.
    [HttpGet("me")]
    [StudentAuth]
    public async Task<IActionResult> Me()
    {
        var studentId = HttpContext.StudentId();
        var student = await _db.QueryOneAsync<StudentMe>(
            "SELECT id, display_name, email, created_at FROM students WHERE id = @studentId", new { studentId });

        if (student is null)
            return NotFound(new { error = "Student not found" });

        return Ok(new
        {
            id = student.id,
            displayName = student.display_name,
            email = student.email,
            createdAt = student.created_at,
        });
    }

    // New students start with the "accepted" step already completed.
    private async Task AutoCompleteAcceptedStepAsync(string studentId, int? termId)
    {
        if (termId is null) return;
        var stepId = await _db.QueryOneAsync<int?>(
            "SELECT TOP 1 id FROM steps WHERE term_id = @termId AND step_key = 'accepted' ORDER BY id", new { termId });
        if (stepId is null) return;
        await _db.ExecuteAsync(
            @"IF NOT EXISTS (SELECT 1 FROM student_progress WHERE student_id = @studentId AND step_id = @stepId)
              INSERT INTO student_progress (student_id, step_id) VALUES (@studentId, @stepId)",
            new { studentId, stepId });
    }

    private sealed class StudentLite
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
    }

    private sealed class StudentMe
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public DateTime created_at { get; set; }
    }
}
