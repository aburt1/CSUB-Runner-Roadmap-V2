using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Concurrency coverage for Progress.ApplyAsync (Api/Services/Progress.cs) — the single
// write path for student_progress. The read-modify-write runs in ONE transaction under
// UPDLOCK+HOLDLOCK: HOLDLOCK takes a key-range lock when the row is ABSENT, so two
// concurrent first-completions can't both pass the existence check and race to a
// duplicate-key 500. Previously this invariant was guarded only by a comment.
//
// The admin complete-step endpoint (POST /api/admin/students/{id}/steps/{stepId}/complete)
// is the cited caller; every request here targets a FRESH student + a real step so nothing
// touches rows other test classes rely on.
[Collection("api")]
public class ConcurrencyTests
{
    private readonly WebAppFixture _fx;

    public ConcurrencyTests(WebAppFixture fx) => _fx = fx;

    // Fresh student via dev-login so no other test shares this (student, step) key.
    private async Task<string> NewStudentIdAsync()
    {
        var email = $"u{Guid.NewGuid():N}@t.edu";
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/auth/dev-login", new { name = "Concurrent Student", email });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("student").GetProperty("id").GetString()!;
    }

    // A real numeric step id in the active term (Fall 2026 / term 1) that is NOT the
    // "accepted" step — dev-login auto-completes "accepted", which would make every
    // completion a no-op and defeat the first-completion race this test exercises.
    private async Task<int> UncompletedStepIdAsync()
    {
        var steps = await (await _fx.Admin().GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var step = steps.EnumerateArray()
            .First(s => s.GetProperty("step_key").GetString() != "accepted");
        return step.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Concurrent_completions_of_one_step_yield_exactly_one_row_and_one_created()
    {
        const int concurrency = 8;
        var studentId = await NewStudentIdAsync();
        var stepId = await UncompletedStepIdAsync();

        // Fire N simultaneous completions for the SAME (student, step). A barrier releases
        // every task at once so they collide on the ApplyAsync existence-check/insert window.
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            // Each request needs its own client (a shared HttpClient would serialize).
            var client = _fx.Admin();
            barrier.SignalAndWait();
            return await client.PostAsJsonAsync(
                $"/api/admin/students/{studentId}/steps/{stepId}/complete", new { });
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        // Every request succeeds (no duplicate-key 500 escaped the serialization guard).
        foreach (var res in responses)
            Assert.True(
                (int)res.StatusCode is >= 200 and < 300,
                $"expected 2xx, got {(int)res.StatusCode}");

        // Exactly one 'created' result; the rest are noop/updated (they saw the committed row).
        var results = new List<string>();
        foreach (var res in responses)
        {
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("success").GetBoolean());
            results.Add(body.GetProperty("result").GetString()!);
        }
        Assert.Equal(1, results.Count(r => r == "created"));
        Assert.All(results, r => Assert.Contains(r, new[] { "created", "noop", "updated" }));

        // And the DB holds exactly ONE student_progress row for that (student, step).
        var rowCount = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM student_progress WHERE student_id = '{studentId}' AND step_id = {stepId}"));
        Assert.Equal(1, rowCount);
    }
}
