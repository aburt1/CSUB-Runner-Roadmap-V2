using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Concurrency coverage for the integration push idempotency race
// (Api/Controllers/IntegrationsController.cs). Both push paths persist an
// integration_events row keyed on (integration_client_id, source_event_id) and RECOVER
// from a unique-key collision by replaying the now-stored outcome:
//   - StoreIntegrationEventAsync (line ~708): the intended idempotency race
//   - CreateStudentAsync (line ~461): the upsert's own new-emplid race
// Sequential replays (already covered in IntegrationsTests) never enter those catch
// blocks — only genuinely concurrent identical requests do. These tests fire the two
// requests together (Task.WhenAll) and loop the race a few times to widen the window,
// asserting identical response bodies and exactly-once persistence.
//
// Shared-DB discipline: completion pushes target the seeded student/step and reset it
// afterward; student pushes use a fresh never-seeded emplid per iteration.
[Collection("api")]
public class IntegrationsConcurrencyTests
{
    private readonly WebAppFixture _fx;

    public IntegrationsConcurrencyTests(WebAppFixture fx) => _fx = fx;

    private const string Emplid = "001000000";
    private const string StepKey = "submit-final-documents";

    private static string FreshEmplid() => "9" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Concurrent_identical_completions_persist_once_and_return_identical_bodies()
    {
        // Loop to widen the collision window — a single pass may not always race.
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var sourceEventId = Guid.NewGuid().ToString();
            // Explicit completed_at so the two bodies are byte-for-byte comparable.
            var completedAt = "2026-03-15T10:30:00.000Z";
            var payload = new
            {
                student_id_number = Emplid,
                step_key = StepKey,
                status = "completed",
                source_event_id = sourceEventId,
                completed_at = completedAt,
            };

            // Two SEPARATE clients so the requests are not serialized on one connection.
            var clientA = _fx.Integration();
            var clientB = _fx.Integration();
            using var barrier = new Barrier(2);
            async Task<HttpResponseMessage> Fire(HttpClient c) => await Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await c.PutAsJsonAsync("/api/integrations/v1/step-completions", payload);
            });

            var responses = await Task.WhenAll(Fire(clientA), Fire(clientB));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            var bodyA = await responses[0].Content.ReadAsStringAsync();
            var bodyB = await responses[1].Content.ReadAsStringAsync();
            // Idempotency: one request executed, the other replayed the stored outcome —
            // so the raw JSON is identical (same result/status/completed_at/source_event_id).
            Assert.Equal(bodyA, bodyB);

            // Exactly ONE integration_events row for this source_event_id.
            var eventRows = Convert.ToInt32(await _fx.ScalarAsync(
                $"SELECT COUNT(*) FROM integration_events WHERE source_event_id = '{sourceEventId}'"));
            Assert.Equal(1, eventRows);

            // Cleanup: leave the seeded step uncompleted for other tests.
            await _fx.Integration().PutAsJsonAsync(
                "/api/integrations/v1/step-completions",
                new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = Guid.NewGuid().ToString() });
        }
    }

    [Fact]
    public async Task Concurrent_identical_student_pushes_create_one_student_and_return_identical_bodies()
    {
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var emplid = FreshEmplid();
            var sourceEventId = Guid.NewGuid().ToString();
            var payload = new
            {
                emplid,
                display_name = "Race Subject",
                source_event_id = sourceEventId,
            };

            var clientA = _fx.Integration();
            var clientB = _fx.Integration();
            using var barrier = new Barrier(2);
            async Task<HttpResponseMessage> Fire(HttpClient c) => await Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await c.PutAsJsonAsync("/api/integrations/v1/students", payload);
            });

            var responses = await Task.WhenAll(Fire(clientA), Fire(clientB));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            var bodyA = await responses[0].Content.ReadAsStringAsync();
            var bodyB = await responses[1].Content.ReadAsStringAsync();
            // Same source_event_id -> identical replayed body (same student_id + "created").
            Assert.Equal(bodyA, bodyB);

            // Exactly ONE student row for this fresh emplid — neither the idempotency race
            // nor the CreateStudentAsync new-emplid race may leave a duplicate.
            var studentRows = Convert.ToInt32(await _fx.ScalarAsync(
                $"SELECT COUNT(*) FROM students WHERE emplid = '{emplid}'"));
            Assert.Equal(1, studentRows);

            // Exactly ONE integration_events row for this source_event_id.
            var eventRows = Convert.ToInt32(await _fx.ScalarAsync(
                $"SELECT COUNT(*) FROM integration_events WHERE source_event_id = '{sourceEventId}'"));
            Assert.Equal(1, eventRows);
        }
    }

    [Fact]
    public async Task Concurrent_new_emplid_pushes_with_distinct_events_still_create_one_student()
    {
        // Different source_event_ids so the integration_events idempotency guard does NOT
        // fire — the collision is purely on the students unique emplid index, exercising
        // CreateStudentAsync's own catch/re-resolve->UPDATE race (line ~461), not replay.
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var emplid = FreshEmplid();

            var clientA = _fx.Integration();
            var clientB = _fx.Integration();
            using var barrier = new Barrier(2);
            async Task<HttpResponseMessage> Fire(HttpClient c, string name) => await Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await c.PutAsJsonAsync("/api/integrations/v1/students",
                    new { emplid, display_name = name, source_event_id = Guid.NewGuid().ToString() });
            });

            var responses = await Task.WhenAll(Fire(clientA, "First"), Fire(clientB, "Second"));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            // Exactly ONE student row despite two concurrent creates of the same emplid.
            var studentRows = Convert.ToInt32(await _fx.ScalarAsync(
                $"SELECT COUNT(*) FROM students WHERE emplid = '{emplid}'"));
            Assert.Equal(1, studentRows);
        }
    }
}
