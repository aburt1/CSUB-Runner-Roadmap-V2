# Enterprise Readiness — CSUB Runner Roadmap V2

Findings from the enterprise-readiness audit (2026-06-09, 6 dimensions, adversarially
verified) and how each is being resolved. Target deployment: app in Docker containers
(web = nginx + Vue, api = ASP.NET Core) connecting to a **real SQL Server on a Windows
Server** in production (the DB does not run in a container in prod).

## Production database model (decided with the team)

- **Auth is connection-string-driven** — portable across SQL auth, Windows/Integrated auth,
  container or not. No hardcoded auth assumptions.
- **Provisioning:** in production a **DBA provisions** the database and a least-privilege login
  (`db_datareader` + `db_datawriter`, or `db_owner` scoped to the one database). The app does
  **not** run `CREATE DATABASE`. Dev/test keep zero-setup auto-create.
- **Schema:** keep the idempotent `schema.sql` applied on startup, with an append-only
  `schema_version` table for auditability (never destructive).

## Status legend
✅ done · 🟡 doing · ⬜ planned

**As of 2026-06-09 every Critical/High/Medium finding below is resolved.** The work
shipped in waves (2a–2b backend infra, 3 frontend robustness, 4a–4b CI/tooling/hardening,
5 documentation), each committed with the test suites kept green (214 backend + 27 frontend).

## Critical / High

| # | Sev | Finding | Status |
|---|-----|---------|--------|
| 1 | Critical | `CREATE DATABASE` + DDL on every boot needs rights a locked-down enterprise login won't have | ✅ Gated behind `Database:AutoCreate` (off in Production); schema-only apply in prod |
| 2 | High | No transient-fault retry for SqlClient — DB blips fail requests / crash startup | ✅ `Db.cs` retries transient SQL errors with exponential backoff (query + transaction paths) |
| 3 | High | Student dev-login form shows by default in prod builds but the endpoint 404s | ✅ `showDevLogin = import.meta.env.DEV \|\| VITE_ALLOW_DEV_LOGIN==='true'` — off in prod builds |
| 4 | High | No global error boundary / toast / unhandledrejection handler — failures = blank screen | ✅ Toast store + global `errorHandler` + `unhandledrejection` + `onErrorCaptured` fallback UI |
| 5 | High | Containerized client build never wires Azure AD / dev-login env — SSO off, dev-login shipped | ✅ `ARG`/`ENV` build args in `client/Dockerfile` + `web.build.args` in compose + `.env.example` |

## Medium

| # | Finding | Status |
|---|---------|--------|
| 6 | No versioned migration / drift handling | ✅ `schema_version` table added; idempotent apply retained (DBA-applied script documented in DEPLOYMENT) |
| 7 | Windows Integrated auth impossible from Linux container, undocumented | ✅ Connection-string model + SQL-auth-from-container caveat documented in [DEPLOYMENT.md](docs/DEPLOYMENT.md) §2–3 |
| 8 | `/api/health` returns 200 even when DB down — bad readiness probe | ✅ `/api/health/live` (always 200) vs `/api/health/ready` (503 if DB down); legacy kept |
| 9 | No container HEALTHCHECK on api/web | ✅ `HEALTHCHECK` in both Dockerfiles (api → `/api/health/live` via curl; web → SPA via wget) |
| 10 | App logging effectively absent (ILogger used once) | ✅ Structured `ILogger` in `ApiCheckRunner` + controllers + startup error envelope |
| 11 | No `UseForwardedHeaders` behind nginx — rate-limit/audit key on proxy IP | ✅ `UseForwardedHeaders` (XFF/XFP) before rate limiting/audit |
| 12–15 | No frontend tests; `useProgress`/edge untested; no CI | ✅ Vitest suite (27 tests: toast, useAdminApi, auth store, useProgress helpers) + CI |
| 16 | No CI/CD pipeline | ✅ `.github/workflows/ci.yml` — API build+test (SQL Server service) + client lint/test/build |
| 17 | No api/web healthcheck (compose) | ✅ Dockerfile HEALTHCHECKs; compose gates `web` on `api` `service_healthy` |
| 18 | Frontend has no lint/format/test scripts | ✅ ESLint 9 flat config + Prettier + Vitest; `lint`/`format`/`format:check`/`test` scripts |
| 19 | No JWT-expiry handling for students — stale UI on expiry | ✅ `useProgress` 401 → `auth.logout()` + "session expired" toast |
| 20 | Admin writes swallow errors — failed saves look successful | ✅ `StepToggle` toasts on error; `StudentDetail.saveTags` rolls back optimistic tags + toasts |

## Low / Info

| # | Finding | Status |
|---|---------|--------|
| 21 | TLS off in connection strings (`Encrypt=False`) | ✅ `Encrypt=True` / `TrustServerCertificate` guidance in [DEPLOYMENT.md](docs/DEPLOYMENT.md) §3 |
| 22 | Production seeding coupled to DDL bootstrap | ✅ `Database:Seed` flag (default true) decouples seeding; documented |
| 23, 30 | `Console.Error.WriteLine` → `ILogger` in ApiCheckRunner + controllers | ✅ Replaced with `ILogger` warnings/errors |
| 24 | Metrics / OpenTelemetry absent | ⬜ Optional — health endpoints cover liveness/readiness; OTel deferred |
| 28 | nginx web container runs as root | ✅ Switched to `nginx-unprivileged` (uid 101, port 8080) |
| 29 | Base images pinned by tag, not digest | ⬜ Deferred — documented as a future hardening step (pin by `@sha256:` in CI) |
| — | Backend not `warnaserror`; no analyzers | ✅ `EnableNETAnalyzers` + `AnalysisLevel=latest` + `TreatWarningsAsErrors`; `.editorconfig` records intentional suppressions |

## Dead code (removed in the dead-code-removal wave)

- ✅ `client/src/pages/admin/StepsTab.vue`, `TermsTab.vue` (superseded by `TermStepsTab.vue`).
- ✅ `client/src/components/RoadrunnerMascot.vue` (never imported).
- ✅ `FluentValidation` NuGet dependency (declared, never used).
- ✅ `Api/Api.http` + Vite scaffold leftovers (vue.svg/vite.svg); the stock scaffold `client/README.md` was missed in that wave and replaced in the 2026-06-10 audit.
- ✅ C# row models `StudentProgress`, `IntegrationEvent` — initially kept on a substring-match false positive; re-verified in the 2026-06-10 audit as genuinely unused and removed.

## Execution order (all waves complete)

1. ✅ Dead-code removal (shrinks surface).
2. ✅ Backend infra: transient retry, liveness/readiness health, structured logging, ForwardedHeaders, prod-seed gate.
3. ✅ Frontend robustness: dev-login off in prod, error boundary + toast, 401 handling, admin error surfacing, Docker build args.
4. ✅ CI/CD + tests: ESLint/Prettier, Vitest + frontend tests, GitHub Actions, container HEALTHCHECK, nginx non-root, backend analyzers.
5. 🟡 Documentation overhaul (in progress — enhanced docs explaining the logic and the deployment model).

Each wave was committed with both test suites kept green.

## Remaining (optional, non-blocking)

- **#24 Metrics/OpenTelemetry** — add OTel traces/metrics if central observability is desired.
- **#29 Image digest pinning** — pin base images by `@sha256:` digest in CI for supply-chain hardening.

---

# Second full audit (2026-06-10)

A second full-codebase audit ran after all the waves above: 9 dimensions (backend/
frontend security, data layer, API + frontend correctness, infra, tests, dead code,
docs-vs-code), ~110 agents, every finding adversarially verified. **58 findings were
confirmed; all 5 highs, all 16 mediums, and effectively all lows were fixed the same
day** across the "Audit fixes 1–6" commits, with both suites kept green (214 backend +
31 frontend tests). Highlights:

- **Retry safety (high):** the new transient-retry layer could re-run non-idempotent
  writes after ambiguous failures (network drop after the server committed) — silently
  duplicating terms/audit rows. `Db.cs` now splits transient errors into *safe*
  (definitively not applied — retried for everything) and *ambiguous* (retried for
  reads only); `INSERT...SCOPE_IDENTITY()` moved to a write-classified
  `InsertReturningAsync`.
- **CSV export scale (high):** Dapper IN-list expansion hit SQL Server's 2100-parameter
  cap at ~2k students (the export 500'd exactly when cohorts got big) — rewritten as
  JOIN scoping with one parameter.
- **Local bootstrap (high):** a fresh `docker compose up --build` could never create the
  database (the api runs as Production, where AutoCreate correctly defaults off) — the
  local compose stack now sets `Database__AutoCreate=true`, and a new
  **`docker-compose.prod.yml`** (web+api only, external SQL Server, AutoCreate off)
  replaces the broken DEPLOYMENT §5 instructions.
- **Concurrency:** absent-row races fixed with `UPDLOCK, HOLDLOCK` (progress upsert,
  api-check upsert, last-sysadmin guard now inside its transaction); atomic
  run-claiming for student API-check runs.
- **Frontend security:** stored `javascript:` URLs neutralized before any `href`
  (new `safeUrl` helper + tests); RichTextEditor link injection escaped; MSAL
  redirect falls back to the page origin; nginx now sends CSP/security headers.
- **Hardening:** ApiCheck encryption key fail-fast guard in Production (mirrors the
  JWT guard, resolved eagerly at boot); SSRF guard closes IPv4-mapped-IPv6/link-local
  gaps; `appsettings.Development.json` no longer ships in the prod image; readiness
  probe answers within ~3s instead of inheriting ~60s of retries when the DB is down.
- **Docs:** every confirmed doc-vs-code drift corrected (compose SA password claims,
  admin123 quick-start claim, idempotency-replay semantics, OpenAPI path, break-glass
  described as config-gated, ARCHITECTURE tree + retry description, AUDIT.md XFO).
- **Dead code:** unused row models, the orphan `/api/admin/students/overdue` endpoint,
  Recharts-era chart exports, an unused JSON helper, a dead admin-tab branch, and the
  stock Vite scaffold README removed — each verified unused before removal.

**Known-remaining backlog (deliberate, non-blocking):** test-coverage gaps flagged by
the audit (retry plumbing, encryption round-trip, readiness-503 path, batch over-limit,
frontend error-boundary/session-expiry specs, cross-class test order-dependence),
OpenTelemetry (#24), and image digest pinning (#29).
