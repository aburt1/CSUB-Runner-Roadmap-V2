# Authentication Roadmap

This is the .NET / Vue rewrite of the original app. The REST contract (paths,
payloads, status codes) is preserved, but the implementation moved from
Express middleware to ASP.NET Core action filters and from `pg` to Dapper +
T-SQL on SQL Server. The client moved from React to Vue 3 + Pinia. MSAL is still
used on the client for Azure AD.

> **Removed in the rewrite:** the legacy `X-API-Key` admin auth path is gone.
> Admins now authenticate only via local email/password, Azure AD SSO, or the
> env-gated break-glass local login. The dev activity simulator and the dev-only
> mock API-check routes were also dropped.

## Current State

The application has three separate authentication systems, all issuing/validating
the app's own **HS256 JWT** with an **8-hour lifetime** (`Api/Auth/JwtService.cs`).

### Student Authentication
- **Method:** Development login (`POST /api/auth/dev-login`) or Azure AD SSO (`POST /api/auth/sso`)
- **How dev-login works:** Students enter their name and email. The server finds or creates a student record (and auto-completes the `accepted` step for the active term) and returns a JWT. Disabled in production — returns `404` when `ASPNETCORE_ENVIRONMENT=Production`.
- **Token claims:** `{ type: "student", studentId, email }`
- **Token storage (client):** `sessionStorage` under key `csub_token` (cleared on tab close)
- **Token lifetime:** 8 hours
- **Where:** `Api/Controllers/AuthController.cs`, `Api/Auth/StudentAuthAttribute.cs`; client `client/src/stores/auth.ts` (Pinia store), `client/src/auth/msalConfig.ts`
- **Note:** Azure AD SSO is fully implemented and activates automatically when configured (see below); otherwise the app falls back to dev login.

### Admin Authentication
- **Method:** Azure AD SSO (when configured) or email/password login
- **Login endpoint:** `POST /api/admin/auth/login` — email/password. Rate-limited (`login` policy: 10 requests / 15 min). Verifies the bcrypt `password_hash` (work factor 10, `Api/Auth/Passwords.cs`).
- **SSO endpoint:** `POST /api/admin/auth/sso` — validates the Azure AD ID token, looks up an admin by `azure_id`, then falls back to a case-insensitive email match. Returns `501` if Azure AD is not configured, `403` if no matching/active admin exists.
- **Break-glass / local login:** `POST /api/admin/auth/local-login` (UI at `/admin/local-login`) — hidden emergency login, gated by the `LocalLogin__Username` / `LocalLogin__Password` config. Returns `404` if either is unset. Rate-limited (`breakGlass` policy: 5 requests / 15 min) and audit-logged (success and failure) under actor `break-glass`. The break-glass session has **no DB row** and is issued the `sysadmin` role.
- **Change password:** `POST /api/admin/auth/change-password` — current admin changes their own password (minimum 12 characters).
- **Account linking:** First SSO login matches an admin by email, then writes `azure_id` for future logins. The display name is refreshed from the token on each SSO login.
- **No auto-creation:** Admin accounts must be pre-created in the Users tab. SSO handles authentication only — it never creates admins.
- **Token claims:** `{ type: "admin", adminId, role, email, displayName }`
- **Token storage (client):** `sessionStorage` under keys `csub_admin_token` / `csub_admin_user`
- **Token lifetime:** 8 hours
- **RBAC roles:** `viewer`, `admissions`, `admissions_editor`, `sysadmin`
- **Where:** `Api/Controllers/AdminAuthController.cs`, `Api/Auth/AdminAuthAttribute.cs`; client `client/src/pages/admin/AdminLogin.vue`, `client/src/pages/admin/AdminLocalLogin.vue`, `client/src/pages/admin/AdminPage.vue`

### Integration Authentication
- **Method:** Integration key (`X-Integration-Key` header or `Bearer` token)
- **How it works:** External systems authenticate with a bcrypt-verified integration key. If an `X-Client-Name` header is supplied, the server looks up that single client (avoids a bcrypt DoS); otherwise it scans up to 10 active clients.
- **Where:** `Api/Auth/IntegrationAuthAttribute.cs`
- **Default dev key:** `dev-integration-key` for client `PeopleSoft Dev` (seeded on startup; see KEY FACTS).

---

## RBAC Roles

The four roles are enforced by `[AdminAuth(...)]` on controllers/actions in
`Api/Auth/AdminAuthAttribute.cs`. `[AdminAuth]` with no arguments allows any
authenticated admin; passing roles restricts the action to those roles (a
missing role yields `403 Insufficient permissions`).

| Role | Capability (broadly) |
| --- | --- |
| `viewer` | Read-only access |
| `admissions` | Read + day-to-day admissions operations |
| `admissions_editor` | The above + content/config editing |
| `sysadmin` | Full access, including user management and integrations |

Role gates observed in the controllers include `[AdminAuth("sysadmin")]`,
`[AdminAuth("admissions_editor", "sysadmin")]`, and
`[AdminAuth("admissions", "admissions_editor", "sysadmin")]`.

---

## How the JWT works

Both the app's own session tokens and the inbound Azure AD tokens are validated
server-side; do not confuse the two.

- **App session token** (`Api/Auth/JwtService.cs`): HS256, signed with `Jwt__Secret`, 8-hour expiry, `ClockSkew = 0`. Validated by the auth filters, which require `Authorization: Bearer <token>` and a matching `type` claim (`student` vs `admin`). `Program.cs` sets `JwtSecurityTokenHandler.DefaultMapInboundClaims = false` so claim names stay verbatim (`type`, `studentId`, `adminId`, `role`, ...).
- **Azure AD ID token** (`Api/Auth/AzureAdTokenValidator.cs`): RS256, validated against the tenant's OpenID Connect metadata via `ConfigurationManager` (caches and auto-refreshes signing keys, handling rotation). Verifies issuer (`https://login.microsoftonline.com/{tenantId}/v2.0`) and audience (`AzureAd__ClientId`). Reads `oid`, `preferred_username`/`email`, and `name` claims.

If `Jwt__Secret` is missing, the server refuses to start.

---

## Azure AD SSO (Implemented — Disabled by Default)

Azure AD SSO is fully implemented but only activates when both
`AzureAd__ClientId` and `AzureAd__TenantId` are set on the server
(`AzureAdTokenValidator.IsConfigured`). When those are absent, the SSO endpoints
return `501` and the app falls back to dev login (students) / local login (admins).

### Client
- **MSAL config and instance:** `client/src/auth/msalConfig.ts` — exports `msalInstance` (a `PublicClientApplication`, or `null` when Azure AD env vars are unset), `isAzureAdConfigured`, and the `loginRequest` scopes (`openid`, `profile`, `email`). MSAL caches in `sessionStorage`. Authority is `https://login.microsoftonline.com/{tenantId}`.
- **Student auth flow:** `client/src/stores/auth.ts` (Pinia) initializes MSAL, handles the redirect promise, then attempts `loginPopup` and falls back to `loginRedirect` if the popup is blocked. The returned `idToken` is POSTed to `/api/auth/sso`.
- **Admin auth flow:** `client/src/pages/admin/AdminLogin.vue` runs the same popup→redirect MSAL flow and POSTs the `idToken` to `/api/admin/auth/sso`.
- **UI entry points:** the student SSO button is gated by `isAzureAdConfigured`; the dev-login form in `client/src/components/PublicRoadmapPreview.vue` is hidden when `VITE_ALLOW_DEV_LOGIN === 'false'`.

### Server
- **Student endpoint:** `POST /api/auth/sso` — accepts a Microsoft ID token, validates it, then maps to a student record via the `azure_id` column (creating one on first login, auto-completing the `accepted` step).
- **Admin endpoint:** `POST /api/admin/auth/sso` — same validation, but maps to a **pre-existing** admin via `azure_id` then email; never creates an admin.
- **Validation:** `Api/Auth/AzureAdTokenValidator.cs` (see "How the JWT works").

### Enabling SSO

Configuration uses ASP.NET Core's standard providers: environment variables with
the `__` (double-underscore) separator override `appsettings.json`. In local dev
these go in `Api/appsettings.Development.json`, your shell environment, or the
`docker-compose.yml` `app` service. There is **no** server `.env` file.

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

# CORS allowed origin (closed unless set).
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
```

---

## Running it

- **Backend:** `dotnet run` (from `Api/`), tests `dotnet test` (xUnit, `tests/`), build `dotnet build`. API dev server listens on `:3001`.
- **Client:** `npm install` then `npm run dev` (Vite, `:3000`, proxies `/api` to the API) or `npm run build`.
- **Database:** `docker compose up -d sqlserver`. SQL Server runs as a `linux/amd64` container — Apple Silicon needs Rancher/Docker VZ + Rosetta. **No manual DB setup:** the app auto-creates the database, schema, and seed data on startup.
- **Full stack (built app + DB):** `docker compose up --build`. Production runs as a single process on `:8080` serving the SPA + API.

### Default credentials (dev only)
- **Admin:** `admin@csub.edu` / `admin123`
- **Break-glass (local) admin:** `localadmin` / `Local_Admin_2026!`
- **Integration key:** `dev-integration-key`

---

## What's Needed for Production

### 1. Activate Azure AD SSO
- Register the application in the Azure AD portal and note the client ID and tenant ID.
- Set `AzureAd__ClientId` and `AzureAd__TenantId` on the server.
- Set `VITE_AZURE_AD_CLIENT_ID` and `VITE_AZURE_AD_TENANT_ID` in the client build env.
- Set `VITE_AZURE_AD_REDIRECT_URI` to the production frontend URL.
- Dev login is automatically disabled in production (`/api/auth/dev-login` returns `404` when `ASPNETCORE_ENVIRONMENT=Production`); optionally also set `VITE_ALLOW_DEV_LOGIN=false` to hide the form.

### 2. Production Security Checklist
- [ ] Generate a strong random `Jwt__Secret` (at least 64 characters).
- [ ] Set `Admin__DefaultPassword` to a strong password — the seeder **refuses to start** in production without it (it will not seed the `admin123` default).
- [ ] Set `Integration__DefaultKey` to a strong key — in production the seeder skips creating a default integration client unless one is provided.
- [ ] Register the application in Azure AD and configure redirect URIs.
- [ ] Set `Cors__Origin` to match the production domain (CORS is closed unless set).
- [ ] Set `ApiCheck__EncryptionKey` (64-character hex string) for outbound API credential encryption.
- [ ] Review rate limiting: global 200 requests / 15 min per IP; `login` 10 / 15 min; `breakGlass` 5 / 15 min. (Rate limiting can be disabled with `RateLimiting__Disabled=true`, which the integration test suite uses — never set this in production.)

### 3. Admin SSO Setup (when enabling Azure AD)
- Admin accounts must be pre-created in the Users tab (name, email, role).
- On first SSO login, the admin's `azure_id` is linked automatically via email match; the display name is refreshed from the token.
- The break-glass login at `/admin/local-login` requires both `LocalLogin__Username` and `LocalLogin__Password` to be set; otherwise the endpoint returns `404`.
- [ ] Set `LocalLogin__Username` to a non-obvious username.
- [ ] Set `LocalLogin__Password` to a strong random password (at least 32 characters).

### 4. Optional Enhancements
- Token refresh: implement silent token renewal before expiration using MSAL's `acquireTokenSilent`.
- Session management: add a "remember me" option using `localStorage` instead of `sessionStorage`.
