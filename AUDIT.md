# Parity Audit — CSUB Runner Roadmap V2 vs. original

12 read-only auditors compared every endpoint/feature of the new C#/Vue app against the original
Node/React app (2026-06-08, workflow `wp93a61ga`).

## Verdict

**No missing endpoints. No broken response contracts.** All 50+ API endpoints and all frontend
pages/components are reproduced with matching paths, methods, request fields, response JSON keys,
status codes, and auth/role gates. The differences found are behavioral nuances — most from the
PostgreSQL→SQL Server port or from malformed-input edge cases.

Intentional, by-design (not gaps): dropped legacy X-API-Key admin auth, dev activity simulator, and
dev-only mock API-check routes; PostgreSQL→SQL Server dialect changes that are result-equivalent.

## Fixed as a result of this audit

| Area | Issue | Fix |
|---|---|---|
| **All date responses** | Dapper reads SQL Server datetimes as `Unspecified`, so timestamps serialized without the trailing `Z` the old app's `toISOString()` always emitted | Global `UtcDateTimeConverter` → ISO-8601 UTC with `Z` (millisecond precision) |
| **Security headers** | Only CSP + 3 headers vs. Helmet's full default set | Added HSTS, COOP, CORP, X-DNS-Prefetch-Control, X-Permitted-Cross-Domain-Policies, X-Download-Options, Origin-Agent-Cluster; X-Frame-Options → SAMEORIGIN (Helmet default) |
| **Rate limiting** | Global limiter covered ALL routes incl. static assets | Scoped the 200/15min limiter to `/api` only (matches old `/api/` mount) |
| **Error envelope** | Malformed JSON / unhandled errors didn't return the old `{error:...}` body | Added exception-handling middleware → `400 {error:"Invalid JSON body"}` / `500 {error:"Internal server error"}` |
| **Analytics day-math** | `DATEDIFF(day,…)` counts calendar boundaries; Postgres `EXTRACT(DAY FROM interval)` counts elapsed 24h periods → off-by-one bucket membership | Use `DATEDIFF(second,…)/86400` (whole elapsed days) in stalled / velocity / completion-velocity |
| **API-check field path** | Extractor couldn't index into arrays (e.g. `data.0.active`) | `ExtractFieldValue` now indexes array elements by numeric segment |
| **Student self-update audit** | `changed_by` recorded student email instead of display name | Resolve the student's display name for the audit actor |
| **JWT** | 1-min clock skew vs old default of 0 | `ClockSkew = 0` |
| **Frontend a11y** | Authenticated "Skip to main content" link + auth-init loading spinner/ARIA not carried over | Restored both in `HomeView`/`RoadmapPage` |

## Intentionally left as-is

- **Improvements kept:** break-glass logins now actually audit (old INSERT referenced non-existent
  columns and silently no-op'd); `/api/health` now actively pings the DB (`SELECT 1`).
- **Malformed-input edges** (non-numeric `:stepId`/`term_id`, trailing-garbage `days`, non-string
  `displayName`, string `"false"`): the new behavior is equal or stricter (clean 400/404 vs a 500);
  canonical inputs are identical.
- **Behind a reverse proxy:** to get per-real-client rate limiting + correct client IPs, enable
  `ForwardedHeaders` in production (documented; not needed for the single-container compose setup).
