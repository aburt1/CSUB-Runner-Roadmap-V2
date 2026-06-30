# Role-based Docs + Support Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-optimized:subagent-driven-development (recommended) or superpowers-optimized:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each team (EApps/Data, Web/Frontend, Infrastructure, Support) a clear on-ramp into the existing docs, and give Support an operations runbook + a documented observability and Claude-assisted-remediation plan.

**Architecture:** Pure documentation. Two new files — `docs/README.md` (role "Start here" router) and `docs/OPERATIONS.md` (support runbook + observability roadmap + remediation model) — plus pointer edits into the root README, CLAUDE.md, ARCHITECTURE-CONSIDERATIONS.md, and WORKING-WITH-CLAUDE.md. No application code or behavior changes.

**Tech Stack:** Markdown + Mermaid (GitHub-flavored). Validation via shell (`grep`).

**Assumptions:**
- Assumes the existing ARCHITECTURE.md anchors used by the router still exist — will NOT resolve if a heading was renamed (Task 4 verifies them).
- Assumes Docker Compose **service** names `api` / `web` (reliable) rather than guessing container_name — `docker compose logs api` works regardless of container naming.
- Assumes no tool/infra is being built this round (observability + remediation are documented as plans only, per the approved spec's non-goals).

Spec: [docs/superpowers-optimized/specs/2026-06-30-role-docs-and-support-operations-design.md](../specs/2026-06-30-role-docs-and-support-operations-design.md)

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `docs/OPERATIONS.md` | Create | Support runbook: is-it-up, where-to-look, symptom→action, escalation, observability roadmap, Claude-remediation model |
| `docs/README.md` | Create | Role "Start here" router — thin link-router into existing docs per team |
| `README.md` (root) | Modify | Add a "by team" pointer + an OPERATIONS row to the Documentation section |
| `CLAUDE.md` | Modify | Docs map: add the role router + OPERATIONS |
| `docs/ARCHITECTURE-CONSIDERATIONS.md` | Modify | Point the "logging is console only" trade-off at the OPERATIONS observability roadmap |
| `docs/WORKING-WITH-CLAUDE.md` | Modify | Add a pointer to the OPERATIONS Claude-remediation model |

---

### Task 1: Create `docs/OPERATIONS.md`

**Files:**
- Create: `docs/OPERATIONS.md`

**Does NOT cover:** the runbook documents *current* behavior + conservative actions only; it does NOT introduce new endpoints, flags, or destructive operations. Observability tooling and the remediation routine are documented as plans, not built.

- [ ] **Step 1: Write the file** with exactly this content:

```markdown
# Operations & Support Runbook

> **Who this is for:** the Support team — first responders who keep the Runner Roadmap healthy day to day. The goal is to *see* what's happening, resolve the common things yourself, and escalate the rest with enough detail that nobody has to chase you. New to the app? Start at the [docs home](README.md).

## 1. Is it up?

Two health endpoints answer different questions:

| Check | URL | Green | Red means | First action |
|-------|-----|-------|-----------|--------------|
| Liveness | `/api/health/live` | `200 {status:"ok"}` | the process itself is down or hung | restart the api service; if it won't stay up it's almost always a bad secret/config — see §4 and escalate |
| Readiness | `/api/health/ready` | `200 {status:"ready", db:"connected"}` | process is up but **can't reach the database** | check SQL Server is up/reachable; the app reconnects on its own once the DB is back — no restart needed |

Rule of thumb: **`/live` red → restart the app. `/ready` red → check the database, not the app.** Detail: [ARCHITECTURE → Health Probes](ARCHITECTURE.md#health-probes-healthcontrollercs).

## 2. Where to look (today)

The app currently writes logs to **standard output** — there's no aggregator yet (see §6 for the plan). Read them where it runs:

- **Containers:** `docker compose logs api` (add `-f` to follow, `--since 15m` to limit). Web tier: `docker compose logs web`.
- **Windows Server + IIS:** the API's stdout is captured by the ASP.NET Core Module (the site's `logs\stdout` folder); startup/crash events also land in **Windows Event Viewer → Application**.

**Audit history** (Admin UI → Audit Log, backed by the `audit_log` table) is a second, in-app signal. It answers **"did this action happen, and who did it"** — who completed/uncompleted a step, who changed a term — *not* "why did the app throw." Two caveats: it only records in-app actions, and it lives in the database, so it's unavailable exactly when the app or DB is down. Use it to confirm or deny a reported change, not as an error log.

## 3. Quick reference

| Need | Command / location |
|------|--------------------|
| Health | `GET /api/health/live`, `GET /api/health/ready` |
| Logs (containers) | `docker compose logs api --since 30m` |
| Restart API (containers) | `docker compose restart api` |
| Who-did-what | Admin UI → Audit Log |

## 4. Symptom → likely cause → action

Conservative actions only — check, restart, or escalate. Nothing here deletes data.

| Symptom | Likely cause | What to check | Action |
|---------|--------------|---------------|--------|
| API won't start / crashes on boot | Missing or weak required secret (`Jwt:Secret`, `ApiCheck:EncryptionKey`, admin/break-glass password) — the app **fails fast** in Production by design | the startup log line naming the bad/missing key | this is config, not a code bug → escalate to whoever owns the deployment secrets |
| `/api/health/ready` returns 503 | Database unreachable | is SQL Server up? network/firewall? credentials? | bring the DB back; the app reconnects on its own (it retries) — no app restart needed |
| Users can't sign in via SSO | Entra (Azure AD) misconfig, or the `studentId` claim isn't being sent | recent Entra changes; the SSO failure line in the log | escalate to the EApps/identity owner with the log line |
| An outbound API check always fails | Target down, or the URL resolves to a private/internal IP (rejected by design) | the check's URL and the rejection reason in the log | "Resolved to private IP" = expected safety behavior, fix the URL; otherwise it's a target-side issue |
| Bursts of `429 Too Many Requests` | Rate limit tripped (200 per 15 min per IP; tighter on login) | what's the source IP — a real user, a script, or abuse? | legitimate spike → escalate to raise the limit; abusive → block upstream |
| Page is blank / assets 404 | SPA/proxy issue (e.g. `API_URL` has a trailing slash) or a bad deploy | browser console + `docker compose logs web` | escalate with the console errors; check the last deploy |

## 5. Escalation

When you can't resolve it, capture this and hand it to the EApps/dev team — it's everything they need to skip the back-and-forth:

\`\`\`
When:        <date/time + timezone>
Symptom:     <what the user saw / what's broken>
Scope:       <one user? everyone? one term/cohort?>
Health:      /live = <ok|down>   /ready = <ready|503>
Logs:        <the relevant lines — copy 10-20 lines around the error>
Recent:      <any deploy, config, or Entra change in the last 24h>
Tried:       <what you already checked or did>
\`\`\`

## 6. Logging and observability roadmap

> The *plan* — **not built yet.** We have not chosen a logging tool; this section lays out the options and an interim approach so we can decide deliberately. Today = stdout (§2).

Think in three layers:

| Layer | Question | Status |
|-------|----------|--------|
| **Health** | Is it up? | exists (`/health/*`) |
| **Logs** | What happened / why did it throw? | the gap — stdout only |
| **Audit** | Who did what in-app? | exists (`audit_log`); triage aid only |

**Interim plan:** aggregate the container/server logs onto a **separate box** — somewhere that stays up when the app host doesn't, so logs survive an outage (the audit table can't — see §2).

**When we start, do this one enabling step first:** switch the app to **structured (JSON) console logging**. It's a small, low-risk change, and it lets *any* of the tools below ingest the logs cleanly.

**Options (no pick — decide with the checklist):**

| Option | Best when |
|--------|-----------|
| **Sentry / GlitchTip** (GlitchTip self-hosts, Sentry-compatible) | you want error grouping + alerting and like the Sentry model |
| **Azure Application Insights / Monitor** | you lean into the Azure/Entra stack; hosted, searchable, alerting |
| **Seq** | smallest self-hosted, .NET-native structured-log viewer |
| **Grafana Loki** | infrastructure already runs Grafana |
| **Existing CSUB logging software** | institutional tooling already exists — prefer reusing it |

**Decision checklist:** runs on a separate box · survives an app-host outage · ingests structured JSON · searchable with history · supports alerting · has a clear owner.

## 7. Claude-assisted remediation

> The *model* — **not wired up yet.** This is the intended Support "fix it" path once the prerequisites below exist. Documented now so we build toward it deliberately.

The idea: when Support sees a *recurring* error, they trigger a Claude Code routine that diagnoses it and opens a **human-reviewable** pull request, with adversarial agents posting a go/no-go verdict — so the same problem gets a candidate fix without a direct report to the dev team, and a human still approves every merge.

\`\`\`mermaid
flowchart LR
  s([Support spots a recurring error]) --> trig["Trigger a Claude Code routine<br/>(on-demand or scheduled)"]
  trig --> diag["Claude reads the error source,<br/>diagnoses, drafts a fix on a branch"]
  diag --> pr["Opens a GitHub PR"]
  pr --> adv["Adversarial agents review:<br/>red-team vs code-reviewer"]
  adv --> verdict["Go / no-go verdict<br/>posted as a PR comment"]
  verdict --> human["A human reviews and merges"]
\`\`\`

**Guardrails:**
- **A human always merges.** Nothing auto-merges.
- The adversarial verdict is **advisory** — it informs the human, it is not a self-approving gate.
- Fixes arrive as ordinary reviewable PRs on a branch, never straight to `main`.

**Prerequisites (why it's deferred):**
1. **An error source Claude can read** — the logging roadmap (§6) in place, even just the interim aggregation.
2. **A GitHub repo that receives PRs** — the repo pushed, with a branch/PR workflow.
3. **A human reviewer/merger** on the Support or dev side.

When those exist, the build is a small Claude Code routine (a scheduled trigger or an on-demand command) running a diagnose → branch → PR → adversarial-review workflow. See [Working with Claude Code](WORKING-WITH-CLAUDE.md).
```

> Note for the executor: the escalation block and the mermaid block above are shown with escaped fences (`\`\`\``) so they survive inside this plan. Write them to the file as **real** triple-backtick fences.

- [ ] **Step 2: Verify mermaid + structure**

Run: `cd docs && echo "mermaid:$(grep -c '```mermaid' OPERATIONS.md) fences:$(grep -c '```' OPERATIONS.md)"; grep -nE '^\s*(call|end|class|graph|style|state)\b *(\[|\(|\{)' OPERATIONS.md || echo "no-reserved-ids"`
Expected: `mermaid:1 fences:4` (1 mermaid block + the escalation code block = 4 fences, even) and `no-reserved-ids`.

---

### Task 2: Create `docs/README.md` (role router)

**Files:**
- Create: `docs/README.md`

- [ ] **Step 1: Write the file** with exactly this content:

```markdown
# CSUB Runner Roadmap — Documentation

Start with the path for your team. You don't need to read everything — just your lane. The links go into the detailed guides.

## Start here by team

### EApps / Data
*To you, this app is a system you push data into and read structure out of.*

1. **[API Integration Guide](API-GUIDE.md)** — the REST contract: provision students, push step completions, run outbound API checks, health endpoints.
2. [How the app works](ARCHITECTURE.md#how-the-app-works--the-business-logic) · [Authentication](ARCHITECTURE.md#authentication) · [Integration API](ARCHITECTURE.md#integration-api) — enough backend + security to interact safely.
3. [Data Model](ARCHITECTURE.md#data-model) — the tables and what they hold.

*First 15 minutes:* read the API guide's Quick Start and the Idempotency section; get an integration key from [SETUP](SETUP.md).

### Web / Frontend
*To you, this app is a Vue SPA talking to a fixed `/api` contract.*

1. **[How the app works](ARCHITECTURE.md#how-the-app-works--the-business-logic)** — terms, tag-personalized steps, the progression cursor.
2. [Frontend and backend](ARCHITECTURE.md#frontend-and-backend) — how the SPA and API fit together, and the frozen `/api` contract (snake_case, relative paths).
3. [Development Setup](SETUP.md) — run the client against the API.

*First 15 minutes:* get the client running per SETUP, then click through the student and admin views.

### Infrastructure
*To you, this app is three deployables (web / api / SQL Server) you stand up and keep secure.*

1. **[Deployment](DEPLOYMENT.md)** — the run modes, DBA provisioning, secrets, TLS, the go-live checklist.
2. [Deployment Architecture](ARCHITECTURE.md#deployment-architecture) · [Health Probes](ARCHITECTURE.md#health-probes-healthcontrollercs) — topology and what to probe.
3. [Trade-offs to know](ARCHITECTURE-CONSIDERATIONS.md) — scaling, secrets, and the "revisit when…" triggers.

*First 15 minutes:* read DEPLOYMENT's run-modes table; confirm the health endpoints and the required secrets.

### Support
*To you, this app is something to keep healthy and triage fast.*

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
```

- [ ] **Step 2: Verify the in-repo links resolve** (Task 4 checks anchors globally)

Run: `cd docs && for f in API-GUIDE.md ARCHITECTURE.md DEPLOYMENT.md SETUP.md OPERATIONS.md ARCHITECTURE-CONSIDERATIONS.md WORKING-WITH-CLAUDE.md; do [ -f "$f" ] || echo "MISSING: $f"; done; echo done`
Expected: `done` with no `MISSING` lines.

---

### Task 3: Wire pointers into existing docs

**Files:**
- Modify: `README.md` (root)
- Modify: `CLAUDE.md`
- Modify: `docs/ARCHITECTURE-CONSIDERATIONS.md`
- Modify: `docs/WORKING-WITH-CLAUDE.md`

- [ ] **Step 1: Root README — add the "by team" pointer.** Replace the "Which doc do I need?" line:

Find:
```
**Which doc do I need?** &nbsp; Run it locally → **[SETUP](docs/SETUP.md)** &nbsp;·&nbsp; Deploy to production → **[DEPLOYMENT](docs/DEPLOYMENT.md)** &nbsp;·&nbsp; Integrate an external system → **[API](docs/API-GUIDE.md)** &nbsp;·&nbsp; Understand how & why → **[Architecture](docs/ARCHITECTURE.md)**
```
Replace with:
```
**New here? Pick your team:** **[Documentation by role](docs/README.md)** routes EApps / Web / Infrastructure / Support to the right guide.

**Which doc do I need?** &nbsp; Run it locally → **[SETUP](docs/SETUP.md)** &nbsp;·&nbsp; Deploy to production → **[DEPLOYMENT](docs/DEPLOYMENT.md)** &nbsp;·&nbsp; Integrate an external system → **[API](docs/API-GUIDE.md)** &nbsp;·&nbsp; Support / operate it → **[Operations](docs/OPERATIONS.md)** &nbsp;·&nbsp; Understand how & why → **[Architecture](docs/ARCHITECTURE.md)**
```

- [ ] **Step 2: Root README — add the OPERATIONS table row.** Find:
```
| [API Integration](docs/API-GUIDE.md) | REST contract for external systems (inbound push + outbound API checks) + health endpoints |
```
Replace with:
```
| [API Integration](docs/API-GUIDE.md) | REST contract for external systems (inbound push + outbound API checks) + health endpoints |
| [Operations & Support](docs/OPERATIONS.md) | Support runbook — health checks, where the logs are, symptom→action triage, escalation, plus the logging/observability roadmap |
```

- [ ] **Step 3: CLAUDE.md — update the Docs map.** Find:
```
## Docs map

[SETUP](docs/SETUP.md) (run locally) · [DEPLOYMENT](docs/DEPLOYMENT.md) (production) ·
[ARCHITECTURE](docs/ARCHITECTURE.md) (how & why) · [API-GUIDE](docs/API-GUIDE.md)
(integration contract) · [ARCHITECTURE-CONSIDERATIONS](docs/ARCHITECTURE-CONSIDERATIONS.md)
(trade-offs) · [Working with Claude](docs/WORKING-WITH-CLAUDE.md) (using the assistant here).
```
Replace with:
```
## Docs map

**Start by team:** [docs/README.md](docs/README.md) routes EApps / Web / Infrastructure / Support to the right guide.

[SETUP](docs/SETUP.md) (run locally) · [DEPLOYMENT](docs/DEPLOYMENT.md) (production) ·
[ARCHITECTURE](docs/ARCHITECTURE.md) (how & why) · [API-GUIDE](docs/API-GUIDE.md)
(integration contract) · [OPERATIONS](docs/OPERATIONS.md) (support runbook + observability) ·
[ARCHITECTURE-CONSIDERATIONS](docs/ARCHITECTURE-CONSIDERATIONS.md)
(trade-offs) · [Working with Claude](docs/WORKING-WITH-CLAUDE.md) (using the assistant here).
```

- [ ] **Step 4: ARCHITECTURE-CONSIDERATIONS — point the logging row at the roadmap.** Find:
```
| **Logging is default console only** (no structured/JSON output or aggregation) | Capturing the container's stdout is a legitimate baseline for one instance | Incidents need cross-request correlation/search, or a 2nd instance is added → JSON console + ship to Seq/Loki/AppInsights |
```
Replace with:
```
| **Logging is default console only** (no structured/JSON output or aggregation) | Capturing the container's stdout is a legitimate baseline for one instance | Incidents need cross-request correlation/search, or a 2nd instance is added → JSON console + ship to a log store. The options + interim plan live in the [observability roadmap](OPERATIONS.md#6-logging-and-observability-roadmap) |
```

- [ ] **Step 5: WORKING-WITH-CLAUDE — append the remediation pointer** at the end of the file:
```

## Operational remediation (Support)

There's an intended Support workflow where Claude Code triages a recurring production error and opens a **human-reviewable** PR, with adversarial agents posting a go/no-go verdict — a human always merges. It's documented (as the model, not yet wired up) in the [Operations runbook → Claude-assisted remediation](OPERATIONS.md#7-claude-assisted-remediation).
```

- [ ] **Step 6: Verify the four edits landed**

Run: `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2 && grep -q "Documentation by role" README.md && grep -q "Operations & Support" README.md && grep -q "Start by team" CLAUDE.md && grep -q "observability roadmap" docs/ARCHITECTURE-CONSIDERATIONS.md && grep -q "Operational remediation" docs/WORKING-WITH-CLAUDE.md && echo "all-pointers-present"`
Expected: `all-pointers-present`

---

### Task 4: Validate the doc set and commit

**Files:** (no new files)

- [ ] **Step 1: Mermaid balance + reserved-id scan across changed docs**

Run: `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2/docs && for f in OPERATIONS.md README.md; do echo "$f: mermaid=$(grep -c '```mermaid' $f) fences=$(grep -c '```' $f)"; done; grep -rnE '^\s*(call|end|class|graph|style|state)\b *(\[|\(|\{)' OPERATIONS.md README.md || echo "no-reserved-ids"`
Expected: fences even on each file; `no-reserved-ids`.

- [ ] **Step 2: Verify every ARCHITECTURE anchor the router/runbook links to exists**

Run:
```bash
cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2/docs
for a in "how-the-app-works--the-business-logic" "authentication" "integration-api" "data-model" "frontend-and-backend" "deployment-architecture" "health-probes-healthcontrollercs"; do
  hdr=$(grep -iE '^#{1,4} ' ARCHITECTURE.md | sed -E 's/^#+ //; s/`//g' | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9 -]//g; s/ /-/g')
  echo "$hdr" | grep -qx "$a" && echo "OK  $a" || echo "MISSING ANCHOR: $a"
done
```
Expected: `OK` for all seven; no `MISSING ANCHOR`.

- [ ] **Step 3: Verify the two OPERATIONS self-anchors used by the pointers exist**

Run: `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2/docs && grep -qE '^## 6\. Logging and observability roadmap *$' OPERATIONS.md && grep -qE '^## 7\. Claude-assisted remediation *$' OPERATIONS.md && echo "self-anchors-ok"`
Expected: `self-anchors-ok` (these back `#6-logging-and-observability-roadmap` and `#7-claude-assisted-remediation`).

- [ ] **Step 4: Accuracy grep — no invented endpoints/commands**

Run: `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2/docs && grep -nE '/api/health/(live|ready)' OPERATIONS.md && grep -n 'docker compose logs' OPERATIONS.md && echo "facts-present"`
Expected: shows the real health endpoints + `docker compose logs` usage; `facts-present`.

- [ ] **Step 5: Commit (local only — do NOT push)**

```bash
cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2
git add docs/README.md docs/OPERATIONS.md README.md CLAUDE.md docs/ARCHITECTURE-CONSIDERATIONS.md docs/WORKING-WITH-CLAUDE.md docs/superpowers-optimized/
git commit -F- <<'EOF'
docs: add role-based docs router + Support operations runbook

A "Start here by team" router (docs/README.md) points EApps/Data, Web/Frontend,
Infrastructure, and Support into the existing guides at the right depth. A new
Support runbook (docs/OPERATIONS.md) covers is-it-up, where to read logs today,
a symptom -> cause -> action table, an escalation template, a logging/observability
roadmap (options + interim plan, no tool chosen yet), and the intended
Claude-assisted remediation model (human-reviewable PRs; not wired up yet).

Pointers added from the root README, CLAUDE.md, ARCHITECTURE-CONSIDERATIONS, and
WORKING-WITH-CLAUDE. Docs only; no code changed.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
git log --oneline -1
```
Expected: one new commit; working tree clean.

---

## Self-Review

**Spec coverage:**
- Role router (4 teams, routing matrix, skill-level intro line) → Task 2 ✓
- OPERATIONS runbook (is-it-up / where-to-look / symptom→action / escalation) → Task 1 §1–5 ✓
- Observability roadmap (3 layers, interim plan, options menu, checklist, JSON-logging first step) → Task 1 §6 ✓
- Claude-remediation model (mermaid, guardrails, prerequisites, "not wired up" banner) → Task 1 §7 ✓
- Doc-map updates (README, CLAUDE.md, CONSIDERATIONS, WORKING-WITH-CLAUDE) → Task 3 ✓
- Audit-history reframed as who-did-what triage aid, with downtime caveat → Task 1 §2 ✓
- Non-goal honesty (banners on §6/§7) → Task 1 ✓

**Placeholder scan:** none — both new docs are written verbatim; all edits are exact find/replace.

**Type/anchor consistency:** the pointer in Task 3 §4 targets `#6-logging-and-observability-roadmap` and Task 3 §5 targets `#7-claude-assisted-remediation`; Task 1 uses headings "## 6. Logging and observability roadmap" (no "&", anchor-clean) and "## 7. Claude-assisted remediation" — matched. Task 4 §3 verifies both.
