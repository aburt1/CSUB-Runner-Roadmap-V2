# Architecture

## Tech Stack

| Layer | Technologies |
|-------|-------------|
| **Frontend** | Vue 3 (SFCs), TypeScript, Vite, Pinia, vue-router, Tailwind CSS 3, vuedraggable (reorder), vue-chartjs + Chart.js (analytics), Tiptap (rich text), vue3-emoji-picker, canvas-confetti, DOMPurify |
| **Backend** | ASP.NET Core controllers (.NET 10), C#, Dapper + hand-written T-SQL, SQL Server 2022 |
| **Auth** | App-issued JWT sessions (HS256), bcrypt password hashing (BCrypt.Net-Next), Azure AD SSO via MSAL (optional) |
| **Security** | Helmet-equivalent security headers, CORS, ASP.NET rate limiting, AES-256-GCM credential encryption |
| **Testing** | xUnit + `WebApplicationFactory` integration tests against a real SQL Server database |
| **Deployment** | Docker multi-stage build; single ASP.NET Core process serves the built SPA + API |

The rewrite deliberately keeps the REST API contract (paths, payloads, status codes, even snake_case vs. camelCase JSON keys) identical to the original React + Node/Express + PostgreSQL app, so the two diff cleanly. A few things were intentionally **dropped**: the legacy `X-API-Key` admin auth path, the dev "live activity" simulator, and the dev-only mock API-check routes.

---

## Project Structure

```
CSUB-Runner-Roadmap-V2/
├── client/                          # Vue 3 SPA (Vite + Tailwind + Pinia)
│   ├── src/
│   │   ├── auth/                    # msalConfig.ts (Azure AD SSO)
│   │   ├── stores/                  # Pinia stores (auth.ts)
│   │   ├── composables/             # useProgress.ts, useAdminApi.ts
│   │   ├── components/
│   │   │   └── roadmap/             # TimelineStep, StepDetailPanel, ListView, etc.
│   │   ├── pages/
│   │   │   ├── RoadmapPage.vue      # Main student view
│   │   │   └── admin/               # Admin dashboard (tabs + charts/)
│   │   ├── views/                   # HomeView.vue
│   │   ├── router/index.ts          # /, /admin, /admin/local-login
│   │   ├── types/api.ts             # Shared TypeScript types
│   │   └── main.ts                  # App entry
│   ├── vite.config.ts               # Dev server on :3000, proxies /api -> :3001
│   └── package.json
│
├── Api/                             # ASP.NET Core API
│   ├── Program.cs                   # App entry: DI, CORS, rate limiting, headers,
│   │                                #   schema init + seed, SPA fallback
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
│   │   ├── Passwords.cs              # bcrypt hash/verify
│   │   ├── StudentAuthAttribute.cs   # Student JWT action filter
│   │   ├── AdminAuthAttribute.cs     # Admin JWT + role-check action filter
│   │   ├── IntegrationAuthAttribute.cs  # Integration-key action filter
│   │   ├── AzureAdTokenValidator.cs  # Azure AD id_token validation
│   │   └── RequestContext.cs         # HttpContext.Items typed accessors
│   ├── Data/
│   │   ├── Db.cs                     # Dapper wrapper (QueryOne/QueryAll/Execute/Transaction)
│   │   ├── SchemaInitializer.cs      # Create DB + run schema.sql on startup
│   │   ├── Seeder.cs                 # Seed defaults + dev sample data
│   │   ├── schema.sql                # Hand-written T-SQL schema (idempotent)
│   │   └── seed/                     # fall2026-onboarding-checklist.json
│   ├── Services/
│   │   ├── Progress.cs               # Step completion logic (ApplyAsync)
│   │   ├── StudentTags.cs            # Manual + derived tag merging
│   │   ├── StepKeys.cs               # Unique step-key slug generation
│   │   ├── QueryHelpers.cs           # parseTermId/pagination/active-step helpers
│   │   ├── Audit.cs                  # Audit logging
│   │   ├── Encryption.cs             # AES-256-GCM credential encryption
│   │   ├── ApiCheckRunner.cs         # Outbound API-check execution + SSRF guard
│   │   └── Json.cs                   # Safe JSON parsing helpers
│   ├── Models/Rows.cs                # DB row / model types
│   ├── Serialization/
│   │   └── UtcDateTimeConverter.cs   # ISO-8601 UTC 'Z' timestamp output
│   ├── appsettings.json / appsettings.Development.json
│   └── Api.csproj
│
├── tests/
│   └── Api.IntegrationTests/         # xUnit + WebApplicationFactory route tests
│       ├── WebAppFixture.cs          # Hosts the app against a test SQL Server DB
│       └── *Tests.cs                 # Auth, Steps, Admin*, Integrations, ApiChecks, ...
│
├── docs/                            # Documentation
├── docker-compose.yml               # SQL Server (+ full app via --build)
└── Dockerfile                       # Multi-stage: build Vue -> publish API -> runtime
```

---

## How Student Steps Work

Each student's roadmap is built from four things:

1. **Assigned term** — Students are assigned to a term (e.g., Fall 2026 via `students.term_id`). They only see steps from that term. If a student has no assigned term, the active term's steps are used as a fallback (`SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC`).

2. **Step visibility rules** — Each step can have `required_tags` and `excluded_tags` (stored as JSON arrays), plus a `required_tag_mode` of `any` (default) or `all`. A step is hidden if the student carries any excluded tag; otherwise it appears when the student matches the required tags (any-of or all-of, per the mode). See `StepsController.StepAppliesToStudent` and `Api/Services/StudentTags.cs`.

3. **Manual + derived tags** — Tags come from two sources (`StudentTags.Merged`):
   - **Manual tags**: Set by admissions staff and stored as a JSON array in `students.tags` (e.g., `honors`, `athlete`, `eop`, `first-gen`, `veteran`).
   - **Derived tags**: Auto-generated from profile fields — `applicant_type` containing "transfer"/"freshman"/"readmit" produces `transfer`/`freshman`/`readmit`; `residency` produces `out-of-state`/`in-state`; and `major` produces a slugged `major:<slug>` tag (e.g., `major:computer-science`).

4. **Progress records** — Each step can be `completed`, `waived`, or `not_completed`. Progress is tracked in the `student_progress` table (primary key `student_id` + `step_id`) with `completed_at`, `status`, `note`, and a `completed_by` attribution (`manual` | `integration` | `api_check` | `auto`). All writes flow through `Progress.ApplyAsync`.

**Step keys.** Each step has a stable `step_key` (a slugged title, made unique per term with `-2`/`-3` suffixes — see `Api/Services/StepKeys.cs`). Integrations and API checks reference steps by `(term, step_key)` rather than database id, so the same key is portable across cloned terms.

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

`Db.QueryOneAsync` / `QueryAllAsync` / `ExecuteAsync` open a fresh SQL Server connection per call; `Db.TransactionAsync` runs a unit of work on a single connection/transaction (commit on success, rollback on throw). The row lock in `Progress.ApplyAsync` is the T-SQL `WITH (UPDLOCK)` hint (the SQL Server equivalent of the old Postgres `SELECT ... FOR UPDATE`).

---

## Request Pipeline (`Program.cs`)

On startup the app:

1. Ensures the database exists, runs `Data/schema.sql` (idempotent — every object is guarded by an existence check), and seeds defaults (`SchemaInitializer` + `Seeder`). **No manual DB setup is required.**
2. Builds the middleware pipeline in order: an outer error handler that returns `{ "error": "Internal server error" }` (never a stack trace) → Helmet-equivalent security headers (CSP, `X-Frame-Options`, HSTS, etc.) → CORS → rate limiting → static files (the built SPA) → controllers → SPA fallback to `index.html`.

**JSON contract.** No naming policy is applied, so each response spells its keys verbatim — snake_case for DB-row responses (`step_id`, `completed_at`), camelCase for hand-built auth responses (`displayName`). Timestamps are emitted as ISO-8601 UTC with a trailing `Z` via `UtcDateTimeConverter`. Controllers validate input by hand and return `{ "error": "..." }`, so the automatic model-state 400 is suppressed.

**Rate limiting.** Global 200 requests / 15 min per IP, scoped to `/api` so SPA/static requests don't consume the budget; named policies `login` (10/15 min) and `breakGlass` (5/15 min) tighten the auth endpoints. Can be disabled with `RateLimiting:Disabled=true` (used by the integration test suite).

---

## Authentication

Two app-issued JWT session types, both HS256 with an 8-hour lifetime (`JwtService`), with claim names matching the original app:

- **Student** — `{ type: "student", studentId, email }`. Issued by `POST /api/auth/dev-login` (dev/POC only, name + email) or `POST /api/auth/sso` (Azure AD). Enforced by `[StudentAuth]`. `GET /api/steps` reads the token optionally; new students start with the `accepted` step auto-completed.
- **Admin** — `{ type: "admin", adminId, role, email, displayName }`. Issued by `POST /api/admin/auth/login` (email + bcrypt password), `POST /api/admin/auth/sso` (Azure AD), or the env-gated break-glass `POST /api/admin/auth/local-login`. Enforced by `[AdminAuth]`, which also does the role check: `[AdminAuth]` = any authenticated admin, `[AdminAuth("admissions_editor", "sysadmin")]` = only those roles.

The auth filters stash identity on `HttpContext.Items`; controllers read it through the typed accessors in `RequestContext.cs` (`StudentId()`, `StudentEmail()`, `AdminUser()`).

**RBAC roles** (least → most privileged): `viewer`, `admissions`, `admissions_editor`, `sysadmin`. Roughly: viewers read analytics; `admissions` can change individual student progress/profiles/tags; `admissions_editor` can also edit steps and terms; `sysadmin` can additionally manage admin users and API-check configuration.

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

SQL Server tables (see `Api/Data/schema.sql`): `students`, `terms`, `steps`, `student_progress`, `admin_users`, `audit_log`, `integration_clients`, `integration_events`, `step_api_checks`. Notable translations from the old Postgres schema: `SERIAL` → `INT IDENTITY(1,1)`, `TEXT` → `NVARCHAR(MAX)`, `TIMESTAMPTZ` → `DATETIME2` (UTC), integer-boolean flags kept as `INT` 0/1 to preserve the JSON contract, and partial unique indexes on `lower(trim(col))` reproduced via persisted computed columns (`emplid_norm`) + filtered unique indexes.

---

## Key Design Decisions

- **No ORM** — Every query is hand-written T-SQL passed to Dapper through the thin `Db` wrapper (`QueryOne`/`QueryAll`/`Execute`/`Transaction`). Parameters are plain anonymous objects (`new { id, term_id }`); no repositories, no LINQ, no query builder. This mirrors the old Node `db/pool.ts` so ported code reads the same.
- **App-managed schema + seed** — `SchemaInitializer` + `Seeder` run on every boot. The schema is idempotent; seeding is guarded by "is this table empty?" checks. Dev (non-Production) also seeds a deterministic 50-student sample (fixed RNG seed) for realistic analytics.
- **Integration-test isolation** — `tests/Api.IntegrationTests` hosts the real app via `WebApplicationFactory` against a dedicated `csub_admissions_test` database that is dropped before the run, so startup rebuilds the schema and re-seeds deterministically. Requires the SQL Server container to be running.
- **Split admin API** — The admin surface is split into focused controllers (Analytics, Steps, Students, Terms, Users, ApiChecks) under `/api/admin`, each declaring its own `[AdminAuth(...)]` role gates per action.
- **Shared helpers** — Common patterns (`ParseTermId`, `ParsePagination`, `CountActiveStepsAsync`, the active-step SQL fragment) live in `Api/Services/QueryHelpers.cs` to eliminate duplication.

---

## UI Reference

The three primary surfaces, with screenshots in `docs/screenshots/`:

- **Public landing / preview** — anonymous roadmap preview of public steps.

  <img src="screenshots/public-preview.png" alt="Public landing page" width="720" />

- **Student dashboard** — the personalized timeline with progress tracking (`RoadmapPage.vue`).

  <img src="screenshots/student-dashboard.png" alt="Student dashboard with progress tracking" width="720" />

- **Admin dashboard** — the tabbed admin console (steps, students, terms, analytics, users) under `/admin` (`pages/admin/AdminPage.vue`).

  <img src="screenshots/admin-dashboard.png" alt="Admin dashboard" width="720" />
