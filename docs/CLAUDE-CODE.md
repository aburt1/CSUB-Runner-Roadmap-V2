# Development with Claude Code

This project was built and maintained using [Claude Code](https://docs.anthropic.com/en/docs/claude-code) with the [Superpowers](https://github.com/obra/superpowers) plugin. This guide explains how to set up the same workflow for adding new features.

The codebase is a **Vue 3 + ASP.NET Core (.NET 10) + SQL Server** rewrite of the original React + Node/Express + PostgreSQL app. The two stacks live side by side in the repository:

- `Api/` — the ASP.NET Core controllers, Dapper data access, authentication, and services (the backend).
- `client/` — the Vue 3 + Vite single-page app (pages, components, Pinia stores, composables, router).
- `tests/` — the xUnit integration test suite (`Api.IntegrationTests`), which hosts the real API in-process and exercises it over HTTP.

Because most features touch both halves of the stack, Claude Code is especially useful here: a single change request can fan out into a controller, a Dapper query, a Vue view, and a Pinia store, and the structured workflow keeps all of those pieces aligned with one approved design.

---

## What is Claude Code?

Claude Code is Anthropic's CLI tool for AI-assisted development. It can read your codebase, edit files, run commands, and execute multi-step development tasks autonomously. It works directly in your terminal, so it can build the client (`npm run build`), run the backend test suite (`dotnet test`), start the dev servers, and inspect their output the same way you would.

## What is Superpowers?

Superpowers is an open-source Claude Code plugin that adds structured development workflows — brainstorming, planning, implementation, code review, and verification steps. It prevents common AI development pitfalls like skipping design, shipping untested code, or losing context between sessions.

The plugin's skills are opinionated about *order*: it pushes you to agree on a design before writing code, to capture that design as an editable plan, to implement against the plan, and to verify the result before declaring the work done. That ordering is what kept the original-to-Vue/C# conversion honest, and it is the recommended way to extend the app.

---

## Setup

### 1. Install Claude Code

Follow the official installation guide at [docs.anthropic.com](https://docs.anthropic.com/en/docs/claude-code).

### 2. Install the Superpowers Plugin

From within Claude Code, run:

```
/install-plugin https://github.com/obra/superpowers
```

This installs the plugin and its skills into your Claude Code environment.

### 3. Open the Project

```bash
cd CSUB-Runner-Roadmap-V2
claude
```

Claude Code will read the project's `CLAUDE.md` file and `project-map.md` (if present) to orient itself to the codebase. The repo also ships a top-level `README.md` and a set of focused guides under `docs/` — point Claude at these when onboarding a new session so it learns the stack and conventions quickly:

| Doc | What it covers |
|-----|----------------|
| `README.md` | Stack overview, local dev workflow, configuration env vars, repo layout |
| `docs/ARCHITECTURE.md` | Tech stack, project structure, request/data flow |
| `docs/SETUP.md` | Prerequisites, running locally, environment variables, default credentials |
| `docs/AUTH-ROADMAP.md` | Student/admin/integration authentication, JWT, RBAC, Azure AD |
| `docs/API-GUIDE.md` | External integration API (inbound push + outbound API checks) |
| `docs/TESTING.md` | The xUnit integration test suite and how to run it |

---

## Development Workflow

The Superpowers plugin enforces a structured workflow for feature development. Here's the typical flow used throughout this project:

### 1. Brainstorming

Start with a feature request or bug report. Superpowers guides you through exploring the problem space, considering alternatives, and selecting an approach before any code is written.

```
Use brainstorming to design a new student notification system
```

This is the right moment to decide *where* a feature lives — for example, whether a new piece of data belongs on an existing controller and table or warrants its own — before any code commits you to a shape.

### 2. Planning

After brainstorming produces an approved design, create an implementation plan with specific tasks, file changes, and acceptance criteria.

```
Use writing-plans to create the implementation plan
```

Plans are saved to `docs/superpowers-optimized/plans/` with corresponding design specs in `docs/superpowers-optimized/specs/`. (These directories are created the first time you run the workflow.)

### 3. Implementation

Execute the plan using subagent-driven development, which dispatches parallel agents for independent tasks and enforces spec compliance and code quality review gates.

```
Use subagent-driven-development to execute the plan
```

A typical change spans both stacks. On the backend you will usually touch an ASP.NET Core controller in `Api/Controllers/`, the Dapper data access in `Api/Data/` (plus any hand-written T-SQL — there is no ORM, and the schema lives in `Api/Data/schema.sql`), and possibly a service in `Api/Services/`. On the frontend you will touch a Vue 3 single-file component or view in `client/src/`, a Pinia store in `client/src/stores/`, or a composable in `client/src/composables/`. Because the client calls a relative `/api` (proxied to the backend), front-end and back-end changes can be developed and tested together against the same running pair of dev servers.

### 4. Verification

Before marking work complete, run verification to confirm all acceptance criteria are met, tests pass, and type-checking succeeds.

```
Use verification-before-completion
```

For this stack that means two checks both pass:

- **Backend:** `dotnet test` runs the xUnit integration suite in `tests/Api.IntegrationTests/`. These tests host the real API in-process (via `WebApplicationFactory`) against a dedicated `csub_admissions_test` database, so the SQL Server container must be running first (`docker compose up -d sqlserver`).
- **Frontend:** `npm run build` (in `client/`) runs `vue-tsc` type-checking ahead of the Vite production bundle, so a type error fails the build just like a compile error fails the backend.

### 5. Branch Integration

Finish by merging the work branch, running final checks, and cleaning up.

```
Use finishing-a-development-branch
```

---

## Key Skills Reference

| Skill | When to Use |
|-------|-------------|
| `brainstorming` | New feature or architecture decision |
| `writing-plans` | Convert approved design into implementation tasks |
| `subagent-driven-development` | Execute a plan with parallel agents and review gates |
| `systematic-debugging` | Diagnose and fix bugs or test failures |
| `verification-before-completion` | Verify work is complete before merging |
| `finishing-a-development-branch` | Merge branch and clean up |
| `context-management` | Persist decisions and context across sessions |
| `requesting-code-review` | Get structured code review |

---

## Project Context Files

These files help Claude Code understand the project across sessions:

| File | Purpose |
|------|---------|
| `CLAUDE.md` | Project conventions, tech stack, and development rules |
| `project-map.md` | Auto-generated codebase map for orientation |
| `state.md` | Current session state and in-progress work |
| `known-issues.md` | Tracked error-to-solution mappings |

These are produced and maintained by the Superpowers workflow (`context-management` writes `state.md` and `project-map.md`; `error-recovery` writes `known-issues.md`). They are most useful on a long-lived feature where work spans multiple sessions and you want Claude to pick up exactly where it left off.

---

## Design Specs and Plans

Feature designs and plans are stored under `docs/superpowers-optimized/`. These serve as reference for how architectural decisions were made:

```
docs/superpowers-optimized/
├── specs/     # Design specifications (the "what" and "why")
└── plans/     # Implementation plans (the "how")
```

When adding a feature that touches similar areas, reviewing these specs helps avoid re-discovering constraints that were already worked through — for example, how authentication and RBAC roles are enforced, how the inbound integration push API authenticates, or how outbound API-check credentials are encrypted at rest.

---

## Tips

- **Start sessions from the project root** so Claude Code picks up `CLAUDE.md` and context files automatically.
- **Use the structured workflow** for any change beyond a typo fix — it catches issues early.
- **Review generated plans before executing** — plans are editable markdown files.
- **Run `dotnet test` and `npm run build` before finishing** — the verification skill does this automatically. `dotnet test` runs the xUnit integration suite in `tests/Api.IntegrationTests/`; `npm run build` (in `client/`) runs `vue-tsc` type-checking ahead of the Vite bundle.
- **Make sure SQL Server is up before running the tests.** The integration suite drops and recreates a `csub_admissions_test` database on a real SQL Server instance, so start it first with `docker compose up -d sqlserver`. There is no separate migration step — the API creates the database, schema, and seed data on startup, so the tests rebuild everything deterministically each run.
- **Remember the database is automatic.** Both the running app and the test suite create the SQL Server database, schema, and seed data on startup; you only need SQL Server itself running. This means Claude can iterate on schema changes (in `Api/Data/schema.sql`) and re-run without you ever invoking a migration tool.
- **Know the two run modes when asking Claude to "run the app".** Locally without containers, the backend is `cd Api && dotnet run` (`:3001`) and the frontend is `cd client && npm run dev` (`:3000`, proxying `/api` to `:3001`). The full stack runs as three containers via `docker compose up --build`, reachable at `http://localhost:3000` (the `web` nginx container proxies `/api` to the `api` container, which talks to `sqlserver`). The relevant dev knob is `VITE_API_PROXY_TARGET` if you need the client to point at a non-default backend.
- **Check `docs/superpowers-optimized/specs/`** before designing new features that touch existing systems.
