using System.Text.Json;
using Api.Services;
using Microsoft.Extensions.Configuration;

namespace Api.IntegrationTests;

// Unit tests for the in-memory helpers that encode subtle JS-mirroring semantics.
// No WebAppFixture needed — these are pure in-process assertions.
public class HelperTests
{
    // ---- Json.IsTruthy (JavaScript Boolean() semantics) ----

    [Theory]
    [InlineData("true",  true)]
    [InlineData("false", false)]
    [InlineData("null",  false)]
    [InlineData("0",     false)]
    [InlineData("1",     true)]
    [InlineData("-1",    true)]
    [InlineData("\"\"",  false)]  // empty string is falsy in JS
    [InlineData("\"0\"", true)]   // non-empty string is truthy even if "0"
    [InlineData("{}",    true)]   // objects are always truthy
    [InlineData("[]",    true)]   // arrays are always truthy
    [InlineData("[0]",   true)]   // non-empty array is truthy
    public void IsTruthy_matches_js_boolean_semantics(string json, bool expected)
    {
        var el = JsonDocument.Parse(json).RootElement;
        Assert.Equal(expected, Json.IsTruthy(el));
    }

    // ---- Json.TryGetProperty ----

    [Fact]
    public void TryGetProperty_returns_false_for_non_object_body()
    {
        // A JSON array is not an object; TryGetProperty must not throw.
        var body = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var found = Json.TryGetProperty(body, "anyKey", out var value);
        Assert.False(found);
        Assert.Equal(JsonValueKind.Undefined, value.ValueKind);
    }

    [Fact]
    public void TryGetProperty_returns_false_for_string_body()
    {
        var body = JsonDocument.Parse("\"hello\"").RootElement;
        Assert.False(Json.TryGetProperty(body, "key", out _));
    }

    [Fact]
    public void TryGetProperty_returns_true_and_value_when_key_exists()
    {
        var body = JsonDocument.Parse("{\"x\": 42}").RootElement;
        var found = Json.TryGetProperty(body, "x", out var value);
        Assert.True(found);
        Assert.Equal(42, value.GetInt32());
    }

    [Fact]
    public void TryGetProperty_returns_false_when_key_missing_on_object()
    {
        var body = JsonDocument.Parse("{\"a\": 1}").RootElement;
        Assert.False(Json.TryGetProperty(body, "missing", out _));
    }

    // ---- ApiCheckRunner.TryBeginRun (CAS race fix) ----

    [Fact]
    public void TryBeginRun_first_call_succeeds_and_second_call_returns_false_while_running()
    {
        // TryBeginRun is pure in-memory (no DB), so we can call it directly.
        // Two concurrent attempts for the same student must yield exactly one true.
        var runner = new ApiCheckRunner(
            db: null!,
            encryption: null!,
            config: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiCheckRunner>.Instance);

        var runningState = new ApiCheckRunner.RunState { status = "running" };

        var first  = runner.TryBeginRun("student-abc", runningState);
        var second = runner.TryBeginRun("student-abc", runningState);

        Assert.True(first,   "first call should claim the slot");
        Assert.False(second, "second call should see 'running' and return false");
        Assert.Equal("running", runner.GetRunState("student-abc").status);
    }

    [Fact]
    public void TryBeginRun_allows_new_run_after_state_is_no_longer_running()
    {
        var runner = new ApiCheckRunner(
            db: null!,
            encryption: null!,
            config: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiCheckRunner>.Instance);

        var runningState  = new ApiCheckRunner.RunState { status = "running" };
        var completeState = new ApiCheckRunner.RunState { status = "complete" };

        Assert.True(runner.TryBeginRun("s1", runningState));
        // Simulate run finishing.
        runner.SetRunState("s1", completeState);
        // Now a new run attempt should succeed (complete != running).
        Assert.True(runner.TryBeginRun("s1", runningState));
    }

    // ---- ApiCheckRunner SSRF: private-target allowance is flag-gated, not env-gated ----

    private static ApiCheckRunner RunnerWith(bool allowPrivateTargets)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiCheck:AllowPrivateTargets"] = allowPrivateTargets ? "true" : "false",
            })
            .Build();
        return new ApiCheckRunner(
            db: null!,
            encryption: null!,
            config: config,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiCheckRunner>.Instance);
    }

    [Fact]
    public async Task ValidateUrl_blocks_localhost_when_flag_off()
    {
        var result = await RunnerWith(allowPrivateTargets: false).ValidateUrlAsync("http://localhost:3000/x");
        Assert.False(result.valid);
    }

    [Fact]
    public async Task ValidateUrl_allows_localhost_only_when_flag_on()
    {
        var result = await RunnerWith(allowPrivateTargets: true).ValidateUrlAsync("http://localhost:3000/x");
        Assert.True(result.valid);
    }

    [Theory]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud metadata endpoint
    [InlineData("http://192.168.1.1/x")]
    public async Task ValidateUrl_blocks_private_and_metadata_ip_when_flag_off(string url)
    {
        var result = await RunnerWith(allowPrivateTargets: false).ValidateUrlAsync(url);
        Assert.False(result.valid);
    }

    // ---- ApiCheckRunner: custom-header allow/denylist (Host/CRLF) ----

    private static System.Net.Http.HttpRequestMessage ApplyHeaders(string headersJson)
    {
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://example.com");
        var method = typeof(ApiCheckRunner).GetMethod(
            "ApplyCustomHeaders",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, new object?[] { request, headersJson });
        return request;
    }

    [Fact]
    public void ApplyCustomHeaders_drops_restricted_and_crlf_headers()
    {
        var json = """
        [
          {"key":"Host","value":"internal.vhost"},
          {"key":"Content-Length","value":"0"},
          {"key":"Transfer-Encoding","value":"chunked"},
          {"key":"X-Inject","value":"ok\r\nX-Evil: smuggled"},
          {"key":"X-Custom","value":"fine"}
        ]
        """;
        var request = ApplyHeaders(json);

        // Content-Length is skipped before TryAddWithoutValidation; asserting via
        // request.Headers.Contains would itself throw (it is a content header), so the
        // safety here is simply that ApplyHeaders did not throw and added no such header.
        Assert.False(request.Headers.Contains("Host"));
        Assert.False(request.Headers.Contains("Transfer-Encoding"));
        Assert.False(request.Headers.Contains("X-Inject"));   // CRLF in value -> dropped
        Assert.False(request.Headers.Contains("X-Evil"));     // never smuggled in
        Assert.True(request.Headers.Contains("X-Custom"));    // benign header still applied
    }

    [Fact]
    public void ApplyCustomHeaders_restricted_name_match_is_case_insensitive()
    {
        var request = ApplyHeaders("""[{"key":"host","value":"internal.vhost"}]""");
        Assert.False(request.Headers.Contains("host"));
        Assert.False(request.Headers.Contains("Host"));
    }
}
