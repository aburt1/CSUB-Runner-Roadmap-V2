# Development Setup Guide

This guide walks through everything needed to get the CSUB Runner Roadmap (V2)
running, whether you want the full stack in Docker, a local dev loop with hot
reload, or just the Vue frontend on its own machine pointed at a remote backend.

The app is split into three pieces:

- **`web`** — the Vue 3 client (Vite build) served by nginx.
- **`api`** — the ASP.NET Core API (.NET 10) backed by Dapper.
- **`sqlserver`** — SQL Server 2022 holding all application data.

In production these run as **three separate containers**; in local development
you typically run SQL Server in a container and the API + client as two host
processes. Both arrangements are described below.

## Prerequisites

| Tool | Version | Needed for |
|------|---------|------------|
| **.NET SDK** | **10.0** | Building/running the ASP.NET Core API and the xUnit test project |
| **Node.js** | **20+** (LTS) and **npm 9+** | Building/running the Vue 3 client |
| **Docker** | Rancher Desktop **or** Docker Desktop | Running SQL Server locally, and the full containerized stack |

You only need .NET and Node if you intend to run the API/client directly on the
host. To run the entire stack you only need Docker. To run *just the frontend*
(see [Running the frontend on its own](#running-the-frontend-on-its-own-eg-a-windows-desktop)),
you only need Node.

> **Apple Silicon (M-series) note:** SQL Server has no native arm64 build, so it
> runs as a `linux/amd64` container under emulation. Use the **VZ backend with
> Rosetta** enabled, otherwise the server segfaults under qemu. On Rancher
> Desktop: `rdctl set --virtual-machine.use-rosetta=true`. On Docker Desktop,
> enable "Use Rosetta for x86_64/amd64 emulation" together with the VirtioFS/VZ
> VM in Settings.

## Quick Start (full stack in Docker)

The simplest way to see the whole app is to build and run all three containers.

**First set your secrets.** The `api` container runs in Production and refuses to start with
missing or weak secrets, so copy the template and fill it in (generate values, e.g.
`openssl rand -base64 48` for the JWT secret and `openssl rand -hex 32` for the API-check key):

```bash
cp .env.example .env        # then edit .env and set MSSQL_SA_PASSWORD, JWT_SECRET, ADMIN_DEFAULT_PASSWORD, API_CHECK_ENCRYPTION_KEY
# Build the client + API images and start everything (SQL Server, API, web)
docker compose up --build
```

Then open **[http://localhost:3000](http://localhost:3000)**. (Compose will stop with a clear
message if a required secret is unset.) For purely local development without containers you don't
need any of this — `dotnet run` + `npm run dev` use the dev defaults (see below).

What this does:

- Starts **`sqlserver`** (SQL Server 2022) on `localhost:1433`.
- Builds and starts **`api`** (ASP.NET Core) on `localhost:8080`. On startup it
  waits for SQL Server to become healthy, then creates the `csub_admissions`
  database, applies the schema (`Api/Data/schema.sql`), and seeds default data.
- Builds and starts **`web`** (the Vue bundle served by nginx) on
  `localhost:3000`. nginx reverse-proxies any request under `/api` to the `api`
  container, so the browser only ever talks to a **single origin** — there is
  **no CORS** to configure.

Because the client always calls **relative `/api` URLs** (proxied by nginx in
containers, and by Vite in local dev), no API URL is hardcoded anywhere in the
frontend.

### Starting the containers individually

`depends_on` wiring means you can bring up any single service and Docker will
pull in everything it needs:

```bash
# Just the database (:1433)
docker compose up -d sqlserver

# Database + API (:8080) — depends_on starts sqlserver first and waits for health
docker compose up -d --build api

# Full stack (:3000) — depends_on pulls in api, which pulls in sqlserver
docker compose up -d --build web
```

This is handy when you want, for example, the containerized database and API but
the client running from source with hot reload (see the next section).

### Stopping and resetting

```bash
docker compose down           # stop and remove the containers (keeps the DB volume)
docker compose down -v        # also delete the csub_sqlserver_data volume (fresh DB next start)
docker compose logs -f api    # follow the API logs (schema + seed output appears here)
```

The database lives in the named volume `csub_sqlserver_data`, so data survives
`docker compose down`. Use `down -v` when you want the API to re-create and
re-seed a clean database on the next boot.

## Local development without containers

For the tightest edit/run loop, run SQL Server in Docker but run the API and the
client directly on the host:

```bash
# 1. Start SQL Server (Docker)
docker compose up -d sqlserver

# 2. Run the API (creates DB + schema + seed on first boot)
cd Api && dotnet run                       # http://localhost:3001

# 3. In another terminal, run the Vue client
cd client && npm install && npm run dev    # http://localhost:3000
```

The client runs at [http://localhost:3000](http://localhost:3000) and proxies
`/api` calls to the API at **port 3001** (configured in
`client/vite.config.ts`). The proxy target defaults to `http://localhost:3001`
and can be overridden with the `VITE_API_PROXY_TARGET` environment variable.

There is **no manual database setup**: on startup the API ensures the
`csub_admissions` database exists, applies the schema (`Api/Data/schema.sql`),
and seeds default data (admin account, break-glass local admin, integration
client, Fall 2026 checklist, and — when not running in Production — 50 sample
students). No `createdb`, migrations, or hand-run scripts are required.

> **Why two different API ports?** In local dev the API listens on **`:3001`**
> (set via `Urls` in `appsettings.Development.json`), matching the old Express
> server's port so the Vite proxy default just works. In containers the API
> listens on **`:8080`** (set via `ASPNETCORE_URLS` in `Api/Dockerfile`), and
> nginx proxies to it there.

## Database Setup

### Local SQL Server via Docker (recommended)

```bash
docker compose up -d sqlserver
```

This starts **SQL Server 2022** on `localhost:1433` with SA credentials
`sa` / `Csub_Local_Dev_2026!` (override with the `MSSQL_SA_PASSWORD` env var).
Data persists in the `csub_sqlserver_data` volume. The compose file defines a
health check (`sqlcmd ... SELECT 1`) so the API can wait for the database to be
ready before it starts.

The connection string is preconfigured for local dev in
`Api/appsettings.Development.json`:

```
Server=localhost,1433;Database=csub_admissions;User Id=sa;Password=Csub_Local_Dev_2026!;TrustServerCertificate=True;Encrypt=False
```

In the `api` container the connection string instead targets the
`sqlserver` service name (`Server=sqlserver,1433;...`), supplied via the
`ConnectionStrings__Default` environment variable in `docker-compose.yml`.

The database, schema, and seed data are applied automatically on first API start
— no `createdb`, migrations, or manual scripts required.

### Confirm it's up

Once the API is running, the health endpoint reports DB connectivity:

```bash
# Local dev (dotnet run)
curl http://localhost:3001/api/health   # -> {"status":"ok","db":"connected", ...}

# Containerized API directly
curl http://localhost:8080/api/health

# Through the web container (nginx proxy)
curl http://localhost:3000/api/health
```

## Running the App

### API

**Local dev (host process):**

```bash
cd Api
dotnet run            # serves http://localhost:3001, creates DB + schema + seed on boot
```

In development the API listens on `:3001` (set via `Urls` in
`appsettings.Development.json`). `dotnet build` compiles without running.
OpenAPI docs are exposed in development at `/openapi`.

**Containerized:**

```bash
docker compose up -d --build api    # http://localhost:8080
```

The container runs with `ASPNETCORE_ENVIRONMENT=Production` and listens on
`:8080`. It waits for the `sqlserver` health check before starting.

### Client

**Local dev (host process):**

```bash
cd client
npm install
npm run dev           # http://localhost:3000, proxies /api -> :3001
```

**Containerized:**

```bash
docker compose up -d --build web    # http://localhost:3000
```

The container builds the Vue bundle and serves it with nginx, reverse-proxying
`/api` to the `api` container.

### Full stack via Docker (three containers)

```bash
docker compose up --build   # sqlserver + api + web on http://localhost:3000
```

This is the canonical "run everything" command — open
[http://localhost:3000](http://localhost:3000). See
[Quick Start](#quick-start-full-stack-in-docker) above for the per-service
details.

## Running the frontend on its own (e.g. a Windows desktop)

You can run **only the Vue client** on a separate machine — for example a
Windows desktop — and point it at a backend running somewhere else. The client
never hardcodes an API URL; it always calls relative `/api`, and Vite proxies
those calls to whatever `VITE_API_PROXY_TARGET` points at.

### Option A: run the client from source with Node

1. **Install Node.js LTS.** Download the LTS installer from
   [https://nodejs.org](https://nodejs.org) and run it (accept the defaults).
   Confirm it installed by opening a terminal and running `node -v` and
   `npm -v`.

2. **Get the client.** Copy or clone the repository and open a terminal in the
   `client/` folder.

3. **Install dependencies and start the dev server.** In **PowerShell**:

   ```powershell
   cd client
   npm install
   npm run dev
   ```

4. **Open the app** at [http://localhost:3000](http://localhost:3000).

By default the dev server proxies `/api` to `http://localhost:3001` — i.e. it
expects an API running locally on that machine.

### Pointing at a backend that is NOT on localhost:3001

Set the `VITE_API_PROXY_TARGET` environment variable **before** starting the dev
server, giving it the scheme, host, and port of the backend you want to hit
(for example a containerized API on `:8080`, or a remote server).

**PowerShell:**

```powershell
$env:VITE_API_PROXY_TARGET="http://<host>:<port>"
npm run dev
```

**Windows cmd.exe:**

```cmd
set VITE_API_PROXY_TARGET=http://<host>:<port> && npm run dev
```

**macOS / Linux (bash):**

```bash
VITE_API_PROXY_TARGET=http://<host>:<port> npm run dev
```

Examples of `<host>:<port>`:

- `http://localhost:8080` — a containerized API on the same machine.
- `http://192.168.1.50:8080` — an API on another machine on your network.
- `http://api.example.edu:8080` — a remote backend.

### Option B: use Docker Desktop on Windows

If you have Docker Desktop installed, you can run the prebuilt frontend
container instead of installing Node:

```bash
docker compose up web
```

This serves the client on [http://localhost:3000](http://localhost:3000). By
default the `web` container proxies `/api` to the `api` service on the internal
Docker network. To point it at a different backend, set `WEB_API_URL` (which
feeds the container's `API_URL`):

```bash
WEB_API_URL=http://<host>:<port> docker compose up web
```

## Environment Variables

Local development reads settings from `Api/appsettings.Development.json`. In
production (Docker, real deployments) override them with environment variables
using the **double-underscore** syntax (`Section__Key`, e.g.
`ConnectionStrings__Default`). The `api` service in `docker-compose.yml` shows
the full set with their defaults.

### API

| Variable | Required | Description |
|----------|----------|-------------|
| `ConnectionStrings__Default` | Yes | SQL Server connection string |
| `Jwt__Secret` | **Yes** | HS256 signing secret (≥ 32 chars). Compose requires it (no default); the API refuses to start in Production if it's missing, < 32 chars, or a known placeholder. |
| `Admin__DefaultEmail` | No | First admin account email (default: `admin@csub.edu`) |
| `Admin__DefaultPassword` | **Yes** | First admin password. Compose requires it; the seeder rejects a missing/weak/default value (e.g. `admin123`) in Production. |
| `ApiCheck__EncryptionKey` | **Yes** | 64-hex (32-byte) key for encrypting stored API-check credentials. Compose requires it. |
| `LocalLogin__Username` | No | Break-glass local admin username. **Disabled unless both username and password are set** (no compose default). |
| `LocalLogin__Password` | No | Break-glass local admin password (no compose default). |
| `AzureAd__ClientId` | No | Azure AD application client ID (omit to disable SSO; endpoints return 501) |
| `AzureAd__TenantId` | No | Azure AD tenant ID |
| `Integration__DefaultName` | No | Seeded integration client name (default: `PeopleSoft Dev`) |
| `Integration__DefaultKey` | No | Seeded integration API key (default: `dev-integration-key`) |
| `ApiCheck__EncryptionKey` | No | 64-hex (32-byte) key to encrypt stored API-check credentials |
| `Cors__Origin` | No | Allowed CORS origin (defaults to `http://localhost:3000` in dev; closed in prod unless set). Normally unnecessary because nginx keeps the client same-origin. |

> **CORS is usually a no-op.** Because the `web` container's nginx proxy makes
> the browser see one origin, you typically do **not** need to set
> `Cors__Origin`. Only set it if you deliberately serve the client from a
> different origin and bypass the proxy.

The `sqlserver` service is configured with `MSSQL_SA_PASSWORD` (default
`Csub_Local_Dev_2026!`), `ACCEPT_EULA=Y`, and `MSSQL_PID=Developer`. The same
`MSSQL_SA_PASSWORD` value is interpolated into the `api` service's connection
string, so overriding it in one place keeps both in sync.

### Vite / client (dev only)

| Variable | Where | Description |
|----------|-------|-------------|
| `VITE_API_PROXY_TARGET` | host env when running `npm run dev` | Backend the dev server proxies `/api` to (default: `http://localhost:3001`) |
| `WEB_API_URL` | host env for `docker compose` | Backend the `web` container's nginx proxies `/api` to (default: `http://api:8080`) |

### Client SSO (`client/.env`)

Copy from `client/.env.example`. These are only needed when Azure AD SSO is
configured; leave them blank to use dev-login (students) and the env-gated
local-login (admins):

| Variable | Description |
|----------|-------------|
| `VITE_AZURE_AD_CLIENT_ID` | Azure AD application client ID |
| `VITE_AZURE_AD_TENANT_ID` | Azure AD tenant ID |
| `VITE_AZURE_AD_REDIRECT_URI` | OAuth redirect URI (default: `http://localhost:3000`) |

## Default Credentials

On first run, the seeder creates the following accounts. These exist for local
development and demos — change or disable them for any real deployment.

| Account | Username / Email | Password |
|---------|------------------|----------|
| Admin (sysadmin) | `admin@csub.edu` | `admin123` |
| Local (break-glass) admin | `localadmin` | `Local_Admin_2026!` |
| Integration client | key: `dev-integration-key` | — |
| Sample students | Various `@csub.edu` emails | Dev login (name + email) |

50 deterministic sample students with realistic progress data are seeded **only
when the API is not running in Production** (i.e. local dev). In Production the
seeder refuses to create the default admin unless `Admin__DefaultPassword` is
explicitly set.

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
| `npm run dev` | Vite dev server on :3000 (proxies `/api` to `VITE_API_PROXY_TARGET`, default :3001) |
| `npm run build` | Type-check (`vue-tsc -b`) and build the production bundle |
| `npm run preview` | Preview the production build locally |

### Docker

| Command | Description |
|---------|-------------|
| `docker compose up --build` | Build + run the full three-container stack on :3000 |
| `docker compose up -d sqlserver` | SQL Server only (:1433), for local dev |
| `docker compose up -d --build api` | Database + API (:8080); depends_on starts sqlserver first |
| `docker compose up -d --build web` | Full stack (:3000); depends_on pulls in api + sqlserver |
| `docker compose down` | Stop and remove containers (keeps the DB volume) |
| `docker compose down -v` | Also delete the `csub_sqlserver_data` volume (fresh DB) |
| `docker compose logs -f api` | Follow the API logs (schema + seed output) |

## What's Different From the Original

The REST API contract (paths, payloads, status codes) is preserved, but the
deployment model changed and a few legacy pieces of the old Node/Express app
were intentionally **dropped**:

- **Three containers instead of one.** The old app served the React build and
  the API from a single Express process. V2 splits these into a `web`
  (Vue + nginx) container and an `api` (ASP.NET Core) container, plus the
  `sqlserver` database container. nginx in the `web` container reverse-proxies
  `/api` to `api`, preserving the single-origin (no-CORS) experience.
- Legacy `X-API-Key` admin authentication — removed.
- The dev activity simulator — removed.
- Dev-only mock API-check routes — removed.

For the full project structure, see [Architecture](ARCHITECTURE.md).
