using System.Net;

namespace Api.IntegrationTests;

// Verifies that admin authorization is re-checked against the DB on every request,
// so a deactivated admin's still-valid token is rejected immediately.
// (The collection runs serially, so toggling the seeded admin's is_active here does
// not race other tests; a finally restores it.)
[Collection("api")]
public class AdminRevocationTests
{
    private readonly WebAppFixture _fx;

    public AdminRevocationTests(WebAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Deactivated_admin_token_is_rejected_then_restored()
    {
        // Active: the seeded admin token works.
        Assert.Equal(HttpStatusCode.OK, (await _fx.Admin().GetAsync("/api/admin/students?per_page=1")).StatusCode);

        try
        {
            // Deactivate the seeded admin (id 1) directly in the DB.
            await _fx.ExecSqlAsync("UPDATE admin_users SET is_active = 0 WHERE id = 1");

            var res = await _fx.Admin().GetAsync("/api/admin/students?per_page=1");
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        finally
        {
            await _fx.ExecSqlAsync("UPDATE admin_users SET is_active = 1 WHERE id = 1");
        }

        // Restored: access works again.
        Assert.Equal(HttpStatusCode.OK, (await _fx.Admin().GetAsync("/api/admin/students?per_page=1")).StatusCode);
    }
}
