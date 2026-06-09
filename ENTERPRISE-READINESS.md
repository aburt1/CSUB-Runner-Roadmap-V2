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
✅ done · �doing · ⬜ planned

## Critical / High

| # | Sev | Finding | Status |
|---|-----|---------|--------|
| 1 | Critical | `CREATE DATABASE` + DDL on every boot needs rights a locked-down enterprise login won't have | ✅ Gated behind `Database:AutoCreate` (off in Production); schema-only apply in prod |
| 2 | High | No transient-fault retry for SqlClient — DB blips fail requests / crash startup | ⬜ Add `SqlRetryLogicProvider` + `Connect Timeout`; retry startup schema/seed with backoff |
| 3 | High | Student dev-login form shows by default in prod builds but the endpoint 404s | ⬜ Default dev-login OFF in production builds |
| 4 | High | No global error boundary / toast / unhandledrejection handler — failures = blank screen | ⬜ Add `errorHandler`, `unhandledrejection`, toast store, `onErrorCaptured` fallback |
| 5 | High | Containerized client build never wires Azure AD / dev-login env — SSO off, dev-login shipped | ⬜ Add `ARG`/`ENV` build args to client Dockerfile + compose |

## Medium

| # | Finding | Status |
|---|---------|--------|
| 6 | No versioned migration / drift handling | ✅ `schema_version` table added; idempotent apply retained (DBA-applied script documented) |
| 7 | Windows Integrated auth impossible from Linux container, undocumented | ⬜ Document SQL-auth requirement + least-privilege grant in SETUP |
| 8 | `/api/health` returns 200 even when DB down — bad readiness probe | ⬜ Split liveness (always 200) vs readiness (503 if DB down) |
| 9 | No container HEALTHCHECK on api/web | ⬜ Add HEALTHCHECK to both images |
| 10 | App logging effectively absent (ILogger used once) | ⬜ Structured logging: auth failures, integration sync, startup |
| 11 | No `UseForwardedHeaders` behind nginx — rate-limit/audit key on proxy IP | ⬜ Add ForwardedHeaders trusting the proxy |
| 12–15 | No frontend tests; `useProgress`/SSRF/edge untested; no CI/Testcontainers | ⬜ Vitest + component/composable tests; CI workflow |
| 16 | No CI/CD pipeline | ⬜ GitHub Actions: build + test + lint |
| 17 | No api/web healthcheck (compose) | ⬜ (with #9) |
| 18 | Frontend has no lint/format/test scripts | ⬜ ESLint + Prettier + vitest scripts |
| 19 | No JWT-expiry handling for students — stale UI on expiry | ⬜ 401 → logout + "session expired" |
| 20 | Admin writes swallow errors — failed saves look successful | ⬜ Surface errors + roll back optimistic state |

## Low / Info (selected)

- TLS off in connection strings (`Encrypt=False`) — document `Encrypt=True` for prod (#21).
- Production seeding gated with the DDL bootstrap (#22).
- `Console.Error.WriteLine` → `ILogger` in ApiCheckRunner + controllers (#23, #30).
- Metrics/OpenTelemetry absent (#24) — optional.
- nginx web container runs as root (#28); base images pinned by tag not digest (#29).
- Backend not `warnaserror`; no analyzers (low).

## Dead code to remove (verified unused)

- `client/src/pages/admin/StepsTab.vue`, `TermsTab.vue` (vestigial — superseded by `TermStepsTab.vue`).
- `client/src/components/RoadrunnerMascot.vue` (never imported).
- `FluentValidation` NuGet dependency (declared, never used).
- `Api/Api.http` (scaffold, references nonexistent `/weatherforecast`).
- Vite scaffold leftovers: `client/src/assets/vue.svg`, `vite.svg`, orphaned assets; scaffold README.
- Unused C# row models `StudentProgress`, `IntegrationEvent` (confirm before delete).

## Execution order

1. Dead-code removal (fast, reversible, shrinks surface).
2. Backend infra: transient retry, liveness/readiness health, structured logging, ForwardedHeaders, prod-seed gate.
3. Frontend robustness: dev-login off in prod, error boundary + toast, 401 handling, admin error surfacing, Docker build args.
4. CI/CD + tests: ESLint/Prettier, Vitest + frontend tests, GitHub Actions, container HEALTHCHECK, nginx non-root.
5. Documentation overhaul (enhanced, explains the logic and the deployment model).

Each wave is committed with tests kept green.
