# Testing Guide

## Overview

The project uses **xUnit** integration tests that host the real ASP.NET Core API in-process
(via `WebApplicationFactory<Program>`) against a **real SQL Server** test database — no mocking
of the database layer. Tests exercise the actual HTTP pipeline, controllers, Dapper queries, and
hand-written T-SQL, so they catch dialect- and contract-level bugs that mocked databases would miss.

| Suite | Tests | Framework | Environment |
|-------|-------|-----------|-------------|
| API integration | 199 | xUnit + `Microsoft.AspNetCore.Mvc.Testing` | .NET 10 + SQL Server (`csub_admissions_test`) |

The Vue client does not currently have an automated test suite; all coverage lives in the API
integration project at `tests/Api.IntegrationTests/`.

---

## Running Tests

The integration suite needs the SQL Server container running, then a single `dotnet test`:

```bash
# 1. Start the SQL Server container (linux/amd64 image; see notes below)
docker compose up -d sqlserver

# 2. Run the whole suite
dotnet test

# Or target the test project explicitly
dotnet test tests/Api.IntegrationTests/Api.IntegrationTests.csproj

# Run a single test class or method (xUnit filter)
dotnet test --filter "FullyQualifiedName~AdminAnalyticsTests"
dotnet test --filter "FullyQualifiedName~StalledStudents_returns_rows_with_iso_utc_last_completion"
```

> **Apple Silicon (M-series) note:** SQL Server has no native ARM build, so the container runs as
> a `linux/amd64` image under emulation. You need Rancher Desktop (or Docker Desktop) with the VZ
> backend + Rosetta enabled. This is a local-dev convenience only — production targets a real
> SQL Server / Azure SQL instance.

You do **not** need to create or migrate the database by hand. The app auto-creates the database,
schema, and deterministic seed on startup; the test fixture simply points the app at a dedicated
test database and lets startup build it.

---

## Test Strategy

### Hosting model

All tests share a single `WebAppFixture` (`tests/Api.IntegrationTests/WebAppFixture.cs`), which:

1. Subclasses `WebApplicationFactory<Program>` to boot the real API in-process. `Program` is
   exposed as a `public partial class` (`Api/Program.cs`) specifically so the test project can host it.
2. Overrides configuration so the app runs against a **dedicated test database**,
   `csub_admissions_test`, on the local SQL Server container at `localhost,1433`.
3. Runs in the **Development** environment so the deterministic 50-student sample seed executes.

The fixture is shared across every test class via `[Collection("api")]` and the
`ApiCollection` / `ICollectionFixture<WebAppFixture>` definition, so the app and DB are built once
per `dotnet test` run.

### Database isolation: drop + reseed per run

Unlike the old app's per-test transaction rollback, the new suite uses a **drop-and-rebuild-per-run**
strategy. In `InitializeAsync` the fixture connects to `master` and drops `csub_admissions_test`
(forcing `SINGLE_USER WITH ROLLBACK IMMEDIATE` first), then the first `CreateClient()` triggers the
app's startup path, which recreates the database, applies the schema, and runs the deterministic seed.

Because every test class shares one database for the whole run, tests follow **shared-DB discipline**:

- **Reads** target known seeded rows (e.g. `seed-student-001`, emplid `001000000`, term 1
  "Fall 2026", step_key `submit-final-documents`).
- **Writes** create their own uniquely-named rows (steps, terms, users) or fresh dev-login students
  with `Guid`-based emails, and assert only on those.
- Assertions use **presence / shape / `>=` bounds** rather than exact global counts, since other
  test classes add rows to the same database during the run.

### Test configuration overrides

`WebAppFixture.ConfigureWebHost` injects the settings the suite needs (mirroring the production
double-underscore env vars, but as in-memory config keys):

| Setting | Value in tests |
|---|---|
| `ConnectionStrings:Default` | points at `csub_admissions_test` |
| `Jwt:Secret` | a fixed long test secret |
| `RateLimiting:Disabled` | `true` |
| `Admin:DefaultEmail` / `Admin:DefaultPassword` | `admin@csub.edu` / `admin123` |
| `LocalLogin:Username` / `LocalLogin:Password` | `localadmin` / `Local_Admin_2026!` |
| `Integration:DefaultName` / `Integration:DefaultKey` | `PeopleSoft Dev` / `dev-integration-key` |
| `ApiCheck:EncryptionKey` | a fixed 64-hex key |

**Rate limiting is disabled in tests.** In production the API applies a per-IP login limiter and a
global `/api` limiter; without `RateLimiting:Disabled=true` the suite's many login calls would trip
the per-IP login limit. `Api/Program.cs` only calls `app.UseRateLimiter()` when that flag is false.

### Auth helpers

`WebAppFixture` exposes typed `HttpClient` factories so each test gets the right credentials:

- `Anonymous()` — no auth (for 401 paths and public endpoints).
- `Admin()` — bearer token cached from the `admin@csub.edu` login in `InitializeAsync`.
- `Integration()` — sets the `X-Integration-Key: dev-integration-key` header.
- `StudentAsync(name, email)` — performs a `POST /api/auth/dev-login` and returns an authed client
  plus token.

---

## What's Covered

199 tests span every endpoint group. Each test class is ported from a specific old Express route
file and matched against its new ASP.NET Core controller.

| Test class | Endpoints | New controller |
|---|---|---|
| `SmokeTests.cs` | Harness sanity: `/api/health`, admin login + token cache, seed presence | — |
| `MiscTests.cs` | `/api/health`, Helmet-equivalent security headers, unknown-`/api` 404s | `HealthController`, `Program.cs` middleware |
| `AuthTests.cs` | `POST /api/auth/dev-login`, `POST /api/auth/sso`, `GET /api/auth/me` | `AuthController` |
| `AdminAuthTests.cs` | `POST /api/admin/auth/login`, `GET me`, `POST change-password`, `POST sso`, `POST local-login` | `AdminAuthController` |
| `StepsTests.cs` | `GET /api/steps` (anon + authed, term/tag filters), `GET /api/steps/progress`, `PUT /api/steps/{id}/status` | `StepsController` |
| `AdminStepsTests.cs` | `GET/POST /api/admin/steps`, `PUT /api/admin/steps/{id}`, `PUT .../reorder`, `PUT .../bulk-status` | `Admin/StepsController` |
| `AdminStudentsTests.cs` | list (pagination/search/sort/overdue), progress, complete/uncomplete, profile, tags, audit | `Admin/StudentsController` |
| `AdminAnalyticsTests.cs` | `/api/admin/stats`, `/api/admin/export/progress`, all `/api/admin/analytics/*` endpoints + drilldown | `Admin/AnalyticsController` |
| `AdminTermsTests.cs` | `/api/admin/terms` CRUD, clone-with-steps, delete guards | `Admin/TermsController` |
| `AdminUsersTests.cs` | sysadmin-only admin-user CRUD, duplicate-email detection, role guards | `Admin/UsersController` |
| `ApiChecksTests.cs` | per-step API-check config (sysadmin-gated) + student-triggered runs | `Admin/ApiChecksController`, `RoadmapApiChecksController` |
| `IntegrationsTests.cs` | `PUT /step-completions`, `POST /step-completions/batch`, `GET /step-catalog` (X-Integration-Key) | `IntegrationsController` |

### Edge cases specifically tested

The analytics tests (`AdminAnalyticsTests.cs`) exercise every filter builder branch — the area most
sensitive to the PostgreSQL → SQL Server port:

- **Cohort buckets:** `0%`, `1-25%`, `26-50%`, `51-75%`, `76-100%` (validated in `cohort-summary`
  and the `cohort_bucket` drilldown).
- **Completion-velocity buckets:** `1-3 days`, `4-7 days`, `1-2 weeks`, `2-4 weeks`, `4+ weeks`
  (all five, in order).
- **Stalled-students day windows** and **deadline-risk** day windows.
- **Drilldown** (`/api/admin/analytics/students`): tag filters, cohort-bucket filters, plus the exact
  400 messages for missing `term_id`/`filter_type`, unknown `filter_type`, and invalid `cohort_bucket`.

The suite also pins the **corrected date contract** from the parity audit: timestamps in API
responses (e.g. `/api/health`, `stalled-students`) must be ISO-8601 UTC ending in `Z`. This guards
against the Dapper `Unspecified`-kind regression that the global `UtcDateTimeConverter` fixes.

---

## Adding New Tests

Add a new `[Fact]` to the matching test class, or create a new file under
`tests/Api.IntegrationTests/`. Every class shares the fixture, so:

1. Mark the class `[Collection("api")]` and take a `WebAppFixture` in the constructor.
2. Use the right auth helper: `_fx.Anonymous()`, `_fx.Admin()`, `_fx.Integration()`, or
   `await _fx.StudentAsync(name, email)`.
3. Follow shared-DB discipline: create your own uniquely-named rows for writes, and assert with
   presence / `>=` bounds rather than exact global counts.

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

[Collection("api")]
public class YourFeatureTests
{
    private readonly WebAppFixture _fx;
    public YourFeatureTests(WebAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Endpoint_returns_expected_shape()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/your-endpoint?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("someField", out _));
    }

    [Fact]
    public async Task Endpoint_without_token_is_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/your-endpoint");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
```

To set up write fixtures, create a uniquely-named row first (see the `CreateStepAsync` /
`UniqueTitle` / `UniqueEmail` helpers in the existing test classes) and assert only on it.

---

## Configuration Files

| File | Purpose |
|------|---------|
| `tests/Api.IntegrationTests/Api.IntegrationTests.csproj` | Test project: net10.0, xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Data.SqlClient`, coverlet; references `Api/Api.csproj` |
| `tests/Api.IntegrationTests/WebAppFixture.cs` | Shared fixture: hosts the app, drops + rebuilds `csub_admissions_test`, injects test config, exposes auth-helper clients, caches the admin token |
| `tests/Api.IntegrationTests/*Tests.cs` | One file per endpoint group (see table above) |
| `Api/Program.cs` | `public partial class Program {}` lets `WebApplicationFactory` host the app; the `RateLimiting:Disabled` switch lives here |
| `docker-compose.yml` | Defines the `sqlserver` service the suite runs against (`docker compose up -d sqlserver`) |

---

## Parity Audit

The new app's REST contract was validated against the original Node/React app in a dedicated parity
review — see [`AUDIT.md`](../AUDIT.md) at the repo root. The verdict: **no missing endpoints and no
broken response contracts**; every endpoint and page is reproduced with matching paths, methods,
request fields, response JSON keys, status codes, and auth/role gates.

Differences are intentional or behavioral nuances from the PostgreSQL → SQL Server port. The
integration tests above encode several audit outcomes directly, including:

- **ISO-8601 UTC date contract** (`Z` suffix) enforced in `MiscTests` and `AdminAnalyticsTests`.
- **Analytics day-math** corrected to `DATEDIFF(second, …)/86400` for stalled / velocity buckets.
- **Error envelope** (`{ "error": ... }`) and **security headers** verified in `MiscTests` and the
  401/400 paths throughout.

Intentionally **dropped** vs the old app (by design, not gaps): the legacy `X-API-Key` admin auth,
the dev activity simulator, and the dev-only mock API-check routes. These have no corresponding tests.
