using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Api.IntegrationTests;

// End-to-end coverage for ApiCheckRunner's check->progress write path
// (Api/Services/ApiCheckRunner.cs). A loopback HTTP server returns controllable JSON so
// the runner's truthy/falsy branches drive real student_progress writes:
//   - truthy  -> the step is auto-completed with completed_by = 'api_check'
//   - falsy   -> ONLY an existing api_check completion is reverted
//   - a pre-existing WAIVED step survives a truthy result (never clobbered)
//   - a MANUAL completion survives a falsy result (only api_check rows revert)
//
// The runner is resolved from the hosted app's DI (the real singleton), and each scenario
// uses its OWN fresh step so it never disturbs the seeded steps other tests depend on.
// WebAppFixture sets ApiCheck:AllowPrivateTargets=true, so the loopback target is allowed
// past the SSRF guard.
//
// Direct unit tests for the pure helpers (field extraction / truthiness / placeholder
// substitution) sit at the bottom — they need neither the fixture nor the loopback server.
[Collection("api")]
public class ApiCheckRunnerTests
{
    private readonly WebAppFixture _fx;

    public ApiCheckRunnerTests(WebAppFixture fx) => _fx = fx;

    // ---- loopback server: fixed JSON per route -----------------------------

    // Minimal Kestrel app on a random localhost port. /truthy => enrolled:true,
    // /falsy => enrolled:false. Returned along with its base URL and a disposer.
    private static async Task<(string BaseUrl, WebApplication App)> StartLoopbackAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // Bind an ephemeral loopback port; "urls" is the setting UseUrls writes.
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/truthy", () => Results.Json(new { data = new { enrolled = true } }));
        app.MapGet("/falsy", () => Results.Json(new { data = new { enrolled = false } }));
        await app.StartAsync();
        var baseUrl = app.Urls.First();
        return (baseUrl, app);
    }

    private async Task<int> CreateStepAsync()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/steps", new
        {
            term_id = 1,
            title = $"ApiCheckE2E-{Guid.NewGuid():N}",
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    // Fresh student with a known emplid in the active term (term 1). emplid is the
    // default student_param_source; the run resolves checks for the student's term.
    private async Task<(string StudentId, string Emplid)> CreateStudentAsync()
    {
        var emplid = "8" + Guid.NewGuid().ToString("N")[..8];
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/auth/dev-login", new { name = "ApiCheck Subject", email = $"{emplid}@t.edu", emplid });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var studentId = body.GetProperty("student").GetProperty("id").GetString()!;
        return (studentId, emplid);
    }

    // Configure an enabled, no-auth api-check on a step directly (no encryption needed).
    private async Task ConfigureCheckAsync(int stepId, string url, string responseFieldPath)
    {
        await _fx.ExecSqlAsync(
            $@"INSERT INTO step_api_checks
                 (step_id, is_enabled, http_method, url, auth_type, student_param_name,
                  student_param_source, response_field_path, updated_at)
               VALUES
                 ({stepId}, 1, 'GET', '{url.Replace("'", "''")}', 'none', 'studentId',
                  'emplid', '{responseFieldPath.Replace("'", "''")}', SYSUTCDATETIME())");
    }

    private async Task<(string? Status, string? CompletedBy)> ReadProgressAsync(string studentId, int stepId)
    {
        var status = (string?)await _fx.ScalarAsync(
            $"SELECT status FROM student_progress WHERE student_id = '{studentId}' AND step_id = {stepId}");
        var completedBy = (string?)await _fx.ScalarAsync(
            $"SELECT completed_by FROM student_progress WHERE student_id = '{studentId}' AND step_id = {stepId}");
        return (status, completedBy);
    }

    private async Task<List<ApiCheckRunner.CheckedStep>> RunForStudentAsync(string studentId)
    {
        var runner = _fx.Services.GetRequiredService<ApiCheckRunner>();
        var student = _fx.Services.GetRequiredService<Api.Data.Db>();
        var row = await student.QueryOneAsync<ApiCheckRunner.StudentForCheck>(
            "SELECT id, email, emplid, term_id FROM students WHERE id = @id", new { id = studentId });
        return await runner.RunApiChecksForStudentAsync(row!);
    }

    // ---- end-to-end write-path scenarios -----------------------------------

    [Fact]
    public async Task Truthy_response_auto_completes_step_as_api_check()
    {
        var (baseUrl, app) = await StartLoopbackAsync();
        try
        {
            var (studentId, _) = await CreateStudentAsync();
            var stepId = await CreateStepAsync();
            await ConfigureCheckAsync(stepId, $"{baseUrl}/truthy", "data.enrolled");

            var checkedSteps = await RunForStudentAsync(studentId);

            Assert.Contains(checkedSteps, c => c.stepId == stepId && c.newStatus == "completed");
            var (status, completedBy) = await ReadProgressAsync(studentId, stepId);
            Assert.Equal("completed", status);
            Assert.Equal("api_check", completedBy);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Falsy_response_reverts_a_prior_api_check_completion()
    {
        var (baseUrl, app) = await StartLoopbackAsync();
        try
        {
            var (studentId, _) = await CreateStudentAsync();
            var stepId = await CreateStepAsync();

            // Pre-existing completion authored by a PRIOR api_check run.
            await _fx.ExecSqlAsync(
                $@"INSERT INTO student_progress (student_id, step_id, completed_at, status, completed_by)
                   VALUES ('{studentId}', {stepId}, SYSUTCDATETIME(), 'completed', 'api_check')");
            await ConfigureCheckAsync(stepId, $"{baseUrl}/falsy", "data.enrolled");

            var checkedSteps = await RunForStudentAsync(studentId);

            Assert.Contains(checkedSteps, c => c.stepId == stepId && c.newStatus == "not_completed");
            // ApplyAsync deletes the row on not_completed -> no row remains.
            var rowCount = Convert.ToInt32(await _fx.ScalarAsync(
                $"SELECT COUNT(*) FROM student_progress WHERE student_id = '{studentId}' AND step_id = {stepId}"));
            Assert.Equal(0, rowCount);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Truthy_response_never_clobbers_a_waived_step()
    {
        var (baseUrl, app) = await StartLoopbackAsync();
        try
        {
            var (studentId, _) = await CreateStudentAsync();
            var stepId = await CreateStepAsync();

            // A human waived this step; a truthy api-check must leave it exactly as-is.
            await _fx.ExecSqlAsync(
                $@"INSERT INTO student_progress (student_id, step_id, completed_at, status, note, completed_by)
                   VALUES ('{studentId}', {stepId}, SYSUTCDATETIME(), 'waived', 'exempt', 'manual')");
            await ConfigureCheckAsync(stepId, $"{baseUrl}/truthy", "data.enrolled");

            var checkedSteps = await RunForStudentAsync(studentId);

            Assert.DoesNotContain(checkedSteps, c => c.stepId == stepId);
            var (status, completedBy) = await ReadProgressAsync(studentId, stepId);
            Assert.Equal("waived", status);
            Assert.Equal("manual", completedBy);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Falsy_response_never_reverts_a_manual_completion()
    {
        var (baseUrl, app) = await StartLoopbackAsync();
        try
        {
            var (studentId, _) = await CreateStudentAsync();
            var stepId = await CreateStepAsync();

            // A human manually completed this step; a falsy api-check must NOT revert it
            // (revert only touches completed_by = 'api_check').
            await _fx.ExecSqlAsync(
                $@"INSERT INTO student_progress (student_id, step_id, completed_at, status, completed_by)
                   VALUES ('{studentId}', {stepId}, SYSUTCDATETIME(), 'completed', 'manual')");
            await ConfigureCheckAsync(stepId, $"{baseUrl}/falsy", "data.enrolled");

            var checkedSteps = await RunForStudentAsync(studentId);

            Assert.DoesNotContain(checkedSteps, c => c.stepId == stepId);
            var (status, completedBy) = await ReadProgressAsync(studentId, stepId);
            Assert.Equal("completed", status);
            Assert.Equal("manual", completedBy);
        }
        finally { await app.DisposeAsync(); }
    }

    // ---- concurrency: a run racing a manual waive must not clobber it -------

    // Deterministically reproduces the clobber the bug allowed: the runner reads step status
    // OUTSIDE ApplyAsync's write lock, so a manual waive that commits AFTER that read but
    // BEFORE the runner's write used to be overwritten to completed/api_check.
    //
    // We force exactly that interleaving with a gating transaction that holds the (student,
    // step) row's key-range lock (UPDLOCK+HOLDLOCK on the absent row). The runner's own
    // status read (no lock hint, no committed row) does NOT block, so it decides "not done"
    // — but its ApplyAsync write blocks on our lock. While it is blocked we INSERT the manual
    // waive and commit; the runner then unblocks and its LOCK-READ sees the waived row.
    // Pre-fix (no guard) it clobbered the waive to completed/api_check; post-fix the guard
    // makes it a noop and the human waive survives.
    [Fact]
    public async Task Run_racing_a_manual_waive_committed_before_its_write_never_clobbers_it()
    {
        var (baseUrl, app) = await StartLoopbackAsync();
        try
        {
            var (studentId, _) = await CreateStudentAsync();
            var stepId = await CreateStepAsync();
            await ConfigureCheckAsync(stepId, $"{baseUrl}/truthy", "data.enrolled");

            // Gating transaction: hold the absent row's key-range lock so the runner's
            // ApplyAsync write blocks. Its plain status read still returns "no row".
            await using var gate = await _fx.OpenConnectionAsync();
            var tran = (SqlTransaction)await gate.BeginTransactionAsync();
            await using (var lockCmd = gate.CreateCommand())
            {
                lockCmd.Transaction = tran;
                lockCmd.CommandText =
                    $@"SELECT student_id FROM student_progress WITH (UPDLOCK, HOLDLOCK)
                       WHERE student_id = '{studentId}' AND step_id = {stepId}";
                await lockCmd.ExecuteNonQueryAsync();
            }

            // Start the runner; it reads "not done", then blocks on our lock at its write.
            var runTask = Task.Run(() => RunForStudentAsync(studentId));

            // Give the runner time to reach its blocked ApplyAsync write, then commit the
            // human waive INSIDE our lock and release it — so the runner's lock-read sees it.
            await Task.Delay(750);
            await using (var waiveCmd = gate.CreateCommand())
            {
                waiveCmd.Transaction = tran;
                waiveCmd.CommandText =
                    $@"INSERT INTO student_progress (student_id, step_id, completed_at, status, note, completed_by)
                       VALUES ('{studentId}', {stepId}, SYSUTCDATETIME(), 'waived', 'manual exemption', 'manual')";
                await waiveCmd.ExecuteNonQueryAsync();
            }
            await tran.CommitAsync();

            await runTask; // runner now proceeds against the committed waived row.

            // The human waive must survive: the truthy api-check may not overwrite it.
            var (status, completedBy) = await ReadProgressAsync(studentId, stepId);
            Assert.Equal("waived", status);
            Assert.Equal("manual", completedBy);
        }
        finally { await app.DisposeAsync(); }
    }

    // ---- pure helper unit tests (no fixture / server needed) ----------------

    [Theory]
    [InlineData("data.enrolled", true)]           // nested object hop
    [InlineData("items.0.ok", true)]              // numeric segment indexes an array
    [InlineData("missing.path", false)]           // absent intermediate -> null
    [InlineData("data.absent", false)]            // absent leaf -> null
    public void ExtractFieldValue_walks_dot_and_array_paths(string path, bool shouldResolve)
    {
        using var doc = JsonDocument.Parse(
            """{"data":{"enrolled":true},"items":[{"ok":1}]}""");
        var extracted = ApiCheckRunner.ExtractFieldValue(doc.RootElement.Clone(), path);
        Assert.Equal(shouldResolve, extracted is not null);
    }

    [Fact]
    public void ExtractFieldValue_empty_path_returns_null()
    {
        using var doc = JsonDocument.Parse("""{"a":1}""");
        Assert.Null(ApiCheckRunner.ExtractFieldValue(doc.RootElement.Clone(), ""));
    }

    // JsTruthy is private static — reach it by reflection (as HelperTests does for
    // ApplyCustomHeaders) so the JS-Boolean() coercion the completion decision hinges on
    // is asserted directly, not just through the end-to-end run.
    private static bool JsTruthy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(ApiCheckRunner).GetMethod(
            "JsTruthy", BindingFlags.NonPublic | BindingFlags.Static)!;
        JsonElement? element = doc.RootElement.Clone();
        return (bool)method.Invoke(null, new object?[] { element })!;
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"yes\"", true)]
    [InlineData("\"\"", false)]   // empty string is falsy
    [InlineData("{}", true)]      // objects are always truthy
    [InlineData("[]", true)]      // arrays are always truthy
    public void JsTruthy_matches_js_boolean_semantics(string json, bool expected)
    {
        Assert.Equal(expected, JsTruthy(json));
    }

    [Fact]
    public void JsTruthy_null_element_is_falsy()
    {
        var method = typeof(ApiCheckRunner).GetMethod(
            "JsTruthy", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False((bool)method.Invoke(null, new object?[] { (JsonElement?)null })!);
    }

    // ReplacePlaceholder is private static — same reflection seam.
    private static string ReplacePlaceholder(string url, string? placeholderName, string value)
    {
        var method = typeof(ApiCheckRunner).GetMethod(
            "ReplacePlaceholder", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object?[] { url, placeholderName, value })!;
    }

    [Fact]
    public void ReplacePlaceholder_substitutes_named_token_and_url_encodes()
    {
        var url = ReplacePlaceholder("https://x/api/{{id}}", "id", "a b/c");
        // Value is URL-encoded (space -> %20, slash -> %2F) to keep it a single path segment.
        Assert.Equal("https://x/api/a%20b%2Fc", url);
    }

    [Fact]
    public void ReplacePlaceholder_defaults_to_studentId_when_name_blank()
    {
        var url = ReplacePlaceholder("https://x/api/{{studentId}}", null, "12345");
        Assert.Equal("https://x/api/12345", url);
    }
}
