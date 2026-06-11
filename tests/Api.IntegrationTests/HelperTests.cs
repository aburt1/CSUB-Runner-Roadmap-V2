using System.Text.Json;
using Api.Services;

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
            env: new FakeHostEnvironment(),
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
            env: new FakeHostEnvironment(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiCheckRunner>.Instance);

        var runningState  = new ApiCheckRunner.RunState { status = "running" };
        var completeState = new ApiCheckRunner.RunState { status = "complete" };

        Assert.True(runner.TryBeginRun("s1", runningState));
        // Simulate run finishing.
        runner.SetRunState("s1", completeState);
        // Now a new run attempt should succeed (complete != running).
        Assert.True(runner.TryBeginRun("s1", runningState));
    }

    // Minimal IHostEnvironment stub (only IsProduction() is called by the runner constructor).
    private sealed class FakeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
