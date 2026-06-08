# CSUB Runner Roadmap V2

A rewrite of the CSUB Admissions Roadmap onto a **Vue 3 + C# (ASP.NET Core) + SQL Server** stack,
keeping the original functionality but with deliberately simple, low-abstraction, readable code.

The original app (React + Node/Express + PostgreSQL) lives in the sibling `CSUB-admissions/`
folder and is used as the reference for behavior parity during the conversion.

## Status

- **Phase 0 — Local environment:** done. SQL Server 2022 runs in Docker (`docker-compose.yml`).
- **Phase 1a — Backend foundation:** in progress. Dapper data layer, T-SQL schema, startup schema
  initializer, and a health endpoint are working end-to-end against SQL Server.
- Remaining: seeder, models, auth + security middleware, the endpoint port, then the Vue frontend.

## Stack

- **Backend:** ASP.NET Core (.NET 10) controllers + Dapper (hand-written T-SQL). No ORM.
- **Database:** SQL Server 2022.
- **Frontend:** Vue 3 + Vite + Tailwind (added in Phase 3).

## Local development

### 1. Database (SQL Server in Docker)

> On Apple Silicon, SQL Server runs as a linux/amd64 container. It needs Rancher Desktop (or
> Docker Desktop) configured with the **VZ backend + Rosetta** enabled, otherwise it segfaults
> under qemu.

```bash
docker compose up -d sqlserver        # starts SQL Server on localhost:1433 (sa / Csub_Local_Dev_2026!)
```

### 2. API

```bash
cd Api
dotnet run                            # listens on http://localhost:3001, creates the schema on boot
curl http://localhost:3001/api/health # -> {"status":"ok","db":"connected", ...}
```

The API listens on port **3001** so the existing React client (which proxies `/api` to `:3001`)
keeps working against the new backend during the conversion.

## Configuration

`Api/appsettings.Development.json` holds the local connection string. Production settings come from
environment variables / `appsettings.json` (connection string, JWT secret, Azure AD, local-login
password, API-check encryption key, CORS origin, default admin/integration seeds).
