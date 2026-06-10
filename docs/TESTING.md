# Testing Guide

This guide covers both halves of the test suite — the **xUnit API integration tests** (backend) and
the **Vitest unit tests** (frontend) — and the **CI pipeline** that runs both on every push. For how
the pieces fit into the system, see [`ARCHITECTURE.md`](ARCHITECTURE.md); for shipping to a server,
see [`DEPLOYMENT.md`](DEPLOYMENT.md).

## Overview

The project ships **two** automated test layers, each chosen to match what that layer is most likely
to get wrong:

1. **API integration tests** (`tests/Api.IntegrationTests/`) — a single **xUnit** project that hosts
   the real ASP.NET Core API in-process (via `WebApplicationFactory<Program>`) and runs it against a
   **real SQL Server test database** — no mocking of the database layer. Tests exercise the actual
   HTTP pipeline, authentication/authorization filters, controllers, Dapper queries, and the
   hand-written T-SQL, so they catch dialect- and contract-level bugs that mocked databases would
   never surface (most notably the kind of SQL-parameter and date-serialization bugs the
   PostgreSQL → SQL Server port introduced).
2. **Frontend unit tests** (`client/src/**/*.test.ts`) — **Vitest** specs running under **jsdom**
   that pin the pure logic in the Vue client: the toast store, the admin fetch wrapper
   (`useAdminApi`), the student auth store, and the exported tag/status helpers from `useProgress`.
   These cover the branchy, easy-to-regress logic that lives *between* the API contract and the
   rendered DOM — exactly the code the integration tests cannot see.

| Suite | Tests | Framework | Runs against | Where |
|-------|-------|-----------|--------------|-------|
| API integration | 214 | xUnit + `Microsoft.AspNetCore.Mvc.Testing` | .NET 10 + real SQL Server (`csub_admissions_test`) | `tests/Api.IntegrationTests/` |
| Frontend unit | 27 | Vitest + jsdom + Pinia | in-process, `fetch`/`sessionStorage` stubbed | `client/src/**/*.test.ts` |

### Testing philosophy

The guiding principle is consistent across both layers: **test the contract, not the implementation,
at the layer where the contract actually lives.**

- For the **API**, the contract is the HTTP request/response: status codes, JSON keys and shapes,
  auth gates, and the exact error envelopes. So the backend tests drive the real app over real HTTP
  against real SQL rather than unit-testing controllers in isolation. This is a deliberate choice
  carried over from the original Node/React app, which tested its Express routes against a real
  PostgreSQL database rather than mocks. The stack changed (Express → ASP.NET Core, PostgreSQL → SQL
  Server), but the philosophy did not: **test the real request/response contract against real SQL.**
  This is what caught the port's SQL-parameter and `datetime`-kind regressions.
- For the **frontend**, the contract is the *behavior of the logic units* the components depend on:
  "does a 401 log the student out?", "does `stepApplies` exclude a held student?", "does the admin
  client build the right query string?". Those are deterministic functions and stores, so they are
  unit-tested directly with `fetch` and `sessionStorage` stubbed — no browser, no running API. Full
  end-to-end UI rendering is intentionally **not** covered here; the API integration tests already
  protect the data and error shapes the UI renders, and the frontend specs protect the logic that
  transforms them.

Both layers are **fast and hermetic enough to run on every push** — that is the bar CI enforces (see
[CI Pipeline](#ci-pipeline)).

---

## Running Tests

### API integration tests

The integration suite needs the SQL Server container running, then a single `dotnet test`. There is
**no** separate "set up the database" step — the API builds and seeds the test database itself the
first time the harness boots it (see [Test Strategy](#test-strategy) below).

```bash
# 1. Start the SQL Server container (linux/amd64 image; see the Apple Silicon note)
docker compose up -d sqlserver

# 2. Run the whole suite from the repo root
dotnet test

# Or target the test project explicitly
dotnet test tests/Api.IntegrationTests/Api.IntegrationTests.csproj

# Run a single test class (xUnit fully-qualified-name filter)
dotnet test --filter "FullyQualifiedName~AdminAnalyticsTests"

# Run a single test method
dotnet test --filter "FullyQualifiedName~StalledStudents_returns_rows_with_iso_utc_last_completion"

# Build first, run with detailed console output
dotnet test --logger "console;verbosity=detailed"
```

The first `dotnet test` after a clean checkout will restore the NuGet packages and build both
`Api/Api.csproj` and the test project; subsequent runs are faster.

> **Apple Silicon (M-series) note:** SQL Server has no native ARM build, so the container runs as a
> `linux/amd64` image under emulation. You need Rancher Desktop (or Docker Desktop) with the VZ
> backend + Rosetta enabled. This is a local-dev convenience only — production targets a real SQL
> Server / Azure SQL instance. The container exposes port **1433** and uses SA password
> `Csub_Local_Dev_2026!` (matching the connection strings the fixture builds).

You do **not** need to create or migrate the database by hand. On startup the app:

1. Connects to `master` and creates `csub_admissions_test` if it does not exist.
2. Applies the schema (tables, indexes, constraints).
3. Runs the deterministic seed (one sysadmin, one active term, 22 Fall-2026 steps, 50 sample
   students with progress and tags).

The test fixture simply points the app at that dedicated test database and lets startup build it.

### Frontend unit tests

The Vitest suite has **no external dependencies** — no database, no running API, no browser. It runs
entirely in-process under jsdom, so it is fast and works the same on a laptop or in CI. From the
`client/` directory:

```bash
# Install deps once (uses the lockfile)
npm ci

# Run the whole suite once and exit (this is what CI runs)
npm run test            # => vitest run

# Watch mode for local development (re-runs on file change)
npm run test:watch      # => vitest

# Run a single file
npx vitest run src/stores/toast.test.ts

# Filter by test name (substring of the describe/it titles)
npx vitest run -t "auto-dismisses"
```

The npm scripts are defined in [`client/package.json`](../client/package.json). The same project also
exposes `npm run lint` (ESLint), `npm run format:check` (Prettier), and `npm run build` (a `vue-tsc`
type-check plus a production Vite bundle) — CI runs all four. See [Frontend Unit Tests
(Vitest)](#frontend-unit-tests-vitest) below for how the suite is wired and what it covers.

---

## Test Strategy

### Hosting model — real app, in-process

All tests share a single `WebAppFixture` (`tests/Api.IntegrationTests/WebAppFixture.cs`), which:

1. Subclasses `WebApplicationFactory<Program>` to boot the **real** API in-process — the same
   `Program.cs`, the same DI container, the same middleware order, the same controllers. `Program`
   is exposed as a `public partial class` (`Api/Program.cs`, last line `public partial class Program {}`)
   specifically so the test project can reference it as the generic type argument.
2. Overrides configuration so the app runs against a **dedicated test database**,
   `csub_admissions_test`, on the local SQL Server container at `localhost,1433` — never the
   real `csub_admissions` database the running app uses.
3. Runs in the **Development** environment (`builder.UseEnvironment("Development")`) so the
   deterministic 50-student sample seed executes. (In Production the app still creates the schema and
   the baseline sysadmin/term/steps, but not the sample students the read tests rely on.)

The fixture is shared across every test class via `[Collection("api")]` and the `ApiCollection` /
`ICollectionFixture<WebAppFixture>` definition, so the app and the database are built **once per
`dotnet test` run** rather than once per test. Every test class takes the fixture in its constructor:

```csharp
[Collection("api")]
public class AdminTermsTests
{
    private readonly WebAppFixture _fx;
    public AdminTermsTests(WebAppFixture fx) => _fx = fx;
    // ...
}
```

### Database isolation: drop + reseed per run

The original Express suite used **per-test transaction rollback** — each test ran inside a `BEGIN` /
`ROLLBACK` so its writes vanished afterward. That approach does not translate cleanly to the new
hosting model: `WebApplicationFactory` boots a long-lived app with its own connection management, and
the API opens its own Dapper connections per request, so there is no single ambient transaction a
test could wrap around an in-process HTTP call.

So the new suite uses a **drop-and-rebuild-per-run** strategy instead. In `InitializeAsync` the
fixture connects to `master` and drops `csub_admissions_test` (forcing `SINGLE_USER WITH ROLLBACK
IMMEDIATE` first to evict any lingering connections):

```sql
IF DB_ID('csub_admissions_test') IS NOT NULL
BEGIN
  ALTER DATABASE csub_admissions_test SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE csub_admissions_test;
END;
```

The first `CreateClient()` call then triggers the app's startup path, which recreates the database,
applies the schema, and runs the deterministic seed. After the seed, `InitializeAsync` performs the
admin login once and caches the resulting JWT in `AdminToken` for the whole run.

### Shared-DB discipline

Because every test class shares one database for the whole run — and they execute in an
unspecified order, potentially in parallel collections — tests must not assume the database is
pristine. The suite follows three rules, and any new test must too:

- **Reads** target known seeded rows: student `seed-student-001` (emplid `001000000`, tag `first-gen`,
  major Business Administration), term `1` ("Fall 2026"), and stable step keys like `accepted`,
  `apply-for-housing`, `submit-final-documents`, and `register-for-future-runner-day`. Step **ids**
  are not stable across runs, so tests look them up by `step_key` rather than hard-coding ids.
- **Writes** create their own uniquely-named rows — steps and terms with `Guid`-based titles, admin
  users with `Guid`-based emails, or fresh dev-login students with `Guid`-based emails — and assert
  only on those. Integration push tests use a unique `source_event_id` per call and reset the shared
  seeded step back to `not_completed` when they finish.
- **Assertions** use **presence / shape / `>=` bounds** rather than exact global counts, since other
  test classes add rows to the same database during the run. For example, "at least 50 students" and
  "at least 22 active Fall-2026 steps" rather than "exactly 50/22".

### Test configuration overrides

`WebAppFixture.ConfigureWebHost` injects the settings the suite needs. These mirror the production
double-underscore environment variables (`ConnectionStrings__Default`, `Jwt__Secret`, etc.) but are
supplied as in-memory configuration keys via `builder.UseSetting(...)`:

| Setting | Value in tests | Why |
|---|---|---|
| `ConnectionStrings:Default` | points at `csub_admissions_test` on `localhost,1433` | isolate from the real DB |
| `Jwt:Secret` | a fixed long test secret | deterministic token signing |
| `RateLimiting:Disabled` | `true` | the suite makes many login calls (see below) |
| `Admin:DefaultEmail` / `Admin:DefaultPassword` | `admin@csub.edu` / `admin123` | seeded sysadmin login |
| `LocalLogin:Username` / `LocalLogin:Password` | `localadmin` / `Local_Admin_2026!` | break-glass login tests |
| `Integration:DefaultName` / `Integration:DefaultKey` | `PeopleSoft Dev` / `dev-integration-key` | `X-Integration-Key` push tests |
| `ApiCheck:EncryptionKey` | a fixed 64-hex (32-byte) key | encrypt/decrypt stored API-check credentials |

**Why rate limiting is disabled in tests.** In production the API applies a per-IP login limiter and
a global `/api` limiter (scoped to `/api` only, so it does not throttle static assets). The suite
performs dozens of admin and student logins; without `RateLimiting:Disabled=true` it would trip the
per-IP login limit partway through and start returning 429s. `Api/Program.cs` only calls
`app.UseRateLimiter()` when that flag is false:

```csharp
if (!app.Configuration.GetValue<bool>("RateLimiting:Disabled"))
    app.UseRateLimiter();
```

### Auth helpers

`WebAppFixture` exposes typed `HttpClient` factories so each test gets a client with the right
credentials already attached. This is the new-stack equivalent of the old `adminToken()` /
`studentToken()` helpers:

- **`Anonymous()`** — a plain client with no auth. Used for 401 paths and genuinely public endpoints
  (`/api/health`, `GET /api/steps`).
- **`Admin()`** — a client whose `Authorization: Bearer` header carries the admin JWT cached during
  `InitializeAsync` (the seeded `admin@csub.edu` sysadmin).
- **`Integration()`** — a client with the `X-Integration-Key: dev-integration-key` header set, for
  the PeopleSoft-style push endpoints.
- **`StudentAsync(name, email)`** — performs `POST /api/auth/dev-login` (creating the student if the
  email is new), and returns a tuple of `(HttpClient Client, string Token)` with the bearer header
  already applied. Fresh students are assigned the active term (Fall 2026) and start with the
  `accepted` step auto-completed.

---

## What's Covered

214 tests span every endpoint group. Each test class is ported from a specific old Express route
file and matched against its new ASP.NET Core controller; the file header comments in each test class
name both sides of the port. The table gives the count per class, the endpoints covered, and the
controller under test:

| Test class | Tests | Endpoints | New controller |
|---|---:|---|---|
| `SmokeTests.cs` | 6 | Harness sanity: `/api/health` connected, admin login + token cache, seed presence (≥50 students, ≥22 steps), bad password 401, `schema_version` recorded on startup, liveness/readiness probes respond | — |
| `MiscTests.cs` | 6 | `/api/health` (shape + ISO-UTC timestamp, public access), Helmet-equivalent security headers (incl. on 404s), unknown-`/api` 404s | `HealthController`, `Program.cs` middleware |
| `AuthTests.cs` | 11 | `POST /api/auth/dev-login` (create, idempotent, validation), `POST /api/auth/sso` (501 when Azure unconfigured), `GET /api/auth/me` (token-type gating, ISO-UTC `createdAt`) | `AuthController` |
| `AdminAuthTests.cs` | 22 | `POST /api/admin/auth/login`, `GET me`, `POST change-password`, `POST sso`, `POST local-login` (break-glass) | `AdminAuthController` |
| `StepsTests.cs` | 10 | `GET /api/steps` (anon + authed, term/tag filtered, sorted), `GET /api/steps/progress`, `PUT /api/steps/{id}/status` (optional/required/tag guards) | `StepsController` |
| `AdminStepsTests.cs` | 21 | `GET/POST /api/admin/steps`, `PUT /api/admin/steps/{id}`, `PUT .../reorder`, `PUT .../bulk-status` | `Admin/StepsController` |
| `AdminStudentsTests.cs` | 26 | list (pagination/search/sort/overdue), `{id}/progress`, complete/uncomplete, profile, tags, `/api/admin/audit` | `Admin/StudentsController` |
| `AdminAnalyticsTests.cs` | 19 | `/api/admin/stats`, `/api/admin/export/progress`, all `/api/admin/analytics/*` endpoints + the drilldown | `Admin/AnalyticsController` |
| `AdminTermsTests.cs` | 20 | `/api/admin/terms` CRUD, `{id}/clone` with steps, delete guards | `Admin/TermsController` |
| `AdminUsersTests.cs` | 21 | sysadmin-only admin-user CRUD, duplicate-email detection, role validation, last-sysadmin guard | `Admin/UsersController` |
| `ApiChecksTests.cs` | 21 | per-step API-check config (credential masking, validation, test run; sysadmin-gated) + student-triggered runs | `Admin/ApiChecksController`, `RoadmapApiChecksController` |
| `IntegrationsTests.cs` | 18 | `PUT /step-completions`, `POST /step-completions/batch`, `GET /step-catalog` (X-Integration-Key) | `IntegrationsController` |
| `AdminRevocationTests.cs` | 1 | Per-request admin re-authorization: a deactivated admin's still-valid token is rejected immediately | `AdminAuth` filter |
| `SecurityHardeningTests.cs` | 12 | Production fail-safe guards (no server/DB needed): JWT secret missing/weak-default/too-short rejected in Production (strong accepted; dev default allowed in Development) + admin-password strength checks | `JwtService`, `Seeder` |

That sums to **214** tests — 207 `[Fact]` tests plus 7 `[Theory]` cases (the two `SecurityHardeningTests` theories expand to 7 `[InlineData]` cases).

### Detail by area

**`SmokeTests`** verifies the harness itself before any feature test runs: the health endpoint
reports `db: connected`, the cached admin token is non-empty, the seed produced at least 50 students
and 22 Fall-2026 steps, and a wrong admin password is rejected with 401.

**`MiscTests`** covers the cross-cutting middleware. `/api/health` returns `{ status: "ok", db:
"connected", timestamp }` with an ISO-8601 UTC `timestamp` ending in `Z`, and is reachable without
auth. The security-header tests assert the full Helmet-equivalent set on every response — including
on a 404 (the header middleware runs ahead of routing): `Content-Security-Policy` (`default-src
'self'`, `frame-src 'none'`), `X-Content-Type-Options: nosniff`, `Strict-Transport-Security`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`.
Unknown `/api/*` routes return a genuine 404 (they do **not** fall through to the SPA `index.html`,
because `MapFallbackToFile` only catches non-`/api` paths).

**`AuthTests`** (student auth) checks that dev-login creates a student and returns a token, that it
is idempotent on email (a second call with the same email returns the same student id and keeps the
stored display name), that missing/empty name or email returns `400 "Name and email are required"`,
that SSO returns `501 "Azure AD SSO is not configured"` (the not-configured gate runs before input
validation), and that `/api/auth/me` rejects no-token / garbage-token / admin-token requests with the
exact 401 messages, while a valid student token returns the session with an ISO-UTC `createdAt`.

**`AdminAuthTests`** is the largest auth file. It covers normal login (returns token + sysadmin user),
case-insensitive + trimmed email lookup, wrong-password / unknown-email / missing-field error paths,
`GET me` (incl. ISO-UTC `createdAt` and rejection of student tokens), `change-password` (12-char
minimum enforced before the current-password check; wrong current password is 401), SSO 501 when
Azure is unconfigured, and the **break-glass `local-login`**: good credentials mint a `break-glass`
sysadmin token that actually works as an admin on `/me`, while bad username/password or missing
fields return the documented 401/400.

**`StepsTests`** (student-facing steps) verifies the anonymous public listing is an array of active
steps sorted by `sort_order` with verbatim snake_case fields, that an authed student's listing is
filtered to their term, that `/api/steps/progress` returns progress/tags/term with the auto-completed
`accepted` step, and that `PUT /api/steps/{id}/status` enforces the student rules: optional steps can
be toggled (create → noop → clear → noop), required steps return `403 "Students may only update
optional steps"`, and tag-gated steps the student doesn't qualify for return `403 "Step does not apply
to this student"`.

**`AdminStepsTests`** covers the admin step editor: the list includes inactive steps (unlike the
public feed), create auto-derives a unique `step_key` even for duplicate titles, and validation
returns the exact 400s (`Title is required`, `term_id is required`, `Invalid term_id`). Update,
reorder (by `{id, sort_order}` array), and bulk activate/deactivate are each verified by reading the
change back, plus their auth and validation failure modes.

**`AdminStudentsTests`** is the broadest admin file. List supports pagination envelopes, emplid
search, term filtering, name sort (asserted as exact reverses to stay collation-agnostic), and
`overdue_only`. `{id}/progress` returns the student row, manual/derived/merged tags, and progress with
ISO-UTC `created_at`. Admin complete/uncomplete returns `created` / `updated` / `noop` results and
handles `waived` status, with 404s for unknown student/step. Profile and tags updates are read back
to confirm, and the `/api/admin/audit` tests generate a write and confirm it is logged with the
correct `entity_type`, `action`, non-empty `changed_by`, and ISO-UTC `created_at`, filterable by
`studentId` and `action`.

**`AdminAnalyticsTests`** is the parity-sensitive file (see edge cases below).

**`AdminTermsTests`** covers the term list (with `step_count` / `student_count` and ISO-UTC
`created_at`), create (new terms are inactive), update/rename, **activation** (which flips every other
term inactive so exactly one stays active), clone-with-steps (`{ term, steps }` shape, each cloned
step attached to the new term), and delete **guards**: deleting a term that still has students
assigned returns `409`, deleting an empty term succeeds, and an unknown term is `404`.

**`AdminUsersTests`** covers sysadmin-only admin-user management: the list never leaks
`password_hash`, create normalizes email to lowercase/trimmed and defaults the role to `viewer`,
duplicate emails (case-insensitive) collide with `409 "Email already exists"`, invalid roles return
the exact allow-list message, and updates round-trip role/display-name/active changes. The
**last-active-sysadmin guard** is covered via its complementary behavior — the suite proves the
count-based guard correctly *allows* demoting/deactivating a sysadmin the test owns while the seeded
sysadmin keeps the active count above zero (it cannot exercise the true 409 path without touching the
shared seeded admin, which shared-DB discipline forbids).

**`ApiChecksTests`** covers per-step API-check configuration (sysadmin-gated). It verifies the
unconfigured shape (`{ configured: false }` only), configuring a check and reading it back with
credentials **masked** (eight `U+2022` bullet characters, not ASCII asterisks) and headers parsed
back to JSON, the "preserve credentials when re-submitted masked" round-trip (the preserved creds
still decrypt and drive a test run), `auth_type: none` storing no credentials, the full validation
matrix (missing url / missing `response_field_path` / invalid url / invalid method / invalid
auth_type), the test-run endpoint (pointed at an unresolvable host for determinism), and the
student-triggered run endpoints (`/api/roadmap/run-api-checks`, `/api/roadmap/check-status`) with
their started/running/skipped statuses and cross-token-type 401 gating.

**`IntegrationsTests`** covers the PeopleSoft-style push API gated by `X-Integration-Key`: the auth
gate (no key / wrong key), `PUT /step-completions` happy path with ISO-UTC `completed_at`, **idempotent
replay** (the same `source_event_id` replays the stored response verbatim, ignoring a changed
payload), the noop case, the full validation matrix with `code` fields (`invalid_source_event_id`,
`invalid_status`, `student_not_found`, `step_not_found`, `invalid_completed_at`), the batch endpoint
(per-item success/failure plus a `summary` of total/succeeded/failed, and batch replay), and
`GET /step-catalog` (all terms, term-filtered, and `400` on a non-numeric `term_id`).

### Edge cases specifically tested

The analytics tests (`AdminAnalyticsTests.cs`) exercise every filter-builder branch — the area most
sensitive to the PostgreSQL → SQL Server port, and the same area the original suite singled out for
SQL-parameter-mismatch coverage:

- **Cohort buckets:** `0%`, `1-25%`, `26-50%`, `51-75%`, `76-100%` — validated in `cohort-summary`
  (every Fall-2026 student lands in exactly one bucket) and in the `cohort_bucket` drilldown (the
  `0%` bucket returns only students with zero completions).
- **Completion-velocity buckets:** `1-3 days`, `4-7 days`, `1-2 weeks`, `2-4 weeks`, `4+ weeks` — all
  five asserted **in order** with a length check.
- **Stalled-students day windows** and **deadline-risk day windows** — shape and bounds verified, and
  the stalled list confirmed non-empty (the seed gives some students zero completions).
- **Cohort comparison** — emits only cohorts with members, sorted by `student_count` descending,
  with `avg_completion_pct` in `[0, 100]`.
- **Drilldown** (`/api/admin/analytics/students`) — tag filters, cohort-bucket filters, pagination
  envelope, plus the exact `400` messages for missing `term_id`/`filter_type`
  (`"term_id and filter_type are required"`), unknown `filter_type` (`"Invalid filter_type"`), and
  an invalid `cohort_bucket` value (`"Invalid cohort_bucket value"`).

The suite also pins the **corrected date contract** from the parity audit: timestamps in API responses
(`/api/health`, `stalled-students`, `created_at`, `completed_at`, `updated_at`, audit `created_at`,
…) must be ISO-8601 UTC ending in `Z`. This guards against the Dapper `Unspecified`-kind regression
that the global `UtcDateTimeConverter` (registered in `Program.cs`) fixes. The error-envelope shape
(`{ "error": ... }`) and the security headers are likewise asserted directly throughout the 401/400
paths.

---

## Adding New Tests

Add a new `[Fact]` to the matching test class, or create a new file under
`tests/Api.IntegrationTests/`. There is no per-test setup boilerplate beyond joining the collection;
every class shares the one fixture. The checklist:

1. Mark the class `[Collection("api")]` and take a `WebAppFixture` in the constructor.
2. Use the right auth helper: `_fx.Anonymous()`, `_fx.Admin()`, `_fx.Integration()`, or
   `await _fx.StudentAsync(name, email)`.
3. Follow shared-DB discipline: create your own uniquely-named rows for writes, and assert with
   presence / `>=` bounds rather than exact global counts. Look steps up by `step_key`, not by id.
4. Assert both the **success contract** (status code + JSON keys/shape) and at least one **failure
   path** (a 401 without auth, and the relevant 400/404/409 where applicable). Most existing classes
   pair every happy-path test with an auth-gate test.

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
        // Date fields must be ISO-8601 UTC ending in 'Z'.
        Assert.EndsWith("Z", body.GetProperty("created_at").GetString());
    }

    [Fact]
    public async Task Endpoint_without_token_is_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/your-endpoint");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }
}
```

### Setting up write fixtures

For tests that mutate data, create a uniquely-named row first and assert only on it. The existing
classes show the patterns:

- **A fresh student** — `POST /api/auth/dev-login` with a `Guid`-based email and read `student.id`
  from the response (see `NewStudentIdAsync` in `AdminStudentsTests`).
- **A fresh step** — `POST /api/admin/steps` with `term_id: 1` and a `T-{Guid}` title, read `id`
  back (see `CreateStepAsync` in `AdminStepsTests` / `ApiChecksTests`).
- **A fresh term** — `POST /api/admin/terms` with a unique name (see `CreateTermAsync` in
  `AdminTermsTests`).
- **A fresh admin user** — `POST /api/admin/users` with a `Guid`-based email (see
  `CreateOwnedUserAsync` in `AdminUsersTests`).

If your write touches a **shared seeded** row (as the integration push tests must, since they target
`seed-student-001` + `submit-final-documents`), reset it to a clean `not_completed` state at the end
of the test so later/parallel tests are unaffected, and use a unique `source_event_id` for each call.

---

## Configuration Files

| File | Purpose |
|------|---------|
| `tests/Api.IntegrationTests/Api.IntegrationTests.csproj` | Test project: `net10.0`, `Nullable`/`ImplicitUsings` enabled, packages `xunit` 2.9.x, `xunit.runner.visualstudio`, `Microsoft.AspNetCore.Mvc.Testing` 10.x, `Microsoft.Data.SqlClient` 7.x, `Microsoft.NET.Test.Sdk`, `coverlet.collector`; references `Api/Api.csproj`; global `using Xunit;` |
| `tests/Api.IntegrationTests/WebAppFixture.cs` | Shared fixture: hosts the app, drops + rebuilds `csub_admissions_test`, injects test config, exposes the `Anonymous`/`Admin`/`Integration`/`StudentAsync` client helpers, caches the admin token; defines the `ApiCollection` collection |
| `tests/Api.IntegrationTests/*Tests.cs` | One file per endpoint group (see the [What's Covered](#whats-covered) table) |
| `Api/Program.cs` | `public partial class Program {}` lets `WebApplicationFactory` host the app; the `RateLimiting:Disabled` switch and the global `UtcDateTimeConverter` registration both live here |
| `docker-compose.yml` | Defines the `sqlserver` service (SQL Server 2022, `linux/amd64`, port 1433, SA password `Csub_Local_Dev_2026!`, with a `SELECT 1` healthcheck) that the suite runs against (`docker compose up -d sqlserver`) |

---

## Parity Audit

The new app's REST contract was validated against the original Node/React app in a dedicated parity
review by 12 read-only auditors — see [`AUDIT.md`](../AUDIT.md) at the repo root. The verdict: **no
missing endpoints and no broken response contracts.** All 50+ API endpoints and every frontend
page/component are reproduced with matching paths, methods, request fields, response JSON keys, status
codes, and auth/role gates.

The differences found are intentional or behavioral nuances from the PostgreSQL → SQL Server port.
The integration tests above encode several audit outcomes directly:

- **ISO-8601 UTC date contract** (`Z` suffix, millisecond precision) — Dapper read SQL Server
  `datetime` values as `Unspecified` kind, so timestamps serialized without the trailing `Z` the old
  app's `toISOString()` always emitted. The global `UtcDateTimeConverter` fixes this; it is enforced
  in `MiscTests`, `AdminAnalyticsTests`, and across the `created_at` / `completed_at` / `updated_at`
  assertions throughout.
- **Analytics day-math** — `DATEDIFF(day, …)` counts calendar boundaries, while Postgres
  `EXTRACT(DAY FROM interval)` counts elapsed 24-hour periods, causing off-by-one bucket membership.
  The fix uses `DATEDIFF(second, …)/86400` (whole elapsed days) for stalled / velocity /
  completion-velocity buckets.
- **Error envelope** (`{ "error": ... }`) and **security headers** — verified in `MiscTests` and the
  401/400 paths throughout. Malformed JSON now returns `400 { error: "Invalid JSON body" }` and
  unhandled errors `500 { error: "Internal server error" }`.

Intentionally **dropped** vs the old app (by design, not gaps), and therefore with **no corresponding
tests**:

- the legacy `X-API-Key` admin authentication,
- the dev activity simulator, and
- the dev-only mock API-check routes.

A few behaviors were intentionally left as the new (equal-or-stricter) behavior — e.g. malformed-input
edges such as a non-numeric `term_id` now return a clean `400` instead of a `500`. These are documented
in `AUDIT.md` and reflected in the validation tests above.
