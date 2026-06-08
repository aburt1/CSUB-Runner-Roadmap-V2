# Development Setup Guide

## Prerequisites

- **.NET 10 SDK** — for the ASP.NET Core API and the xUnit test project
- **Node.js 20+** and **npm 9+** — for the Vue 3 client
- **Docker** (Rancher Desktop or Docker Desktop) — to run SQL Server locally

> **Apple Silicon (M-series) note:** SQL Server has no native arm64 build, so it
> runs as a `linux/amd64` container under emulation. Use the **VZ backend with
> Rosetta** enabled, otherwise the server segfaults under qemu. On Rancher Desktop:
> `rdctl set --virtual-machine.use-rosetta=true`. On Docker Desktop, enable
> "Use Rosetta for x86_64/amd64 emulation" and the VirtioFS/VZ VM in Settings.

## Quick Start

```bash
# 1. Start SQL Server (Docker)
docker compose up -d sqlserver

# 2. Run the API (creates DB + schema + seed on first boot)
cd Api && dotnet run            # http://localhost:3001

# 3. In another terminal, run the Vue client
cd client && npm install && npm run dev   # http://localhost:3000
```

The client runs at [http://localhost:3000](http://localhost:3000) and proxies `/api`
calls to the API at port 3001 (configured in `client/vite.config.ts`).

There is **no manual database setup**: on startup the API creates the `csub_admissions`
database, applies the schema (`Api/Data/schema.sql`), and seeds default data
(admin account, integration client, Fall 2026 checklist, and — in development —
50 sample students).

## Database Setup

### Local SQL Server via Docker (recommended)

```bash
docker compose up -d sqlserver
```

This starts SQL Server 2022 on `localhost:1433` with SA credentials
`sa` / `Csub_Local_Dev_2026!` (override with the `MSSQL_SA_PASSWORD` env var).
Data persists in the `csub_sqlserver_data` volume.

The connection string is preconfigured in `Api/appsettings.Development.json`:

```
Server=localhost,1433;Database=csub_admissions;User Id=sa;Password=Csub_Local_Dev_2026!;TrustServerCertificate=True;Encrypt=False
```

The database, schema, and seed data are applied automatically on first API start —
no `createdb`, migrations, or manual scripts required.

### Confirm it's up

Once the API is running, the health endpoint reports DB connectivity:

```bash
curl http://localhost:3001/api/health   # -> {"status":"ok","db":"connected", ...}
```

## Running the App

### API (port 3001)

```bash
cd Api
dotnet run                  # serves http://localhost:3001, creates DB + schema + seed on boot
```

In development the API listens on `:3001` (set via `Urls` in
`appsettings.Development.json`). `dotnet build` compiles without running; OpenAPI
docs are exposed in development at `/openapi`.

### Client (port 3000)

```bash
cd client
npm install
npm run dev                 # http://localhost:3000, proxies /api -> :3001
```

### Full stack via Docker (single process, port 8080)

The production image builds the Vue client, copies the bundle into the API's
`wwwroot`, and serves the SPA + API together from one ASP.NET Core process on
port 8080 — the same single-process model the old Express server used.

```bash
docker compose up --build   # SQL Server + the built app on http://localhost:8080
```

## Environment Variables

Local development reads settings from `Api/appsettings.Development.json`. In
production (Docker, real deployments) override them with environment variables
using the **double-underscore** syntax (`Section__Key`). The `app` service in
`docker-compose.yml` shows the full set with their defaults.

### API

| Variable | Required | Description |
|----------|----------|-------------|
| `ConnectionStrings__Default` | Yes | SQL Server connection string |
| `Jwt__Secret` | Yes | HS256 signing secret (≥ 32 chars) for JWT tokens |
| `Admin__DefaultEmail` | No | First admin account email (default: `admin@csub.edu`) |
| `Admin__DefaultPassword` | No | First admin password (default: `admin123`; required in Production) |
| `LocalLogin__Username` | No | Break-glass local admin username (default: `localadmin`; omit to disable) |
| `LocalLogin__Password` | No | Break-glass local admin password (default: `Local_Admin_2026!`) |
| `AzureAd__ClientId` | No | Azure AD application client ID (omit to disable SSO; endpoints return 501) |
| `AzureAd__TenantId` | No | Azure AD tenant ID |
| `Integration__DefaultName` | No | Seeded integration client name (default: `PeopleSoft Dev`) |
| `Integration__DefaultKey` | No | Seeded integration API key (default: `dev-integration-key`) |
| `ApiCheck__EncryptionKey` | No | 64-hex (32-byte) key to encrypt stored API-check credentials |
| `Cors__Origin` | No | Allowed CORS origin (defaults to `http://localhost:3000` in dev; closed in prod unless set) |

### Client (`client/.env`)

Copy from `client/.env.example`. These are only needed when Azure AD SSO is configured:

| Variable | Description |
|----------|-------------|
| `VITE_AZURE_AD_CLIENT_ID` | Azure AD application client ID |
| `VITE_AZURE_AD_TENANT_ID` | Azure AD tenant ID |
| `VITE_AZURE_AD_REDIRECT_URI` | OAuth redirect URI (default: `http://localhost:3000`) |

## Default Credentials

On first run, the seeder creates:

| Account | Username / Email | Password |
|---------|------------------|----------|
| Admin (sysadmin) | `admin@csub.edu` | `admin123` |
| Local (break-glass) admin | `localadmin` | `Local_Admin_2026!` |
| Integration client | key: `dev-integration-key` | — |
| Sample students | Various `@csub.edu` emails | Dev login (name + email) |

50 deterministic sample students with realistic progress data are seeded **only
when the API is not running in Production** (i.e. local dev).

## Available Commands

### Backend (`Api/`)

| Command | Description |
|---------|-------------|
| `dotnet run` | Start the API (hot-build) on :3001 |
| `dotnet build` | Compile the API without running |
| `dotnet test` | Run the xUnit integration tests (`tests/Api.IntegrationTests`) |

`dotnet build` / `dotnet test` can also be run from the repo root against
`CsubRunnerRoadmapV2.slnx`, which includes both the API and the test project.

### Client (`client/`)

| Command | Description |
|---------|-------------|
| `npm install` | Install client dependencies |
| `npm run dev` | Vite dev server on :3000 (proxies /api to :3001) |
| `npm run build` | Type-check (`vue-tsc`) and build the production bundle |
| `npm run preview` | Preview the production build locally |

### Docker

| Command | Description |
|---------|-------------|
| `docker compose up -d sqlserver` | SQL Server only (for local dev) |
| `docker compose up --build` | Build + run the full stack (SQL Server + app on :8080) |

## What's Different From the Original

The REST API contract (paths, payloads, status codes) is preserved, but a few
legacy pieces of the old Node/Express app were intentionally **dropped**:

- Legacy `X-API-Key` admin authentication
- The dev activity simulator
- Dev-only mock API-check routes

For the full project structure, see [Architecture](ARCHITECTURE.md).
