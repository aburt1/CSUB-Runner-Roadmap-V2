# CSUB Runner Roadmap — Documentation

Start with the path for your team. You don't need to read everything — just follow your path. The links lead into the detailed guides.

## Start here by team

### EApps / Data
*For your team, it's mainly a system you push data into and read structure out of.*

1. **[API Integration Guide](API-GUIDE.md)** — the REST contract: provision students, push step completions, run outbound API checks, health endpoints.
2. [How the app works](ARCHITECTURE.md#how-the-app-works--the-business-logic) · [Authentication](ARCHITECTURE.md#authentication) · [Integration API](ARCHITECTURE.md#integration-api) — enough backend + security to interact safely.
3. [Data Model](ARCHITECTURE.md#data-model) — the tables and what they hold.

*First 15 minutes:* read the API guide's Quick Start and the Idempotency section; get an integration key from [SETUP](SETUP.md).

### Web / Frontend
*For your team, it's mainly a Vue SPA talking to a fixed `/api` contract.*

1. **[How the app works](ARCHITECTURE.md#how-the-app-works--the-business-logic)** — terms, tag-personalized steps, the progression cursor.
2. [Frontend and backend](ARCHITECTURE.md#frontend-and-backend) — how the SPA and API fit together, and the frozen `/api` contract (snake_case, relative paths).
3. [Development Setup](SETUP.md) — run the client against the API.

*First 15 minutes:* get the client running per SETUP, then click through the student and admin views.

### Infrastructure
*For your team, it's three deployables (web / api / SQL Server) you stand up and keep secure.*

1. **[Deployment](DEPLOYMENT.md)** — the run modes, DBA provisioning, secrets, TLS, the go-live checklist.
2. [Deployment Architecture](ARCHITECTURE.md#deployment-architecture) · [Health Probes](ARCHITECTURE.md#health-probes-healthcontrollercs) — topology and what to probe.
3. [Trade-offs to know](ARCHITECTURE-CONSIDERATIONS.md) — scaling, secrets, and the "revisit when…" triggers.

*First 15 minutes:* read DEPLOYMENT's run-modes table; confirm the health endpoints and the required secrets.

### Support
*For your team, it's something to keep healthy and triage quickly.*

1. **[Operations & Support Runbook](OPERATIONS.md)** — is-it-up, where the logs are, symptom→action, escalation.
2. [Health Probes](ARCHITECTURE.md#health-probes-healthcontrollercs) — the up/down checks behind §1 of the runbook.
3. [Working with Claude Code](WORKING-WITH-CLAUDE.md) — the assisted-remediation model.

*First 15 minutes:* skim the runbook's §1–§5; bookmark the log commands for our hosting.

## All docs

| Doc | What |
|-----|------|
| [SETUP](SETUP.md) | Run it locally |
| [DEPLOYMENT](DEPLOYMENT.md) | Production deploy |
| [ARCHITECTURE](ARCHITECTURE.md) | How & why it works |
| [API-GUIDE](API-GUIDE.md) | Integration contract |
| [OPERATIONS](OPERATIONS.md) | Support runbook + observability plan |
| [ARCHITECTURE-CONSIDERATIONS](ARCHITECTURE-CONSIDERATIONS.md) | Deliberate trade-offs |
| [WORKING-WITH-CLAUDE](WORKING-WITH-CLAUDE.md) | Using the AI assistant |
