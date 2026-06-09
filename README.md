# CSUB Runner Roadmap V2

A rewrite of the CSUB Admissions Roadmap onto a **Vue 3 + C# (ASP.NET Core) + SQL Server** stack,
keeping the original functionality but with deliberately simple, low-abstraction, readable code.

The original app (React + Node/Express + PostgreSQL) lives in the sibling `CSUB-admissions/`
folder and was used as the reference for behavior parity during the conversion.

## Stack

- **Backend:** ASP.NET Core (.NET 10) controllers + Dapper (hand-written T-SQL). No ORM.
- **Database:** SQL Server 2022. The app creates the database, schema, and seed data on startup.
- **Frontend:** Vue 3 + Vite + Tailwind + Pinia. Tiptap (rich text), vue-chartjs (analytics),
  vuedraggable (reorder), MSAL (Azure AD SSO).

## What it does

Newly admitted students sign in and follow a personalized roadmap of admission steps (filtered by
tags); admins manage steps/terms/students, view analytics, audit logs, and configure the push
integration API + outbound "API checks". Four RBAC roles: viewer, admissions, admissions_editor,
sysadmin.

## Local development

The backend and client run as two dev processes; the client proxies `/api` to the API on `:3001`.

### 1. Database (SQL Server in Docker)

> On Apple Silicon, SQL Server runs as a linux/amd64 container. It needs Rancher Desktop (or
> Docker Desktop) on the **VZ backend with Rosetta** enabled, otherwise it segfaults under qemu:
> `rdctl set --virtual-machine.use-rosetta=true`.

```bash
docker compose up -d sqlserver        # SQL Server on localhost:1433 (sa / Csub_Local_Dev_2026!)
```

### 2. API (port 3001)

```bash
cd Api
dotnet run                            # creates DB + schema + seed on boot
curl http://localhost:3001/api/health # -> {"status":"ok","db":"connected", ...}
```

Default seeded admin: `admin@csub.edu` / `admin123`. Local (break-glass) admin login:
`localadmin` / `Local_Admin_2026!`. Students sign in with dev-login (name + email).

### 3. Client (port 3000)

```bash
cd client
npm install
npm run dev                           # http://localhost:3000
```

## Run the whole thing with Docker

Three containers — **web** (Vue build on nginx, :3000, reverse-proxies `/api`), **api** (ASP.NET
Core, :8080), and **sqlserver** (:1433). The api runs in Production and refuses weak/missing
secrets, so set them first:

```bash
cp .env.example .env                  # then set JWT_SECRET, ADMIN_DEFAULT_PASSWORD, etc.
docker compose up --build             # -> http://localhost:3000
```

Each piece can also be launched on its own:

```bash
docker compose up -d sqlserver        # just the database
docker compose up -d --build api      # database + API (depends_on starts sqlserver)
docker compose up -d --build web      # full stack (depends_on pulls in api + sqlserver)
```

See [docs/SETUP.md](docs/SETUP.md) for running the frontend by itself (e.g. on a Windows desktop).

## Configuration

`Api/appsettings.Development.json` holds local settings. In production, override via environment
variables (double-underscore syntax):

| Env var | Purpose |
|---|---|
| `ConnectionStrings__Default` | SQL Server connection string (required) |
| `Jwt__Secret` | HS256 signing secret, ≥ 32 chars (required) |
| `Admin__DefaultEmail` / `Admin__DefaultPassword` | Seeded default admin (password required in Production) |
| `LocalLogin__Username` / `LocalLogin__Password` | Env-gated local admin login (omit to disable) |
| `AzureAd__ClientId` / `AzureAd__TenantId` | Azure AD SSO (omit to disable; endpoints return 501) |
| `Integration__DefaultName` / `Integration__DefaultKey` | Seeded integration client |
| `ApiCheck__EncryptionKey` | 64-hex (32-byte) key to encrypt stored API-check credentials |
| `Cors__Origin` | Allowed CORS origin (only needed if the client is served from a different origin) |

Client SSO config (`client/.env.example`): `VITE_AZURE_AD_CLIENT_ID`, `VITE_AZURE_AD_TENANT_ID`,
`VITE_AZURE_AD_REDIRECT_URI`.

## Layout

```
Api/         ASP.NET Core API (Controllers, Data, Auth, Services, Models, Serialization) + Dockerfile
client/      Vue 3 client (pages, components, stores, composables) + Dockerfile + nginx config
tests/       xUnit integration tests (Api.IntegrationTests)
docs/        documentation + screenshots
docker-compose.yml   web + api + sqlserver
.env.example         template for the secrets the api container requires
AUDIT.md             parity audit of the conversion vs. the original app
SECURITY-AUDIT.md    security/code audit findings and resolutions
```

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — tech stack, project structure, request/data flow, 3-container deployment.
- [docs/SETUP.md](docs/SETUP.md) — prerequisites, running locally, running the frontend on Windows, environment variables, default credentials.
- [docs/AUTH-ROADMAP.md](docs/AUTH-ROADMAP.md) — student/admin/integration authentication, JWT, RBAC, Azure AD.
- [docs/API-GUIDE.md](docs/API-GUIDE.md) — external integration API (inbound push + outbound API checks).
- [docs/TESTING.md](docs/TESTING.md) — the xUnit integration test suite and how to run it.
- [docs/LIBRARIES.md](docs/LIBRARIES.md) — every library/dependency, what it does, and links to its docs.
- [docs/CLAUDE-CODE.md](docs/CLAUDE-CODE.md) — developing this repo with Claude Code.
- [AUDIT.md](AUDIT.md) · [SECURITY-AUDIT.md](SECURITY-AUDIT.md) — conversion parity audit and security audit.
