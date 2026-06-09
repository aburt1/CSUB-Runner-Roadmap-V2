# Security & Code Audit — CSUB Runner Roadmap V2

A multi-dimension audit (auth/authz, injection & data exposure, SSRF/XSS/headers,
correctness/concurrency, config/deps/containers) with adversarial verification of every
non-trivial finding (2026-06-09).

## What was clean

- **SQL injection:** every Dapper query — including the dynamic analytics filter builders and
  pagination/sort — is parameterized. No injection vectors found.
- **XSS:** the single `v-html` sink (rich-text `guide_content`) is sanitized with DOMPurify; Tiptap
  output is controlled.
- **Mass assignment:** profile/tags/user/step/term updates write only allow-listed columns.
- **Data exposure:** no endpoint returns `password_hash`, `key_hash`, or raw `auth_credentials`
  (API-check credentials are masked on read). CSV export sanitizes formula-injection.

## Confirmed findings & resolutions

| # | Sev | Finding | Resolution |
|---|-----|---------|-----------|
| 1 | **Critical** | Compose supplied a weak default for `Jwt__Secret` under `Production`, so the "missing secret" guard never fired — tokens signed with a public, git-committed key (forgeable sysadmin tokens). | Compose now **requires** `JWT_SECRET` (`${JWT_SECRET:?…}`, no default). `JwtService` additionally **fails startup in Production** if the secret is missing, < 32 chars, or a known placeholder. Unit-tested. |
| 2 | **Critical** | Compose supplied `admin123` for `Admin__DefaultPassword` under `Production`, so the "no default admin in prod" guard was dead — `admin@csub.edu/admin123` seeded in production. | Compose now **requires** `ADMIN_DEFAULT_PASSWORD`. `Seeder` rejects a missing/weak/default admin password in Production (`IsWeakAdminPassword`). Unit-tested. |
| 3 | **High** | Admin role/active state was trusted from the JWT for 8h — a deactivated/demoted admin kept full access until expiry. | `AdminAuthAttribute` now **re-checks the DB every request**: rejects `is_active = 0` and authorizes on the **current** DB role, not the token claim. Integration-tested (deactivate → 403 → restore). |
| 4 | **High** | `ApiCheck__EncryptionKey` and break-glass `LocalLogin__*` defaulted to committed values under `Production`. | Compose **requires** `API_CHECK_ENCRYPTION_KEY`; break-glass is **disabled unless explicitly set** (defaults removed). |
| 5 | **Medium** | SSRF guard bypassable via HTTP redirect (validated URL could 302 to an internal/metadata target). | `ApiCheckRunner` HttpClient now uses `AllowAutoRedirect = false`. |
| 6 | **Medium** | Progress upsert: `WITH (UPDLOCK)` was ineffective — the read and write used separate connections, allowing lost updates / a duplicate-key 500 under concurrency. | `Progress.ApplyAsync` now runs the read-modify-write in **one transaction** (so the lock holds); `Db.TransactionAsync` safely joins an ambient transaction. |
| 7 | **Medium** | SSRF guard validated only the first resolved IP. | Now validates **all** resolved A/AAAA addresses. |
| 8 | **Medium** | `ApiCheckRunner` run-state is in-process, so it breaks under horizontal scaling and the concurrent-run guard isn't cross-instance. | **Accepted / documented:** the app is deployed as a single `api` container. Horizontal scaling would require DB- or cache-backed run-state (noted as a future enhancement). |
| 9 | **Low** | DB port `1433` published to all interfaces; SA password defaulted; API↔DB TLS off. | Published ports (`1433`, `8080`) **bound to `127.0.0.1`**; `MSSQL_SA_PASSWORD` required. `Encrypt=False` retained for the local emulated DB — use `Encrypt=True` + a managed cert in a real deployment. |
| 10 | **Low** | SSRF DNS-rebinding (validation re-resolves separately from the request). | **Accepted residual:** the URL is configured only by a sysadmin and rebinding is mitigated in prod by the all-address check; full connect-time IP pinning is a possible future hardening. |

## Lower / informational (noted, not changed)

- **Containers as root:** the `api` image now runs as the built-in non-root user; nginx workers already drop privileges.
- **npm transitive deps:** a few transitive packages are slightly behind patches — bumped where a clean update was available.
- **JWT in `sessionStorage`:** standard SPA trade-off (XSS-readable); CSP + DOMPurify mitigate the XSS surface. HttpOnly-cookie auth would be a larger redesign.
- **Integration client-name timing:** the `X-Client-Name` fast path is a minor bcrypt-timing enumeration vector (low; client names aren't secrets).
- **Admin SSO email auto-link:** first Azure SSO links the `oid` to an admin matched by email — intended onboarding behavior; documented.
- **`appsettings.Development.json` holds dev secrets:** intentional and dev-only; production reads from environment variables (and the guards above enforce strong values).
- **Analytics `DATEDIFF(second,…)/86400`:** intentional — matches the original Postgres `EXTRACT(DAY FROM interval)` floor semantics (parity, not a bug).

## Tests

Security guards are covered by `tests/Api.IntegrationTests/SecurityHardeningTests.cs` (JWT/seeder
fail-safes) and `AdminRevocationTests.cs` (per-request deactivation). Full suite remains green.
