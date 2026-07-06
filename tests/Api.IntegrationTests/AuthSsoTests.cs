using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Coverage for the SSO account-linking success paths in AuthController.Sso
// (Api/Controllers/AuthController.cs). Previously only the 501 "not configured" branch was
// tested, because AzureAdTokenValidator was a sealed concrete singleton. A minimal test
// seam (virtual ValidateAsync/IsConfigured + WebAppFixture.FakeAzure) lets each test stage
// canned Entra claims and drive all four branches:
//   (a) azure_id match           -> the matched row's profile is updated
//   (b) pre-staged emplid        -> azure_id stamped exactly once onto that row (no dup)
//   (c) already-claimed emplid   -> NOT re-linked to a different oid (collision surfaces)
//   (d) brand-new student        -> created with the "accepted" step auto-completed
//
// Configured is flipped to true only for the duration of each test (reset in finally) so
// the "SSO not configured -> 501" tests in AuthTests keep passing.
[Collection("api")]
public class AuthSsoTests
{
    private readonly WebAppFixture _fx;

    public AuthSsoTests(WebAppFixture fx) => _fx = fx;

    private static string FreshEmplid() => "9" + Guid.NewGuid().ToString("N")[..8];
    private static string FreshOid() => Guid.NewGuid().ToString();

    // Stage claims + enable the fake, POST /sso, then always reset the fake.
    private async Task<HttpResponseMessage> SsoAsync(string oid, string? email, string? name, string? emplid)
    {
        _fx.FakeAzure.Configured = true;
        _fx.FakeAzure.NextClaims = (oid, email, name, emplid);
        try
        {
            return await _fx.Anonymous().PostAsJsonAsync("/api/auth/sso", new { idToken = "fake-token" });
        }
        finally
        {
            _fx.FakeAzure.Configured = false;
            _fx.FakeAzure.NextClaims = null;
        }
    }

    // ---- (d) brand-new student ---------------------------------------------

    [Fact]
    public async Task Sso_creates_new_student_with_accepted_step_auto_completed()
    {
        var oid = FreshOid();
        var emplid = FreshEmplid();
        var email = $"{emplid}@csub.edu";

        var res = await SsoAsync(oid, email, "New SSO Student", emplid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        var studentId = body.GetProperty("student").GetProperty("id").GetString()!;
        Assert.Equal("New SSO Student", body.GetProperty("student").GetProperty("displayName").GetString());
        Assert.Equal(email, body.GetProperty("student").GetProperty("email").GetString());

        // Row exists with the azure_id + emplid stamped.
        Assert.Equal(oid, (string?)await _fx.ScalarAsync(
            $"SELECT azure_id FROM students WHERE id = '{studentId}'"));
        Assert.Equal(emplid, (string?)await _fx.ScalarAsync(
            $"SELECT emplid FROM students WHERE id = '{studentId}'"));

        // The "accepted" step was auto-completed on create.
        var acceptedCount = Convert.ToInt32(await _fx.ScalarAsync(
            $@"SELECT COUNT(*) FROM student_progress sp JOIN steps s ON s.id = sp.step_id
               WHERE sp.student_id = '{studentId}' AND s.step_key = 'accepted'"));
        Assert.True(acceptedCount >= 1);
    }

    // ---- (a) azure_id match updates the profile ----------------------------

    [Fact]
    public async Task Sso_existing_azure_id_updates_profile_and_reuses_row()
    {
        var oid = FreshOid();
        var emplid = FreshEmplid();

        // First sign-in creates the row and links this oid.
        var first = await SsoAsync(oid, $"{emplid}@csub.edu", "Original Name", emplid);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var studentId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("student").GetProperty("id").GetString()!;

        // Second sign-in with the SAME oid but a changed name/email -> azure_id branch:
        // updates display_name + email on the SAME row (no new row).
        var newEmail = $"renamed-{Guid.NewGuid():N}@csub.edu";
        var second = await SsoAsync(oid, newEmail, "Renamed Via SSO", emplid);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(studentId, secondBody.GetProperty("student").GetProperty("id").GetString());
        Assert.Equal("Renamed Via SSO", secondBody.GetProperty("student").GetProperty("displayName").GetString());
        Assert.Equal(newEmail, secondBody.GetProperty("student").GetProperty("email").GetString());

        // Profile updated in the DB; still exactly one row for this oid.
        Assert.Equal("Renamed Via SSO", (string?)await _fx.ScalarAsync(
            $"SELECT display_name FROM students WHERE id = '{studentId}'"));
        var oidRows = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM students WHERE azure_id = '{oid}'"));
        Assert.Equal(1, oidRows);
    }

    // ---- (b) pre-staged emplid gets azure_id stamped once ------------------

    [Fact]
    public async Task Sso_stamps_azure_id_onto_pre_staged_emplid_without_duplicating()
    {
        var emplid = FreshEmplid();
        var provisionedEmail = $"prestaged-{Guid.NewGuid():N}@csub.edu";

        // Pre-stage via the SIS provisioning push: emplid + email, NO azure_id.
        var push = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Pre Staged", email = provisionedEmail, source_event_id = Guid.NewGuid().ToString() });
        push.EnsureSuccessStatusCode();
        var preStagedId = (await push.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("student_id").GetString()!;

        // SSO sign-in carrying that emplid (the "studentId" claim) but a different email.
        var oid = FreshOid();
        var loginEmail = $"login-{Guid.NewGuid():N}@csub.edu";
        var res = await SsoAsync(oid, loginEmail, "Signed In", emplid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var signedInId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("student").GetProperty("id").GetString();

        // Same row reused (not a duplicate) and azure_id now stamped exactly once.
        Assert.Equal(preStagedId, signedInId);
        var emplidRows = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM students WHERE emplid_norm = '{emplid.ToLowerInvariant()}'"));
        Assert.Equal(1, emplidRows);
        Assert.Equal(oid, (string?)await _fx.ScalarAsync(
            $"SELECT azure_id FROM students WHERE id = '{preStagedId}'"));
    }

    // ---- (c) already-claimed emplid is NOT re-linked to a different oid -----

    [Fact]
    public async Task Sso_does_not_relink_an_already_claimed_emplid_to_a_different_oid()
    {
        var emplid = FreshEmplid();

        // First SSO sign-in claims the emplid with oidA (creates + links).
        var oidA = FreshOid();
        var first = await SsoAsync(oidA, $"a-{Guid.NewGuid():N}@csub.edu", "Owner A", emplid);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var ownerId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("student").GetProperty("id").GetString();

        // A DIFFERENT oid arrives carrying the SAME emplid. The pre-staged link branch only
        // matches rows WHERE azure_id IS NULL, so oidA's already-claimed row is skipped, and
        // the new-student INSERT hits the filtered-unique emplid_norm index. The account is
        // therefore NOT re-linked to oidB; the collision surfaces rather than silently
        // stealing the emplid.
        var oidB = FreshOid();
        var second = await SsoAsync(oidB, $"b-{Guid.NewGuid():N}@csub.edu", "Impostor B", emplid);
        Assert.NotEqual(HttpStatusCode.OK, second.StatusCode);

        // The invariant that matters: oidA still owns the emplid — its azure_id was NOT
        // overwritten, and there is still exactly one row for that emplid (no duplicate,
        // no oidB row).
        Assert.Equal(oidA, (string?)await _fx.ScalarAsync(
            $"SELECT azure_id FROM students WHERE id = '{ownerId}'"));
        var emplidRows = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM students WHERE emplid_norm = '{emplid.ToLowerInvariant()}'"));
        Assert.Equal(1, emplidRows);
        var oidBRows = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM students WHERE azure_id = '{oidB}'"));
        Assert.Equal(0, oidBRows);
    }
}
