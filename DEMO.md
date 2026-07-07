# Demo — run the whole app in one command

A self-contained demo of the CSUB Runner Roadmap: **database + API + web app**, all
in containers, seeded with sample data. No config, no secrets to fill in.

> **Testing/demo only.** Uses throwaway, publicly-known dev credentials and
> Developer-edition SQL Server (not licensed for production). For a real deployment
> see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) (external SQL Server + real secrets).

## Run it

Requires Docker (Docker Desktop or Rancher Desktop).

```bash
docker compose -f docker-compose.demo.yml up --build
```

First run takes a few minutes (it builds the images and SQL Server warms up). When
it settles, open:

### 👉 http://localhost:3000

## Log in

- **Student view:** on the landing page, sign in with **any name + email** (e.g.
  `Jane Doe` / `jane@example.com`) — the full 22-step roadmap unlocks.
- **Admin view:** go to **http://localhost:3000/admin** and sign in with
  **`admin@csub.edu` / `admin123`** — students, analytics, terms, audit log.

The demo comes pre-seeded with a Fall 2026 term, the onboarding checklist, and ~50
sample students so the dashboards have real data.

## Stop / reset

```bash
docker compose -f docker-compose.demo.yml down       # stop (keeps the demo database)
docker compose -f docker-compose.demo.yml down -v     # stop AND wipe the database (fresh seed next run)
```

## What's running

| Container | What | Port |
|-----------|------|------|
| `web` | Vue SPA served by nginx, proxies `/api` → api | **3000** (open this) |
| `api` | ASP.NET Core API — auto-creates the DB, applies the schema, seeds demo data | internal |
| `sqlserver` | SQL Server 2022 (Developer edition), data in a Docker volume | internal |

## Notes

- The API runs in **Development** mode here on purpose: that's what enables the
  auto-create + auto-seed and the dev-login form, and skips the production
  secret requirements — perfect for a quick demo, not for real data.
- Moving to **Kubernetes** later? The same three images deploy there too; ask for
  the K8s manifests when you know the target cluster (the readiness review is done).
