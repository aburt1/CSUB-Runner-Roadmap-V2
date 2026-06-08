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

The production image builds the Vue client and serves it together with the API from a single
ASP.NET Core process (port 8080), exactly like the old Express server served the React build.

```bash
docker compose up --build             # SQL Server + the built app on http://localhost:8080
```

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
Api/         ASP.NET Core API (Controllers, Data, Auth, Services, Models)
client/      Vue 3 client (pages, components, stores, composables)
Dockerfile   multi-stage: build client -> publish API (SPA in wwwroot) -> runtime
docker-compose.yml   SQL Server (+ the full app via `--build`)
```
