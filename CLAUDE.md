# CLAUDE.md

Project context for Claude Code (and any AI assistant). Claude Code loads this file
automatically, so keeping it accurate makes the assistant immediately useful here.
Full docs live in [`docs/`](docs/); this file is the short version + the guardrails.

## What this is

CSUB Runner Roadmap — a student-onboarding checklist app. **Vue 3 SPA** (`client/`) +
**ASP.NET Core .NET 10 API** (`Api/`) using **Dapper + hand-written T-SQL** against
**SQL Server**, with **xUnit** integration tests (`tests/`). See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it works and why.

## Where things live

| Path | What |
|------|------|
| `client/src/` | Vue SPA — `pages/` (+ `admin/` + `charts/`), `components/`, `stores/`, `composables/`, `router/` |
| `Api/Controllers/` (+ `Admin/`) | Skinny controllers; the admin API is split by concern |
| `Api/Services/` | Business logic — `Progress.ApplyAsync` (the one write path), `ApiCheckRunner`, `Encryption`, `Audit` |
| `Api/Data/` | `Db.cs` (Dapper + retry), `schema.sql` (idempotent T-SQL), `Seeder.cs` |
| `Api/Auth/` | JWT, bcrypt, Azure AD validation, the `[StudentAuth]`/`[AdminAuth]`/`[IntegrationAuth]` filters |
| `tests/Api.IntegrationTests/` | One class per area; runs the real app against a test SQL Server DB |

## The "boring code" charter (how to write code here)

This codebase is **deliberately low-abstraction**; match it.

- **Explicit over clever.** Straightforward control flow and named locals over dense one-liners.
- **Hand-written T-SQL via Dapper.** No ORM, no repository layer, no query builder, no LINQ-to-DB. SQL lives in one obvious place per feature.
- **No premature abstraction.** Duplicate a small block before inventing a helper used twice. Don't add layers/patterns/frameworks "just in case."
- **`Progress.ApplyAsync` is the single write path** for all progress changes (UPDLOCK+HOLDLOCK). Don't add a second way to write `student_progress`.
- **Comments explain *why*, not *what*** — and never the app's history (it is not framed as a port/clone of anything).

## Conventions & guardrails (don't "fix" these — they're intentional)

- **The REST contract is frozen.** Paths, JSON payloads, **snake_case field names**, status codes, and JWT claim names are a contract integration partners and the SPA depend on. Don't rename or reshape them.
- **snake_case DTO/row properties** are intentional (they mirror the JSON/SQL contract); the `CA1707` analyzer warning is suppressed on purpose in `.editorconfig`.
- **The app applies its own schema on boot** (`SchemaInitializer` runs `schema.sql`, idempotent). Adding a brand-new table/index there is fine; *altering an existing populated table* needs a real migration (see [ARCHITECTURE-CONSIDERATIONS.md](docs/ARCHITECTURE-CONSIDERATIONS.md)).
- **Production fail-fast:** the API refuses to start with missing/weak secrets (`Jwt:Secret`, `ApiCheck:EncryptionKey`, admin/break-glass passwords). Dev uses safe defaults from `appsettings.Development.json`.
- **CI is parked** (`.github/workflows/ci.yml.disabled`) — run the checks locally (below).
- **Behavior-preserving changes** unless asked otherwise; keep the test suite green.

## Commands

```bash
# Local dev loop (recommended)
docker compose up -d sqlserver               # SQL Server on :1433
cd Api && dotnet run                          # API on :3001 (creates DB + schema + seed)
cd client && npm install && npm run dev       # Vue client on :3000, proxies /api → :3001

# Before declaring work done — run the full quality loop:
cd client && npm run lint && npm run format:check && npm run test && npm run build
dotnet test                                   # from the repo ROOT (needs the sqlserver container up)
```

`dotnet test` resolves `CsubRunnerRoadmapV2.slnx` (API + tests) — run it from the repo
root, not from `Api/`. Tests need the `sqlserver` container running.

## Docs map

[SETUP](docs/SETUP.md) (run locally) · [DEPLOYMENT](docs/DEPLOYMENT.md) (production) ·
[ARCHITECTURE](docs/ARCHITECTURE.md) (how & why) · [API-GUIDE](docs/API-GUIDE.md)
(integration contract) · [ARCHITECTURE-CONSIDERATIONS](docs/ARCHITECTURE-CONSIDERATIONS.md)
(trade-offs) · [Working with Claude](docs/WORKING-WITH-CLAUDE.md) (using the assistant here).
