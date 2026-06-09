using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;

namespace Api.Services;

// Runs configured step API checks for a student, ported from
// server/utils/apiCheckRunner.ts. Registered as a singleton so the in-memory
// per-student run state survives across requests (matching the module-level
// Map in the old Node module).
//
// Boring on purpose: a single HttpClient, hand-written SSRF validation, and the
// same sequential 15s-capped run loop the old server used.
public sealed class ApiCheckRunner
{
    // AllowAutoRedirect = false so a validated URL can't 3xx-redirect to an internal
    // target (SSRF bypass); a redirect is treated as a non-success response.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    private readonly Db _db;
    private readonly Encryption _encryption;
    private readonly bool _isDev;

    // In-memory run state, keyed by student id (mirrors `runStates` Map).
    private readonly ConcurrentDictionary<string, RunState> _runStates = new();

    public ApiCheckRunner(Db db, Encryption encryption, IHostEnvironment env)
    {
        _db = db;
        _encryption = encryption;
        // process.env.NODE_ENV !== 'production' -> not the Production environment.
        _isDev = !env.IsProduction();
    }

    public sealed class CheckedStep
    {
        public int stepId { get; set; }
        public string newStatus { get; set; } = "";
    }

    public sealed class RunState
    {
        public string status { get; set; } = "no_run"; // no_run | running | complete
        public List<CheckedStep> checkedSteps { get; set; } = new();
        public long? startedAt { get; set; }
    }

    public sealed class TestResult
    {
        public string? error { get; set; }
        public int? statusCode { get; set; }
        public string? responseBody { get; set; }
        public object? extractedValue { get; set; }
        public bool? wouldMarkComplete { get; set; }
    }

    public RunState GetRunState(string studentId)
    {
        return _runStates.TryGetValue(studentId, out var state)
            ? state
            : new RunState { status = "no_run", checkedSteps = new List<CheckedStep>() };
    }

    public void SetRunState(string studentId, RunState state)
    {
        _runStates[studentId] = state;
        // Clean up after 2 minutes for completed runs, 5 minutes otherwise (safety net).
        var ttl = state.status == "complete" ? 120_000 : 300_000;
        _ = Task.Delay(ttl).ContinueWith(_delayTask =>
        {
            // Only remove if it is still the same state object we scheduled cleanup for.
            if (_runStates.TryGetValue(studentId, out var current) && ReferenceEquals(current, state))
                _runStates.TryRemove(studentId, out _);
        });
    }

    // ---- Field extraction --------------------------------------------------

    // Traverse a parsed JSON object by dot-notation path. Returns null (the
    // JsonValueKind.Undefined sentinel becomes null) if any intermediate is missing.
    public static JsonElement? ExtractFieldValue(JsonElement root, string dotPath)
    {
        if (string.IsNullOrEmpty(dotPath)) return null;
        var parts = dotPath.Split('.');
        JsonElement current = root;
        foreach (var part in parts)
        {
            if (current.ValueKind == JsonValueKind.Null || current.ValueKind == JsonValueKind.Undefined)
                return null;

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(part, out var next))
                    return null;
                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array
                     && int.TryParse(part, out var index)
                     && index >= 0 && index < current.GetArrayLength())
            {
                // Match the old JS extractor, which indexes into arrays via numeric path segments.
                current = current[index];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    // JavaScript Boolean(value) semantics for an extracted JSON value.
    private static bool JsTruthy(JsonElement? value)
    {
        if (value is null) return false;
        var v = value.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.False:
                return false;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.String:
                return v.GetString()!.Length > 0;
            case JsonValueKind.Number:
                // 0 (and -0) are falsy; everything else truthy. NaN cannot appear in JSON.
                return v.TryGetDouble(out var d) && d != 0;
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                // Objects/arrays are always truthy in JS, even when empty.
                return true;
            default:
                return false;
        }
    }

    // Convert an extracted JSON value to a plain CLR object for the test response
    // (so it serializes back to the same JSON shape). null when absent.
    private static object? ToClrValue(JsonElement? value)
    {
        if (value is null) return null;
        var v = value.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return v.GetString();
            case JsonValueKind.Number:
                if (v.TryGetInt64(out var l)) return l;
                return v.GetDouble();
            default:
                // Objects/arrays: hand back the raw JSON element (serializes verbatim).
                return v;
        }
    }

    // ---- SSRF guard --------------------------------------------------------

    public sealed class UrlValidation
    {
        public bool valid { get; set; }
        public string? reason { get; set; }
    }

    private static bool IsPrivateIPv4(string ip)
    {
        if (ip.StartsWith("127.") || ip.StartsWith("10.") || ip.StartsWith("0.") || ip.StartsWith("169.254."))
            return true;
        if (ip.StartsWith("172."))
        {
            var octets = ip.Split('.');
            if (octets.Length > 1 && int.TryParse(octets[1], out var second) && second >= 16 && second <= 31)
                return true;
        }
        if (ip.StartsWith("192.168."))
            return true;
        return false;
    }

    private static bool IsPrivateIPv6(string ip)
    {
        var normalized = ip.ToLowerInvariant();
        return normalized == "::1" || normalized.StartsWith("fc") || normalized.StartsWith("fd");
    }

    // Validate a URL for SSRF protection: reject private IPs, localhost, and
    // non-HTTP(S) schemes. In development the private/localhost targets are
    // allowed (for mock/test APIs), matching the old behavior.
    public async Task<UrlValidation> ValidateUrlAsync(string urlString)
    {
        Uri parsed;
        try
        {
            parsed = new Uri(urlString);
        }
        catch
        {
            return new UrlValidation { valid = false, reason = "Invalid URL format" };
        }

        // Uri.Scheme has no trailing colon; old code compares against "http:"/"https:".
        if (parsed.Scheme != "http" && parsed.Scheme != "https")
            return new UrlValidation { valid = false, reason = $"Scheme \"{parsed.Scheme}:\" not allowed — only http: and https:" };

        var hostname = parsed.Host;

        if (hostname == "localhost" || hostname == "[::1]" || hostname == "::1")
        {
            if (!_isDev)
                return new UrlValidation { valid = false, reason = "Requests to localhost are not allowed" };
            return new UrlValidation { valid = true };
        }

        IPAddress[] addresses;
        try
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(hostname);
        }
        catch
        {
            return new UrlValidation { valid = false, reason = $"DNS resolution failed for {hostname}" };
        }

        if (addresses.Length == 0)
            return new UrlValidation { valid = false, reason = $"DNS resolution failed for {hostname}" };

        // Validate EVERY resolved address (a host can return multiple A/AAAA records);
        // reject if any maps to a private/internal range. In dev these are allowed.
        foreach (var address in addresses)
        {
            var family = address.AddressFamily;
            var addressText = address.ToString();

            if (!_isDev && family == AddressFamily.InterNetwork && IsPrivateIPv4(addressText))
                return new UrlValidation { valid = false, reason = $"Resolved to private IP {addressText}" };
            if (!_isDev && family == AddressFamily.InterNetworkV6 && IsPrivateIPv6(addressText))
                return new UrlValidation { valid = false, reason = $"Resolved to private IPv6 {addressText}" };
        }

        return new UrlValidation { valid = true };
    }

    // ---- Auth + header building -------------------------------------------

    private static void ApplyAuthHeaders(HttpRequestMessage request, string? authType, string? credentials)
    {
        if (authType == "basic" && !string.IsNullOrEmpty(credentials))
        {
            var creds = JsonSerializer.Deserialize<BasicCreds>(credentials);
            var username = creds?.username ?? "";
            var password = creds?.password ?? "";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {encoded}");
            return;
        }
        if (authType == "bearer" && !string.IsNullOrEmpty(credentials))
        {
            var creds = JsonSerializer.Deserialize<BearerCreds>(credentials);
            var token = creds?.token ?? "";
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
    }

    private static void ApplyCustomHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson)) return;
        try
        {
            var custom = JsonSerializer.Deserialize<List<HeaderPair>>(headersJson);
            if (custom is null) return;
            foreach (var pair in custom)
            {
                if (!string.IsNullOrEmpty(pair.key))
                    request.Headers.TryAddWithoutValidation(pair.key, pair.value ?? "");
            }
        }
        catch
        {
            // ignore malformed headers
        }
    }

    private static HttpMethod ResolveMethod(string? httpMethod)
    {
        var method = string.IsNullOrEmpty(httpMethod) ? "GET" : httpMethod;
        return method.ToUpperInvariant() == "POST" ? HttpMethod.Post : HttpMethod.Get;
    }

    private static string ReplacePlaceholder(string url, string? placeholderName, string value)
    {
        var placeholder = string.IsNullOrEmpty(placeholderName) ? "studentId" : placeholderName;
        var token = "{{" + placeholder + "}}";
        return url.Replace(token, Uri.EscapeDataString(value));
    }

    // ---- Test a single check (no DB writes) -------------------------------

    public async Task<TestResult> TestApiCheckAsync(StepApiCheck checkConfig, string testStudentId)
    {
        var url = ReplacePlaceholder(checkConfig.url, checkConfig.student_param_name, testStudentId);

        var urlCheck = await ValidateUrlAsync(url);
        if (!urlCheck.valid)
            return new TestResult { error = $"URL rejected: {urlCheck.reason}" };

        string? credentials = null;
        if (checkConfig.auth_type != "none" && !string.IsNullOrEmpty(checkConfig.auth_credentials))
        {
            try
            {
                credentials = _encryption.Decrypt(checkConfig.auth_credentials);
            }
            catch
            {
                return new TestResult { error = "Failed to decrypt credentials" };
            }
        }

        try
        {
            using var request = new HttpRequestMessage(ResolveMethod(checkConfig.http_method), url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            ApplyAuthHeaders(request, checkConfig.auth_type, credentials);
            ApplyCustomHeaders(request, checkConfig.headers);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
            using var response = await Http.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            JsonElement? extracted = null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                extracted = ExtractFieldValue(doc.RootElement.Clone(), checkConfig.response_field_path);
            }
            catch
            {
                extracted = null;
            }

            var truncatedBody = responseBody.Length > 2048 ? responseBody.Substring(0, 2048) + "..." : responseBody;

            return new TestResult
            {
                statusCode = (int)response.StatusCode,
                responseBody = truncatedBody,
                extractedValue = ToClrValue(extracted),
                wouldMarkComplete = JsTruthy(extracted),
            };
        }
        catch (Exception err)
        {
            return new TestResult { error = $"Request failed: {err.Message}" };
        }
    }

    // ---- Run all enabled checks for a student -----------------------------

    public sealed class StudentForCheck
    {
        public string id { get; set; } = "";
        public string email { get; set; } = "";
        public string? emplid { get; set; }
        public int? term_id { get; set; }
    }

    public async Task<List<CheckedStep>> RunApiChecksForStudentAsync(StudentForCheck student)
    {
        var checks = await _db.QueryAllAsync<StepApiCheckWithSort>(
            @"SELECT sac.id, sac.step_id, sac.is_enabled, sac.http_method, sac.url,
                     sac.auth_type, sac.auth_credentials, sac.headers,
                     sac.student_param_name, sac.student_param_source, sac.response_field_path,
                     sac.created_at, sac.updated_at,
                     s.id AS s_id, s.sort_order AS sort_order
              FROM step_api_checks sac
              JOIN steps s ON s.id = sac.step_id
              WHERE sac.is_enabled = 1
                AND s.term_id = @termId
                AND s.is_active = 1
              ORDER BY s.sort_order",
            new { termId = student.term_id });

        var checkedSteps = new List<CheckedStep>();
        var startedAt = Stopwatch();

        foreach (var check in checks)
        {
            // 15-second total cap.
            if (Stopwatch() - startedAt > 15_000)
            {
                Console.Error.WriteLine("[api-check-runner] 15s total cap reached, stopping early");
                break;
            }

            try
            {
                var studentIdentifier = check.student_param_source == "email"
                    ? student.email
                    : student.emplid;

                if (string.IsNullOrEmpty(studentIdentifier))
                {
                    Console.Error.WriteLine($"[api-check-runner] No {check.student_param_source} for student {student.id}, skipping step {check.step_id}");
                    continue;
                }

                var url = ReplacePlaceholder(check.url, check.student_param_name, studentIdentifier);

                var urlCheck = await ValidateUrlAsync(url);
                if (!urlCheck.valid)
                {
                    Console.Error.WriteLine($"[api-check-runner] URL rejected for step {check.step_id}: {urlCheck.reason}");
                    continue;
                }

                string? credentials = null;
                if (check.auth_type != "none" && !string.IsNullOrEmpty(check.auth_credentials))
                {
                    if (!_encryption.IsConfigured)
                    {
                        Console.Error.WriteLine($"[api-check-runner] Encryption not configured, skipping step {check.step_id}");
                        continue;
                    }
                    try
                    {
                        credentials = _encryption.Decrypt(check.auth_credentials);
                    }
                    catch (Exception err)
                    {
                        Console.Error.WriteLine($"[api-check-runner] Failed to decrypt creds for step {check.step_id}: {err.Message}");
                        continue;
                    }
                }

                JsonElement? extracted = null;
                using (var request = new HttpRequestMessage(ResolveMethod(check.http_method), url))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    ApplyAuthHeaders(request, check.auth_type, credentials);
                    ApplyCustomHeaders(request, check.headers);

                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
                    using var response = await Http.SendAsync(request, cts.Token);
                    try
                    {
                        var body = await response.Content.ReadAsStringAsync(cts.Token);
                        using var doc = JsonDocument.Parse(body);
                        extracted = ExtractFieldValue(doc.RootElement.Clone(), check.response_field_path);
                    }
                    catch
                    {
                        extracted = null;
                    }
                }

                var isTruthy = JsTruthy(extracted);

                if (isTruthy)
                {
                    // Mark complete only if not already completed.
                    var existing = await _db.QueryOneAsync<ProgressStatusRow>(
                        "SELECT status FROM student_progress WHERE student_id = @studentId AND step_id = @stepId",
                        new { studentId = student.id, stepId = check.step_id });

                    if (existing is null || existing.status == "not_completed")
                    {
                        await Progress.ApplyAsync(_db, new Progress.ProgressChangeInput
                        {
                            StudentId = student.id,
                            StepId = check.step_id,
                            Status = "completed",
                            CompletedBy = "api_check",
                        });
                        checkedSteps.Add(new CheckedStep { stepId = check.step_id, newStatus = "completed" });
                    }
                }
                else
                {
                    // Only revert if completed_by is 'api_check'.
                    var existing = await _db.QueryOneAsync<ProgressStatusByRow>(
                        "SELECT status, completed_by FROM student_progress WHERE student_id = @studentId AND step_id = @stepId",
                        new { studentId = student.id, stepId = check.step_id });

                    if (existing is not null && existing.status == "completed" && existing.completed_by == "api_check")
                    {
                        await Progress.ApplyAsync(_db, new Progress.ProgressChangeInput
                        {
                            StudentId = student.id,
                            StepId = check.step_id,
                            Status = "not_completed",
                            CompletedBy = "api_check",
                        });
                        checkedSteps.Add(new CheckedStep { stepId = check.step_id, newStatus = "not_completed" });
                    }
                }
            }
            catch (Exception err)
            {
                Console.Error.WriteLine($"[api-check-runner] Error checking step {check.step_id}: {err.Message}");
            }
        }

        // Update throttle timestamp.
        await _db.ExecuteAsync(
            "UPDATE students SET last_api_check_at = SYSUTCDATETIME() WHERE id = @id",
            new { id = student.id });

        return checkedSteps;
    }

    private static long Stopwatch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class StepApiCheckWithSort
    {
        public int id { get; set; }
        public int step_id { get; set; }
        public bool is_enabled { get; set; }
        public string http_method { get; set; } = "GET";
        public string url { get; set; } = "";
        public string? auth_type { get; set; }
        public string? auth_credentials { get; set; }
        public string? headers { get; set; }
        public string student_param_name { get; set; } = "studentId";
        public string student_param_source { get; set; } = "emplid";
        public string response_field_path { get; set; } = "";
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public int s_id { get; set; }
        public int sort_order { get; set; }
    }

    private sealed class ProgressStatusRow
    {
        public string? status { get; set; }
    }

    private sealed class ProgressStatusByRow
    {
        public string? status { get; set; }
        public string? completed_by { get; set; }
    }

    private sealed class BasicCreds
    {
        public string? username { get; set; }
        public string? password { get; set; }
    }

    private sealed class BearerCreds
    {
        public string? token { get; set; }
    }

    private sealed class HeaderPair
    {
        public string? key { get; set; }
        public string? value { get; set; }
    }
}
