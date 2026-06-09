# Architecture

This document describes the architecture of the CSUB Runner Roadmap application after its conversion from the original **React + Node/Express + PostgreSQL** stack to **Vue 3 + ASP.NET Core + SQL Server**. The rewrite deliberately keeps the REST API contract (paths, payloads, status codes, even the snake_case vs. camelCase JSON key conventions) identical to the original app, so the two implementations diff cleanly and the same client behavior is preserved. Where the original prose still applies, it has been kept and updated only where the stack changed.

---

## Tech Stack

| Layer | Technologies |
|-------|-------------|
| **Frontend** | Vue 3 (Single-File Components), TypeScript, Vite, Pinia (state), vue-router, Tailwind CSS 3, vuedraggable (step reorder), vue-chartjs + Chart.js (analytics), Tiptap (rich-text step content), vue3-emoji-picker, canvas-confetti, DOMPurify (HTML sanitization) |
| **Backend** | ASP.NET Core controllers (.NET 10), C#, Dapper + hand-written T-SQL, SQL Server 2022 |
| **Auth** | App-issued JWT sessions (HS256), bcrypt password hashing (BCrypt.Net-Next), Azure AD SSO via MSAL (optional) |
| **Security** | Helmet-equivalent security headers, CORS, ASP.NET Core rate limiting, AES-256-GCM credential encryption, SSRF-guarded outbound API checks |
| **Testing** | xUnit + `WebApplicationFactory` integration tests against a real SQL Server database |
| **Deployment** | Docker Compose — three containers: **web** (Vue build served by nginx), **api** (ASP.NET Core), **sqlserver** (SQL Server 2022). nginx reverse-proxies `/api` to the API so the browser sees a single origin. |

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
│   │   ├── main.ts                  # App entry: createApp, Pinia, router, mount
│   │   ├── App.vue                  # Root component (router-view shell)
│   │   ├── index.css                # Tailwind entry + global styles
│   │   ├── shims.d.ts               # TS shims for .vue imports
│   │   ├── assets/                  # Static assets
│   │   ├── auth/
│   │   │   └── msalConfig.ts        # Azure AD / MSAL SSO config (reads VITE_AZURE_AD_*)
│   │   ├── stores/
│   │   │   └── auth.ts              # Pinia store: session token, identity, login/logout
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
│   ├── nginx.conf.template          # Container: nginx serves SPA + proxies /api
│   ├── Dockerfile                   # Container: build Vue, serve via nginx
│   └── package.json
│
├── Api/                             # ASP.NET Core API (backend only)
│   ├── Program.cs                   # App entry: DI, CORS, rate limiting, security
│   │                                #   headers, schema init + seed, SPA fallback
│   ├── Controllers/
│   │   ├── HealthController.cs       # GET /api/health
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
│   │   ├── Db.cs                     # Dapper wrapper (QueryOne/QueryAll/Execute/Transaction)
│   │   ├── SchemaInitializer.cs      # Create DB + run schema.sql on startup
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
│   ├── Dockerfile                    # Multi-stage: SDK build -> aspnet runtime
│   └── Api.csproj
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
├── docs/                            # Documentation (this file lives here)
│   └── screenshots/                 # public-preview / student-dashboard / admin-dashboard
├── docker-compose.yml               # Three containers: web, api, sqlserver
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

## Data Flow

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

**Database access.** `Db.QueryOneAsync` / `QueryAllAsync` / `ExecuteAsync` open a fresh SQL Server connection per call; `Db.TransactionAsync` runs a unit of work on a single connection/transaction (commit on success, rollback on throw). The row lock in `Progress.ApplyAsync` is the T-SQL `WITH (UPDLOCK)` hint — the SQL Server equivalent of the old Postgres `SELECT ... FOR UPDATE`. This is why the same progress-write path is safe under concurrent admin edits and integration pushes.

---

## Request Pipeline (`Program.cs`)

The ASP.NET Core process is configured entirely in `Api/Program.cs`. On startup the app:

1. **Initializes the database.** It ensures the database exists (`SchemaInitializer.EnsureDatabaseAsync`), runs `Data/schema.sql` (`SchemaInitializer.RunAsync` — idempotent, every object guarded by an existence check), and seeds defaults (`Seeder.RunAsync`). **No manual DB setup is required** — point the API at an empty SQL Server and it builds itself on first boot.

2. **Builds the middleware pipeline**, in order:
   - An **outer error handler** that turns any unhandled exception into the old `{ "error": "Internal server error" }` envelope (status 500) and never leaks a stack trace.
   - **Security headers** — the Helmet-equivalent set: a Content-Security-Policy (`default-src 'self'`, allowing Google Fonts for styles/fonts and `data:`/`https:` images), `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Strict-Transport-Security` (HSTS), `Cross-Origin-Opener-Policy`/`Cross-Origin-Resource-Policy: same-origin`, and more.
   - **CORS** (see below).
   - **Rate limiting** (see below).
   - **Static files** (`UseDefaultFiles` + `UseStaticFiles`) — serves a built SPA out of `wwwroot` when present. In the three-container deployment `wwwroot` is empty (nginx serves the SPA), so these effectively no-op; the hooks remain so the API can still serve a bundled SPA if you publish one into `wwwroot`.
   - **Controllers** (`MapControllers`).
   - **SPA fallback** (`MapFallbackToFile("index.html")`) — any non-`/api` route falls back to `index.html` for client-side routing, again only relevant when the API serves the SPA directly.

**JSON contract.** No naming policy is applied (`PropertyNamingPolicy = null`), so each response spells its keys verbatim — snake_case for DB-row responses (`step_id`, `completed_at`) and camelCase for hand-built auth responses (`displayName`). Inbound JWT claim mapping is disabled (`JwtSecurityTokenHandler.DefaultMapInboundClaims = false`) so claim names stay verbatim (`type`, `studentId`, `role`). Timestamps are emitted as ISO-8601 UTC with a trailing `Z` via `Serialization/UtcDateTimeConverter`. Controllers validate input by hand and return `{ "error": "..." }`, so the automatic model-state 400 (`SuppressModelStateInvalidFilter = true`) is suppressed.

**CORS.** Mirrors the old server: the SPA origin is allowed with credentials. The origin comes from `Cors:Origin`, defaulting to `http://localhost:3000` in non-production and to *nothing* in Production (CORS effectively closed unless explicitly configured). In the container deployment CORS is normally unnecessary because nginx makes the browser see one origin (see below).

**Rate limiting.** A global limiter allows **200 requests / 15 min per IP**, scoped to `/api` only so SPA/static requests don't consume the budget. Two named policies tighten the auth endpoints: `login` (10 / 15 min) and `breakGlass` (5 / 15 min). The whole limiter can be disabled with `RateLimiting:Disabled=true` (used by the integration test suite, which would otherwise trip the per-IP login limit).

---

## Authentication

Two app-issued JWT session types, both HS256 with an 8-hour lifetime (`JwtService`), with claim names matching the original app:

- **Student** — `{ type: "student", studentId, email }`. Issued by `POST /api/auth/dev-login` (dev/POC only, name + email) or `POST /api/auth/sso` (Azure AD). Enforced by `[StudentAuth]`. `GET /api/steps` reads the token optionally; new students start with the `accepted` step auto-completed.
- **Admin** — `{ type: "admin", adminId, role, email, displayName }`. Issued by `POST /api/admin/auth/login` (email + bcrypt password), `POST /api/admin/auth/sso` (Azure AD), or the env-gated break-glass `POST /api/admin/auth/local-login`. Enforced by `[AdminAuth]`, which also does the role check: `[AdminAuth]` = any authenticated admin, `[AdminAuth("admissions_editor", "sysadmin")]` = only those roles.

The auth filters stash identity on `HttpContext.Items`; controllers read it through the typed accessors in `RequestContext.cs` (`StudentId()`, `StudentEmail()`, `AdminUser()`).

**RBAC roles** (least → most privileged): `viewer`, `admissions`, `admissions_editor`, `sysadmin`. Roughly: viewers read analytics; `admissions` can change individual student progress/profiles/tags; `admissions_editor` can also edit steps and terms; `sysadmin` can additionally manage admin users and API-check configuration.

**Default credentials** (override in any real deployment via env vars):

| Account | Username / Email | Password | Source |
|---------|------------------|----------|--------|
| Default admin | `admin@csub.edu` | `admin123` | `Admin:DefaultEmail` / `Admin:DefaultPassword` |
| Break-glass local admin | `localadmin` | `Local_Admin_2026!` | `LocalLogin:Username` / `LocalLogin:Password` |
| Integration key (dev) | `PeopleSoft Dev` | `dev-integration-key` | `Integration:DefaultName` / `Integration:DefaultKey` |

**Azure AD SSO** is optional. When `AzureAd:ClientId`/`AzureAd:TenantId` are unset, the `/sso` endpoints return `501`. The client reads its SSO config from `VITE_AZURE_AD_*` (see `client/src/auth/msalConfig.ts`).

---

## Integration API

Inbound push API for upstream systems (e.g., PeopleSoft) under `/api/integrations/v1`, gated by `[IntegrationAuth]`. The credential is read from `X-Integration-Key` or a `Bearer` token and bcrypt-compared against `integration_clients.key_hash`; supplying `X-Client-Name` looks up a single client (avoiding a bcrypt-per-client scan). Endpoints:

- `PUT /step-completions` — one completion (idempotent).
- `POST /step-completions/batch` — up to 500 completions, returning per-item results + a summary.
- `GET /step-catalog` — the available step keys per term (optionally filtered by `term_id`).

Idempotency is enforced by the unique `(integration_client_id, source_event_id)` index on `integration_events`; a repeated `source_event_id` replays the originally stored status and body byte-for-byte.

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
| **web** | built from `client/` (`node:22-alpine` build → `nginx:1.27-alpine`) | `3000:80` | Serves the static Vue bundle and **reverse-proxies `/api` to the api container** |
| **api** | built from `Api/` (`dotnet/sdk:10.0` → `dotnet/aspnet:10.0`) | `8080:8080` | ASP.NET Core API only; **auto-creates the DB, schema, and seed on startup** |
| **sqlserver** | `mcr.microsoft.com/mssql/server:2022-latest` | `1433:1433` | SQL Server 2022 (persistent volume `csub_sqlserver_data`) |

```
                                  ┌─────────────────────────────────────────────┐
   Browser                        │            Docker Compose network           │
 ───────────                      │                                             │
  http://localhost:3000  ────────►│  web (nginx :80)                            │
   │                              │    ├─ /            → static Vue bundle       │
   │  (same origin — no CORS)     │    └─ /api/*       → proxy_pass http://api:8080
   │                              │                         │                   │
   │                              │                         ▼                   │
   │                              │                       api (ASP.NET :8080)   │
   │                              │                         │                   │
   │                              │                         ▼                   │
   │                              │                  sqlserver (SQL Server :1433)│
   └──────────────────────────────────────────────────────────────────────────┘
```

**Same-origin via nginx.** The browser only ever talks to `web` on `http://localhost:3000`. nginx serves the SPA for `/` (with `try_files ... /index.html` SPA fallback so client routes like `/admin` load), and reverse-proxies everything under `/api/` to the `api` container (`proxy_pass ${API_URL}`, default `http://api:8080`). Because the API responses come back through the same origin, **the browser never makes a cross-origin request, so CORS is not needed** in the container deployment. The nginx config (`client/nginx.conf.template`) is rendered at container start by `envsubst` from the `API_URL` env var, so you can repoint the proxy at a different backend without rebuilding.

**Self-initializing API.** The `api` container waits for `sqlserver` to be healthy (`depends_on: condition: service_healthy`, backed by a `sqlcmd SELECT 1` healthcheck), then on boot ensures the database exists, applies the idempotent `schema.sql`, and seeds defaults. Nothing has to be run by hand against the database.

**Apple Silicon note.** SQL Server has no native ARM build, so `sqlserver` is pinned to `platform: linux/amd64` and runs under Docker Desktop / Rancher VZ + Rosetta emulation on M-series Macs.

### Running the full stack

```bash
docker compose up --build      # builds + starts web, api, sqlserver
# → open http://localhost:3000
```

### Running pieces individually

`depends_on` means starting a higher service pulls the ones below it:

```bash
docker compose up -d sqlserver          # just the database (:1433)
docker compose up -d --build api        # database + API (:8080)
docker compose up -d --build web        # full stack (:3000); pulls api + sqlserver
```

### Configuration / secrets

All secrets and config are supplied as environment variables on the `api` service (and `MSSQL_SA_PASSWORD` on `sqlserver`). ASP.NET Core's configuration binder maps the **double-underscore** form of nested keys:

| Env var | Maps to | Default (dev) |
|---------|---------|---------------|
| `ConnectionStrings__Default` | `ConnectionStrings:Default` | `Server=sqlserver,1433;...` |
| `Jwt__Secret` | `Jwt:Secret` | dev placeholder — **override** |
| `Admin__DefaultEmail` / `Admin__DefaultPassword` | seed admin | `admin@csub.edu` / `admin123` |
| `LocalLogin__Username` / `LocalLogin__Password` | break-glass admin | `localadmin` / `Local_Admin_2026!` |
| `Integration__DefaultName` / `Integration__DefaultKey` | seed integration client | `PeopleSoft Dev` / `dev-integration-key` |
| `ApiCheck__EncryptionKey` | AES-256-GCM key for stored API-check credentials | 64-hex (32-byte) — **override** |
| `AzureAd__ClientId` / `AzureAd__TenantId` | Azure AD SSO | unset (SSO → 501) |
| `Cors__Origin` | allowed SPA origin | only needed if you bypass the nginx proxy |

---

## Local Development (without containers)

For day-to-day development you typically run SQL Server in a container but the API and client as native dev processes:

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

## Testing

The `tests/Api.IntegrationTests` project hosts the **real application** via `WebApplicationFactory` (enabled by `public partial class Program {}` at the bottom of `Program.cs`) and exercises it end-to-end against a real SQL Server instance. `WebAppFixture.cs` points the app at a dedicated `csub_admissions_test` database that is dropped before each run, so startup rebuilds the schema and re-seeds deterministically. Rate limiting is disabled in tests (`RateLimiting:Disabled=true`) so the per-IP login limit doesn't interfere. The test files mirror the controller surface (`AuthTests`, `AdminAuthTests`, `StepsTests`, `AdminStepsTests`, `AdminStudentsTests`, `AdminTermsTests`, `AdminUsersTests`, `AdminAnalyticsTests`, `IntegrationsTests`, `ApiChecksTests`, plus `SmokeTests` and `MiscTests`). Running the suite requires the SQL Server container to be up.

---

## Key Design Decisions

- **No ORM.** Every query is hand-written T-SQL passed to Dapper through the thin `Db` wrapper (`QueryOne`/`QueryAll`/`Execute`/`Transaction`). Parameters are plain anonymous objects (`new { id, term_id }`); no repositories, no LINQ, no query builder. This mirrors the old Node `db/pool.ts` so ported code reads the same — and SQL injection is avoided through parameterization rather than string building.
- **Preserve the API contract exactly.** Paths, payloads, status codes, error envelopes, snake_case vs. camelCase keys, and UTC `Z` timestamps all match the original app byte-for-byte. This is enforced in `Program.cs` (no JSON naming policy, suppressed model-state 400, custom UTC converter) and lets the two implementations diff cleanly.
- **App-managed schema + seed.** `SchemaInitializer` + `Seeder` run on every boot. The schema is idempotent; seeding is guarded by "is this table empty?" checks. Dev (non-Production) also seeds a deterministic 50-student sample (fixed RNG seed) so the analytics dashboard has realistic data. No migration tool, no manual SQL step.
- **Three-container deployment with same-origin proxy.** Splitting `web` / `api` / `sqlserver` lets each scale and ship independently, while nginx reverse-proxying `/api` keeps the browser on a single origin so **CORS is normally unnecessary**. The client's relative `/api` calls work identically in dev (Vite proxy) and in production (nginx proxy).
- **Transaction-based row locking.** Progress writes go through `Progress.ApplyAsync` using `WITH (UPDLOCK)` inside a `Db.TransactionAsync`, the SQL Server equivalent of the old `SELECT ... FOR UPDATE`, so concurrent admin edits and integration pushes can't corrupt a student's progress row.
- **Integration-test isolation.** `tests/Api.IntegrationTests` hosts the real app via `WebApplicationFactory` against a dedicated test database that is dropped and rebuilt per run — no mocks for the data layer, real SQL Server behavior under test.
- **Split admin API.** The admin surface is split into focused controllers (`Analytics`, `Steps`, `Students`, `Terms`, `Users`, `ApiChecks`) under `/api/admin`, each declaring its own `[AdminAuth(...)]` role gates per action — the C# analog of the old "5 focused route modules" refactor of a single 1,660-line file.
- **Shared helpers.** Common patterns (`ParseTermId`, `ParsePagination`, `CountActiveStepsAsync`, the active-step SQL fragment) live in `Api/Services/QueryHelpers.cs` to eliminate duplication across controllers.

---

## UI Reference

The three primary surfaces, with screenshots in `docs/screenshots/`:

- **Public landing / preview** — the anonymous roadmap preview of public steps shown to visitors who aren't signed in (`views/HomeView.vue` + `components/PublicRoadmapPreview.vue`).

  <img src="screenshots/public-preview.png" alt="Public landing page" width="720" />

- **Student dashboard** — the personalized timeline with progress tracking, deadline countdowns, and completion celebration (`pages/RoadmapPage.vue` + `components/roadmap/`).

  <img src="screenshots/student-dashboard.png" alt="Student dashboard with progress tracking" width="720" />

- **Admin dashboard** — the tabbed admin console (steps, students, terms, analytics, users, API checks, audit log) under `/admin` (`pages/admin/AdminPage.vue`).

  <img src="screenshots/admin-dashboard.png" alt="Admin dashboard" width="720" />
