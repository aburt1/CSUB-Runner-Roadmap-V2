using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;

namespace Api.Services;

// Runs configured step API checks for a student. Registered as a singleton so the
// in-memory per-student run state survives across requests.
//
// Boring on purpose: a single HttpClient, hand-written SSRF validation, and a
// sequential 15s-capped run loop.
public sealed class ApiCheckRunner
{
    // AllowAutoRedirect = false so a validated URL can't 3xx-redirect to an internal
    // target (SSRF bypass); a redirect is treated as a non-success response.
    //
    // ConnectCallback re-checks the IP the socket is actually about to connect to,
    // because ValidateUrlAsync resolves DNS separately from the connect that
    // Http.SendAsync performs. Without this, a host that returns a public IP at
    // validation and a private IP a moment later (DNS rebinding, or round-robin
    // where validation happened to miss a private record) would still reach an
    // internal address. Re-running IsPrivateAddress here pins the decision to the
    // endpoint we connect to. Honest public hosts are unaffected; dev keeps the
    // same private/localhost allowance ValidateUrlAsync uses.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var endpoint = context.DnsEndPoint;
            var addresses = await System.Net.Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

            Socket? socket = null;
            foreach (var address in addresses)
            {
                if (!s_allowPrivateConnect && IsPrivateAddress(address))
                    throw new HttpRequestException($"Connect to private IP {address} blocked");
                try
                {
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket?.Dispose();
                    socket = null;
                }
            }
            throw new HttpRequestException($"Unable to connect to {endpoint.Host}:{endpoint.Port}");
        },
    });

    // Set once from config (the handler above is static, so it cannot read the
    // instance _allowPrivateTargets). Mirrors ValidateUrlAsync's allowance for
    // private/localhost mock APIs. Defaults to false (fail-closed): it is only
    // ever set true by the explicit ApiCheck:AllowPrivateTargets flag, never by
    // environment name — so a non-Production deploy that forgets the flag still
    // blocks DNS-rebinding to internal targets.
    private static volatile bool s_allowPrivateConnect;

    // 5s per request keeps one slow upstream from eating most of the 15s run budget.
    private const int PerRequestTimeoutMs = 5_000;
    private const int RunBudgetMs = 15_000;

    private readonly Db _db;
    private readonly Encryption _encryption;
    private readonly ILogger<ApiCheckRunner> _logger;

    // When true, ValidateUrlAsync and the connect-time recheck allow private/
    // localhost targets (for dev/test mock APIs). Driven by an explicit config
    // flag, NOT the environment name — see s_allowPrivateConnect.
    private readonly bool _allowPrivateTargets;

    // In-memory run state, keyed by student id.
    private readonly ConcurrentDictionary<string, RunState> _runStates = new();

    public ApiCheckRunner(Db db, Encryption encryption, IConfiguration config, ILogger<ApiCheckRunner> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
        // Opt-in only: private/localhost targets are blocked everywhere unless this
        // flag is explicitly set. Previously this tracked !IsProduction(), which
        // silently opened both SSRF layers in every non-Production environment.
        _allowPrivateTargets = config.GetValue<bool>("ApiCheck:AllowPrivateTargets");
        // Share the decision with the static ConnectCallback (which gates the
        // connect-time private-IP rejection the same way ValidateUrlAsync gates it).
        s_allowPrivateConnect = _allowPrivateTargets;
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
        ScheduleCleanup(studentId, state);
    }

    // Atomically claims a run slot for the student. Returns false when a run is already
    // in flight — a separate Get-then-Set at the call site would let two concurrent
    // requests both see "not running" and both start background runs.
    public bool TryBeginRun(string studentId, RunState state)
    {
        while (true)
        {
            if (_runStates.TryGetValue(studentId, out var current))
            {
                if (current.status == "running") return false;
                if (_runStates.TryUpdate(studentId, state, current)) { ScheduleCleanup(studentId, state); return true; }
            }
            else if (_runStates.TryAdd(studentId, state)) { ScheduleCleanup(studentId, state); return true; }
        }
    }

    private void ScheduleCleanup(string studentId, RunState state)
    {
        // 2 min keeps results around long enough for the client to poll; 5 min is the
        // safety net that releases a 'running' claim if the background task died without
        // SetRunState (and matches the 5-min run throttle).
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
                // Numeric path segments index into arrays.
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

    // Structured (not string-prefix) check so IPv4-mapped IPv6 (::ffff:10.0.0.1),
    // link-local (fe80::), site-local, and unspecified addresses are all caught.
    // internal (not private) so the integration-test project can table-test the
    // private-range bit logic directly; still inaccessible to ordinary callers.
    internal static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0                                      // 0.0.0.0/8
                || bytes[0] == 10                                     // 10/8
                || (bytes[0] == 169 && bytes[1] == 254)               // 169.254/16 link-local
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)  // 172.16/12
                || (bytes[0] == 192 && bytes[1] == 168);              // 192.168/16
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.Equals(IPAddress.IPv6Any)                  // ::
                || address.IsIPv6LinkLocal                            // fe80::/10
                || address.IsIPv6SiteLocal                            // fec0::/10
                || (bytes[0] & 0xfe) == 0xfc;                         // fc00::/7 unique-local
        }
        return false;
    }

    // Validate a URL for SSRF protection: reject private IPs, localhost, and
    // non-HTTP(S) schemes. Private/localhost targets are allowed only when the
    // explicit ApiCheck:AllowPrivateTargets flag is set (for mock/test APIs).
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

        // Uri.Scheme has no trailing colon, so compare against bare "http"/"https".
        if (parsed.Scheme != "http" && parsed.Scheme != "https")
            return new UrlValidation { valid = false, reason = $"Scheme \"{parsed.Scheme}:\" not allowed — only http: and https:" };

        var hostname = parsed.Host;

        if (hostname == "localhost" || hostname == "[::1]" || hostname == "::1")
        {
            if (!_allowPrivateTargets)
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
        // reject if any maps to a private/internal range. Allowed only behind the flag.
        foreach (var address in addresses)
        {
            if (!_allowPrivateTargets && IsPrivateAddress(address))
                return new UrlValidation { valid = false, reason = $"Resolved to private IP {address}" };
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

    // Header names an admin must not be able to set on the outbound request.
    // Host can redirect the request to an internal vhost behind the vetted IP;
    // Content-Length / Transfer-Encoding enable request smuggling. Compared
    // case-insensitively. TryAddWithoutValidation skips CRLF sanitization, so we
    // also reject any name or value containing CR/LF (header injection).
    private static readonly HashSet<string> RestrictedHeaderNames =
        new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Transfer-Encoding" };

    private static bool HasCrlf(string value) => value.Contains('\r') || value.Contains('\n');

    private static void ApplyCustomHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson)) return;
        try
        {
            var custom = JsonSerializer.Deserialize<List<HeaderPair>>(headersJson);
            if (custom is null) return;
            foreach (var pair in custom)
            {
                if (string.IsNullOrEmpty(pair.key)) continue;
                var value = pair.value ?? "";
                // Drop restricted names and any CRLF-bearing name/value rather than
                // letting TryAddWithoutValidation pass them through unsanitized.
                if (RestrictedHeaderNames.Contains(pair.key)) continue;
                if (HasCrlf(pair.key) || HasCrlf(value)) continue;
                request.Headers.TryAddWithoutValidation(pair.key, value);
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

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(PerRequestTimeoutMs));
            using var response = await Http.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            JsonElement? extracted = null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                // Clone() detaches the element from the JsonDocument — `extracted` is read
                // after `doc` is disposed; an uncloned element would throw ObjectDisposedException.
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
        // Advance the durable 5-minute throttle as soon as the run is attempted, not
        // only on success. The in-memory TryBeginRun claim is lost on process restart,
        // so this timestamp is the real overload guard against repeated full runs hitting
        // upstream APIs — if a check below threw (or the process died mid-run) before a
        // success-only write, the throttle would never engage. Behavior-preserving for the
        // happy path (the timestamp still advances); only the failure path now throttles.
        await _db.ExecuteAsync(
            "UPDATE students SET last_api_check_at = SYSUTCDATETIME() WHERE id = @id",
            new { id = student.id });

        var checks = await _db.QueryAllAsync<StepApiCheckToRun>(
            @"SELECT sac.step_id, sac.http_method, sac.url,
                     sac.auth_type, sac.auth_credentials, sac.headers,
                     sac.student_param_name, sac.student_param_source, sac.response_field_path
              FROM step_api_checks sac
              JOIN steps s ON s.id = sac.step_id
              WHERE sac.is_enabled = 1
                AND s.term_id = @termId
                AND s.is_active = 1
              ORDER BY s.sort_order",
            new { termId = student.term_id });

        var checkedSteps = new List<CheckedStep>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var check in checks)
        {
            // 15-second total cap.
            if (sw.ElapsedMilliseconds > RunBudgetMs)
            {
                _logger.LogWarning("API check run hit the 15s cap, stopping early");
                break;
            }

            try
            {
                var studentIdentifier = check.student_param_source == "email"
                    ? student.email
                    : student.emplid;

                if (string.IsNullOrEmpty(studentIdentifier))
                {
                    _logger.LogWarning("No {Source} for student {StudentId}; skipping API check step {StepId}", check.student_param_source, student.id, check.step_id);
                    continue;
                }

                var url = ReplacePlaceholder(check.url, check.student_param_name, studentIdentifier);

                var urlCheck = await ValidateUrlAsync(url);
                if (!urlCheck.valid)
                {
                    _logger.LogWarning("API check URL rejected for step {StepId}: {Reason}", check.step_id, urlCheck.reason);
                    continue;
                }

                string? credentials = null;
                if (check.auth_type != "none" && !string.IsNullOrEmpty(check.auth_credentials))
                {
                    if (!_encryption.IsConfigured)
                    {
                        _logger.LogWarning("Encryption not configured; skipping API check for step {StepId}", check.step_id);
                        continue;
                    }
                    try
                    {
                        credentials = _encryption.Decrypt(check.auth_credentials);
                    }
                    catch (Exception err)
                    {
                        _logger.LogError(err, "Failed to decrypt credentials for API check step {StepId}", check.step_id);
                        continue;
                    }
                }

                JsonElement? extracted = null;
                using (var request = new HttpRequestMessage(ResolveMethod(check.http_method), url))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    ApplyAuthHeaders(request, check.auth_type, credentials);
                    ApplyCustomHeaders(request, check.headers);

                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(PerRequestTimeoutMs));
                    using var response = await Http.SendAsync(request, cts.Token);
                    try
                    {
                        var body = await response.Content.ReadAsStringAsync(cts.Token);
                        using var doc = JsonDocument.Parse(body);
                        // Clone() — see TestApiCheckAsync; same reason: extracted outlives doc.
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

                    // existing rows normally never have status 'not_completed' (ApplyAsync
                    // deletes them) — this branch also tolerates any legacy 'not_completed'
                    // rows. 'completed'/'waived' rows are left alone so an API check never
                    // clobbers a manual waive.
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
                _logger.LogError(err, "API check failed for step {StepId}", check.step_id);
            }
        }

        // Throttle timestamp is written at the top of the run (see above), so no write here.
        return checkedSteps;
    }

    private sealed class StepApiCheckToRun
    {
        public int step_id { get; set; }
        public string http_method { get; set; } = "GET";
        public string url { get; set; } = "";
        public string? auth_type { get; set; }
        public string? auth_credentials { get; set; }
        public string? headers { get; set; }
        public string student_param_name { get; set; } = "studentId";
        public string student_param_source { get; set; } = "emplid";
        public string response_field_path { get; set; } = "";
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
