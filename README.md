# CSUB Runner Roadmap — Road to Becoming a Roadrunner

An interactive onboarding app for California State University, Bakersfield that turns the admissions maze into a clear, personalized checklist. Every newly admitted student gets a step-by-step roadmap — from accepting their offer to their first day of class — with the right tasks, deadlines, and guidance surfaced for *their* situation, so nothing critical slips through the cracks.

For admissions staff it doubles as a control center: shape the checklist for each incoming term, track progress across the whole cohort, spot who's falling behind a deadline, and let campus systems (SIS/ERP) complete steps automatically through the integration API.

![Vue](https://img.shields.io/badge/Vue-3-4FC08D?logo=vuedotjs&logoColor=white)
![TypeScript](https://img.shields.io/badge/TypeScript-6-3178C6?logo=typescript&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-10-512BD4?logo=dotnet&logoColor=white)
![Dapper](https://img.shields.io/badge/Dapper-micro--ORM-FF6A00)
![SQL Server](https://img.shields.io/badge/SQL_Server-2022-CC2927?logo=microsoftsqlserver&logoColor=white)
![Tailwind CSS](https://img.shields.io/badge/Tailwind_CSS-4-06B6D4?logo=tailwindcss&logoColor=white)
![Vite](https://img.shields.io/badge/Vite-8-646CFF?logo=vite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-3_containers-2496ED?logo=docker&logoColor=white)

---

## Screenshots

### Public Landing Page
<img src="docs/screenshots/public-preview.png" alt="Public landing page" width="720" />

### Student Dashboard
<img src="docs/screenshots/student-dashboard.png" alt="Student dashboard with progress tracking" width="720" />

### Admin Dashboard
<img src="docs/screenshots/admin-dashboard.png" alt="Admin dashboard" width="720" />

---

## Features

- **Interactive admissions roadmap** with personalized step tracking and deadline awareness
- **Admin dashboard** for managing students, steps, analytics, audit logs, terms, and users
- **Integration API** for external systems (SIS, ERP) with inbound push and outbound polling ("API checks")
- **Tag-based step filtering** to show relevant steps per student profile
- **Role-based access control** — viewer, admissions, admissions_editor, sysadmin
- **Multi-term support** for managing separate cohorts (Fall 2026, Spring 2027, etc.)
- **Accessible and responsive** — high-contrast mode, keyboard navigation, mobile-friendly

---

## Tech Stack

- **Frontend:** Vue 3 + Vite + Tailwind + Pinia. Tiptap (rich text), vue-chartjs (analytics), vuedraggable (reorder), MSAL (Azure AD SSO). Served by a non-root nginx in production. Unit-tested with Vitest; linted with ESLint + Prettier.
- **Backend:** ASP.NET Core (.NET 10) controllers + Dapper (hand-written T-SQL). No ORM. Built with .NET analyzers and warnings-as-errors; transient-fault retry on all SQL; liveness/readiness health probes.
- **Database:** SQL Server 2022. Schema is applied idempotently on startup and tracked in a `schema_version` table. **In production a DBA provisions the database and a least-privilege login — the app does not run `CREATE DATABASE`.** Database creation and seeding auto-run only outside Production (see [Deployment](docs/DEPLOYMENT.md)).

Throughout, the code favors explicit, low-abstraction style over cleverness, and the [docs](#documentation) explain the reasoning behind each behavior — so the system stays easy to operate and maintain.

## Quick Start

See the whole app running in one command — the three-container Docker stack:

```bash
cp .env.example .env        # set the 4 secrets it lists (see Configuration)
docker compose up --build   # → http://localhost:3000
```

Sign in as `admin@csub.edu` with the `ADMIN_DEFAULT_PASSWORD` from your `.env`.

> The Docker stack runs the API in **Production**, so it requires real secrets and rejects weak ones. For the day-to-day local dev loop (`dotnet run` + `npm run dev`, no secrets needed) see **[SETUP](docs/SETUP.md)**; for the full dev-vs-prod picture see **[DEPLOYMENT](docs/DEPLOYMENT.md)**.

---

## Documentation

**Which doc do I need?** &nbsp; Run it locally → **[SETUP](docs/SETUP.md)** &nbsp;·&nbsp; Deploy to production → **[DEPLOYMENT](docs/DEPLOYMENT.md)** &nbsp;·&nbsp; Integrate an external system → **[API](docs/API-GUIDE.md)** &nbsp;·&nbsp; Understand how & why → **[Architecture](docs/ARCHITECTURE.md)**

| Document | For |
|----------|-----|
| [Development Setup](docs/SETUP.md) | Running locally (dev loop, full Docker, or frontend-only), env vars, the test/lint/format workflow, troubleshooting |
| [Architecture](docs/ARCHITECTURE.md) | How the app works and *why* — business logic, request/data flow, design decisions, topology |
| [Deployment](docs/DEPLOYMENT.md) | Production runbook: the three run modes, DBA provisioning, secrets, TLS, health probes, go-live checklist |
| [API Integration](docs/API-GUIDE.md) | REST contract for external systems (inbound push + outbound API checks) + health endpoints |
| [Architecture Considerations](docs/ARCHITECTURE-CONSIDERATIONS.md) | Deliberate trade-offs and their "revisit when…" triggers |
| [docs/history/](docs/history/) | Historical audit records |

---

## Running it: dev vs production

One switch — `ASPNETCORE_ENVIRONMENT` — decides everything (DB creation, seeding, required secrets). There are three run modes; **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** has the full comparison table and a side-by-side diagram. In short:

- **Local dev** — `dotnet run` + `npm run dev`, or `docker compose up` for the full stack → **[SETUP](docs/SETUP.md)**
- **Production** — `docker-compose.prod.yml`: **web + api** against an external, DBA-provisioned SQL Server (no `CREATE DATABASE`) → **[DEPLOYMENT](docs/DEPLOYMENT.md)**

The local Docker stack ([`docker-compose.yml`](docker-compose.yml)) is three containers — `web` (non-root nginx serving the SPA + proxying `/api`, same-origin so no CORS), `api` (ASP.NET Core, non-root), and `sqlserver` (2022, local/testing only). Both app containers declare a Docker `HEALTHCHECK`, and compose gates `web` on `api` being healthy. SETUP covers running each piece on its own.

---

## Configuration

Local dev needs **no secrets** — `Api/appsettings.Development.json` supplies dev defaults. The Docker/production stack requires four, set in `.env` (`.env.example` lists every variable):

| Secret (`.env`) | Purpose |
|---|---|
| `MSSQL_SA_PASSWORD` | SA password for the local SQL container (dev stack only) |
| `JWT_SECRET` | HS256 signing secret, ≥ 32 random chars |
| `ADMIN_DEFAULT_PASSWORD` | Seeded first-admin password — weak/default values are rejected in Production |
| `API_CHECK_ENCRYPTION_KEY` | 64-hex (32-byte) key encrypting stored API-check credentials |

In production, `PROD_CONNECTION_STRING` replaces the SA password (the DB is external). The **full reference** — connection string, `Database:AutoCreate`/`Seed`, Azure AD SSO, the Vite build-time vars, and the production fail-fast rules — is in **[DEPLOYMENT.md §4](docs/DEPLOYMENT.md)**.

---

## Layout

```
Api/         ASP.NET Core API (Controllers, Data, Auth, Services, Models, Serialization) + Dockerfile
client/      Vue 3 client (pages, components, stores, composables) + Dockerfile + nginx config
tests/       xUnit integration tests (Api.IntegrationTests)
docs/        documentation + screenshots
docker-compose.yml   web + api + sqlserver
.env.example         template for the secrets the api container requires
docs/history/        historical audit records
```

---

## License

This project was built for CSUB Admissions. All rights reserved.
