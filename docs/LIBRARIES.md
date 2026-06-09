# Libraries & Dependencies

This is the catalogue of every third-party library the converted CSUB Runner Roadmap depends on,
grouped by where it runs and what it does. For each entry you get the **name**, the **version as
declared in the manifest**, a one-line **"what it does"**, **why this project uses it**, and a link to
its **official documentation**.

The goal is that anyone picking up the repo can answer two questions quickly: *"what is this package
and why is it here?"* and *"where do I go to read more?"* — without having to reverse-engineer it from
the code.

## Where the versions come from

Everything below is taken from the actual manifests in the repo, so it stays honest:

| Stack | Manifest | What it pins |
|-------|----------|--------------|
| Backend runtime | [`Api/Api.csproj`](../Api/Api.csproj) | NuGet `PackageReference` entries (exact versions) |
| Backend tests | [`tests/Api.IntegrationTests/Api.IntegrationTests.csproj`](../tests/Api.IntegrationTests/Api.IntegrationTests.csproj) | NuGet `PackageReference` entries (exact versions) |
| Frontend | [`client/package.json`](../client/package.json) | npm `dependencies` / `devDependencies` (semver ranges) |
| Infra | [`docker-compose.yml`](../docker-compose.yml), [`Api/Dockerfile`](../Api/Dockerfile), [`client/Dockerfile`](../client/Dockerfile) | container base images |

> **NuGet versions** are exact — `.csproj` `PackageReference` versions are the resolved version.
> **npm versions** are the semver **ranges** from `package.json` (`^x.y.z`); the exact version that
> `npm ci` actually installs is locked in [`client/package-lock.json`](../client/package-lock.json).
> Where the resolved lockfile version differs from the declared range, this doc notes the resolved
> version in parentheses.

> **Target frameworks / runtimes:** the API and the test project both target **.NET 10** (`net10.0`).
> The frontend is an ESM project (`"type": "module"`) built with Vite. The frontend Docker build runs
> on **Node 22 (alpine)**; local dev only needs Node.js LTS.

---

## At a Glance

| Layer | Key technologies |
|-------|------------------|
| **Backend runtime** | ASP.NET Core (.NET 10), Dapper, Microsoft.Data.SqlClient, BCrypt.Net-Next, System.IdentityModel.Tokens.Jwt, Microsoft.IdentityModel.Protocols.OpenIdConnect, Microsoft.AspNetCore.OpenApi, FluentValidation |
| **Backend tests** | xUnit, Microsoft.AspNetCore.Mvc.Testing, Microsoft.NET.Test.Sdk, coverlet |
| **Frontend runtime** | Vue 3, vue-router, Pinia, Tiptap, Chart.js + vue-chartjs, vuedraggable, vue3-emoji-picker, @azure/msal-browser, DOMPurify, canvas-confetti |
| **Frontend build/dev** | Vite, @vitejs/plugin-vue, TypeScript, vue-tsc, Tailwind CSS + @tailwindcss/typography, PostCSS, Autoprefixer |
| **Frontend test (installed)** | @vue/test-utils, jsdom *(present but no suite wired up yet)* |
| **Infrastructure** | SQL Server 2022, nginx, Docker / Docker Compose |

---

## Backend runtime (`Api/Api.csproj`)

These ship inside the `api` container (and run under `dotnet run` in local dev). The project SDK is
`Microsoft.NET.Sdk.Web`, which brings the ASP.NET Core framework itself (Kestrel, MVC controllers,
routing, authentication/authorization, rate limiting, static files) as part of the shared framework —
there is no separate "ASP.NET Core" NuGet package to pin. The `PackageReference` entries below are the
pieces layered on top of that framework.

### ASP.NET Core (.NET 10) — the web framework
- **Version:** `net10.0` (the `Microsoft.NET.Sdk.Web` shared framework; see `<TargetFramework>` in `Api.csproj`)
- **What it does:** Cross-platform framework for building HTTP APIs — Kestrel server, controller-based
  routing, model binding, middleware pipeline, dependency injection, JWT-bearer auth, output caching,
  rate limiting, and static-file/SPA-fallback serving.
- **Why this project uses it:** It is the backend. Every endpoint under `/api/*` is an ASP.NET Core
  controller; `Api/Program.cs` wires up the middleware pipeline, the JWT auth scheme, the per-IP login
  rate limiter, security headers, the SPA fallback, and the startup hook that auto-creates the database,
  applies the schema, and runs the seed. It is the direct replacement for the old Node.js/Express server.
- **Docs:** <https://learn.microsoft.com/aspnet/core/>

### Dapper
- **Version:** `2.1.79`
- **What it does:** A lightweight "micro-ORM" — a set of extension methods on `IDbConnection`
  (`QueryAsync`, `ExecuteAsync`, `QuerySingleAsync`, …) that map SQL results onto C# objects.
- **Why this project uses it:** The data layer is hand-written T-SQL, not an ORM-generated query
  builder. Dapper executes those queries and materialises the rows into DTOs with minimal overhead and
  no hidden SQL generation, which is exactly what the PostgreSQL → SQL Server port needed: full control
  over dialect-specific SQL while staying terse. Used throughout `Api/Controllers/**` and the data
  helpers (e.g. `AuthController`, `IntegrationsController`, `Admin/*Controller`).
- **Docs:** <https://github.com/DapperLib/Dapper>

### Microsoft.Data.SqlClient
- **Version:** `7.0.1`
- **What it does:** The official ADO.NET driver for Microsoft SQL Server / Azure SQL — provides
  `SqlConnection`, `SqlCommand`, connection pooling, and TLS negotiation.
- **Why this project uses it:** It is the actual database connection Dapper runs on top of. The app
  opens `SqlConnection`s against the `csub_admissions` database using the `ConnectionStrings__Default`
  connection string. It is also referenced directly by the test project so the test fixture can connect
  to `master` to drop/rebuild the dedicated test database. Replaces the old `pg` PostgreSQL driver.
- **Docs:** <https://learn.microsoft.com/sql/connect/ado-net/microsoft-ado-net-sql-server>

### BCrypt.Net-Next
- **Version:** `4.2.0`
- **What it does:** Pure-.NET implementation of the bcrypt adaptive password-hashing function.
- **Why this project uses it:** Admin passwords and integration API keys are stored as bcrypt hashes.
  `Api/Auth/Passwords.cs` wraps it as `Hash`/`Verify` using **work factor 10** — deliberately the same
  cost as the old Node `bcrypt` so existing hashes and the seeded admin (`admin@csub.edu` / `admin123`)
  keep verifying after the port.
- **Docs:** <https://github.com/BcryptNet/bcrypt.net>

### System.IdentityModel.Tokens.Jwt
- **Version:** `8.19.1`
- **What it does:** Creates and validates JSON Web Tokens — `JwtSecurityTokenHandler`,
  `JwtSecurityToken`, `TokenValidationParameters`, signing credentials.
- **Why this project uses it:** It issues and validates the app's own session tokens. `Api/Auth/JwtService.cs`
  signs **HS256** tokens with the `Jwt:Secret` key and an 8-hour lifetime, embedding the same claim shape
  the old server used (`type`/`studentId`/`email` for students; `type`/`adminId`/`role`/`email`/`displayName`
  for admins) so clients keep working unchanged. The same handler validates incoming bearer tokens in the
  custom `StudentAuthAttribute` / `AdminAuthAttribute` filters.
- **Docs:** <https://learn.microsoft.com/dotnet/api/system.identitymodel.tokens.jwt>

### Microsoft.IdentityModel.Protocols.OpenIdConnect
- **Version:** `8.19.1`
- **What it does:** Fetches and caches an OpenID Connect provider's discovery document and JWKS signing
  keys (`ConfigurationManager<OpenIdConnectConfiguration>`, `OpenIdConnectConfigurationRetriever`), and
  automatically refreshes them on key rotation.
- **Why this project uses it:** Azure AD SSO. `Api/Auth/AzureAdTokenValidator.cs` uses it to pull the
  tenant's signing keys from `https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration`,
  then validates incoming Azure AD **ID tokens** (issuer, audience = `AzureAd:ClientId`, lifetime, RS256)
  and extracts the `oid`/`email`/`name` claims. SSO is optional and only active when `AzureAd:ClientId`
  and `AzureAd:TenantId` are configured. Mirrors the old `server/utils/azureAdToken.ts`.
- **Docs:** <https://learn.microsoft.com/dotnet/api/microsoft.identitymodel.protocols.openidconnect>

### FluentValidation
- **Version:** `12.1.1`
- **What it does:** A strongly-typed validation library — define `AbstractValidator<T>` classes with
  fluent `RuleFor(...)` chains instead of attribute-based validation.
- **Why this project uses it:** It is declared as a dependency to back request-payload validation in the
  controllers. *Note for maintainers:* at the time of writing it is referenced in `Api.csproj` but is
  **not yet wired into any source file** — input validation in the controllers is currently done inline.
  The package is in place for when that validation is migrated to dedicated validators.
- **Docs:** <https://docs.fluentvalidation.net/>

### Microsoft.AspNetCore.OpenApi
- **Version:** `10.0.5`
- **What it does:** Generates an OpenAPI (Swagger) document for the API's endpoints at runtime.
- **Why this project uses it:** Developer convenience / API discoverability. `Api/Program.cs` calls
  `builder.Services.AddOpenApi()` and exposes the document via `app.MapOpenApi()` **only in the
  Development environment** (`app.Environment.IsDevelopment()`), so the schema is available during local
  dev but not served in production containers.
- **Docs:** <https://learn.microsoft.com/aspnet/core/fundamentals/openapi/aspnetcore-openapi>

---

## Backend tests (`tests/Api.IntegrationTests/Api.IntegrationTests.csproj`)

The test project targets `net10.0`, references `Api/Api.csproj`, and hosts the real API in-process
against a real SQL Server test database. See [`TESTING.md`](./TESTING.md) for the full strategy.

### xunit
- **Version:** `2.9.3`
- **What it does:** The xUnit.net unit/integration testing framework — `[Fact]`, `[Theory]`,
  `[Collection]`, `Assert`, and the shared-fixture (`ICollectionFixture<T>`) model.
- **Why this project uses it:** It is the test framework for the entire suite. Every test class is a
  `[Collection("api")]` class sharing the single `WebAppFixture`. (The companion runner
  **`xunit.runner.visualstudio` `3.1.4`** is also referenced so the tests are discoverable by
  `dotnet test`, Visual Studio, and VS Code Test Explorer.)
- **Docs:** <https://xunit.net/>

### Microsoft.AspNetCore.Mvc.Testing
- **Version:** `10.0.8`
- **What it does:** Provides `WebApplicationFactory<TEntryPoint>`, which boots an ASP.NET Core app
  in-memory and hands back `HttpClient`s wired to its real HTTP pipeline (no network socket needed).
- **Why this project uses it:** It is how the suite hosts the actual API in-process. `WebAppFixture`
  subclasses `WebApplicationFactory<Program>` (which is why `Api/Program.cs` exposes a
  `public partial class Program`), overrides config to point at `csub_admissions_test`, disables rate
  limiting, and exposes typed `Anonymous()` / `Admin()` / `Integration()` / `StudentAsync()` clients.
- **Docs:** <https://learn.microsoft.com/aspnet/core/test/integration-tests>

### Microsoft.NET.Test.Sdk
- **Version:** `17.14.1`
- **What it does:** The MSBuild + VSTest plumbing that makes a project a runnable test project and
  enables `dotnet test`.
- **Why this project uses it:** Required for the test project to be discovered and executed by
  `dotnet test` and IDE test runners. Without it, xUnit tests would not run.
- **Docs:** <https://github.com/microsoft/vstest>

### coverlet.collector
- **Version:** `6.0.4`
- **What it does:** A cross-platform code-coverage data collector for .NET that plugs into the
  `dotnet test` / VSTest pipeline.
- **Why this project uses it:** Lets `dotnet test --collect:"XPlat Code Coverage"` emit coverage reports
  for the API integration suite without any extra tooling.
- **Docs:** <https://github.com/coverlet-coverage/coverlet>

---

## Frontend runtime (`client/package.json` → `dependencies`)

These are bundled into the production Vue build that the `web` container serves via nginx.

### vue
- **Version:** `^3.5.34` (resolved `3.5.35`)
- **What it does:** The Vue 3 progressive UI framework — reactivity, the Composition API
  (`<script setup>`), Single-File Components (`.vue`), and the rendering runtime.
- **Why this project uses it:** It is the frontend framework — the direct replacement for React. The
  whole SPA (student roadmap, admin dashboard) is built from Vue SFCs; `client/src/main.ts` creates the
  app with `createApp(App)` and installs the router and Pinia.
- **Docs:** <https://vuejs.org/>

### vue-router
- **Version:** `^4.6.4`
- **What it does:** The official client-side router for Vue 3 — maps URL paths to components, supports
  nested routes and navigation guards.
- **Why this project uses it:** Drives all in-app navigation (the public/student roadmap and the
  `/admin` dashboard routes). Configured in `client/src/router/index.ts` with `createRouter`. The nginx
  config and the API's `MapFallbackToFile("index.html")` both fall back to `index.html` so deep links to
  client-side routes load correctly.
- **Docs:** <https://router.vuejs.org/>

### pinia
- **Version:** `^3.0.4`
- **What it does:** The official state-management library for Vue 3 — typed, composable stores via
  `defineStore`.
- **Why this project uses it:** Holds shared app state, most importantly the auth store
  (`client/src/stores/auth.ts`): the current student/admin session, the JWT, and the MSAL-driven SSO
  flow. Installed in `client/src/main.ts` with `createPinia()`. Replaces the old React Context-based
  `AuthProvider`.
- **Docs:** <https://pinia.vuejs.org/>

### @tiptap/vue-3 (+ extensions)
- **Versions:** `@tiptap/vue-3 ^3.26.0`, `@tiptap/starter-kit ^3.26.0`, `@tiptap/pm ^3.26.0`,
  `@tiptap/extension-link ^3.26.0`, `@tiptap/extension-underline ^3.26.0`
- **What it does:** Tiptap is a headless rich-text editor built on ProseMirror. `@tiptap/vue-3` is the
  Vue binding (`useEditor`, `<EditorContent>`); `@tiptap/starter-kit` bundles the common nodes/marks
  (bold, italic, headings, lists…); `@tiptap/pm` is the ProseMirror core it sits on; the `link` and
  `underline` extensions add those marks.
- **Why this project uses it:** Powers the admin rich-text editor used to author step descriptions
  (`client/src/pages/admin/RichTextEditor.vue`). The HTML it produces is sanitised with DOMPurify before
  being rendered to students. Replaces the old React Tiptap integration.
- **Docs:** <https://tiptap.dev/docs>

### chart.js + vue-chartjs
- **Versions:** `chart.js ^4.5.1`, `vue-chartjs ^5.3.3`
- **What it does:** Chart.js is a canvas-based charting library; vue-chartjs is the thin Vue 3 wrapper
  that exposes Chart.js chart types as Vue components.
- **Why this project uses it:** Renders the admin analytics dashboard charts —
  `client/src/pages/admin/AnalyticsTab.vue` and the chart components under
  `client/src/pages/admin/charts/` (cohort distribution, completion velocity, step completion,
  bottlenecks, stalled students). Replaces the old Recharts dependency.
- **Docs:** Chart.js <https://www.chartjs.org/docs/latest/> · vue-chartjs <https://vue-chartjs.org/>

### vuedraggable
- **Version:** `^4.1.0`
- **What it does:** A Vue component wrapper around SortableJS for drag-and-drop reordering of lists.
- **Why this project uses it:** Lets admins reorder steps by dragging them, which maps to the
  `PUT /api/admin/steps/reorder` endpoint. Used in `client/src/pages/admin/StepsTab.vue` and
  `client/src/pages/admin/TermStepsTab.vue`.
- **Docs:** <https://github.com/SortableJS/vue.draggable.next>

### vue3-emoji-picker
- **Version:** `^1.1.8`
- **What it does:** A self-contained emoji-picker component for Vue 3.
- **Why this project uses it:** Lets admins pick an emoji/icon for a step when editing it
  (`client/src/pages/admin/StepForm.vue`; a type shim lives in `client/src/shims.d.ts`).
- **Docs:** <https://www.npmjs.com/package/vue3-emoji-picker>

### @azure/msal-browser
- **Version:** `^5.12.0` (resolved `5.12.0`)
- **What it does:** Microsoft Authentication Library for browser SPAs — runs the OAuth2 / OpenID Connect
  flows against Azure AD and returns ID/access tokens.
- **Why this project uses it:** The browser half of Azure AD SSO. It performs the interactive login and
  hands the resulting ID token to the backend, which validates it server-side (see
  `Microsoft.IdentityModel.Protocols.OpenIdConnect` above). Configured in `client/src/auth/msalConfig.ts`
  and driven from `client/src/stores/auth.ts` and `client/src/pages/admin/AdminLogin.vue`. SSO is optional
  and only wired up when Azure AD config is present.
- **Docs:** <https://learn.microsoft.com/entra/identity-platform/msal-overview>

### dompurify
- **Version:** `^3.4.8`
- **What it does:** An XSS sanitiser — strips dangerous markup/scripts from an HTML string before it's
  inserted into the DOM.
- **Why this project uses it:** Step descriptions are authored as rich HTML (via Tiptap) and rendered to
  students with `v-html`. DOMPurify sanitises that HTML first to prevent stored-XSS
  (`client/src/components/roadmap/StepDetailPanel.vue`). Carried over from the old app.
- **Docs:** <https://github.com/cure53/DOMPurify>

### canvas-confetti
- **Version:** `^1.9.4`
- **What it does:** Renders a confetti animation on a canvas.
- **Why this project uses it:** The celebratory effect when a student completes a milestone/step
  (`client/src/components/Celebration.vue`). Purely cosmetic delight. (Its TypeScript types come from the
  dev dependency `@types/canvas-confetti`.)
- **Docs:** <https://github.com/catdad/canvas-confetti>

---

## Frontend build / dev tooling (`client/package.json` → `devDependencies`)

These run on the developer machine and in the Docker build stage; they are **not** shipped to the
browser. The npm scripts are: `dev` → `vite`, `build` → `vue-tsc -b && vite build`, `preview` →
`vite preview`.

### vite
- **Version:** `^8.0.12` (resolved `8.0.16`)
- **What it does:** The frontend build tool and dev server — instant HMR in development, optimised Rollup
  bundles for production.
- **Why this project uses it:** Serves the SPA in local dev on **port 3000** and produces the static
  `dist/` bundle that nginx serves in the `web` container. The dev server also **proxies `/api`** to the
  backend (default `http://localhost:3001`, overridable via `VITE_API_PROXY_TARGET`) so the client can
  call relative `/api` URLs and never hardcode a backend host. Config in `client/vite.config.ts`.
- **Docs:** <https://vite.dev/>

### @vitejs/plugin-vue
- **Version:** `^6.0.6` (resolved `6.0.7`)
- **What it does:** The official Vite plugin that compiles Vue 3 Single-File Components (`.vue`).
- **Why this project uses it:** Required for Vite to understand `<template>`/`<script setup>`/`<style>`
  blocks. Registered in `client/vite.config.ts`.
- **Docs:** <https://github.com/vitejs/vite-plugin-vue>

### typescript
- **Version:** `~6.0.2` (resolved `6.0.3`)
- **What it does:** The TypeScript language and `tsc` compiler — static typing on top of JavaScript.
- **Why this project uses it:** The whole client is written in TypeScript (`.ts` + typed `.vue` SFCs).
  Project config lives in `client/tsconfig.json` (with `tsconfig.app.json` / `tsconfig.node.json`
  references) and extends `@vue/tsconfig`.
- **Docs:** <https://www.typescriptlang.org/docs/>

### vue-tsc
- **Version:** `^3.2.8` (resolved `3.3.4`)
- **What it does:** A TypeScript type-checker/compiler that understands `.vue` SFCs (type-checks
  templates, not just script blocks).
- **Why this project uses it:** Runs as the first half of the production build script
  (`vue-tsc -b && vite build`) so the build fails on type errors in components before bundling.
- **Docs:** <https://github.com/vuejs/language-tools>

### tailwindcss + @tailwindcss/typography
- **Versions:** `tailwindcss ^3.4.19`, `@tailwindcss/typography ^0.5.20`
- **What it does:** Tailwind is a utility-first CSS framework; the typography plugin adds the `prose`
  classes for nicely-styled long-form rich text.
- **Why this project uses it:** All styling is done with Tailwind utility classes; the typography plugin
  styles the rendered Tiptap/Markdown step descriptions. Config in `client/tailwind.config.js`. Carried
  over (same major version) from the old app.
- **Docs:** Tailwind <https://tailwindcss.com/docs> · Typography plugin <https://github.com/tailwindlabs/tailwindcss-typography>

### postcss
- **Version:** `^8.5.15`
- **What it does:** A CSS transformation pipeline; Tailwind and Autoprefixer run as PostCSS plugins.
- **Why this project uses it:** It is the engine Tailwind and Autoprefixer plug into during the build.
  Config in `client/postcss.config.js`.
- **Docs:** <https://postcss.org/>

### autoprefixer
- **Version:** `^10.5.0`
- **What it does:** A PostCSS plugin that adds vendor prefixes to CSS based on browser-support targets.
- **Why this project uses it:** Ensures the generated Tailwind CSS works across target browsers without
  hand-written prefixes. Runs as a PostCSS plugin alongside Tailwind.
- **Docs:** <https://github.com/postcss/autoprefixer>

### Supporting type packages
- **`@types/node` `^24.12.3`** (resolved `24.13.1`) — Node.js type definitions, used by the Vite/build
  config files (e.g. `vite.config.ts`). Docs: <https://www.npmjs.com/package/@types/node>
- **`@types/canvas-confetti` `^1.9.0`** — TypeScript types for `canvas-confetti` (which ships none).
  Docs: <https://www.npmjs.com/package/@types/canvas-confetti>
- **`@vue/tsconfig` `^0.9.1`** — the recommended base `tsconfig` for Vue 3 projects, extended by
  `client/tsconfig.app.json`. Docs: <https://github.com/vuejs/tsconfig>

---

## Frontend test tooling (installed, not yet wired up)

The client does **not currently have an automated test suite** — there are no `*.spec.*` / `*.test.*`
files under `client/src/`, and no test runner (e.g. Vitest) is declared in `package.json`. All automated
coverage lives in the API integration project (see [`TESTING.md`](./TESTING.md)). The following test
helpers are nonetheless installed as dev dependencies, ready for when a frontend suite is added:

### @vue/test-utils
- **Version:** `^2.4.11`
- **What it does:** The official unit-testing utilities for mounting and asserting against Vue components
  in a test environment.
- **Why this project uses it:** Present so component tests can be authored without adding a dependency
  later. Not referenced by any test yet.
- **Docs:** <https://test-utils.vuejs.org/>

### jsdom
- **Version:** `^29.1.1`
- **What it does:** A pure-JavaScript implementation of web/DOM standards, providing a browser-like DOM
  to Node-based tests.
- **Why this project uses it:** Would supply the DOM environment that `@vue/test-utils` needs when a
  test runner is added. Not referenced by any test yet.
- **Docs:** <https://github.com/jsdom/jsdom>

> If/when a frontend suite is introduced, a runner (such as Vitest) would need to be added and a `test`
> script wired into `package.json`; these two packages would then provide the component-mounting and DOM
> layers.

---

## Infrastructure

Not npm/NuGet packages, but the runtime platforms the three containers are built on. See
[`SETUP.md`](./SETUP.md) and [`ARCHITECTURE.md`](./ARCHITECTURE.md) for the deployment topology.

### SQL Server 2022
- **Version / image:** `mcr.microsoft.com/mssql/server:2022-latest` (Developer edition;
  `MSSQL_PID=Developer`)
- **What it does:** Microsoft's relational database engine.
- **Why this project uses it:** The system of record. It is the **target database of the conversion**
  (replacing PostgreSQL) and runs as the `sqlserver` container on port **1433**. The API auto-creates the
  `csub_admissions` database, applies the schema, and seeds it on startup. On Apple Silicon there is no
  native ARM build, so the image runs as `linux/amd64` under Rancher/Docker Desktop VZ + Rosetta
  emulation (a local-dev convenience; production targets a real SQL Server / Azure SQL).
- **Docs:** <https://learn.microsoft.com/sql/sql-server/>

### nginx
- **Version / image:** `nginx:1.27-alpine` (in `client/Dockerfile`)
- **What it does:** A high-performance web server and reverse proxy.
- **Why this project uses it:** In the `web` container it serves the static Vue `dist/` bundle on port
  **80** (published as **3000**) and **reverse-proxies `/api/` to the `api` container** so the browser
  sees a single origin (no CORS) — the same role the Vite dev proxy plays locally. It also provides the
  SPA fallback (`try_files … /index.html`). The proxy target is rendered from the `API_URL` env var via
  `envsubst` at container start (`client/nginx.conf.template`).
- **Docs:** <https://nginx.org/en/docs/>

### Docker / Docker Compose
- **What it does:** Container build + orchestration.
- **Why this project uses it:** The whole app ships as **three containers** orchestrated by
  `docker-compose.yml` — `web` (Vue + nginx, :3000), `api` (ASP.NET Core, :8080), and `sqlserver`
  (SQL Server 2022, :1433). `docker compose up --build` brings up the full stack at
  <http://localhost:3000>; `depends_on` ordering means starting `web` or `api` pulls in the services
  below it. The container base images are:
  - **`mcr.microsoft.com/dotnet/sdk:10.0`** — builds/publishes the API (build stage of `Api/Dockerfile`).
  - **`mcr.microsoft.com/dotnet/aspnet:10.0`** — runs the published API (final stage of `Api/Dockerfile`).
  - **`node:22-alpine`** — builds the Vue bundle (build stage of `client/Dockerfile`).
  - **`nginx:1.27-alpine`** — serves the bundle and proxies `/api` (final stage of `client/Dockerfile`).
- **Docs:** Docker <https://docs.docker.com/> · Compose <https://docs.docker.com/compose/>

---

## Notes for upgrades

- **NuGet (`.csproj`):** versions are exact. Bump the `Version="…"` attribute and run `dotnet restore`
  (or `dotnet test`/`dotnet run`) to pull the new package.
- **npm (`package.json`):** versions are semver ranges. `npm install` resolves the highest allowed
  version and updates `client/package-lock.json`; commit the lockfile so CI and the Docker build
  (`npm ci`) install the same versions. Run `npm run build` after any frontend bump so `vue-tsc`
  type-checks against the new types.
- **Container base images** are pinned by tag in the Dockerfiles / compose file; `sqlserver` uses
  `2022-latest`, so a rebuild can pull a newer 2022 patch.
- **Dropped vs the old app** (for reference, not dependencies you'll find here): the legacy `X-API-Key`
  admin auth, the dev activity simulator, and the dev-only mock API-check routes were intentionally
  removed in the conversion — see [`AUDIT.md`](../AUDIT.md).
