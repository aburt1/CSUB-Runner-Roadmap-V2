# CSUB Runner Roadmap V2 — Road to Becoming a Roadrunner

An interactive student onboarding application for California State University, Bakersfield. Guides newly admitted students through every step of the admissions process — from acceptance to their first day of classes.

This is a rewrite of the original React + Node/Express + PostgreSQL app onto a **Vue 3 + C# (ASP.NET Core) + SQL Server** stack, keeping the same functionality with deliberately simple, low-abstraction, readable code. The original lives in the sibling `CSUB-admissions/` folder and was the reference for behavior parity.

![Vue](https://img.shields.io/badge/Vue-3-4FC08D?logo=vuedotjs&logoColor=white)
![TypeScript](https://img.shields.io/badge/TypeScript-5-3178C6?logo=typescript&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-10-512BD4?logo=dotnet&logoColor=white)
![Dapper](https://img.shields.io/badge/Dapper-micro--ORM-FF6A00)
![SQL Server](https://img.shields.io/badge/SQL_Server-2022-CC2927?logo=microsoftsqlserver&logoColor=white)
![Tailwind CSS](https://img.shields.io/badge/Tailwind_CSS-3-06B6D4?logo=tailwindcss&logoColor=white)
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

- **Frontend:** Vue 3 + Vite + Tailwind + Pinia. Tiptap (rich text), vue-chartjs (analytics), vuedraggable (reorder), MSAL (Azure AD SSO). Served by nginx in production.
- **Backend:** ASP.NET Core (.NET 10) controllers + Dapper (hand-written T-SQL). No ORM.
- **Database:** SQL Server 2022. The app creates the database, schema, and seed data on startup.

See [docs/LIBRARIES.md](docs/LIBRARIES.md) for every dependency, what it does, and a link to its docs.

---

## Quick Start

The fastest way to see the whole app is the three-container Docker stack. The `api` container runs in Production and refuses weak/missing secrets, so set them first:

```bash
cp .env.example .env        # then set JWT_SECRET, ADMIN_DEFAULT_PASSWORD, API_CHECK_ENCRYPTION_KEY, MSSQL_SA_PASSWORD
docker compose up --build   # -> http://localhost:3000
```

Default admin login: `admin@csub.edu` / `admin123` (override `ADMIN_DEFAULT_PASSWORD` in `.env`).

See the [Development Setup Guide](docs/SETUP.md) for local (non-container) development and for running the frontend on its own (e.g. a Windows desktop).

---

## Documentation

| Document | Description |
|----------|-------------|
| [Development Setup](docs/SETUP.md) | Prerequisites, running locally, running the frontend on Windows, environment variables, default credentials |
| [Architecture](docs/ARCHITECTURE.md) | Tech stack, project structure, request/data flow, three-container deployment |
| [Authentication](docs/AUTH-ROADMAP.md) | Student/admin/integration auth, JWT, RBAC, Azure AD SSO, production checklist |
| [API Integration](docs/API-GUIDE.md) | REST API reference for external system integration (inbound push + outbound API checks) |
| [Libraries](docs/LIBRARIES.md) | Every backend/frontend/infra dependency, what it does, and a link to its docs |
| [Testing](docs/TESTING.md) | Running the xUnit integration suite, test strategy, adding new tests |
| [Development with Claude Code](docs/CLAUDE-CODE.md) | Using Claude Code for feature development |
| [Parity Audit](AUDIT.md) · [Security Audit](SECURITY-AUDIT.md) | Conversion parity audit and security/code audit findings |

---

## Deployment

Three containers, defined in [`docker-compose.yml`](docker-compose.yml):

| Container | What it is | Port |
|-----------|-----------|------|
| **web** | Vue build served by nginx, which reverse-proxies `/api` to the API (same-origin, no CORS) — built from `client/Dockerfile` | `3000` |
| **api** | ASP.NET Core API only — built from `Api/Dockerfile` | `8080` |
| **sqlserver** | SQL Server 2022 | `1433` |

```bash
cp .env.example .env
docker compose up --build         # full stack on http://localhost:3000
```

Each piece can also be launched on its own:

```bash
docker compose up -d sqlserver    # just the database
docker compose up -d --build api  # database + API (depends_on starts sqlserver)
docker compose up -d --build web  # full stack (depends_on pulls in api + sqlserver)
```

> On Apple Silicon, SQL Server runs as a linux/amd64 container — enable Rancher/Docker Desktop's
> **VZ backend with Rosetta** (`rdctl set --virtual-machine.use-rosetta=true`).

---

## Configuration

`Api/appsettings.Development.json` holds local dev settings. In production / containers, set these via `.env` or environment variables (double-underscore syntax):

| Variable | Required | Purpose |
|----------|----------|---------|
| `ConnectionStrings__Default` | Yes | SQL Server connection string |
| `Jwt__Secret` | Yes | HS256 signing secret (≥ 32 chars); the API rejects weak/missing values in Production |
| `Admin__DefaultEmail` / `Admin__DefaultPassword` | Yes (password) | Seeded default admin; the seeder rejects weak/default passwords in Production |
| `ApiCheck__EncryptionKey` | Yes | 64-hex (32-byte) key to encrypt stored API-check credentials |
| `LocalLogin__Username` / `LocalLogin__Password` | No | Break-glass local admin login (disabled unless both are set) |
| `AzureAd__ClientId` / `AzureAd__TenantId` | No | Azure AD SSO (omit to disable; endpoints return 501) |
| `Integration__DefaultName` / `Integration__DefaultKey` | No | Seeded integration client |
| `Cors__Origin` | No | Allowed CORS origin (only if the client is served from a different origin) |

Client SSO config (`client/.env.example`): `VITE_AZURE_AD_CLIENT_ID`, `VITE_AZURE_AD_TENANT_ID`, `VITE_AZURE_AD_REDIRECT_URI`.

---

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

---

## License

This project was built for CSUB Admissions. All rights reserved.
