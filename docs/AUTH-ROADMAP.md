# Authentication &amp; Roadmap Personalization

This is the **.NET / Vue rewrite** of the original CSUB Admissions Roadmap app.
The REST contract — paths, request/response payloads, and status codes — is
preserved so existing clients keep working, but the implementation moved:

- **Server:** Express middleware → **ASP.NET Core controllers (.NET 10)** with
  action filters; `pg` (PostgreSQL) → **Dapper + T-SQL on SQL Server 2022**.
- **Client:** React + Context → **Vue 3 (Vite, vue-router) + Pinia**. MSAL is
  still used on the client for Azure AD.

The authentication design is otherwise faithful to the original: the same three
auth systems, the same HS256 session JWT (same claim names and 8-hour lifetime),
the same four RBAC roles, and the same break-glass local-login escape hatch.

This doc covers two related concerns:

1. **Authentication & RBAC** — who you are and what you may do (the three auth
   systems, the JWT, the role gates).
2. **Roadmap personalization** — once a student is authenticated, *which* steps
   they see and *which* step is highlighted as "current." That logic lives almost
   entirely on the client in
   [`client/src/composables/useProgress.ts`](../client/src/composables/useProgress.ts)
   and is documented in depth below.

For production rollout (Windows Server + SQL Server, TLS termination, service
accounts, backups), see [docs/DEPLOYMENT.md](./DEPLOYMENT.md). For the broader
system picture see [docs/ARCHITECTURE.md](./ARCHITECTURE.md); for endpoint shapes
see [docs/API-GUIDE.md](./API-GUIDE.md).

> **Removed in the rewrite.** The legacy `X-API-Key` admin auth path is gone.
> Admins now authenticate **only** via local email/password, Azure AD SSO, or the
> env-gated break-glass local login. The dev activity simulator and the dev-only
> mock API-check routes were also dropped. See "Dropped vs. the old app" below.

---

## Current State

The application has **three separate authentication systems**. All three issue or
validate the app's own **HS256 JWT** with an **8-hour lifetime**
([`Api/Auth/JwtService.cs`](../Api/Auth/JwtService.cs)). They differ in who they
authenticate and how the credential is presented:

| System | Who | Credential | Server entry point |
| --- | --- | --- | --- |
| Student | Prospective students | Dev login (name + email) or Azure AD SSO | [`Api/Controllers/AuthController.cs`](../Api/Controllers/AuthController.cs) |
| Admin | Staff | Email/password, Azure AD SSO, or break-glass | [`Api/Controllers/AdminAuthController.cs`](../Api/Controllers/AdminAuthController.cs) |
| Integration | External systems (e.g. PeopleSoft) | Integration key (bcrypt-verified) | [`Api/Auth/IntegrationAuthAttribute.cs`](../Api/Auth/IntegrationAuthAttribute.cs) |

### Student Authentication

- **Method:** Development login (`POST /api/auth/dev-login`) or Azure AD SSO
  (`POST /api/auth/sso`).
- **How dev-login works:** Students enter their name and email. The server finds
  the student by email or creates a new student record, **auto-completes the
  `accepted` step** for the active term, and returns a JWT. This endpoint is
  **disabled in production** — it returns `404 { "error": "Not found" }` when
  `ASPNETCORE_ENVIRONMENT=Production` (the very first line of the action in
  [`AuthController.DevLogin`](../Api/Controllers/AuthController.cs) is the
  `_env.IsProduction()` guard).
- **How SSO works:** The client obtains a Microsoft ID token via MSAL and POSTs it
  to `/api/auth/sso`. The server validates the token, then maps it to a student
  via the `azure_id` column — creating the student (and auto-completing `accepted`)
  on first login, or refreshing `display_name`/`email` on subsequent logins.
- **Token claims:** `{ type: "student", studentId, email }`.
- **Token storage (client):** `sessionStorage` under key **`csub_token`** (cleared
  on tab close).
- **Token lifetime:** 8 hours.
- **Where:** [`Api/Controllers/AuthController.cs`](../Api/Controllers/AuthController.cs),
  `Api/Auth/StudentAuthAttribute.cs`; client
  [`client/src/stores/auth.ts`](../client/src/stores/auth.ts) (Pinia store),
  `client/src/auth/msalConfig.ts`,
  [`client/src/components/PublicRoadmapPreview.vue`](../client/src/components/PublicRoadmapPreview.vue).
- **Note:** Azure AD SSO is fully implemented and activates automatically when
  configured (see below); when it is not configured the SSO endpoint returns
  `501` and the app falls back to dev login.

**The "auto-complete `accepted`" detail matters for personalization.** When a new
student is created (via either dev-login or SSO), the server runs
`AutoCompleteAcceptedStepAsync`, which idempotently inserts a `student_progress`
row for the active term's `accepted` step:

```sql
IF NOT EXISTS (SELECT 1 FROM student_progress WHERE student_id = @studentId AND step_id = @stepId)
  INSERT INTO student_progress (student_id, step_id) VALUES (@studentId, @stepId)
```

So a brand-new student already has one completed step the moment they sign in.
This is why the roadmap never opens on a blank "nothing done yet" state — the
client's status-derivation logic (below) sees `accepted` as `completed` and moves
the "in progress" marker to the *next* required step.

### Admin Authentication

- **Method:** Azure AD SSO (when configured) or email/password login.
- **Login endpoint:** `POST /api/admin/auth/login` — email/password. The email is
  lower-cased and trimmed, then matched against active admins; the supplied
  password is checked against the bcrypt `password_hash` (work factor 10,
  `Api/Auth/Passwords.cs`). Rate-limited by the `login` policy (10 requests / 15 min
  per IP). Returns `401 { "error": "Invalid credentials" }` on a bad email or
  password.
- **SSO endpoint:** `POST /api/admin/auth/sso` — validates the Azure AD ID token,
  looks up an admin by `azure_id`, then falls back to a case-insensitive email
  match. Returns `501` if Azure AD is **not** configured, `403` if **no matching
  admin** exists ("No admin account found. Contact your system administrator."),
  and `403` if the matched account is inactive.
- **Break-glass / local login:** `POST /api/admin/auth/local-login` (UI at
  `/admin/local-login`) — a hidden emergency login, gated by the
  `LocalLogin__Username` / `LocalLogin__Password` config. Returns `404` if **either**
  value is unset (the route is effectively invisible). Rate-limited by the
  `breakGlass` policy (5 requests / 15 min per IP) and **audit-logged on both
  success and failure** under actor `break-glass`. Credentials are compared in
  constant time. The break-glass session has **no DB row** and is issued the
  `sysadmin` role.
- **Change password:** `POST /api/admin/auth/change-password` — the current admin
  changes their own password. Requires the current password and a new password of
  **at least 12 characters**. (The break-glass session, having no DB row, cannot
  use this — it returns `404 { "error": "User not found" }`.)
- **Session probe:** `GET /api/admin/auth/me` — returns the current admin's profile.
  For real admins it reads from `admin_users`; for the break-glass session it
  echoes the JWT claims.
- **Account linking:** First SSO login matches an admin by email, then writes
  `azure_id` for future logins. The display name is refreshed from the token on
  each SSO login.
- **No auto-creation:** Admin accounts must be **pre-created** in the Users tab.
  SSO handles authentication only — it never creates admins.
- **Token claims:** `{ type: "admin", adminId, role, email, displayName }`.
- **Token storage (client):** `sessionStorage` under keys **`csub_admin_token`** and
  **`csub_admin_user`** (the latter holds the cached `{ id, email, displayName, role }`).
- **Token lifetime:** 8 hours.
- **RBAC roles:** `viewer`, `admissions`, `admissions_editor`, `sysadmin` (see below).
- **Where:** [`Api/Controllers/AdminAuthController.cs`](../Api/Controllers/AdminAuthController.cs),
  `Api/Auth/AdminAuthAttribute.cs`;
  client `client/src/pages/admin/AdminLogin.vue`,
  `client/src/pages/admin/AdminLocalLogin.vue`,
  `client/src/pages/admin/AdminPage.vue`,
  `client/src/pages/admin/roleConfig.ts`.

### Integration Authentication

- **Method:** Integration key, presented either as an **`X-Integration-Key`** header
  or as a **`Bearer`** token in the `Authorization` header.
- **How it works:** External systems authenticate with a bcrypt-verified
  integration key. If an **`X-Client-Name`** header is supplied, the server looks up
  that single active client and verifies the key against it (this avoids a bcrypt
  DoS — one hash instead of many). Otherwise it falls back to scanning **up to 10**
  active clients (`SELECT TOP 10`) and verifying against each, for backward
  compatibility. On a missing credential it returns
  `401 { "error": "Integration authentication required" }`; on a bad credential,
  `401 { "error": "Invalid integration credentials" }`.
- **Where:** `Api/Auth/IntegrationAuthAttribute.cs`; applied to every action on
  `Api/Controllers/IntegrationsController.cs` (`[IntegrationAuth]` at class level).
- **Default dev key:** **`dev-integration-key`** for client **`PeopleSoft Dev`**,
  seeded on startup in non-production (see "Default credentials" and the seeder
  notes below).
- **On success:** the resolved client name and id are stashed on `HttpContext.Items`
  and used by the audit layer (`Audit.ResolveActor`) to attribute write actions.

---

## RBAC Roles

There are **four** admin roles. They are enforced server-side by the
`[AdminAuth(...)]` action filter in `Api/Auth/AdminAuthAttribute.cs`:

- `[AdminAuth]` with **no arguments** allows **any authenticated admin** (the JWT
  must be valid and carry `type: "admin"`).
- `[AdminAuth("role1", "role2", ...)]` restricts the action to those roles. A valid
  admin whose role is not in the list gets `403 { "error": "Insufficient permissions" }`.

The filter also rejects a missing/blank `Authorization` header
(`401 Authentication required`), an invalid/expired token (`401 Invalid or expired
token`), and a token whose `type` is not `admin` (`401 Authentication required`).

| Role | Capability (broadly) |
| --- | --- |
| `viewer` | Read-only access to dashboards and student data |
| `admissions` | Read + day-to-day admissions operations (e.g. editing student progress/notes) |
| `admissions_editor` | The above + content/config editing (steps, terms) |
| `sysadmin` | Full access, including user management, integrations, and API-check config |

The client mirrors these in `client/src/pages/admin/roleConfig.ts`
(`ROLE_OPTIONS = ['viewer', 'admissions', 'admissions_editor', 'sysadmin']`) for
display labels and the role picker in the Users tab. **The client list is for UI
convenience only — every gate is enforced server-side**, so a tampered client
cannot grant itself a role it does not hold in its JWT.

### Per-endpoint role gates (as wired in the controllers)

The gates below were read directly from the controllers. The pattern throughout
is: **reads** are open to any authenticated admin, **writes** are restricted.

| Area / controller | Read (GET) | Write (POST/PUT/PATCH/DELETE) |
| --- | --- | --- |
| Students (`Admin/StudentsController.cs`) | `[AdminAuth]` | `[AdminAuth("admissions", "admissions_editor", "sysadmin")]` |
| Steps (`Admin/StepsController.cs`) | `[AdminAuth]` | `[AdminAuth("admissions_editor", "sysadmin")]` |
| Terms (`Admin/TermsController.cs`) | `[AdminAuth]` | `[AdminAuth("admissions_editor", "sysadmin")]` |
| Analytics / stats / export (`Admin/AnalyticsController.cs`) | `[AdminAuth]` | — |
| Users (`Admin/UsersController.cs`) | `[AdminAuth("sysadmin")]` (class-level) | `[AdminAuth("sysadmin")]` |
| API-check config (`Admin/ApiChecksController.cs`) | `[AdminAuth("sysadmin")]` (class-level) | `[AdminAuth("sysadmin")]` |

Non-admin endpoints for completeness:

| Endpoint | Gate |
| --- | --- |
| `GET /api/steps` (public step list) | none (anonymous) |
| `GET /api/steps/progress`, student progress mutations | `[StudentAuth]` |
| `POST /api/roadmap/run-api-checks`, `GET /api/roadmap/check-status` | `[StudentAuth]` (class-level on `RoadmapApiChecksController`) |
| `GET /api/auth/me` | `[StudentAuth]` |
| `POST /api/integrations/*` | `[IntegrationAuth]` (class-level) |
| `GET /api/health`, `GET /api/health/live`, `GET /api/health/ready` | none (anonymous) — see [docs/DEPLOYMENT.md](./DEPLOYMENT.md) |

---

## How the JWT works

Both the app's own session tokens and the inbound Azure AD tokens are validated
server-side — they are **different tokens with different algorithms and different
validators**; do not confuse the two.

- **App session token** ([`Api/Auth/JwtService.cs`](../Api/Auth/JwtService.cs)):
  **HS256**, signed with the symmetric `Jwt__Secret`, **8-hour** expiry,
  `ClockSkew = TimeSpan.Zero` (no leeway — an expired token is rejected the instant
  it expires). Issuer and audience are **not** validated (`ValidateIssuer = false`,
  `ValidateAudience = false`); only the signature, lifetime, and algorithm are. The
  validator pins the algorithm to HS256 (`ValidAlgorithms = [HmacSha256]`), which
  closes the classic "alg: none" / RS256→HS256 confusion attack. Validated by the
  `StudentAuth` / `AdminAuth` filters, which require `Authorization: Bearer <token>`
  and a matching `type` claim (`student` vs `admin`). `Program.cs` sets
  `JwtSecurityTokenHandler.DefaultMapInboundClaims = false` so claim names stay
  **verbatim** (`type`, `studentId`, `adminId`, `role`, `email`, `displayName`)
  instead of being remapped to long XML URIs.
- **Azure AD ID token** (`Api/Auth/AzureAdTokenValidator.cs`): **RS256**, validated
  against the tenant's OpenID Connect metadata via `ConfigurationManager`, which
  fetches and caches the signing keys from
  `https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration`
  and auto-refreshes them (handling key rotation transparently). Verifies the
  issuer (`https://login.microsoftonline.com/{tenantId}/v2.0`) and audience
  (`AzureAd__ClientId`), plus lifetime and signature. Reads the `oid` (object id,
  **required**), `preferred_username` (falling back to `email`), and `name` claims.

### Startup secret hardening (fail-fast)

The `JwtService` constructor refuses to start the server when the secret is unsafe
— misconfiguration is caught at boot, never at request time:

| Condition | Behavior |
| --- | --- |
| `Jwt__Secret` missing/empty (any environment) | Throws `InvalidOperationException("Jwt:Secret is not configured. Server cannot start.")` |
| In **Production**, secret `< 32` characters | Throws — "must be at least 32 characters in Production." |
| In **Production**, secret is a known placeholder | Throws — `IsKnownWeakSecret` rejects anything containing `change-me` / `CHANGE_ME` or starting with `dev-only` |

```csharp
if (env.IsProduction())
{
    if (secret.Length < 32)
        throw new InvalidOperationException("Jwt:Secret must be at least 32 characters in Production.");
    if (IsKnownWeakSecret(secret))
        throw new InvalidOperationException("Jwt:Secret is a known default/placeholder value; set a real secret in Production.");
}
```

The practical effect: you cannot accidentally deploy to Production while still
signing tokens with the committed dev default or an `.env.example` placeholder —
the container will crash-loop on startup until a real secret is supplied.

### Token lifecycle (student)

1. Client calls `devLogin(name, email)` or `ssoLogin()` in
   [`client/src/stores/auth.ts`](../client/src/stores/auth.ts).
2. On success the returned token is written to `sessionStorage['csub_token']` and
   held in the Pinia store's `token` ref.
3. On app start, `init()` reads any existing `csub_token`, calls `GET /api/auth/me`
   with it, and clears it if the server rejects it. If the server is merely
   unreachable, the token is kept and retried later.
4. `logout()` removes `csub_token`, clears the store, and clears the MSAL cache
   when Azure AD is configured.

```
                    ┌─────────────────────────────────────────────┐
 app start ──init()─►  csub_token in sessionStorage?               │
                    │     ├─ no  → loading = false (public view)    │
                    │     └─ yes → GET /api/auth/me                 │
                    │              ├─ 200 → set user (signed in)    │
                    │              ├─ 4xx → clear token (logged out)│
                    │              └─ network err → keep, retry     │
                    └─────────────────────────────────────────────┘
```

### Token lifecycle (admin)

The admin pages (`AdminPage.vue`, `AdminLogin.vue`, `AdminLocalLogin.vue`) store
the token in `sessionStorage['csub_admin_token']` and the cached user profile in
`sessionStorage['csub_admin_user']`. A rejected token clears both keys and returns
to the login screen.

### Session expiry handling (new)

Because the app session JWT has a hard **8-hour** expiry and **zero clock skew**,
a long-lived browser tab will eventually be holding a token the server rejects.
The student side now handles this gracefully instead of silently failing requests.

The progress poller in
[`useProgress.ts`](../client/src/composables/useProgress.ts) treats a `401` from
`GET /api/steps/progress` as a session-expired signal: it raises a toast and logs
the student out, which drops the UI back to the public/login view.

```ts
} else if (res.status === 401) {
  // Token expired/invalid — drop back to the public/login view.
  toast.error('Your session expired — please sign in again.')
  auth.logout()
}
```

Note the asymmetry that makes this safe:

- A **`401`** is an explicit "your token is no longer valid" — the right response
  is to clear it and prompt re-login.
- A **thrown fetch error** (server unreachable, transient network blip) is caught
  separately and **does not** log the student out — it keeps the existing data and
  simply waits for the next 30-second poll to recover. We never destroy a valid
  session just because the network hiccupped.

The toast store is `client/src/stores/toast.ts`; the same store backs the global
error handlers and unhandled-rejection boundary described in
[docs/ARCHITECTURE.md](./ARCHITECTURE.md).

---

## Roadmap Personalization Logic

Authentication answers *who you are*. **Personalization answers *what you see*.**
Two students who are both signed in can be shown different checklists and have a
different step highlighted as "current," and almost all of that decision-making
happens on the client in
[`client/src/composables/useProgress.ts`](../client/src/composables/useProgress.ts).

The composable does four things:

1. **Fetches** the full step catalog (`GET /api/steps`) and the student's progress
   + tags + term (`GET /api/steps/progress`).
2. **Filters** the catalog down to the steps that *apply* to this student, using
   their tags (`stepApplies`).
3. **Derives** a status (`not_started` / `in_progress` / `completed` / `waived`)
   for each applicable step (`deriveAllStepStatuses`).
4. **Polls** progress every 30 seconds so the UI stays fresh while the tab is open.

The two pure functions (`stepApplies`, `deriveAllStepStatuses`) are **exported
specifically so they can be unit-tested in isolation** — see the Vitest suites
under `client/src/**/*.test.ts` and [docs/TESTING.md](./TESTING.md).

### Data flow at a glance

```
 GET /api/steps           ──►  rawSteps  (the full catalog, all students see same rows)
 GET /api/steps/progress  ──►  progressMap  (step_id → {status, completed_at})
                               studentTags  (e.g. ["transfer", "first-gen"])
                               term

           rawSteps ──filter(stepApplies, studentTags)──► applicable steps
                                                              │
                            deriveAllStepStatuses(applicable, progressMap)
                                                              │
                                                              ▼
                                                  steps: StepWithStatus[]
                                                  (drives the timeline UI +
                                                   completion percentage)
```

### Step 1 — Which steps apply? `stepApplies(step, studentTags)`

Each step carries optional tag rules. `stepApplies` decides whether a given step
belongs on a given student's checklist by combining three fields:

| Field | Meaning |
| --- | --- |
| `excluded_tags` | If the student has **any** of these tags, the step is hidden — full stop. |
| `required_tags` | The tags a student must have for the step to apply. Empty/absent ⇒ applies to everyone. |
| `required_tag_mode` | How to evaluate `required_tags`: `'all'` (student must have every one) or `'any'` (at least one). Anything that isn't the literal string `'all'` is treated as `'any'`. |

A subtle but important implementation detail: `required_tags` and `excluded_tags`
may arrive **either as a JSON string or as an already-parsed array**, so the
function normalizes both. This is why the code does a `typeof === 'string'` check
before `JSON.parse`:

```ts
const requiredTags: string[] | null = step.required_tags
  ? typeof step.required_tags === 'string'
    ? JSON.parse(step.required_tags)
    : step.required_tags
  : null
```

The evaluation order encodes the precedence rules:

```ts
// 1. Exclusion wins over everything.
if (excludedTags && excludedTags.some((tag) => studentTags.includes(tag))) return false
// 2. No required tags ⇒ universal step, applies to all.
if (!requiredTags || requiredTags.length === 0) return true
// 3. Otherwise match by mode.
return requiredTagMode === 'all'
  ? requiredTags.every((tag) => studentTags.includes(tag))
  : requiredTags.some((tag) => studentTags.includes(tag))
```

**Worked examples** (student tags = `["transfer", "first-gen"]`):

| Step rules | Result | Why |
| --- | --- | --- |
| no tags at all | ✅ applies | universal step |
| `required_tags: ["transfer"]`, mode `any` | ✅ applies | has `transfer` |
| `required_tags: ["transfer", "honors"]`, mode `any` | ✅ applies | has at least one (`transfer`) |
| `required_tags: ["transfer", "honors"]`, mode `all` | ❌ hidden | missing `honors` |
| `required_tags: ["freshman"]`, mode `any` | ❌ hidden | has neither |
| `excluded_tags: ["transfer"]` | ❌ hidden | exclusion fires first, even if also required |

This is the core of personalization: a transfer student and a first-time freshman
walk away with genuinely different checklists from the same catalog, and a student
flagged with an exclusion tag (say, an athlete with a separate workflow) never sees
steps that don't apply to them.

### Step 2 — What is each step's status? `deriveAllStepStatuses(steps, progressMap)`

Given the **applicable** steps (already filtered and in `sort_order` from the API)
and the student's `progressMap`, this assigns each step a status. The key behavior
is the "current step" cursor — and it treats **required** and **optional** steps
very differently.

```ts
let foundCurrent = false
return steps.map((step) => {
  const progress = progressMap.get(step.id)
  if (step.is_optional === 1) {
    // Optional steps NEVER auto-advance. They show their saved status or 'not_started'.
    if (progress) return { ...step, status: progress.status }
    return { ...step, status: 'not_started' as const }
  }
  // Required steps:
  if (progress) return { ...step, status: progress.status }   // explicit DB status wins
  if (!foundCurrent) {
    foundCurrent = true
    return { ...step, status: 'in_progress' as const }         // first incomplete required = current
  }
  return { ...step, status: 'not_started' as const }           // everything after = not_started
})
```

The rules, in plain English:

- **A step with a saved progress row uses that exact status.** If the DB says
  `completed` / `in_progress` / `waived`, that's what's shown. This is the source of
  truth — the cursor logic only fills in the *gaps*.
- **Required steps without a saved row** follow a strict linear progression. The
  **first** required step that has no progress is marked `in_progress` (this is the
  student's "you are here"). Every required step *after* it is `not_started`. The
  `foundCurrent` flag is the latch that guarantees **exactly one** step is auto-set
  to `in_progress`.
- **Optional steps never participate in the cursor.** An optional step is either
  exactly what the DB says or `not_started` — it is never auto-promoted to
  `in_progress`, and crucially it does **not** consume the cursor. So an optional
  step sitting between two required steps won't "absorb" the current-step marker;
  the marker lands on the next *required* incomplete step.

Why design it this way? Required steps form the spine of the admissions journey, so
the app guides the student to the next required action automatically. Optional steps
(extra resources, recommended-but-not-mandatory tasks) are opt-in — the student
chooses to engage with them, so the app never nudges them as "the thing you must do
next."

**Worked example.** Required steps R1–R4 and one optional step O between R2 and R3.
The student has completed R1 and R2 (rows in `progressMap`):

| Step | `is_optional` | Has progress row? | Derived status |
| --- | --- | --- | --- |
| R1 | no | yes (`completed`) | `completed` |
| R2 | no | yes (`completed`) | `completed` |
| O | yes | no | `not_started` (does **not** become current) |
| R3 | no | no | `in_progress` ← the cursor lands here |
| R4 | no | no | `not_started` |

### Step 3 — Derived completion metrics

The composable exposes a set of computed values built from the derived steps. Note
that **completion percentage is measured against required steps only** — optional
steps never count toward "done":

```ts
const steps = computed(() =>
  deriveAllStepStatuses(rawSteps.value.filter((s) => stepApplies(s, studentTags.value)),
                        progressMap.value))
const requiredOnly   = computed(() => steps.value.filter((s) => s.is_optional !== 1))
const totalSteps     = computed(() => requiredOnly.value.length)
const completedCount = computed(() =>
  requiredOnly.value.filter((s) => s.status === 'completed' || s.status === 'waived').length)
const percentage     = computed(() =>
  totalSteps.value > 0 ? Math.round((completedCount.value / totalSteps.value) * 100) : 0)
const currentStep    = computed(() =>
  requiredOnly.value.find((s) => s.status === 'in_progress') || null)
const allComplete    = computed(() =>
  totalSteps.value > 0 && completedCount.value === totalSteps.value)
```

| Exposed value | Meaning |
| --- | --- |
| `steps` | The filtered + status-derived list that drives the timeline UI. |
| `totalSteps` | Count of **required** applicable steps. |
| `completedCount` | Required steps whose status is `completed` **or** `waived` (a waiver counts as done). |
| `percentage` | `round(completedCount / totalSteps * 100)`, or `0` when there are no required steps. |
| `currentStep` | The single required step marked `in_progress`, or `null`. |
| `allComplete` | `true` only when there is at least one required step and all are done. |

`waived` counting as complete is deliberate: if an admin waives a requirement for a
student, that student shouldn't be stuck below 100% forever.

### Step 4 — Fetching and polling

```ts
const POLL_INTERVAL = 30000 // 30 seconds
```

- **`fetchSteps()`** hits `GET /api/steps`. It sends the bearer token when present
  but works anonymously too (the public preview uses the same endpoint).
- **`fetchProgress()`** hits `GET /api/steps/progress` and is a **no-op without a
  token** (`if (!token.value) return`). It populates `progressMap`, `completedDates`,
  `studentTags`, and `term` from the response.
- **A `watch` on the auth `token`** (with `immediate: true`) drives the whole
  lifecycle: it refetches steps, tears down any existing poll, and — only while
  `isAuthenticated` — fetches progress once and then starts a `setInterval` poll
  every 30 s. On logout (token → `null`) the interval is cleared and no further
  polling happens.
- **`onUnmounted`** clears the interval so the poller never leaks past the
  component's life.

This means progress updates an admin makes (or an integration push from PeopleSoft)
show up in a student's open tab within ~30 seconds without a manual refresh.

```
 token changes ─► fetchSteps()
                  clear old interval
                  authenticated? ── yes ─► fetchProgress() once
                                          setInterval(fetchProgress, 30s)
                                  └─ no ─► loading = false (public view)
```

### The public (unauthenticated) preview

Before sign-in, the student sees a teaser rendered by
[`PublicRoadmapPreview.vue`](../client/src/components/PublicRoadmapPreview.vue),
which does **not** use the personalization logic above (there's no student yet, so
no tags and no progress). Instead it splits the public step catalog by the
`is_public` flag:

```ts
const publicSteps = computed(() => steps.value.filter((s) => s.is_public === 1))
const lockedSteps = computed(() => steps.value.filter((s) => s.is_public !== 1))
```

- **Public steps** (`is_public === 1`) render as interactive "preview" steps the
  visitor can open and read — these are the "Get Started / activate your account"
  steps.
- **Locked steps** (everything else) render compact and blurred under a "What's
  Ahead" heading with a fade-out gradient, to entice sign-in.

Once the student signs in, the app switches to the authenticated roadmap view,
which *does* run `useProgress` and shows the full personalized checklist.

---

## Azure AD SSO (Implemented — Disabled by Default)

Azure AD SSO is fully implemented but only activates when **both**
`AzureAd__ClientId` and `AzureAd__TenantId` are set on the server
(`AzureAdTokenValidator.IsConfigured`). When those are absent, the SSO endpoints
return `501` and the app falls back to dev login (students) / local login (admins).
The client gates its SSO buttons independently on its own
`VITE_AZURE_AD_CLIENT_ID` / `VITE_AZURE_AD_TENANT_ID` (`isAzureAdConfigured` in
`client/src/auth/msalConfig.ts`), so client and server must be configured together.

### Client

- **MSAL config and instance:** `client/src/auth/msalConfig.ts` exports:
  - `msalInstance` — a `PublicClientApplication`, or **`null`** when the Azure AD env
    vars are unset.
  - `isAzureAdConfigured` — `true` only when both client id and tenant id are set.
  - `loginRequest` — the requested scopes: `openid`, `profile`, `email`.
  - Authority is `https://login.microsoftonline.com/{tenantId}`; redirect URI comes
    from `VITE_AZURE_AD_REDIRECT_URI` (default `http://localhost:3000`); MSAL caches
    in `sessionStorage`.
- **Student auth flow:** [`client/src/stores/auth.ts`](../client/src/stores/auth.ts)
  (Pinia) initializes MSAL on app start, handles the redirect promise (for the
  redirect fallback path), then attempts `loginPopup`. If the popup is blocked
  (`popup_window_error`, `empty_window_error`, `popup_timeout`) it falls back to
  `loginRedirect`. The resulting `idToken` is POSTed to `/api/auth/sso`. A
  `user_cancelled` error is swallowed silently.
- **Admin auth flow:** `client/src/pages/admin/AdminLogin.vue` runs the same
  popup→redirect MSAL flow and POSTs the `idToken` to `/api/admin/auth/sso`.
- **UI entry points:** the student SSO button is gated by `isAzureAdConfigured`; the
  dev-login form in
  [`PublicRoadmapPreview.vue`](../client/src/components/PublicRoadmapPreview.vue) is
  governed by `showDevLogin` (see the next section).

### Server

- **Student endpoint:** `POST /api/auth/sso` — accepts a Microsoft ID token,
  validates it, then maps to a student record via the `azure_id` column (creating
  one on first login and auto-completing the `accepted` step; refreshing name/email
  thereafter).
- **Admin endpoint:** `POST /api/admin/auth/sso` — same validation, but maps to a
  **pre-existing** admin via `azure_id` then case-insensitive email; **never creates
  an admin**. Links `azure_id` on first login and refreshes the display name.
- **Validation:** `Api/Auth/AzureAdTokenValidator.cs` (see "How the JWT works").

### Dev-login visibility (off by default in production builds)

The dev-login name/email form is a development convenience: the server `404`s
`/api/auth/dev-login` in Production, and the client hides the form unless it's a dev
build or the operator explicitly opts in. From
[`PublicRoadmapPreview.vue`](../client/src/components/PublicRoadmapPreview.vue):

```ts
// Dev-login is a development convenience (the server 404s the endpoint in Production).
// Show it only in dev builds, or when explicitly opted in at build time.
const showDevLogin = import.meta.env.DEV || import.meta.env.VITE_ALLOW_DEV_LOGIN === 'true'
```

So in a normal production build (`npm run build`, where `import.meta.env.DEV` is
`false`), the form is **hidden unless** you set `VITE_ALLOW_DEV_LOGIN=true` at build
time. This is defense in depth — the server already blocks the endpoint, and the
client doesn't even render the form. The two layers are independent: even if someone
forces the form to show, the server still returns `404` in Production.

### Enabling SSO

Configuration uses ASP.NET Core's standard providers: environment variables with
the **`__` (double-underscore)** separator override `appsettings.json`. In local dev
these go in `Api/appsettings.Development.json`, your shell environment, or the
`api` service of `docker-compose.yml`. There is **no** server `.env` file — that was
a Node-only convention.

**Server environment variables:**

```
# Database — the app creates the DB + schema and seeds defaults on startup.
ConnectionStrings__Default=Server=localhost,1433;Database=csub_admissions;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False

# JWT secret for the app's own session tokens (server refuses to start if unset;
# must be >= 32 chars and not a known placeholder in Production).
Jwt__Secret=change-this-to-a-secure-random-string

# Azure AD SSO (leave unset to disable; SSO endpoints return 501).
AzureAd__ClientId=your-client-id
AzureAd__TenantId=your-tenant-id

# Seeded default admin (admin@csub.edu / admin123 in dev; password REQUIRED in production).
Admin__DefaultEmail=admin@csub.edu
Admin__DefaultPassword=change-me

# Break-glass local login (BOTH must be set to enable /admin/local-login).
LocalLogin__Username=localadmin
LocalLogin__Password=Local_Admin_2026!

# Seeded integration client (key REQUIRED in production; dev default 'dev-integration-key').
Integration__DefaultName=PeopleSoft Dev
Integration__DefaultKey=dev-integration-key

# 64-hex (32-byte) key encrypting stored API-check credentials.
ApiCheck__EncryptionKey=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef

# CORS allowed origin (closed unless set; usually unneeded behind the nginx proxy).
Cors__Origin=http://localhost:3000
```

> For the database connection string mechanics (SQL auth vs. Windows integrated
> auth, `Encrypt=True`, and the startup `Database:AutoCreate` / `Database:Seed`
> gates), and the transient-fault retry around SQL, see
> [docs/DEPLOYMENT.md](./DEPLOYMENT.md) and [docs/SETUP.md](./SETUP.md).

**Client (`client/.env.example`):**

```
# Azure AD SSO (optional; leave blank to use dev-login / local-login).
VITE_AZURE_AD_CLIENT_ID=your-client-id
VITE_AZURE_AD_TENANT_ID=your-tenant-id
VITE_AZURE_AD_REDIRECT_URI=http://localhost:3000

# Dev-login form visibility (set to 'false' to hide the form; it is also hidden
# automatically in any non-dev build unless this is 'true').
VITE_ALLOW_DEV_LOGIN=true

# Dev-only: where the Vite proxy forwards /api (default http://localhost:3001).
VITE_API_PROXY_TARGET=http://localhost:3001
```

---

## Deployment topology (three containers)

The rewrite deploys as **three containers** wired together by
`docker-compose.yml`. There is no longer a single all-in-one process — the SPA, the
API, and the database are each their own container, and the browser only ever talks
to the **web** container.

| Service | Image / build | Port (host:container) | Role |
| --- | --- | --- | --- |
| `web` | built from `client/` (Vue build served by **nginx-unprivileged**) | `3000:8080` | Serves the SPA; reverse-proxies `/api` → `api` |
| `api` | built from `Api/` (ASP.NET Core, non-root) | `127.0.0.1:8080:8080` | API only; applies schema + seed on startup (`CREATE DATABASE` gated to non-prod) |
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest` | `127.0.0.1:1433:1433` | SQL Server 2022 (persistent volume `csub_sqlserver_data`) |

**Same-origin, no CORS.** The browser loads everything from `http://localhost:3000`.
nginx (`client/nginx.conf.template`) proxies `location /api/` to the `api`
container (`proxy_pass ${API_URL}`, default `http://api:8080`) and falls back to
`index.html` for SPA routes. Because the SPA and the API share the `:3000` origin,
**CORS is normally not needed** — the client calls **relative `/api`** paths and
nothing is hard-coded. Set `Cors__Origin` on the API only if you intend to call it
directly from a different origin (bypassing the proxy).

Both app containers run **non-root** and ship Docker `HEALTHCHECK`s; compose gates
the `web` container on the `api` container's health. The API honors
`X-Forwarded-For` / `X-Forwarded-Proto` from the internal proxy via
`UseForwardedHeaders`, which is what makes the per-IP rate limits meaningful behind
nginx. Full production deployment to Windows Server is covered in
[docs/DEPLOYMENT.md](./DEPLOYMENT.md).

**Run the whole stack:**

```
docker compose up --build      # -> http://localhost:3000
```

**Run pieces individually** (each pulls its dependencies via `depends_on`):

```
docker compose up -d sqlserver          # just the database (:1433)
docker compose up -d --build api        # database + API (:8080)
docker compose up -d --build web        # full stack (:3000)
```

The `api` container waits for `sqlserver` to pass its health check, then
**auto-creates the database, schema, and seed data** — no manual DB setup. On
Apple Silicon, SQL Server runs as a `linux/amd64` image under Rancher / Docker
Desktop's VZ + Rosetta emulation (it has no native arm64 build).

Auth-relevant compose env vars live on the `api` service and are **required, with
no dev defaults**: `JWT_SECRET`, `ADMIN_DEFAULT_PASSWORD`, and `API_CHECK_ENCRYPTION_KEY`
use compose's `${VAR:?}` form, so `docker compose up` fails fast until you copy
`.env.example` to `.env` and set them. `LocalLogin__*` default to empty (break-glass
login disabled unless both are set). See the production checklist and
[docs/DEPLOYMENT.md](./DEPLOYMENT.md).

---

## Running it locally (without containers)

- **Backend:** `cd Api && dotnet run` — the API dev server listens on **`:3001`**.
  Build with `dotnet build`; run the xUnit tests in `tests/` with `dotnet test`.
  You still need SQL Server reachable via `ConnectionStrings__Default` (e.g.
  `docker compose up -d sqlserver`).
- **Frontend:** `cd client && npm install && npm run dev` — Vite serves on
  **`:3000`** and **proxies `/api`** to the backend. The proxy target is
  `VITE_API_PROXY_TARGET`, defaulting to `http://localhost:3001` (the `dotnet run`
  port). Build the production bundle with `npm run build`. Frontend unit tests run
  with `npm run test` (Vitest), lint with `npm run lint`, format with
  `npm run format` — see [docs/TESTING.md](./TESTING.md).

Because the client always calls **relative `/api`** (proxied by Vite in dev, by
nginx in containers), there is **no API URL hard-coded** anywhere in the client.

### Running the frontend on its own (e.g. a Windows desktop)

You can run just the Vue dev server on a separate machine and point it at a backend
elsewhere:

1. Install **Node.js LTS** from <https://nodejs.org>.
2. Open **PowerShell**.
3. `cd client`
4. `npm install`
5. `npm run dev`
6. Open <http://localhost:3000>.

To point the proxy at a backend that is **not** on `localhost:3001`, set the env var
before starting the dev server:

- **PowerShell:**
  ```powershell
  $env:VITE_API_PROXY_TARGET="http://<host>:<port>"; npm run dev
  ```
- **cmd.exe:**
  ```bat
  set VITE_API_PROXY_TARGET=http://<host>:<port> && npm run dev
  ```

Alternatively, use **Docker Desktop on Windows** and `docker compose up web` (it
pulls `api` + `sqlserver` via `depends_on`).

### Default credentials (dev only)

These work out of the box in non-production; **change them for any real deployment.**

- **Admin (email/password):** `admin@csub.edu` / `admin123` — seeded into `admin_users`
  by `Seeder.cs` on an empty database.
- **Break-glass (local) admin:** `localadmin` / `Local_Admin_2026!` — **not a seeded
  account**: it is enabled by the `LocalLogin:Username`/`LocalLogin:Password` config
  values (defaulted in `appsettings.Development.json`); no `admin_users` row exists,
  and `Database:Seed=false` does not disable it.
- **Integration key:** `dev-integration-key` (client `PeopleSoft Dev`) — seeded.

The seeder (`Api/Data/Seeder.cs`) enforces production guards:

- It **refuses to start** in production if `Admin__DefaultPassword` is unset
  (it will not seed the `admin123` default).
- It **skips** creating a default integration client in production unless
  `Integration__DefaultKey` is provided.

---

## Dropped vs. the old app

- **Legacy `X-API-Key` admin auth** — removed. Admins authenticate only via
  email/password, Azure AD SSO, or break-glass local login.
- **Dev activity simulator** — removed.
- **Dev-only mock API-check routes** — removed. (Real API-check config lives behind
  `[AdminAuth("sysadmin")]` on `Admin/ApiChecksController.cs`.)

---

## What's Needed for Production

> This is the auth/roadmap-specific checklist. The full server build-out (Windows
> Server, IIS/Kestrel, TLS, SQL Server provisioning, backups, service accounts) is
> in [docs/DEPLOYMENT.md](./DEPLOYMENT.md), and the broader hardening review is in
> [ENTERPRISE-READINESS.md](../ENTERPRISE-READINESS.md) / [SECURITY-AUDIT.md](../SECURITY-AUDIT.md).

### 1. Activate Azure AD SSO

- Register the application in the Azure AD portal and note the client ID and tenant
  ID.
- Set `AzureAd__ClientId` and `AzureAd__TenantId` on the server.
- Set `VITE_AZURE_AD_CLIENT_ID` and `VITE_AZURE_AD_TENANT_ID` in the client build
  env (both client and server must be configured together).
- Set `VITE_AZURE_AD_REDIRECT_URI` to the production frontend URL.
- Dev login is automatically disabled in production: `/api/auth/dev-login` returns
  `404` when `ASPNETCORE_ENVIRONMENT=Production`, **and** the dev-login form is
  hidden in any non-dev build unless `VITE_ALLOW_DEV_LOGIN=true` is set at build
  time. For belt-and-suspenders, leave `VITE_ALLOW_DEV_LOGIN` unset (or `false`).

### 2. Production Security Checklist

- [ ] Generate a strong random `Jwt__Secret` (at least 64 characters). The server
      refuses to start if it is unset, **and in Production also refuses to start if
      the secret is under 32 characters or matches a known placeholder** like
      `change-me` / `dev-only…` (`JwtService.IsKnownWeakSecret`).
- [ ] Set `Admin__DefaultPassword` to a strong password — the seeder **refuses to
      start** in production without it (it will not seed the `admin123` default).
- [ ] Set `Integration__DefaultKey` to a strong key — in production the seeder
      **skips** creating a default integration client unless one is provided.
- [ ] Register the application in Azure AD and configure redirect URIs.
- [ ] Set `Cors__Origin` to match the production domain **only if** the API is
      called directly from another origin. Behind the nginx same-origin proxy
      this is normally unnecessary; CORS is closed unless `Cors__Origin` is set.
- [ ] Set `ApiCheck__EncryptionKey` (64-character hex string = 32 bytes) for
      outbound API-check credential encryption.
- [ ] Review rate limiting: global **200 requests / 15 min per IP** (scoped to
      `/api`), `login` **10 / 15 min**, `breakGlass` **5 / 15 min**. Rate limiting
      can be disabled with `RateLimiting__Disabled=true` (the integration test suite
      uses this) — **never set it in production.** Because the limits are per-IP,
      confirm `UseForwardedHeaders` is trusting your proxy (see
      [docs/DEPLOYMENT.md](./DEPLOYMENT.md)) or every request will appear to come
      from the proxy's address.
- [ ] Serve everything over HTTPS at the proxy/ingress. The API already emits
      `Strict-Transport-Security`, `Cross-Origin-Opener-Policy`,
      `Cross-Origin-Resource-Policy`, and related security headers
      (`Api/Program.cs`).

### 3. Admin SSO Setup (when enabling Azure AD)

- Admin accounts must be **pre-created** in the Users tab (name, email, role) — SSO
  never creates them.
- On first SSO login, the admin's `azure_id` is linked automatically via email
  match; the display name is refreshed from the token.
- The break-glass login at `/admin/local-login` requires **both**
  `LocalLogin__Username` and `LocalLogin__Password` to be set; otherwise the endpoint
  returns `404` and the route is effectively invisible.
- [ ] Set `LocalLogin__Username` to a non-obvious username.
- [ ] Set `LocalLogin__Password` to a strong random password (at least 32
      characters).
- [ ] Confirm break-glass logins appear in the audit log (actor `break-glass`,
      actions `break_glass_login_success` / `break_glass_login_failure`).

### 4. Optional Enhancements

- **Token refresh:** implement silent token renewal before expiration using MSAL's
  `acquireTokenSilent`. The app session JWT currently has a hard 8-hour expiry with
  zero clock skew and no refresh, so a long session ends abruptly. Today the
  student-facing softening is the `401` handler in `useProgress.ts`, which toasts
  "Your session expired — please sign in again." and logs the student out cleanly.
- **Session management:** add a "remember me" option using `localStorage` instead
  of `sessionStorage` (today all tokens are tab-scoped and cleared on tab close).
- **Audit coverage:** extend audit logging beyond break-glass to cover all admin
  authentication events if a fuller security trail is required.

---

## Related docs

- [README.md](../README.md) — project overview and quick start.
- [docs/SETUP.md](./SETUP.md) — local environment setup.
- [docs/ARCHITECTURE.md](./ARCHITECTURE.md) — system architecture, frontend error
  handling, and the toast store.
- [docs/DEPLOYMENT.md](./DEPLOYMENT.md) — production deployment (Windows Server + SQL
  Server), TLS, secrets, backups.
- [docs/API-GUIDE.md](./API-GUIDE.md) — endpoint reference.
- [docs/LIBRARIES.md](./LIBRARIES.md) — dependency rationale (MSAL, Dapper, etc.).
- [docs/TESTING.md](./TESTING.md) — xUnit (API) and Vitest (client) test suites,
  including the unit tests for `stepApplies` / `deriveAllStepStatuses`.
- [ENTERPRISE-READINESS.md](../ENTERPRISE-READINESS.md), [AUDIT.md](../AUDIT.md),
  [SECURITY-AUDIT.md](../SECURITY-AUDIT.md) — hardening reviews.
