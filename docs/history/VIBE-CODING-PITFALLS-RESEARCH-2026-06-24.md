# Vibe-Coded App Pitfalls — Research + How This App Stands

Research into the documented failure modes of AI/"vibe-coded" applications, and a
point-in-time map of how the CSUB Runner Roadmap V2 addresses each. Used to drive a
focused audit-and-fix pass (2026-06-24).

## Why this matters

Independent studies converge on the same picture: AI-generated code usually *runs* but
ships with security and robustness holes far more often than human-written code.

- Carnegie Mellon: ~61% of AI-generated code is functionally correct, but only ~10.5%
  passes security review.
- A Dec 2025 study (Tenzai) of 15 apps from the top AI coding tools found **69**
  vulnerabilities; **every** app had SSRF, **0/15** had CSRF protection, **0/15** set
  security headers.
- A scan of 5,600 vibe-coded apps found 2,000+ vulnerabilities, 400+ exposed secrets,
  175 PII exposures.
- ~20% of AI-generated code references **non-existent packages** ("slopsquatting" risk).
- Columbia DAPLab: **silent error handling is the single most common failure mode.**

Sources: [Kaspersky](https://www.kaspersky.com/blog/vibe-coding-2025-risks/54584/) ·
[Databricks](https://www.databricks.com/blog/passing-security-vibe-check-dangers-vibe-coding) ·
[OX Security](https://www.ox.security/blog/vibe-coding-security/) ·
[Invicti checklist](https://www.invicti.com/blog/web-security/vibe-coding-security-checklist-how-to-secure-ai-generated-apps) ·
[Contrast](https://www.contrastsecurity.com/glossary/vibe-coding) ·
[CSA research note](https://labs.cloudsecurityalliance.org/research/csa-research-note-ai-generated-code-vulnerability-surge-2026/) ·
[Modall](https://modall.ca/blog/vibe-coded-app-breaks-production) ·
[DEV: "your app works, that's the problem"](https://dev.to/bezael/your-vibe-coding-app-works-thats-exactly-the-problem-1omm) ·
[Retool](https://retool.com/blog/vibe-coding-risks)

## The pitfall checklist → our status

Legend: ✅ already addressed (prior audits) · 🔍 re-verify this pass · ⚠️ gap to fix · ⬜ owner decision

### Security
| # | Pitfall (from the research) | Status |
|---|------------------------------|--------|
| 1 | Auth missing/silently dropped on endpoints | ✅ `[AdminAuth]`/`[StudentAuth]`/`[IntegrationAuth]` filters; admin re-checked against DB every request |
| 2 | Broken object-level authorization (BOLA/IDOR) — peer/admin data | 🔍 adversarial re-check (student scoping, admin role gates) |
| 3 | Endpoints left active after UI removal | ✅ dead endpoint (`/students/overdue`) already removed; re-confirm |
| 4 | SQL / command / RCE injection | ✅ all SQL is parameterized Dapper; interpolated fragments are server constants only |
| 5 | Exposed secrets / secrets in responses / reaching frontend | ✅ fail-fast guards; client config inlined at build time only; recon: no real `.env` tracked, none in git history, only documented dev defaults committed |
| 6 | SSRF | ✅ structured private-IP guard + connect-time recheck + opt-in `AllowPrivateTargets` |
| 7 | CSRF (0/15 apps had it) | 🔍 confirm applicability: auth is **bearer-token in sessionStorage**, no ambient cookies → classic CSRF does not apply; document it |
| 8 | Missing security headers (0/15 apps set them) | ✅ full Helmet-equivalent set in `Program.cs` + nginx; HSTS present |
| 9 | XSS | ✅ DOMPurify (bumped to 3.4.11 for the advisory) on the one v-html sink; `safeUrl` scheme guard |
| 10 | Vulnerable / hallucinated dependencies (slopsquatting) | ✅ recon: all 34 npm deps resolve in lockfile; `npm audit` 1 low dev-only residual; NuGet clean |
| 11 | HTTPS not enforced / debug settings exposed | 🔍 confirm HSTS + that Development-only config/Swagger never ships to prod |
| 12 | Error handling leaks stack traces / secrets | ✅ global envelope returns generic message, logs the stack server-side; confirm no leak path |

### Robustness / quality
| # | Pitfall | Status |
|---|---------|--------|
| 13 | No / incomplete tests | ✅ 276 backend + 39 frontend, CI written (parked) |
| 14 | Silent failures / missing error handling (the #1 failure mode) | ✅ toast store + boundary + 401 handling + observability logging; 🔍 sweep for any remaining swallowed catch |
| 15 | Performance: N+1, races, no pagination | ✅ hot paths reviewed; pagination capped; locks correct; 🔍 confirm no residual N+1 |
| 16 | Data integrity / edge cases (malformed input, network failure) | ✅ input clamps + validation; ⬜ enum CHECK constraints + term_id FK flagged for owner |
| 17 | Technical debt: inconsistent style, copy-paste, complexity | ✅ dedup + readability audits; lint/format enforced |
| 18 | No observability | ✅ structured ILogger across critical paths |

## This pass

A focused audit confirms the above against live code and fixes only genuine residuals
(behavior-preserving), explicitly **without** adding speculative architecture — the same
discipline the prior audits followed. Anything contract- or schema-changing is routed to
owner review, not auto-applied.
