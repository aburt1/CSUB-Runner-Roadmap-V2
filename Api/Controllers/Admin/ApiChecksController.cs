using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers.Admin;

// Admin configuration for per-step API checks, ported from server/routes/apiChecks.ts.
// Mounted under /api/admin and gated to sysadmin only.
[ApiController]
[Route("api/admin")]
[AdminAuth("sysadmin")]
public sealed class ApiChecksController : ControllerBase
{
    // The saved-credential mask: eight U+2022 bullet characters, byte-for-byte the
    // same literal the old server used so the round-trip "preserve if masked" works.
    private const string Masked = "••••••••";

    private readonly Db _db;
    private readonly Encryption _encryption;
    private readonly ApiCheckRunner _runner;
    private readonly ILogger<ApiChecksController> _logger;

    public ApiChecksController(Db db, Encryption encryption, ApiCheckRunner runner, ILogger<ApiChecksController> logger)
    {
        _db = db;
        _encryption = encryption;
        _runner = runner;
        _logger = logger;
    }

    public sealed record ApiCheckRequest(
        string? Url,
        string? Response_Field_Path,
        string? Http_Method,
        string? Auth_Type,
        JsonElement? Auth_Credentials,
        JsonElement? Headers,
        string? Student_Param_Name,
        string? Student_Param_Source,
        JsonElement? Is_Enabled);

    public sealed record TestRequest(string? TestStudentId);

    // GET /api/admin/steps/:id/api-check
    [HttpGet("steps/{id}/api-check")]
    public async Task<IActionResult> Get(string id)
    {
        try
        {
            var check = await _db.QueryOneAsync<StepApiCheck>(
                "SELECT * FROM step_api_checks WHERE step_id = @stepId",
                new { stepId = id });

            if (check is null)
                return Ok(new { configured = false });

            // Mask credentials when present (mirror of the old { ...check } spread, which
            // only replaces a *truthy* value — null and "" are passed through unchanged).
            object? authCredentials = string.IsNullOrEmpty(check.auth_credentials) ? check.auth_credentials : Masked;

            // Parse headers JSON to an object when possible, else return as-is.
            object? headers = ParseHeadersForResponse(check.headers);

            var result = new
            {
                id = check.id,
                step_id = check.step_id,
                is_enabled = check.is_enabled,
                http_method = check.http_method,
                url = check.url,
                auth_type = check.auth_type,
                auth_credentials = authCredentials,
                headers,
                student_param_name = check.student_param_name,
                student_param_source = check.student_param_source,
                response_field_path = check.response_field_path,
                created_at = check.created_at,
                updated_at = check.updated_at,
                configured = true,
            };

            return Ok(result);
        }
        catch (Exception err)
        {
            _logger.LogError(err, "get-api-check failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // PUT /api/admin/steps/:id/api-check
    [HttpPut("steps/{id}/api-check")]
    public async Task<IActionResult> Put(string id, [FromBody] ApiCheckRequest? body)
    {
        try
        {
            var url = body?.Url;
            var responseFieldPath = body?.Response_Field_Path;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(responseFieldPath))
                return BadRequest(new { error = "url and response_field_path are required" });

            // Validate URL format (use configured placeholder name, default to 'studentId').
            var paramName = string.IsNullOrEmpty(body?.Student_Param_Name) ? "studentId" : body!.Student_Param_Name!;
            var probe = url.Replace("{{" + paramName + "}}", "test");
            if (!Uri.TryCreate(probe, UriKind.Absolute, out _))
                return BadRequest(new { error = "Invalid URL format" });

            var method = (string.IsNullOrEmpty(body?.Http_Method) ? "GET" : body!.Http_Method!).ToUpperInvariant();
            if (method != "GET" && method != "POST")
                return BadRequest(new { error = "http_method must be GET or POST" });

            var aType = string.IsNullOrEmpty(body?.Auth_Type) ? "none" : body!.Auth_Type!;
            if (aType != "none" && aType != "basic" && aType != "bearer")
                return BadRequest(new { error = "auth_type must be none, basic, or bearer" });

            // Normalize auth_credentials (string -> as-is, object -> JSON string), and
            // detect the masked sentinel exactly like the old code.
            var (credsString, credsIsMasked, credsIsPresent) = ReadCredentials(body?.Auth_Credentials);

            // Handle credentials — encrypt if new, preserve if masked.
            string? encryptedCreds = null;
            if (aType != "none" && credsIsPresent && !credsIsMasked)
            {
                if (!_encryption.IsConfigured)
                    return StatusCode(500, new { error = "Encryption key not configured on server" });
                encryptedCreds = _encryption.Encrypt(credsString!);
            }
            else if (aType != "none" && credsIsMasked)
            {
                // Preserve existing credentials.
                var existing = await _db.QueryOneAsync<ExistingCreds>(
                    "SELECT auth_credentials FROM step_api_checks WHERE step_id = @stepId",
                    new { stepId = id });
                encryptedCreds = existing?.auth_credentials;
            }

            var headersJson = ReadHeaders(body?.Headers);

            // is_enabled === true (strict): only a literal JSON true counts.
            var isEnabled = body?.Is_Enabled is { ValueKind: JsonValueKind.True };

            var paramSource = string.IsNullOrEmpty(body?.Student_Param_Source) ? "emplid" : body!.Student_Param_Source!;
            var storedParamName = string.IsNullOrEmpty(body?.Student_Param_Name) ? "studentId" : body!.Student_Param_Name!;

            // Upsert on step_id (the old ON CONFLICT (step_id) DO UPDATE).
            await _db.TransactionAsync(async tx =>
            {
                var parameters = new
                {
                    stepId = id,
                    isEnabled,
                    method,
                    url,
                    aType,
                    encryptedCreds,
                    headersJson,
                    storedParamName,
                    paramSource,
                    responseFieldPath,
                };

                var updated = await tx.ExecuteAsync(
                    @"UPDATE step_api_checks
                      SET is_enabled = @isEnabled, http_method = @method, url = @url,
                          auth_type = @aType, auth_credentials = @encryptedCreds, headers = @headersJson,
                          student_param_name = @storedParamName, student_param_source = @paramSource,
                          response_field_path = @responseFieldPath, updated_at = SYSUTCDATETIME()
                      WHERE step_id = @stepId",
                    parameters);

                if (updated == 0)
                {
                    await tx.ExecuteAsync(
                        @"INSERT INTO step_api_checks
                            (step_id, is_enabled, http_method, url, auth_type, auth_credentials,
                             headers, student_param_name, student_param_source, response_field_path, updated_at)
                          VALUES
                            (@stepId, @isEnabled, @method, @url, @aType, @encryptedCreds,
                             @headersJson, @storedParamName, @paramSource, @responseFieldPath, SYSUTCDATETIME())",
                        parameters);
                }
            });

            await Audit.LogAsync(_db, Audit.ResolveActor(HttpContext), "step_api_check", id, "upsert",
                new { url, auth_type = aType, response_field_path = responseFieldPath });

            return Ok(new { success = true });
        }
        catch (Exception err)
        {
            _logger.LogError(err, "put-api-check failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // DELETE /api/admin/steps/:id/api-check
    [HttpDelete("steps/{id}/api-check")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _db.ExecuteAsync(
                "DELETE FROM step_api_checks WHERE step_id = @stepId",
                new { stepId = id });

            await Audit.LogAsync(_db, Audit.ResolveActor(HttpContext), "step_api_check", id, "delete");

            return Ok(new { success = true });
        }
        catch (Exception err)
        {
            _logger.LogError(err, "delete-api-check failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // POST /api/admin/steps/:id/api-check/test
    [HttpPost("steps/{id}/api-check/test")]
    public async Task<IActionResult> Test(string id, [FromBody] TestRequest? body)
    {
        try
        {
            if (string.IsNullOrEmpty(body?.TestStudentId))
                return BadRequest(new { error = "testStudentId is required" });

            var check = await _db.QueryOneAsync<StepApiCheck>(
                "SELECT * FROM step_api_checks WHERE step_id = @stepId",
                new { stepId = id });

            if (check is null)
                return NotFound(new { error = "No API check configured for this step" });

            var result = await _runner.TestApiCheckAsync(check, body.TestStudentId);

            // Mirror the old key sets exactly: an error response carries only { error },
            // a success response carries only { statusCode, responseBody, extractedValue,
            // wouldMarkComplete } (no stray null keys from the unified result type).
            if (result.error is not null)
                return Ok(new { error = result.error });

            return Ok(new
            {
                statusCode = result.statusCode,
                responseBody = result.responseBody,
                extractedValue = result.extractedValue,
                wouldMarkComplete = result.wouldMarkComplete,
            });
        }
        catch (Exception err)
        {
            _logger.LogError(err, "test-api-check failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // headers can be stored as a JSON string; parse to an object for the GET
    // response, else hand back the raw string (mirrors the try/catch passthrough).
    private static object? ParseHeadersForResponse(string? headers)
    {
        // Old code only parses a *truthy* value; null and "" pass through unchanged.
        if (string.IsNullOrEmpty(headers)) return headers;
        try
        {
            using var doc = JsonDocument.Parse(headers);
            return doc.RootElement.Clone();
        }
        catch
        {
            return headers;
        }
    }

    // Mirror: const headersJson = headers ? (typeof headers === 'string' ? headers : JSON.stringify(headers)) : null
    private static string? ReadHeaders(JsonElement? headers)
    {
        if (headers is null) return null;
        var h = headers.Value;
        if (h.ValueKind == JsonValueKind.Null || h.ValueKind == JsonValueKind.Undefined)
            return null;
        if (h.ValueKind == JsonValueKind.String)
        {
            var s = h.GetString();
            // A falsy empty string short-circuits to null, like JS `headers ? ...`.
            return string.IsNullOrEmpty(s) ? null : s;
        }
        // Object/array/etc -> JSON.stringify equivalent.
        return h.GetRawText();
    }

    // Mirror the credential branch: returns the string form, whether it equals
    // the masked sentinel, and whether a (truthy) value was provided at all.
    private static (string? Value, bool IsMasked, bool IsPresent) ReadCredentials(JsonElement? creds)
    {
        if (creds is null) return (null, false, false);
        var c = creds.Value;
        switch (c.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return (null, false, false);
            case JsonValueKind.String:
                var s = c.GetString();
                if (string.IsNullOrEmpty(s)) return (null, false, false); // falsy empty string
                return (s, s == Masked, true);
            default:
                // Object/array -> JSON.stringify equivalent; never equals the mask.
                return (c.GetRawText(), false, true);
        }
    }

    private sealed class ExistingCreds
    {
        public string? auth_credentials { get; set; }
    }
}
