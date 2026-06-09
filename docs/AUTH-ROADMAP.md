# Authentication Roadmap

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

> **Removed in the rewrite.** The legacy `X-API-Key` admin auth path is gone.
> Admins now authenticate **only** via local email/password, Azure AD SSO, or the
> env-gated break-glass local login. The dev activity simulator and the dev-only
> mock API-check routes were also dropped. See "Dropped vs. the old app" below.

---

## Current State

The application has **three separate authentication systems**. All three issue or
validate the app's own **HS256 JWT** with an **8-hour lifetime**
(`Api/Auth/JwtService.cs`). They differ in who they authenticate and how the
credential is presented:

| System | Who | Credential | Server entry point |
| --- | --- | --- | --- |
| Student | Prospective students | Dev login (name + email) or Azure AD SSO | `Api/Controllers/AuthController.cs` |
| Admin | Staff | Email/password, Azure AD SSO, or break-glass | `Api/Controllers/AdminAuthController.cs` |
| Integration | External systems (e.g. PeopleSoft) | Integration key (bcrypt-verified) | `Api/Auth/IntegrationAuthAttribute.cs` |

### Student Authentication

- **Method:** Development login (`POST /api/auth/dev-login`) or Azure AD SSO
  (`POST /api/auth/sso`).
- **How dev-login works:** Students enter their name and email. The server finds
  the student by email or creates a new student record, **auto-completes the
  `accepted` step** for the active term, and returns a JWT. This endpoint is
  **disabled in production** — it returns `404 { "error": "Not found" }` when
  `ASPNETCORE_ENVIRONMENT=Production`.
- **How SSO works:** The client obtains a Microsoft ID token via MSAL and POSTs it
  to `/api/auth/sso`. The server validates the token, then maps it to a student
  via the `azure_id` column — creating the student (and auto-completing `accepted`)
  on first login, or refreshing `display_name`/`email` on subsequent logins.
- **Token claims:** `{ type: "student", studentId, email }`.
- **Token storage (client):** `sessionStorage` under key **`csub_token`** (cleared
  on tab close).
- **Token lifetime:** 8 hours.
- **Where:** `Api/Controllers/AuthController.cs`, `Api/Auth/StudentAuthAttribute.cs`;
  client `client/src/stores/auth.ts` (Pinia store), `client/src/auth/msalConfig.ts`,
  `client/src/components/PublicRoadmapPreview.vue`.
- **Note:** Azure AD SSO is fully implemented and activates automatically when
  configured (see below); when it is not configured the SSO endpoint returns
  `501` and the app falls back to dev login.

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
- **Where:** `Api/Controllers/AdminAuthController.cs`, `Api/Auth/AdminAuthAttribute.cs`;
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
display labels and the role picker in the Users tab.

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
| `GET /api/health` | none (anonymous) |

---

## How the JWT works

Both the app's own session tokens and the inbound Azure AD tokens are validated
server-side — they are **different tokens with different algorithms and different
validators**; do not confuse the two.

- **App session token** (`Api/Auth/JwtService.cs`): **HS256**, signed with the
  symmetric `Jwt__Secret`, **8-hour** expiry, `ClockSkew = TimeSpan.Zero` (no
  leeway — an expired token is rejected the instant it expires). Issuer and
  audience are **not** validated (`ValidateIssuer = false`,
  `ValidateAudience = false`); only the signature, lifetime, and algorithm are.
  Validated by the `StudentAuth` / `AdminAuth` filters, which require
  `Authorization: Bearer <token>` and a matching `type` claim (`student` vs
  `admin`). `Program.cs` sets `JwtSecurityTokenHandler.DefaultMapInboundClaims =
  false` so claim names stay **verbatim** (`type`, `studentId`, `adminId`, `role`,
  `email`, `displayName`) instead of being remapped to long XML URIs.
- **Azure AD ID token** (`Api/Auth/AzureAdTokenValidator.cs`): **RS256**, validated
  against the tenant's OpenID Connect metadata via `ConfigurationManager`, which
  fetches and caches the signing keys from
  `https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration`
  and auto-refreshes them (handling key rotation transparently). Verifies the
  issuer (`https://login.microsoftonline.com/{tenantId}/v2.0`) and audience
  (`AzureAd__ClientId`), plus lifetime and signature. Reads the `oid` (object id,
  **required**), `preferred_username` (falling back to `email`), and `name` claims.

If `Jwt__Secret` is missing, the server **refuses to start** (the `JwtService`
constructor throws `InvalidOperationException`).

### Token lifecycle (student)

1. Client calls `devLogin(name, email)` or `ssoLogin()` in
   `client/src/stores/auth.ts`.
2. On success the returned token is written to `sessionStorage['csub_token']` and
   held in the Pinia store's `token` ref.
3. On app start, `init()` reads any existing `csub_token`, calls `GET /api/auth/me`
   with it, and clears it if the server rejects it. If the server is merely
   unreachable, the token is kept and retried later.
4. `logout()` removes `csub_token`, clears the store, and clears the MSAL cache
   when Azure AD is configured.

### Token lifecycle (admin)

The admin pages (`AdminPage.vue`, `AdminLogin.vue`, `AdminLocalLogin.vue`) store
the token in `sessionStorage['csub_admin_token']` and the cached user profile in
`sessionStorage['csub_admin_user']`. A rejected token clears both keys and returns
to the login screen.

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
- **Student auth flow:** `client/src/stores/auth.ts` (Pinia) initializes MSAL on app
  start, handles the redirect promise (for the redirect fallback path), then
  attempts `loginPopup`. If the popup is blocked (`popup_window_error`,
  `empty_window_error`, `popup_timeout`) it falls back to `loginRedirect`. The
  resulting `idToken` is POSTed to `/api/auth/sso`. A `user_cancelled` error is
  swallowed silently.
- **Admin auth flow:** `client/src/pages/admin/AdminLogin.vue` runs the same
  popup→redirect MSAL flow and POSTs the `idToken` to `/api/admin/auth/sso`.
- **UI entry points:** the student SSO button is gated by `isAzureAdConfigured`; the
  dev-login form in `client/src/components/PublicRoadmapPreview.vue` is hidden when
  `VITE_ALLOW_DEV_LOGIN === 'false'`.

### Server

- **Student endpoint:** `POST /api/auth/sso` — accepts a Microsoft ID token,
  validates it, then maps to a student record via the `azure_id` column (creating
  one on first login and auto-completing the `accepted` step; refreshing name/email
  thereafter).
- **Admin endpoint:** `POST /api/admin/auth/sso` — same validation, but maps to a
  **pre-existing** admin via `azure_id` then case-insensitive email; **never creates
  an admin**. Links `azure_id` on first login and refreshes the display name.
- **Validation:** `Api/Auth/AzureAdTokenValidator.cs` (see "How the JWT works").

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

# JWT secret for the app's own session tokens (server refuses to start if unset).
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

**Client (`client/.env.example`):**

```
# Azure AD SSO (optional; leave blank to use dev-login / local-login).
VITE_AZURE_AD_CLIENT_ID=your-client-id
VITE_AZURE_AD_TENANT_ID=your-tenant-id
VITE_AZURE_AD_REDIRECT_URI=http://localhost:3000

# Dev-login form visibility (set to 'false' to hide the form).
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
| `web` | built from `client/` (Vue build served by **nginx**) | `3000:80` | Serves the SPA; reverse-proxies `/api` → `api` |
| `api` | built from `Api/` (ASP.NET Core) | `8080:8080` | API only; auto-creates DB + schema + seed on startup |
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest` | `1433:1433` | SQL Server 2022 (persistent volume `csub_sqlserver_data`) |

**Same-origin, no CORS.** The browser loads everything from `http://localhost:3000`.
nginx (`client/nginx.conf.template`) proxies `location /api/` to the `api`
container (`proxy_pass ${API_URL}`, default `http://api:8080`) and falls back to
`index.html` for SPA routes. Because the SPA and the API share the `:3000` origin,
**CORS is normally not needed** — the client calls **relative `/api`** paths and
nothing is hard-coded. Set `Cors__Origin` on the API only if you intend to call it
directly from a different origin (bypassing the proxy).

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

Auth-relevant compose env vars live on the `api` service and default to the dev
values (`Jwt__Secret`, `Admin__DefaultEmail/Password`, `LocalLogin__Username/Password`,
`ApiCheck__EncryptionKey`). **Override every secret for a real deployment** (see the
production checklist).

---

## Running it locally (without containers)

- **Backend:** `cd Api && dotnet run` — the API dev server listens on **`:3001`**.
  Build with `dotnet build`; run the xUnit tests in `tests/` with `dotnet test`.
  You still need SQL Server reachable via `ConnectionStrings__Default` (e.g.
  `docker compose up -d sqlserver`).
- **Frontend:** `cd client && npm install && npm run dev` — Vite serves on
  **`:3000`** and **proxies `/api`** to the backend. The proxy target is
  `VITE_API_PROXY_TARGET`, defaulting to `http://localhost:3001` (the `dotnet run`
  port). Build the production bundle with `npm run build`.

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

These are seeded automatically in non-production; **change them for any real
deployment.**

- **Admin (email/password):** `admin@csub.edu` / `admin123`
- **Break-glass (local) admin:** `localadmin` / `Local_Admin_2026!`
- **Integration key:** `dev-integration-key` (client `PeopleSoft Dev`)

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

### 1. Activate Azure AD SSO

- Register the application in the Azure AD portal and note the client ID and tenant
  ID.
- Set `AzureAd__ClientId` and `AzureAd__TenantId` on the server.
- Set `VITE_AZURE_AD_CLIENT_ID` and `VITE_AZURE_AD_TENANT_ID` in the client build
  env (both client and server must be configured together).
- Set `VITE_AZURE_AD_REDIRECT_URI` to the production frontend URL.
- Dev login is automatically disabled in production (`/api/auth/dev-login` returns
  `404` when `ASPNETCORE_ENVIRONMENT=Production`); optionally also set
  `VITE_ALLOW_DEV_LOGIN=false` to hide the form.

### 2. Production Security Checklist

- [ ] Generate a strong random `Jwt__Secret` (at least 64 characters). The server
      refuses to start if it is unset.
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
      uses this) — **never set it in production.**
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
  zero clock skew and no refresh, so a long session ends abruptly.
- **Session management:** add a "remember me" option using `localStorage` instead
  of `sessionStorage` (today all tokens are tab-scoped and cleared on tab close).
- **Audit coverage:** extend audit logging beyond break-glass to cover all admin
  authentication events if a fuller security trail is required.
