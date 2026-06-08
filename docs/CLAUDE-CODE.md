# Development with Claude Code

This project was built and maintained using [Claude Code](https://docs.anthropic.com/en/docs/claude-code) with the [Superpowers](https://github.com/obra/superpowers) plugin. This guide explains how to set up the same workflow for adding new features.

The codebase is a Vue 3 + ASP.NET Core (.NET 10) + SQL Server rewrite of the original React + Node/Express + PostgreSQL app. Backend code lives in `Api/`, the Vue client in `client/`, and integration tests in `tests/`.

---

## What is Claude Code?

Claude Code is Anthropic's CLI tool for AI-assisted development. It can read your codebase, edit files, run commands, and execute multi-step development tasks autonomously.

## What is Superpowers?

Superpowers is an open-source Claude Code plugin that adds structured development workflows — brainstorming, planning, implementation, code review, and verification steps. It prevents common AI development pitfalls like skipping design, shipping untested code, or losing context between sessions.

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

Claude Code will read the project's `CLAUDE.md` file and `project-map.md` (if present) to orient itself to the codebase. The repo also ships a `README.md` that documents the stack, the local dev workflow, and configuration env vars — worth pointing Claude at when onboarding a new session.

---

## Development Workflow

The Superpowers plugin enforces a structured workflow for feature development. Here's the typical flow used throughout this project:

### 1. Brainstorming

Start with a feature request or bug report. Superpowers guides you through exploring the problem space, considering alternatives, and selecting an approach before any code is written.

```
Use brainstorming to design a new student notification system
```

### 2. Planning

After brainstorming produces an approved design, create an implementation plan with specific tasks, file changes, and acceptance criteria.

```
Use writing-plans to create the implementation plan
```

Plans are saved to `docs/superpowers-optimized/plans/` with corresponding design specs in `docs/superpowers-optimized/specs/`.

### 3. Implementation

Execute the plan using subagent-driven development, which dispatches parallel agents for independent tasks and enforces spec compliance and code quality review gates.

```
Use subagent-driven-development to execute the plan
```

A typical change touches both stacks: an ASP.NET Core controller in `Api/Controllers/` (plus Dapper data access in `Api/Data/` and any T-SQL), and a Vue 3 SFC, Pinia store, or composable in `client/src/`.

### 4. Verification

Before marking work complete, run verification to confirm all acceptance criteria are met, tests pass, and type-checking succeeds.

```
Use verification-before-completion
```

For this stack that means the backend test suite (`dotnet test`) and the client production build (`npm run build`, which runs `vue-tsc` type-checking before bundling) both pass.

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

---

## Design Specs and Plans

Previous feature designs and plans are stored in `docs/superpowers-optimized/` (and `docs/superpowers/` for earlier work). These serve as reference for how architectural decisions were made:

```
docs/superpowers-optimized/
├── specs/     # Design specifications (the "what" and "why")
└── plans/     # Implementation plans (the "how")
```

When adding a feature that touches similar areas, reviewing these specs helps avoid re-discovering constraints that were already worked through.

---

## Tips

- **Start sessions from the project root** so Claude Code picks up `CLAUDE.md` and context files automatically
- **Use the structured workflow** for any change beyond a typo fix — it catches issues early
- **Review generated plans before executing** — plans are editable markdown files
- **Run `dotnet test` and `npm run build`** before finishing — the verification skill does this automatically. `dotnet test` runs the xUnit integration suite in `tests/Api.IntegrationTests/`; `npm run build` runs `vue-tsc` type-checking ahead of the Vite bundle
- **Remember the database is automatic** — the API creates the SQL Server database, schema, and seed data on startup, so there's no manual migration step to run before testing a change. You only need SQL Server itself (`docker compose up -d sqlserver`)
- **Check `docs/superpowers-optimized/specs/`** before designing new features that touch existing systems
