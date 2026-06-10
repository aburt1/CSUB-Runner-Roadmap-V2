# Architecture

This document describes the architecture of the CSUB Runner Roadmap application after its conversion from the original **React + Node/Express + PostgreSQL** stack to **Vue 3 + ASP.NET Core + SQL Server**. The rewrite deliberately keeps the REST API contract (paths, payloads, status codes, even the snake_case vs. camelCase JSON key conventions) identical to the original app, so the two implementations diff cleanly and the same client behavior is preserved. Where the original prose still applies, it has been kept and updated only where the stack changed.

This is the "how it all fits together" document. For specific topics it hands off to the sibling docs:

- Getting a dev environment running → [SETUP.md](SETUP.md)
- Shipping to a Windows Server + SQL Server box → [DEPLOYMENT.md](DEPLOYMENT.md)
- Endpoint-by-endpoint reference → [API-GUIDE.md](API-GUIDE.md)
- The auth model and its roadmap → [AUTH-ROADMAP.md](AUTH-ROADMAP.md)
- Third-party packages and why → [LIBRARIES.md](LIBRARIES.md)
- The test strategy → [TESTING.md](TESTING.md)
- Security and enterprise-readiness posture → [../SECURITY-AUDIT.md](../SECURITY-AUDIT.md), [../ENTERPRISE-READINESS.md](../ENTERPRISE-READINESS.md), [../AUDIT.md](../AUDIT.md)

---

## Tech Stack

| Layer | Technologies |
|-------|-------------|
| **Frontend** | Vue 3 (Single-File Components), TypeScript, Vite, Pinia (state), vue-router, Tailwind CSS 3, vuedraggable (step reorder), vue-chartjs + Chart.js (analytics), Tiptap (rich-text step content), vue3-emoji-picker, canvas-confetti, DOMPurify (HTML sanitization) |
| **Backend** | ASP.NET Core controllers (.NET 10), C#, Dapper + hand-written T-SQL, SQL Server 2022 |
| **Auth** | App-issued JWT sessions (HS256), bcrypt password hashing (BCrypt.Net-Next), Azure AD SSO via MSAL (optional) |
| **Security** | Helmet-equivalent security headers, CORS, ASP.NET Core rate limiting, AES-256-GCM credential encryption, SSRF-guarded outbound API checks, forwarded-headers handling behind a reverse proxy |
| **Resilience** | Transient-fault retry with exponential backoff in the Dapper layer, idempotent app-managed schema, liveness/readiness health probes |
| **Quality gates** | Backend: .NET analyzers (`EnableNETAnalyzers`, `AnalysisLevel=latest`, `TreatWarningsAsErrors`). Frontend: Vitest unit tests, ESLint flat config + Prettier. CI builds + tests both halves against a real SQL Server. |
| **Testing** | xUnit + `WebApplicationFactory` integration tests against a real SQL Server database |
| **Deployment** | Docker Compose — three containers: **web** (Vue build served by nginx), **api** (ASP.NET Core), **sqlserver** (SQL Server 2022). nginx reverse-proxies `/api` to the API so the browser sees a single origin. See [DEPLOYMENT.md](DEPLOYMENT.md) for production. |

A few things from the old app were intentionally **dropped** in the rewrite:

- The legacy `X-API-Key` admin authentication path (admins now authenticate exclusively via JWT — password login, SSO, or break-glass local login).
- The development-only "live activity" simulator.
- The development-only mock API-check routes.

Everything else — the data model, the student/admin/integration flows, the analytics, and the JSON contract — was carried over faithfully.

---

## Project Structure

```
CSUB-Runner-Roadmap-V2/
├── client/                          # Vue 3 SPA (Vite + Tailwind + Pinia)
│   ├── src/
│   │   ├── main.ts                  # App entry: createApp, Pinia, router, global error handlers, mount
│   │   ├── App.vue                  # Root component (router-view shell + onErrorCaptured boundary)
│   │   ├── index.css                # Tailwind entry + global styles
│   │   ├── shims.d.ts               # TS shims for .vue imports
│   │   ├── assets/                  # Static assets
│   │   ├── auth/
│   │   │   └── msalConfig.ts        # Azure AD / MSAL SSO config (reads VITE_AZURE_AD_*)
│   │   ├── stores/
│   │   │   ├── auth.ts              # Pinia store: session token, identity, login/logout
│   │   │   └── toast.ts            # Pinia store: transient error/notice toasts
│   │   ├── composables/
│   │   │   ├── useProgress.ts       # Student step-completion logic + tag filtering
│   │   │   └── useAdminApi.ts       # Authenticated fetch wrapper for admin endpoints
│   │   ├── components/
│   │   │   ├── Celebration.vue          # canvas-confetti completion celebration
│   │   │   ├── HighContrastToggle.vue   # Accessibility toggle
│   │   │   ├── PublicRoadmapPreview.vue # Anonymous public step preview
│   │   │   ├── RoadrunnerMascot.vue     # CSUB Roadrunner mascot
│   │   │   └── roadmap/                 # The student timeline UI
│   │   │       ├── RoadmapTimeline.vue
│   │   │       ├── TimelineStep.vue
│   │   │       ├── StepDetailPanel.vue
│   │   │       ├── ListView.vue
│   │   │       ├── ProgressSummary.vue
│   │   │       ├── CurrentStepCallout.vue
│   │   │       ├── CompletionBanner.vue
│   │   │       ├── DeadlineCountdown.vue
│   │   │       └── HelpSection.vue
│   │   ├── pages/
│   │   │   ├── RoadmapPage.vue       # Main student view
│   │   │   └── admin/               # Admin dashboard (tabs + supporting components)
│   │   │       ├── AdminPage.vue         # Tabbed console shell
│   │   │       ├── AdminLogin.vue        # Admin password / SSO login
│   │   │       ├── AdminLocalLogin.vue   # Break-glass local login
│   │   │       ├── StepsTab.vue          # Step CRUD + reorder
│   │   │       ├── StudentsTab.vue       # Student list/search
│   │   │       ├── StudentDetail.vue     # Per-student progress/profile/tags
│   │   │       ├── StudentDrillDown.vue
│   │   │       ├── TermsTab.vue / TermStepsTab.vue / TermBar.vue / TermHeader.vue
│   │   │       ├── CloneTermModal.vue
│   │   │       ├── AnalyticsTab.vue      # Charts + summary stats
│   │   │       ├── SummaryStats.vue
│   │   │       ├── AdminUsersTab.vue     # Admin user CRUD (sysadmin)
│   │   │       ├── ApiCheckConfig.vue    # Per-step API-check config (sysadmin)
│   │   │       ├── AuditLogTab.vue / AuditTimeline.vue
│   │   │       ├── StepForm.vue / StepToggle.vue / TagEditor.vue / NoteModal.vue
│   │   │       ├── RichTextEditor.vue    # Tiptap editor for step content
│   │   │       ├── ExportButton.vue      # CSV/data export
│   │   │       ├── roleConfig.ts         # RBAC role labels/permissions for the UI
│   │   │       └── charts/               # vue-chartjs analytics charts
│   │   │           ├── StepCompletionChart.vue
│   │   │           ├── CompletionTrendChart.vue
│   │   │           ├── CompletionVelocityChart.vue
│   │   │           ├── BottleneckChart.vue
│   │   │           ├── CohortComparisonChart.vue
│   │   │           ├── CohortDistributionChart.vue
│   │   │           ├── DeadlineRiskChart.vue
│   │   │           ├── StalledStudentsChart.vue
│   │   │           ├── chartTheme.ts        # Shared Chart.js theme
│   │   │           └── registerCharts.ts    # Chart.js component registration
│   │   ├── views/
│   │   │   └── HomeView.vue          # `/` landing — public preview + student entry
│   │   ├── router/
│   │   │   └── index.ts              # Routes: /, /admin, /admin/local-login
│   │   └── types/
│   │       └── api.ts                # Shared TypeScript types for API payloads
│   ├── vite.config.ts               # Dev server on :3000, proxies /api -> :3001
│   ├── tailwind.config.js
│   ├── postcss.config.js
│   ├── eslint.config.js             # ESLint flat config
│   ├── .prettierrc.json             # Prettier config
│   ├── nginx.conf.template          # Container: nginx serves SPA + proxies /api
│   ├── Dockerfile                   # Container: build Vue, serve via nginx-unprivileged
│   └── package.json                 # scripts: dev / build / test / lint / format
│
├── Api/                             # ASP.NET Core API (backend only)
│   ├── Program.cs                   # App entry: DI, forwarded headers, CORS, rate limiting,
│   │                                #   security headers, schema init + seed, SPA fallback
│   ├── Controllers/
│   │   ├── HealthController.cs       # GET /api/health, /api/health/live, /api/health/ready
│   │   ├── AuthController.cs         # Student auth (dev-login, SSO, me)
│   │   ├── AdminAuthController.cs    # Admin auth (login, SSO, break-glass, change-password)
│   │   ├── StepsController.cs        # Public/student step routes (/api/steps)
│   │   ├── IntegrationsController.cs # Inbound integration push API (/api/integrations/v1)
│   │   ├── RoadmapApiChecksController.cs  # Student-triggered API-check runs (/api/roadmap)
│   │   └── Admin/                    # Admin API (split by concern)
│   │       ├── AnalyticsController.cs   # Stats, charts, exports
│   │       ├── StepsController.cs       # Step CRUD, reorder, duplicate, bulk-status
│   │       ├── StudentsController.cs    # Student progress, profiles, tags, audit
│   │       ├── TermsController.cs       # Term CRUD, clone
│   │       ├── UsersController.cs       # Admin user CRUD (sysadmin only)
│   │       └── ApiChecksController.cs   # Per-step API-check config (sysadmin only)
│   ├── Auth/
│   │   ├── JwtService.cs             # Issue/validate HS256 tokens
│   │   ├── Passwords.cs              # bcrypt hash/verify (BCrypt.Net-Next)
│   │   ├── StudentAuthAttribute.cs   # [StudentAuth] action filter
│   │   ├── AdminAuthAttribute.cs     # [AdminAuth(...)] action filter + role check
│   │   ├── IntegrationAuthAttribute.cs  # [IntegrationAuth] action filter
│   │   ├── AzureAdTokenValidator.cs  # Azure AD id_token validation
│   │   └── RequestContext.cs         # Typed HttpContext.Items accessors
│   ├── Data/
│   │   ├── Db.cs                     # Dapper wrapper (QueryOne/QueryAll/Execute/Transaction) + transient-fault retry
│   │   ├── SchemaInitializer.cs      # Optional CREATE DATABASE + run idempotent schema.sql + record schema_version
│   │   ├── Seeder.cs                 # Seed defaults + dev sample data
│   │   ├── schema.sql                # Hand-written T-SQL schema (idempotent)
│   │   └── seed/
│   │       └── fall2026-onboarding-checklist.json
│   ├── Services/
│   │   ├── Progress.cs               # Step completion logic (ApplyAsync)
│   │   ├── StudentTags.cs            # Manual + derived tag merging
│   │   ├── StepKeys.cs               # Unique step-key slug generation
│   │   ├── QueryHelpers.cs           # ParseTermId/pagination/active-step helpers
│   │   ├── Audit.cs                  # Audit logging
│   │   ├── Encryption.cs             # AES-256-GCM credential encryption
│   │   ├── ApiCheckRunner.cs         # Outbound API-check execution + SSRF guard
│   │   └── Json.cs                   # Safe JSON parsing helpers
│   ├── Models/
│   │   └── Rows.cs                   # DB row / model types
│   ├── Serialization/
│   │   └── UtcDateTimeConverter.cs   # ISO-8601 UTC 'Z' timestamp output
│   ├── appsettings.json              # Base config
│   ├── appsettings.Development.json  # Dev config (Urls=:3001, dev secrets)
│   ├── .editorconfig                 # Analyzer rules + documented CA1707/CA1848 suppressions
│   ├── Dockerfile                    # Multi-stage: SDK build -> aspnet runtime (non-root + HEALTHCHECK)
│   └── Api.csproj                    # EnableNETAnalyzers + AnalysisLevel=latest + TreatWarningsAsErrors
│
├── tests/
│   └── Api.IntegrationTests/         # xUnit + WebApplicationFactory route tests
│       ├── WebAppFixture.cs          # Hosts the app against a test SQL Server DB
│       ├── SmokeTests.cs             # Health / startup
│       ├── AuthTests.cs              # Student auth
│       ├── AdminAuthTests.cs         # Admin auth + break-glass
│       ├── StepsTests.cs             # Public/student steps
│       ├── AdminStepsTests.cs        # Step CRUD/reorder/duplicate
│       ├── AdminStudentsTests.cs     # Student progress/profile/tags
│       ├── AdminTermsTests.cs        # Term CRUD/clone
│       ├── AdminUsersTests.cs        # Admin user CRUD
│       ├── AdminAnalyticsTests.cs    # Analytics + exports
│       ├── IntegrationsTests.cs      # Inbound integration push API
│       ├── ApiChecksTests.cs         # Outbound API checks
│       └── MiscTests.cs              # Cross-cutting cases
│
├── .github/workflows/ci.yml         # CI: build+test API vs. SQL Server service; lint+test+build client
├── docs/                            # Documentation (this file lives here)
│   └── screenshots/                 # public-preview / student-dashboard / admin-dashboard
├── docker-compose.yml               # Three containers: web, api, sqlserver
├── .env.example                     # Compose secrets template
├── CsubRunnerRoadmapV2.slnx         # .NET solution
└── README.md
```

Note the structural change from the original: the old layout had a single `server/` Express app that **also** served the React build out of one process. In the rewrite the frontend and backend are fully separated — `client/` produces a static bundle that is served by its own nginx container, and `Api/` is a backend-only ASP.NET Core process. They are stitched back into a single browser origin by an nginx reverse proxy (see [Deployment Architecture](#deployment-architecture)).

---

## How Student Steps Work

Each student's roadmap is built from four things:

1. **Assigned term** — Students are assigned to a term (e.g., Fall 2026 via `students.term_id`). They only see steps from that term. If a student has no assigned term, the active term's steps are used as a fallback (`SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC`).

2. **Step visibility rules** — Each step can have `required_tags` and `excluded_tags` (stored as JSON arrays), plus a `required_tag_mode` of `any` (default) or `all`. A step only appears for a student if their tags match the requirements: a step is hidden if the student carries any excluded tag; otherwise it appears when the student matches the required tags (any-of or all-of, per the mode). See `StepsController.StepAppliesToStudent` and `Api/Services/StudentTags.cs`.

3. **Manual + derived tags** — Tags come from two sources (merged in `StudentTags.Merged`):
   - **Manual tags**: Set by admissions staff and stored as a JSON array in `students.tags` (e.g., `honors`, `athlete`, `eop`, `first-gen`, `veteran`).
   - **Derived tags**: Auto-generated from profile fields — `applicant_type` containing "transfer"/"freshman"/"readmit" produces the `transfer`/`freshman`/`readmit` tag; `residency` produces `out-of-state`/`in-state`; and `major` produces a slugged `major:<slug>` tag (e.g., `major: "Computer Science"` produces `major:computer-science`).

4. **Progress records** — Each step can be `completed`, `waived`, or `not_completed`. Progress is tracked in the `student_progress` table (primary key `student_id` + `step_id`) with `completed_at`, `status`, `note`, and a `completed_by` attribution (`manual` | `integration` | `api_check` | `auto`). All writes flow through `Progress.ApplyAsync`.

**Step keys.** Each step has a stable `step_key` (a slugged title, made unique per term with `-2`/`-3` suffixes — see `Api/Services/StepKeys.cs`). Integrations and API checks reference steps by `(term, step_key)` rather than by database id, so the same key is portable across cloned terms.

---

## Request / Data Flow

A single page load illustrates the full path through the system. The numbered hops correspond to the topology in [Deployment Architecture](#deployment-architecture).

```
 Browser                nginx (web)              ASP.NET Core (api)         SQL Server
 ───────                ───────────              ──────────────────         ──────────
   │  GET /                  │                          │                       │
   │ ──────────────────────► │  serve index.html        │                       │
   │ ◄────────────────────── │  (SPA shell + JS)         │                       │
   │                         │                          │                       │
   │  GET /api/steps         │                          │                       │
   │ ──────────────────────► │  proxy_pass ──────────►  │  (forwarded headers)  │
   │                         │  X-Forwarded-For/-Proto  │  filter pipeline:     │
   │                         │                          │   error-handler →     │
   │                         │                          │   fwd-headers →       │
   │                         │                          │   security-headers →  │
   │                         │                          │   CORS → rate-limit    │
   │                         │                          │   → controller        │
   │                         │                          │  Db.QueryAll ────────►│ SELECT ...
   │                         │                          │ ◄──────────────────── │ rows
   │ ◄────────────────────── │ ◄──────────────────────  │  JSON (verbatim keys) │
   │  render personalized timeline                      │                       │
```

The application-level flows on top of that transport:

```
Student loads roadmap
  → GET /api/steps (filtered by term; optional student auth)
  → GET /api/steps/progress (completion status + merged tags + term info)
  → Client merges steps + progress + tag filtering (StepAppliesToStudent)
  → Renders personalized timeline

Student self-completes an optional step
  → PUT /api/steps/:stepId/status   (StudentAuth; optional steps only)
  → Progress.ApplyAsync(...)        (UPDLOCK row lock on student_progress)
  → Audit log entry created
  → Student sees update on next load

Admin updates a student
  → POST   /api/admin/students/:studentId/steps/:stepId/complete    (admissions+)
  → DELETE /api/admin/students/:studentId/steps/:stepId/complete    (admissions+)
  → Progress.ApplyAsync(...) with completed_by = "manual"
  → Audit log entry created

Integration pushes completions
  → PUT  /api/integrations/v1/step-completions          (single)
  → POST /api/integrations/v1/step-completions/batch     (up to 500)
  → Authenticated via integration key (X-Integration-Key or Bearer)
  → Resolves student by Student ID # (emplid) + step by (term, step_key)
  → Same Progress.ApplyAsync() path, completed_by = "integration"
  → Idempotent via unique (integration_client_id, source_event_id):
    a repeated source_event_id replays the cached status + body

Outbound API checks (student-triggered)
  → POST /api/roadmap/run-api-checks  (StudentAuth; 5-minute throttle)
  → ApiCheckRunner hits each enabled step's configured external API
    (SSRF-guarded, 5s per request, 15s total cap)
  → Truthy response field marks the step complete (completed_by = "api_check");
    a falsy response reverts only steps previously set by api_check
  → GET /api/roadmap/check-status  polls the in-memory run state
```

**Database access.** `Db.QueryOneAsync` / `QueryAllAsync` / `ExecuteAsync` open a fresh SQL Server connection per call; `Db.TransactionAsync` runs a unit of work on a single connection/transaction (commit on success, rollback on throw). The row lock in `Progress.ApplyAsync` is the T-SQL `WITH (UPDLOCK)` hint — the SQL Server equivalent of the old Postgres `SELECT ... FOR UPDATE`. This is why the same progress-write path is safe under concurrent admin edits and integration pushes. See [The Data Layer (`Db.cs`)](#the-data-layer-dbcs) for how this layer also absorbs transient SQL faults.

**Client-side resilience.** The frontend is hardened to fail gracefully (see `client/src/main.ts`, `client/src/App.vue`, `client/src/stores/toast.ts`): a global `app.config.errorHandler`, a `window` `unhandledrejection` listener, and an `onErrorCaptured` boundary in the root component all funnel into a Pinia **toast store** so an unexpected error surfaces as a dismissible notice rather than a blank screen. A student `401` response triggers logout plus a toast, so an expired session lands the user back at sign-in cleanly.

---

## Startup Sequence (`Program.cs` + `SchemaInitializer.cs`)

The API is self-initializing: point it at a SQL Server instance and it brings the database to the right shape on boot — no manual migration step. The sequence is deliberately gated so the **same image** behaves correctly in zero-setup dev and in a locked-down production database where the app login has no `CREATE DATABASE` rights.

```
          ┌─────────────────────────────────────────────────────────────┐
          │                    api process starts                       │
          └─────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
          Read ConnectionStrings:Default  ── missing? ──► throw, fail fast
                                       │
                                       ▼
          Build DI container, ForwardedHeaders, CORS, RateLimiter
                                       │
                                       ▼
          ┌──────────────────────────────────────────────┐
          │ Database:AutoCreate ?                         │
          │   (config value  ??  !IsProduction())        │
          └──────────────────────────────────────────────┘
                │ true (dev/test default)        │ false (Production default)
                ▼                                 │
   EnsureDatabaseAsync(connStr):                  │
     connect to [master], retry up to 15×/3s      │
     IF DB_ID('csub_admissions') IS NULL          │
        CREATE DATABASE [csub_admissions]         │
     (db name validated ^[A-Za-z0-9_]+$ first)    │
                │                                 │
                └──────────────┬──────────────────┘
                               ▼
          SchemaInitializer.RunAsync(db, Data/schema.sql)
            • read schema.sql, ExecuteAsync the whole batch
            • every object guarded by IF NOT EXISTS — idempotent, never drops data
            • INSERT CurrentSchemaVersion ("2026.06.09") into schema_version
              if not already present  (append-only audit of applied versions)
                               │
                               ▼
          ┌──────────────────────────────────────────────┐
          │ Database:Seed  (default: true) ?              │
          └──────────────────────────────────────────────┘
                │ true                              │ false (DBA seeds out-of-band)
                ▼                                    │
   Seeder.RunAsync(db, config, env):                 │
     idempotent "is this table empty?" checks        │
     • default term + onboarding checklist            │
     • default admin (Admin:DefaultEmail/Password)    │
     • default integration client                     │
     • DEV only: 50-student deterministic sample      │
                │                                    │
                └──────────────┬─────────────────────┘
                               ▼
          Build middleware pipeline, then app.Run()
```

### The three gates

| Gate | Config key | Default | Why |
|------|-----------|---------|-----|
| **Auto-create the database** | `Database:AutoCreate` | `!IsProduction()` (true off-Production) | In dev/test you want zero setup: connect to `master` and `CREATE DATABASE` if missing. In production the database is provisioned by a DBA and the app login is **not** expected to hold server-level `CREATE DATABASE` rights, so the step is skipped. |
| **Apply the schema** | *(always runs)* | always | `schema.sql` is fully idempotent (`IF NOT EXISTS` around every object), so re-applying it every boot is safe and keeps the schema in lock-step with the code. The applied `CurrentSchemaVersion` is recorded append-only in `dbo.schema_version` so an operator can see the upgrade history. |
| **Seed bootstrap data** | `Database:Seed` | `true` | Seeds the default term, onboarding checklist, default admin, and integration client into an *empty* database (guarded by emptiness checks). Set `false` when a DBA seeds out-of-band. Dev also gets a deterministic 50-student sample (fixed RNG seed) so analytics has realistic data. |

The relevant lines in `Api/Program.cs`:

```csharp
var autoCreateDatabase = app.Configuration.GetValue<bool?>("Database:AutoCreate")
    ?? !app.Environment.IsProduction();
if (autoCreateDatabase)
    await SchemaInitializer.EnsureDatabaseAsync(connectionString);

await SchemaInitializer.RunAsync(db, Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql"));

if (app.Configuration.GetValue<bool?>("Database:Seed") ?? true)
    await Seeder.RunAsync(db, app.Configuration, app.Environment);
```

`EnsureDatabaseAsync` is also responsible for tolerating a still-warming-up SQL Server. In `docker compose up`, `sqlserver` may accept connections before it is truly ready, so the method retries the `master` connection up to 15 times with a 3-second pause. It also validates the database name against `^[A-Za-z0-9_]+$` before interpolating it into the `CREATE DATABASE` statement, since that one value cannot be parameterized.

For the production provisioning story — what a DBA runs by hand, which rights the app login needs, and how `Database:AutoCreate=false` / `Database:Seed=false` fit in — see [DEPLOYMENT.md](DEPLOYMENT.md).

---

## The Data Layer (`Db.cs`)

`Api/Data/Db.cs` is the **only** place that opens database connections. It is a thin wrapper over Dapper + `Microsoft.Data.SqlClient` that mirrors the old Node `server/db/pool.ts` helper (`queryOne` / `queryAll` / `execute` / `transaction`) so ported code reads the same. There is no ORM, no LINQ, no query builder — every query is hand-written T-SQL passed in by the caller, with parameters supplied as a plain anonymous object (`new { id, term_id }`), which is also how SQL injection is avoided.

Three things make this layer enterprise-grade:

**1. One connection string drives everything.** SQL auth vs. Windows/integrated auth, `Encrypt=True`, failover partners, custom ports — all of it lives in `ConnectionStrings:Default`. The code never branches on auth mode; it just hands the string to `SqlConnection`. That is what lets the same binary run against a local dev container (`User Id=sa;...;Encrypt=False`) and a hardened production instance (`Integrated Security=true;Encrypt=True`) with only configuration changing.

**2. Transient-fault retry with exponential backoff.** Routine SQL Server events — failover, throttling, brief network drops, deadlocks (`1205`), command timeouts (`-2`) — should not surface to the user as a 500. Non-transactional reads/writes are wrapped in `RetryAsync`, which retries up to **4 attempts** with backoff of `200ms · 2^(attempt-1)` (200ms → 400ms → 800ms):

```csharp
private const int MaxAttempts = 4;

private static async Task<T> RetryAsync<T>(Func<Task<T>> operation)
{
    for (var attempt = 1; ; attempt++)
    {
        try { return await operation(); }
        catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)));
        }
    }
}
```

`IsTransient` returns true for any `TimeoutException` and for `SqlException`s whose error number is in a curated set of standard transient codes (Azure SQL throttling `40197/40501/40613/49918-49920`, on-prem failover/connection codes `10053/10054/10060/10928/10929/10936`, deadlock `1205`, and the connection-level `-2/20/64/121/233/4221`). Non-transient errors (a constraint violation, a syntax error) are **not** retried — they re-throw immediately.

**3. Transactions retry as a whole, not piecemeal.** When a `Db` instance represents an open transaction, individual calls run on that single connection and are **not** retried in isolation — retrying one statement inside an aborted transaction would be incorrect. Instead `TransactionAsync` wraps the entire unit of work in `RetryAsync`: on a transient fault the transaction rolls back cleanly and the whole closure runs again. Nested `TransactionAsync` calls simply join the outer transaction (the outer commit/rollback governs), so callers can compose units of work safely.

This is the layer underneath every flow in [Request / Data Flow](#request--data-flow) and the readiness probe in [Health Probes](#health-probes).

---

## Health Probes (`HealthController.cs`)

`Api/Controllers/HealthController.cs` exposes three endpoints under `/api/health`, split along the standard **liveness vs. readiness** distinction so orchestrators (Docker, Kubernetes, a load balancer) can make the right call:

| Endpoint | Touches DB? | Returns | Use for |
|----------|-------------|---------|---------|
| `GET /api/health/live` | No | always `200` `{ status: "ok", timestamp }` | **Liveness** — "is the process up?" A failure here means restart the container. It must never depend on the database, or a DB outage would needlessly kill healthy app processes. |
| `GET /api/health/ready` | Yes (`SELECT 1`) | `200` `{ status: "ready", db: "connected" }` or `503` `{ status: "not_ready", db: "disconnected" }` | **Readiness** — "can this instance serve traffic?" A `503` pulls the instance out of the load-balancer rotation until the database is reachable again, without restarting it. |
| `GET /api/health` | Yes (`SELECT 1`) | always `200`, reports `db: "connected" \| "disconnected"` | **Legacy** combined check, kept for the old contract. Always `200` so a probe of it never trips a restart; read the `db` field to learn DB state. |

The DB probe is intentionally minimal and swallows exceptions:

```csharp
private async Task<bool> DbReachableAsync()
{
    try { await _db.QueryOneAsync<int>("SELECT 1"); return true; }
    catch { return false; }
}
```

The container HEALTHCHECKs use **liveness** (see [Hardened Containers](#hardened-containers)): the `api` container curls `/api/health/live`, and Compose gates `web` on the `api` container reporting healthy. Production load balancers should poll `/api/health/ready` so traffic only arrives once the DB is reachable. See [DEPLOYMENT.md](DEPLOYMENT.md) for wiring these into a Windows/IIS or reverse-proxy front end.

---

## Request Pipeline (`Program.cs`)

The ASP.NET Core process is configured entirely in `Api/Program.cs`. After the [startup sequence](#startup-sequence-programcs--schemainitializercs) it builds the middleware pipeline. **Order matters** — each layer wraps the ones after it:

```
   incoming request
        │
        ▼
  ┌──────────────────────────────────────────────────────────┐
  │ 1. Error handler   try/catch → 500 {"error":"Internal     │  outermost — catches
  │                    server error"}, never leaks a stack    │  everything below it
  ├──────────────────────────────────────────────────────────┤
  │ 2. UseForwardedHeaders   rewrite RemoteIp/scheme from      │  must precede rate
  │                          X-Forwarded-For / -Proto          │  limiting + audit
  ├──────────────────────────────────────────────────────────┤
  │ 3. Security headers      CSP, nosniff, X-Frame-Options,    │
  │                          HSTS, COOP/CORP, …                │
  ├──────────────────────────────────────────────────────────┤
  │ 4. UseCors               SPA origin + credentials          │
  ├──────────────────────────────────────────────────────────┤
  │ 5. UseRateLimiter        200/15min per IP on /api          │  skippable in tests
  ├──────────────────────────────────────────────────────────┤
  │ 6. Static files          UseDefaultFiles + UseStaticFiles  │  no-op when wwwroot
  │                          (serve SPA from wwwroot if any)   │  is empty (containers)
  ├──────────────────────────────────────────────────────────┤
  │ 7. MapControllers        the API itself                    │
  ├──────────────────────────────────────────────────────────┤
  │ 8. MapFallbackToFile     non-/api → index.html (SPA route) │
  └──────────────────────────────────────────────────────────┘
```

### Forwarded headers / reverse proxy

Because the browser never talks to the API directly — every request arrives through the `web` container's nginx — the `RemoteIpAddress` the API would otherwise see is nginx's, not the real client's. That would make per-IP rate limiting and audit logging meaningless. `UseForwardedHeaders` rewrites the connection's IP and scheme from the `X-Forwarded-For` / `X-Forwarded-Proto` headers that nginx sets (see `client/nginx.conf.template`, which sets `X-Real-IP`, `X-Forwarded-For`, and `X-Forwarded-Proto` on every proxied request).

The configuration intentionally **clears the known-proxy/known-network allowlists**:

```csharp
options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
options.KnownIPNetworks.Clear();
options.KnownProxies.Clear();
```

This trusts the forwarded headers regardless of source — which is safe **only** because the api is never directly reachable: it is published on `127.0.0.1:8080` and otherwise sits on the internal Docker network behind nginx. If you ever expose the api port to untrusted networks, this trust must be tightened to the proxy's address. See [DEPLOYMENT.md](DEPLOYMENT.md) for the production proxy topology.

### Security headers

A single middleware sets the Helmet-equivalent header set the old Express server used:

| Header | Value | Purpose |
|--------|-------|---------|
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; connect-src 'self'; frame-src 'none'` | Locks scripts/connections to same-origin; allows Google Fonts for CSS/fonts and `data:`/`https:` images; blocks framing of third-party content. |
| `X-Content-Type-Options` | `nosniff` | Stop MIME sniffing. |
| `X-Frame-Options` | `DENY` | Clickjacking defense (the app refuses to be framed). |
| `Referrer-Policy` | `no-referrer` | Don't leak URLs in the `Referer` header. |
| `Strict-Transport-Security` | `max-age=15552000; includeSubDomains` | Force HTTPS for 180 days. |
| `Cross-Origin-Opener-Policy` / `Cross-Origin-Resource-Policy` | `same-origin` | Process/resource isolation. |
| `X-DNS-Prefetch-Control` / `X-Download-Options` / `X-Permitted-Cross-Domain-Policies` / `Origin-Agent-Cluster` | `off` / `noopen` / `none` / `?1` | The remaining Helmet hardening headers. |

### Rate limiting

A global limiter allows **200 requests / 15 min per IP**, scoped to `/api` only so SPA/static requests don't consume the budget; everything outside `/api` gets a no-limiter partition. The partition key is the client IP — which, thanks to forwarded headers, is the *real* client IP, not nginx's. Rejected requests get **429**. Two named policies tighten the auth endpoints, which opt in via `[EnableRateLimiting("...")]`:

| Policy | Limit | Applied to |
|--------|-------|-----------|
| *(global)* | 200 / 15 min per IP | all `/api/*` |
| `login` | 10 / 15 min per IP | password/SSO login |
| `breakGlass` | 5 / 15 min per IP | the break-glass local login |

The entire limiter can be turned off with `RateLimiting:Disabled=true` — used by the integration test suite, which would otherwise trip the per-IP login limit while exercising auth.

### JSON contract

No naming policy is applied (`PropertyNamingPolicy = null`), so each response spells its keys verbatim — snake_case for DB-row responses (`step_id`, `completed_at`) and camelCase for hand-built auth responses (`displayName`). Inbound JWT claim mapping is disabled (`JwtSecurityTokenHandler.DefaultMapInboundClaims = false`) so claim names stay verbatim (`type`, `studentId`, `role`). Timestamps are emitted as ISO-8601 UTC with a trailing `Z` via `Serialization/UtcDateTimeConverter`. Controllers validate input by hand and return `{ "error": "..." }`, so the automatic model-state 400 (`SuppressModelStateInvalidFilter = true`) is suppressed.

### CORS

Mirrors the old server: the SPA origin is allowed with credentials. The origin comes from `Cors:Origin`, defaulting to `http://localhost:3000` in non-production and to *nothing* in Production (CORS effectively closed unless explicitly configured). In the container deployment CORS is normally unnecessary because nginx makes the browser see one origin (see [Deployment Architecture](#deployment-architecture)).

---

## Authentication

Two app-issued JWT session types, both HS256 with an 8-hour lifetime (`JwtService`), with claim names matching the original app:

- **Student** — `{ type: "student", studentId, email }`. Issued by `POST /api/auth/dev-login` (dev/POC only, name + email) or `POST /api/auth/sso` (Azure AD). Enforced by `[StudentAuth]`. `GET /api/steps` reads the token optionally; new students start with the `accepted` step auto-completed.
- **Admin** — `{ type: "admin", adminId, role, email, displayName }`. Issued by `POST /api/admin/auth/login` (email + bcrypt password), `POST /api/admin/auth/sso` (Azure AD), or the env-gated break-glass `POST /api/admin/auth/local-login`. Enforced by `[AdminAuth]`, which also does the role check: `[AdminAuth]` = any authenticated admin, `[AdminAuth("admissions_editor", "sysadmin")]` = only those roles.

The auth filters stash identity on `HttpContext.Items`; controllers read it through the typed accessors in `RequestContext.cs` (`StudentId()`, `StudentEmail()`, `AdminUser()`).

**RBAC roles** (least → most privileged): `viewer`, `admissions`, `admissions_editor`, `sysadmin`. Roughly: viewers read analytics; `admissions` can change individual student progress/profiles/tags; `admissions_editor` can also edit steps and terms; `sysadmin` can additionally manage admin users and API-check configuration.

**Default credentials** (override in any real deployment via env vars; in container deployments these are *required*, not defaulted — see [`.env.example`](../.env.example)):

| Account | Username / Email | Password | Source |
|---------|------------------|----------|--------|
| Default admin | `admin@csub.edu` | `admin123` (dev) | `Admin:DefaultEmail` / `Admin:DefaultPassword` |
| Break-glass local admin | `localadmin` | `Local_Admin_2026!` (dev) | `LocalLogin:Username` / `LocalLogin:Password` |
| Integration key (dev) | `PeopleSoft Dev` | `dev-integration-key` | `Integration:DefaultName` / `Integration:DefaultKey` |

**Azure AD SSO** is optional. When `AzureAd:ClientId`/`AzureAd:TenantId` are unset, the `/sso` endpoints return `501`. The client reads its SSO config from `VITE_AZURE_AD_*` (see `client/src/auth/msalConfig.ts`). The full auth design and its future direction live in [AUTH-ROADMAP.md](AUTH-ROADMAP.md).

---

## Integration API

Inbound push API for upstream systems (e.g., PeopleSoft) under `/api/integrations/v1`, gated by `[IntegrationAuth]`. The credential is read from `X-Integration-Key` or a `Bearer` token and bcrypt-compared against `integration_clients.key_hash`; supplying `X-Client-Name` looks up a single client (avoiding a bcrypt-per-client scan). Endpoints:

- `PUT /step-completions` — one completion (idempotent).
- `POST /step-completions/batch` — up to 500 completions, returning per-item results + a summary.
- `GET /step-catalog` — the available step keys per term (optionally filtered by `term_id`).

Idempotency is enforced by the unique `(integration_client_id, source_event_id)` index on `integration_events`; a repeated `source_event_id` replays the originally stored status and body byte-for-byte. The full request/response shapes are in [API-GUIDE.md](API-GUIDE.md).

---

## Data Model

SQL Server tables (see `Api/Data/schema.sql`):

| Table | Purpose |
|-------|---------|
| `students` | Student records, profile fields, assigned `term_id`, manual `tags` (JSON) |
| `terms` | Admission terms (e.g., Fall 2026); `is_active` flag |
| `steps` | Roadmap steps per term: title, content, order, deadlines, `required_tags`/`excluded_tags`/`required_tag_mode`, `step_key` |
| `student_progress` | Per-student per-step status (`student_id` + `step_id` PK), `status`, `completed_at`, `completed_by`, `note` |
| `admin_users` | Admin accounts, bcrypt `password_hash`, `role` |
| `audit_log` | Append-only record of progress/profile/config changes with attribution |
| `integration_clients` | Upstream integration credentials (`key_hash`) |
| `integration_events` | Idempotency ledger, unique `(integration_client_id, source_event_id)` |
| `step_api_checks` | Per-step outbound API-check config (encrypted credentials) |
| `schema_version` | Append-only log of applied schema versions (written by `SchemaInitializer`) |

Notable translations from the old Postgres schema:

- `SERIAL` → `INT IDENTITY(1,1)`
- `TEXT` → `NVARCHAR(MAX)`
- `TIMESTAMPTZ` → `DATETIME2` (stored as UTC)
- integer-boolean flags kept as `INT` 0/1 to preserve the exact JSON contract
- partial unique indexes on `lower(trim(col))` reproduced via persisted computed columns (e.g., `emplid_norm`) + filtered unique indexes
- JSON array columns (`tags`, `required_tags`, `excluded_tags`) kept as `NVARCHAR(MAX)` holding JSON text, parsed in C# via `Services/Json.cs`

---

## Deployment Architecture

This is the most significant change from the original. The old app shipped as a **single Docker container** running one Express process that served both the API and the pre-built React bundle. The rewrite splits the deployment into **three containers** orchestrated by `docker-compose.yml`:

| Container | Image / Build | Port (host:container) | Role |
|-----------|---------------|-----------------------|------|
| **web** | built from `client/` (`node:22-alpine` build → `nginxinc/nginx-unprivileged:1.27-alpine`) | `3000:8080` | Serves the static Vue bundle and **reverse-proxies `/api` to the api container**. Runs as non-root nginx on 8080. |
| **api** | built from `Api/` (`dotnet/sdk:10.0` → `dotnet/aspnet:10.0`) | `127.0.0.1:8080:8080` | ASP.NET Core API only; **auto-creates the DB/schema/seed on startup** (per the gates above). Runs as the image's non-root user. |
| **sqlserver** | `mcr.microsoft.com/mssql/server:2022-latest` | `127.0.0.1:1433:1433` | SQL Server 2022 (persistent volume `csub_sqlserver_data`) |

```
                                  ┌─────────────────────────────────────────────┐
   Browser                        │            Docker Compose network           │
 ───────────                      │                                             │
  http://localhost:3000  ────────►│  web (nginx-unprivileged :8080, non-root)   │
   │                              │    ├─ /            → static Vue bundle       │
   │  (same origin — no CORS)     │    └─ /api/*       → proxy_pass ${API_URL}    │
   │                              │            + X-Forwarded-For / -Proto         │
   │                              │                         │                   │
   │                              │                         ▼                   │
   │                              │                       api (ASP.NET :8080, non-root)
   │                              │                  HEALTHCHECK /api/health/live │
   │                              │                         │                   │
   │                              │                         ▼                   │
   │                              │                  sqlserver (SQL Server :1433)│
   │                              │                  HEALTHCHECK sqlcmd SELECT 1 │
   └──────────────────────────────────────────────────────────────────────────┘

   depends_on (service_healthy):  web  ──waits-for──►  api  ──waits-for──►  sqlserver
```

**Same-origin via nginx.** The browser only ever talks to `web` on `http://localhost:3000`. nginx serves the SPA for `/` (with `try_files $uri $uri/ /index.html` SPA fallback so client routes like `/admin` load), and reverse-proxies everything under `/api/` to the `api` container (`proxy_pass ${API_URL}`, default `http://api:8080`). Because the API responses come back through the same origin, **the browser never makes a cross-origin request, so CORS is not needed** in the container deployment. The nginx config (`client/nginx.conf.template`) is rendered at container start by `envsubst` from the `API_URL` env var, so you can repoint the proxy at a different backend without rebuilding. The proxy also sets `X-Real-IP` / `X-Forwarded-For` / `X-Forwarded-Proto`, which the API consumes via `UseForwardedHeaders` (see [Forwarded headers / reverse proxy](#forwarded-headers--reverse-proxy)).

**Self-initializing API.** The `api` container waits for `sqlserver` to be healthy (`depends_on: condition: service_healthy`, backed by a `sqlcmd SELECT 1` healthcheck), then runs the [startup sequence](#startup-sequence-programcs--schemainitializercs). Nothing has to be run by hand against the database in dev. (In production, `Database:AutoCreate`/`Database:Seed` are turned off and a DBA provisions the database — see [DEPLOYMENT.md](DEPLOYMENT.md).)

**Apple Silicon note.** SQL Server has no native ARM build, so `sqlserver` is pinned to `platform: linux/amd64` and runs under Docker Desktop / Rancher VZ + Rosetta emulation on M-series Macs.

### Hardened Containers

Both application containers are built to run with the smallest possible attack surface, and all three report health so the orchestrator can sequence and self-heal them.

**Non-root by construction.**

- **api** (`Api/Dockerfile`) is multi-stage: `dotnet/sdk:10.0` builds and `dotnet publish`es; the final `dotnet/aspnet:10.0` stage copies only the publish output, sets `ASPNETCORE_URLS=http://+:8080`, and ends with `USER $APP_UID` so the process runs as the image's built-in **non-root** user. `curl` is installed (as root, *before* the privilege drop) solely for the HEALTHCHECK.
- **web** (`client/Dockerfile`) builds the Vue bundle with `node:22-alpine`, then serves it from `nginxinc/nginx-unprivileged:1.27-alpine`, which runs as a non-root user (uid 101) and binds **8080** instead of 80 (a non-root user cannot bind low ports). That is why the host maps `3000 → 8080`. Vite's `VITE_*` values are inlined at *build* time, so SSO/dev-login config is passed as build args, not runtime env.

**Healthchecks and ordering.**

| Container | HEALTHCHECK | Cadence |
|-----------|-------------|---------|
| **api** | `curl -fsS http://127.0.0.1:8080/api/health/live` | every 15s, 3s timeout, 20s start-period, 3 retries |
| **web** | `wget -q --spider http://127.0.0.1:8080/` (busybox wget; serves the SPA shell) | every 15s, 3s timeout, 10s start-period, 3 retries |
| **sqlserver** | `sqlcmd ... -Q "SELECT 1"` | every 10s, 5s timeout, 40s start-period, 12 retries |

Both app containers explicitly probe `127.0.0.1` rather than `localhost`, because `localhost` can resolve to `::1` (IPv6) first while the servers bind IPv4. Compose chains the dependencies with `condition: service_healthy`: `web` won't start until `api` is healthy, and `api` won't start until `sqlserver` is healthy — so a `docker compose up` brings the stack up in the correct order and waits out SQL Server's slow first boot.

**Secrets are required, not defaulted.** In `docker-compose.yml` the api's sensitive env vars use the `${VAR:?message}` form (`MSSQL_SA_PASSWORD`, `JWT_SECRET`, `ADMIN_DEFAULT_PASSWORD`, `API_CHECK_ENCRYPTION_KEY`), so `docker compose up` fails fast with a helpful message if any is unset. Copy `.env.example` to `.env` and fill them in. The api and sqlserver ports are published on `127.0.0.1` only, so they aren't reachable off-box; the browser reaches the api exclusively through the web proxy.

### Running the full stack

```bash
cp .env.example .env           # fill in the required secrets
docker compose up --build      # builds + starts web, api, sqlserver
# → open http://localhost:3000
```

### Running pieces individually

`depends_on` means starting a higher service pulls the ones below it:

```bash
docker compose up -d sqlserver          # just the database (:1433)
docker compose up -d --build api        # database + API (:8080); pulls sqlserver
docker compose up -d --build web        # full stack (:3000); pulls api + sqlserver
```

### Configuration / secrets

All secrets and config are supplied as environment variables on the `api` service (and `MSSQL_SA_PASSWORD` on `sqlserver`). ASP.NET Core's configuration binder maps the **double-underscore** form of nested keys:

| Env var | Maps to | Default (dev) |
|---------|---------|---------------|
| `ConnectionStrings__Default` | `ConnectionStrings:Default` | `Server=sqlserver,1433;...` |
| `Jwt__Secret` | `Jwt:Secret` | **required** in containers |
| `Admin__DefaultEmail` / `Admin__DefaultPassword` | seed admin | `admin@csub.edu` / **required** |
| `LocalLogin__Username` / `LocalLogin__Password` | break-glass admin (off unless *both* set) | unset |
| `Integration__DefaultName` / `Integration__DefaultKey` | seed integration client | `PeopleSoft Dev` / `dev-integration-key` |
| `ApiCheck__EncryptionKey` | AES-256-GCM key for stored API-check credentials | 64-hex (32-byte) — **required** |
| `AzureAd__ClientId` / `AzureAd__TenantId` | Azure AD SSO | unset (SSO → 501) |
| `Cors__Origin` | allowed SPA origin | only needed if you bypass the nginx proxy |
| `Database__AutoCreate` | gate the `CREATE DATABASE` step | unset → `!IsProduction()` |
| `Database__Seed` | gate the bootstrap seed | unset → `true` |
| `RateLimiting__Disabled` | disable all rate limiting | unset → `false` |

For the production deployment (Windows Server, IIS/reverse proxy, a DBA-provisioned SQL Server, `Database__AutoCreate=false`), see **[DEPLOYMENT.md](DEPLOYMENT.md)** rather than the dev recipe above.

---

## Local Development (without containers)

For day-to-day development you typically run SQL Server in a container but the API and client as native dev processes (the full step-by-step is in [SETUP.md](SETUP.md)):

```bash
# 1. Database
docker compose up -d sqlserver           # SQL Server on localhost:1433

# 2. API (port 3001)  — appsettings.Development.json sets Urls=http://localhost:3001
cd Api
dotnet run                               # creates DB + schema + seed on boot
curl http://localhost:3001/api/health    # -> {"status":"ok","db":"connected", ...}

# 3. Client (port 3000)
cd client
npm install
npm run dev                              # http://localhost:3000
```

In dev, the **Vite dev server** (`client/vite.config.ts`) plays the role nginx plays in containers: it serves the SPA on `:3000` and proxies `/api` to the backend. The proxy target is the `VITE_API_PROXY_TARGET` env var, defaulting to `http://localhost:3001` (the `dotnet run` port). Because the client always calls **relative `/api`** paths — proxied by Vite in dev and by nginx in containers — **no API URL is ever hardcoded** in the frontend.

### Running the frontend on its own (e.g., a Windows desktop)

You can run just the Vue client on a separate machine and point it at a backend elsewhere:

1. Install **Node.js LTS** from [nodejs.org](https://nodejs.org).
2. Open **PowerShell**.
3. `cd client`
4. `npm install`
5. `npm run dev`
6. Open **http://localhost:3000**.

To point the dev proxy at a backend that is **not** on `localhost:3001`, set the env var before starting:

```powershell
# PowerShell
$env:VITE_API_PROXY_TARGET="http://<host>:<port>"; npm run dev
```

```bat
:: cmd.exe
set VITE_API_PROXY_TARGET=http://<host>:<port> && npm run dev
```

Alternatively, use **Docker Desktop on Windows** and run `docker compose up web` (which pulls `api` and `sqlserver` via `depends_on`).

---

## Quality Gates & CI

The project treats lint, format, and tests as build-time contracts on both halves of the stack.

**Backend.** `Api/Api.csproj` enables the .NET analyzers (`EnableNETAnalyzers`, `AnalysisLevel=latest`) and turns warnings into errors (`TreatWarningsAsErrors`), so an analyzer warning fails the build. `Api/.editorconfig` carries the analyzer rule set and documents the few **intentional** suppressions — `CA1707` (the snake_case identifiers that mirror the SQL/JSON contract) and `CA1848` (the high-performance `LoggerMessage` pattern, deliberately not adopted for this app's logging volume).

**Frontend.** `client/` ships Vitest unit tests (`client/src/**/*.test.ts`), an ESLint **flat** config (`eslint.config.js`), and Prettier (`.prettierrc.json`). The `npm` scripts `test`, `lint`, and `format` run them.

**CI.** `.github/workflows/ci.yml` builds and tests the API against a real **SQL Server service container** (the integration tests need real SQL behavior — no mocks), and separately lints, tests, and builds the client. A red pipeline blocks merge. The integration-test design is detailed in [TESTING.md](TESTING.md).

---

## Testing

The `tests/Api.IntegrationTests` project hosts the **real application** via `WebApplicationFactory` (enabled by `public partial class Program {}` at the bottom of `Program.cs`) and exercises it end-to-end against a real SQL Server instance. `WebAppFixture.cs` points the app at a dedicated `csub_admissions_test` database that is dropped before each run, so the [startup sequence](#startup-sequence-programcs--schemainitializercs) rebuilds the schema and re-seeds deterministically. Rate limiting is disabled in tests (`RateLimiting:Disabled=true`) so the per-IP login limit doesn't interfere. The test files mirror the controller surface (`AuthTests`, `AdminAuthTests`, `StepsTests`, `AdminStepsTests`, `AdminStudentsTests`, `AdminTermsTests`, `AdminUsersTests`, `AdminAnalyticsTests`, `IntegrationsTests`, `ApiChecksTests`, plus `SmokeTests` and `MiscTests`). Running the suite requires the SQL Server container to be up. See [TESTING.md](TESTING.md) for the full strategy.

---

## Key Design Decisions

- **No ORM.** Every query is hand-written T-SQL passed to Dapper through the thin `Db` wrapper (`QueryOne`/`QueryAll`/`Execute`/`Transaction`). Parameters are plain anonymous objects (`new { id, term_id }`); no repositories, no LINQ, no query builder. This mirrors the old Node `db/pool.ts` so ported code reads the same — and SQL injection is avoided through parameterization rather than string building.
- **Resilient data layer.** That same `Db` wrapper retries transient SQL faults with exponential backoff (4 attempts), retrying whole transactions rather than individual statements, so routine failover/throttling/deadlock events don't surface as 500s. See [The Data Layer](#the-data-layer-dbcs).
- **Preserve the API contract exactly.** Paths, payloads, status codes, error envelopes, snake_case vs. camelCase keys, and UTC `Z` timestamps all match the original app byte-for-byte. This is enforced in `Program.cs` (no JSON naming policy, suppressed model-state 400, custom UTC converter) and lets the two implementations diff cleanly.
- **App-managed schema + seed, gated for production.** `SchemaInitializer` + `Seeder` run on boot. The schema is idempotent and its version is recorded in `schema_version`; the optional `CREATE DATABASE` is gated behind `Database:AutoCreate` (off in Production, where a DBA provisions the DB) and seeding behind `Database:Seed`. No migration tool, no manual SQL step in dev.
- **Liveness vs. readiness health probes.** `/api/health/live` never touches the DB (so a DB outage doesn't restart healthy processes); `/api/health/ready` returns 503 when the DB is unreachable (so the instance is pulled from rotation instead). Containers probe liveness; load balancers should probe readiness.
- **Three-container deployment with same-origin proxy.** Splitting `web` / `api` / `sqlserver` lets each scale and ship independently, while nginx reverse-proxying `/api` keeps the browser on a single origin so **CORS is normally unnecessary**. The client's relative `/api` calls work identically in dev (Vite proxy) and in production (nginx proxy).
- **Hardened, self-sequencing containers.** Both app containers run non-root (the api as `$APP_UID`, the web as nginx-unprivileged on 8080); all three declare HEALTHCHECKs, and Compose gates each service on the next being healthy. Required secrets use the `${VAR:?msg}` form so a misconfigured stack fails fast.
- **Trust the proxy, but only the proxy.** `UseForwardedHeaders` honors `X-Forwarded-For`/`-Proto` from nginx so rate limiting and audit log key on the real client IP. The known-proxy allowlist is cleared because the api is only reachable behind nginx on the internal network / `127.0.0.1`.
- **Transaction-based row locking.** Progress writes go through `Progress.ApplyAsync` using `WITH (UPDLOCK)` inside a `Db.TransactionAsync`, the SQL Server equivalent of the old `SELECT ... FOR UPDATE`, so concurrent admin edits and integration pushes can't corrupt a student's progress row.
- **Integration-test isolation.** `tests/Api.IntegrationTests` hosts the real app via `WebApplicationFactory` against a dedicated test database that is dropped and rebuilt per run — no mocks for the data layer, real SQL Server behavior under test.
- **Split admin API.** The admin surface is split into focused controllers (`Analytics`, `Steps`, `Students`, `Terms`, `Users`, `ApiChecks`) under `/api/admin`, each declaring its own `[AdminAuth(...)]` role gates per action — the C# analog of the old "5 focused route modules" refactor of a single 1,660-line file.
- **Shared helpers.** Common patterns (`ParseTermId`, `ParsePagination`, `CountActiveStepsAsync`, the active-step SQL fragment) live in `Api/Services/QueryHelpers.cs` to eliminate duplication across controllers.
- **Quality as a build contract.** Backend analyzers run with `TreatWarningsAsErrors`; the frontend lints, formats, and unit-tests; CI exercises both against a real SQL Server. See [Quality Gates & CI](#quality-gates--ci).

---

## UI Reference

The three primary surfaces, with screenshots in `docs/screenshots/`:

- **Public landing / preview** — the anonymous roadmap preview of public steps shown to visitors who aren't signed in (`views/HomeView.vue` + `components/PublicRoadmapPreview.vue`).

  <img src="screenshots/public-preview.png" alt="Public landing page" width="720" />

- **Student dashboard** — the personalized timeline with progress tracking, deadline countdowns, and completion celebration (`pages/RoadmapPage.vue` + `components/roadmap/`).

  <img src="screenshots/student-dashboard.png" alt="Student dashboard with progress tracking" width="720" />

- **Admin dashboard** — the tabbed admin console (steps, students, terms, analytics, users, API checks, audit log) under `/admin` (`pages/admin/AdminPage.vue`).

  <img src="screenshots/admin-dashboard.png" alt="Admin dashboard" width="720" />
