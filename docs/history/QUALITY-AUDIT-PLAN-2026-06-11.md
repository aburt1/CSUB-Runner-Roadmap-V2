# Quality Audit — Triaged Fix Plan (2026-06-11)

**Status: TRIAGED, NOT YET FIXED.** This is the output of the third audit pass
(code cleanliness / readability / comprehensibility — new dimensions plus a
re-audit of recent changes). Eight finder agents produced **117
candidate findings**; triage merged duplicates and dropped out-of-scope items,
leaving **85 actionable fixes in
5 batches** below. Work was deliberately paused here to be
executed later.

## How to execute this plan later

- Every fix below is **behavior-preserving**: the REST contract (paths, JSON keys,
  status codes, error strings) is frozen, and the boring-code charter applies
  (explicit code, hand-written SQL, comments say WHY, no new abstractions or deps).
- Per the owner's preference, apply fixes with **Opus 4.8** agents (one batch at a
  time — batch file sets are disjoint, but sequential execution also keeps the
  shared test database safe), with **Fable** orchestrating.
- After each batch run its verification commands (listed per batch); if a fix can't
  be made to pass, revert that file and record it as skipped.
- If a finding turns out to be wrong on closer reading, skip it with a reason.
- Baseline at the time of triage: commit `b76faff`, 214 backend + 35 frontend
  tests green, lint 0 errors.
- (Same-session only: workflow run `wf_c111b740-f24` can be resumed with the Fix
  phase restored; from a fresh session, just feed each batch below to a fixer.)


---

## Batch: backend-analytics (16 items)

**Files:** `Api/Controllers/Admin/AnalyticsController.cs`

**Verification after fixing:** `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2 && dotnet build Api/Api.csproj && dotnet test`


### backend-analytics #1 — Deadline-risk headline counts waived students as at-risk; its own drilldown excludes them

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:355`


VERIFIED. at_risk_count uses "sp.status IS NULL OR sp.status != 'completed'" (line 355) and the two inline student queries repeat it (lines ~376, ~385), so waived counts as at-risk. BuildDeadlineRiskFilter (lines ~878-888) joins on status IN ('completed','waived') + sp.student_id IS NULL, so waived is safe. Clicking through from '5 at risk' can show 4 students. Every other endpoint in the file treats waived as done.


**Fix:** Confirm against old-app parity first. Preferred: change line 355 to CASE WHEN sp.status IS NULL OR sp.status NOT IN ('completed','waived') and convert the two inline student queries to the LEFT-JOIN-on-IN('completed','waived')/IS NULL shape used by BuildDeadlineRiskFilter; update affected integration tests. If parity demands the mismatch, instead add a WHY comment on both the summary and the drilldown builder stating waived intentionally counts as at-risk in the headline only.


### backend-analytics #2 — Deadline-risk per-step students query is term-unscoped in the no-term branch

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:381`


VERIFIED. The aggregate joins students st ON st.term_id = s.term_id (line 357), but the else-branch students query (lines ~381-386) has no term constraint at all — it returns every student in the database lacking a completed row for the step, so at_risk_count disagrees with students.length in the same response object.


**Fix:** Scope the else branch to the step's term: WHERE st.term_id = (SELECT term_id FROM steps WHERE id = @stepId) AND (sp.status IS NULL OR sp.status != 'completed'), mirroring the aggregate. Verify old-app parity; if the unscoped list were somehow intentional, a comment must say so instead. Update tests if counts change.


### backend-analytics #3 — Three definitions of 'done' in one controller (any-row / completed+waived / completed-only) with no stated rule

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:49`


MERGED from 3 findings. Stats avg subquery (lines 49-65) counts ANY progress row; StepCompletion/CohortSummary/CompletionTrend use status IN ('completed','waived') (197, 232, 322); StalledStudents/CohortComparison/CompletionVelocity use status = 'completed' only (418/423, 557, 596). Finder verified each variant mirrors the old analytics.ts, so they are deliberate parity — but nothing says so, and dashboard numbers visibly disagree. Comment-only fix; do NOT normalize predicates.


**Fix:** Add a short block to the class header comment documenting the three tiers, e.g.: "'Done' deliberately differs per metric (port parity with the old analytics.ts): completion/cohort/trend metrics count completed+waived; velocity and stalled count real completions only (waiving isn't activity); Stats counts any progress row because not_completed rows are deleted by Progress.ApplyAsync." Add a one-line pointer at each divergent subquery (Stats line 51, StalledStudents 418, CohortComparison 557, CompletionVelocity 596).


### backend-analytics #4 — BuildFilterAsync returns null! for unknown filter_type while using InvalidFilterException for every other bad input

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:666`


VERIFIED: default arm is `return null!;` from a method declared to return non-nullable Task<FilterQuerySet>; caller follows with `if (filterSet is null)` (~line 475). Two error channels for one kind of input error plus a nullability-annotation lie. Same 400 response either way, so consolidating is behavior-preserving for clients (response body becomes the exception message 'Invalid filter_type' — keep the existing message text if tests pin it).


**Fix:** Replace `return null!;` with `throw new InvalidFilterException("Invalid filter_type");` and delete the null check at the call site (the existing catch already returns BadRequest with the exception message). Confirm no integration test pins a different body for unknown filter_type.


### backend-analytics #5 — Cohort drilldown bucket edges (0.251/0.501/0.751) mismatch the summary's (<=0.25/0.50/0.75); lo/hi dead in 0% arm

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:714`


MERGED from 2 findings. CohortSummary buckets with <=0.25/<=0.50/<=0.75 (lines 311-314); BuildCohortBucketFilter encodes lo=0.251/0.501/0.751 with strict `> @lo` (lines ~720-724, 761), leaving a (0.25, 0.251] dead zone where a student is in the summary bucket but no drilldown bucket. The switch also assigns lo/hi for the "0%" case that the dedicated 0% branch never uses.


**Fix:** Set lo to 0.25/0.50/0.75 (strict `>` keeps boundaries exclusive, exactly matching the summary's `<=` upper bounds — for '1-25%' use lo=0), and move the lo/hi switch below the filterValue == "0%" early-return so the dead assignment disappears. Alternatively, if the 0.251 values must stay for port parity, comment that the (0.25,0.251] gap is a known accepted artifact of the old filterBuilders.


### backend-analytics #6 — {divisor} string-interpolated into SQL while neighboring values are bound parameters

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:311`


MERGED from 2 findings. CohortSummary (lines 311-314 and repeated GROUP BY at 329-333), CohortComparison (551), and BuildCohortBucketFilter (761-762) interpolate {divisor} into SQL text. It is a server-computed int (no injection), but the old server bound it as a parameter, so inlining is a port deviation, and every parameterization audit must stop here to trace provenance.


**Fix:** Bind it: add divisor to the parameter objects and use @divisor in the CASE/HAVING expressions (CAST(... AS float) / @divisor). This restores both the old server's behavior and the file's everything-is-a-parameter convention.


### backend-analytics #7 — CompletionTrend filters is_optional but not is_active, unlike every other analytics query

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:225`


CompletionTrend joins steps with only COALESCE(st.is_optional, 0) = 0 (lines 225-226) while siblings use ActiveStepFilter. BuildTrendDateFilter (line 954) matches the trend, so trend and drilldown agree with each other — but a reader comparing trend to step-completion numbers must guess whether retired steps' completions deliberately stay in the historical trend.


**Fix:** Comment-only (do not change the predicate without parity verification): add at line 224: "// deliberately no is_active filter: completions on since-deactivated steps remain part of the historical trend (ported from analytics.ts; BuildTrendDateFilter must stay in sync)."


### backend-analytics #8 — NULL/empty progress status silently defaults to 'completed' in export with no explanation

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:119`


ExportProgress maps string.IsNullOrEmpty(p.status) ? "completed" : p.status. The reason lives only in schema.sql (status NVARCHAR(20) NULL DEFAULT 'completed' — legacy NULL means completed). Without the comment it looks like paranoid dead code, and elsewhere the analytics SQL excludes NULL-status rows from 'done'. (The mirrored client comment in useProgress.ts is handled in the frontend batch.)


**Fix:** Add at line 119: "// NULL/empty status is legacy data and means completed (schema default — see schema.sql student_progress.status)."


### backend-analytics #9 — DATEDIFF(second,...)/86400 idiom repeated five times with no comment on why not DATEDIFF(day)

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:594`


CompletionVelocity (594), BuildStalledFilter (~841-864), and BuildVelocityBucketFilter (~916, 928) all use seconds/86400. The rationale (DATEDIFF(day) counts midnight crossings, not elapsed 24h periods, which would change bucket membership) is never stated; a maintainer may 'simplify' and silently rebucket.


**Fix:** At line 594 add: "// seconds/86400, not DATEDIFF(day): day counts midnight boundaries, not elapsed 24h periods — same rule as the old Node code." Add a brief pointer ('see CompletionVelocity comment') at the two filter builders.


### backend-analytics #10 — deadline_date CAST in DeadlineRisk can 500 on malformed text; idiom differs from the string-compare elsewhere

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:360`


deadline_date is NVARCHAR(32) holding 'YYYY-MM-DD'. DeadlineRisk does CAST(s.deadline_date AS date) (lines 360-361), which raises error 241 for any non-date string — and Admin step Update binds deadline_date with no format validation, so such a row is reachable. ListStudents uses a lexicographic string compare instead (handled as a comment item in the controllers batch).


**Fix:** Use TRY_CAST(s.deadline_date AS date) in the two comparisons (junk rows drop out instead of failing the endpoint — behavior-preserving for valid data) and add: "// deadline_date stores 'YYYY-MM-DD' text (ported from CURRENT_DATE::text comparisons); TRY_CAST tolerates legacy free-text values."


### backend-analytics #11 — Dapper IN-list used 400 lines after a comment teaching that IN-lists are forbidden

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:491`


ExportProgress's comment (lines 98-101) explains the 2100-parameter cap; AnalyticsStudents then uses WHERE student_id IN @studentIds (491). It is safe (one page, per_page <= 100 via ParsePagination) but the file itself taught the reader the pattern is forbidden.


**Fix:** Add one line above the query: "// IN-list is safe here: studentIds is one page, capped at per_page <= 100 by ParsePagination — far below the 2100-parameter limit (cf. the export comment above)."


### backend-analytics #12 — Drilldown step filters bind the already-validated step id as a string against INT columns

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:672`


BuildFilterAsync validates with int.TryParse (lines ~640-642) but the builders bind the raw string (new { id = filterValue } at 672/694/872 and sp.step_id = @filterValue at 681/703/881). Works via implicit conversion, but a reader pauses on string-vs-INT when the parsed int is in hand one frame up.


**Fix:** Parse once in BuildFilterAsync (var stepId = int.Parse(filterValue!)) and pass the int to BuildStepCompletedFilter/BuildStepNotCompletedFilter/BuildDeadlineRiskFilter, binding new { stepId }.


### backend-analytics #13 — QueryHelpers.CountActiveStepsAsync exists but StalledStudents and CohortComparison hand-write the identical query

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:428`


Lines 428-432 and 532-536 inline SELECT COUNT(*) ... {ActiveStepFilter} AND term_id = @termId — exactly what CountActiveStepsAsync wraps and what CohortSummary (297) and AnalyticsStudents (463) already call. The half-adopted helper makes a reader diff the inline copies for hidden differences.


**Fix:** In the termId.HasValue branches of StalledStudents and CohortComparison call QueryHelpers.CountActiveStepsAsync(_db, termId.Value); keep inline SQL only for the no-term case, as CohortSummary does.


### backend-analytics #14 — CohortSummary ORDER BY bucket works only because labels happen to sort alphabetically in range order

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:334`


'0%' < '1-25%' < '26-50%' < '51-75%' < '76-100%' is correct numeric order purely by accident of the label spellings; renaming a bucket would silently reorder the chart.


**Fix:** Add: "// the bucket labels happen to sort alphabetically in range order — keep that property if renaming buckets." (Or order by a CASE expression.)


### backend-analytics #15 — Cohort comparison hard-codes eight tags and substring-matches them against the raw JSON tags column

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:540`


Line 540 hard-codes the cohort tag list and line 560 matches with s.tags LIKE '%tag%' against JSON-array text. Substring matching can false-positive on overlapping tag names, and nothing states where the canonical list comes from.


**Fix:** Comment-only: "// fixed well-known cohort tags (ported from the old analytics route); matched as substrings of the JSON tags text — exact-token matching is deliberately not attempted here, so keep tag names non-overlapping."


### backend-analytics #16 — Table aliases s/st flip between 'students' and 'steps' across queries in the same file

*Location:* `Api/Controllers/Admin/AnalyticsController.cs:353`


Stats and CohortSummary use s=students/st=steps; StepCompletion, Bottlenecks, DeadlineRisk use s=steps/st=students; drilldown builders use st=students. Each query is self-contained, but a reader must re-derive the alias table per method. Low priority — do this last, mechanically, after the substantive items in this batch, relying on the integration tests.


**Fix:** Standardize within this file on the majority usage (st = students, s = steps), renaming the minority queries (Stats, CohortSummary). Pure rename of self-contained SQL text; zero intended behavior change — the full backend test run is the safety net.


---

## Batch: backend-controllers (15 items)

**Files:** `Api/Services/QueryHelpers.cs`, `Api/Controllers/StepsController.cs`, `Api/Controllers/IntegrationsController.cs`, `Api/Controllers/RoadmapApiChecksController.cs`, `Api/Controllers/Admin/StudentsController.cs`, `Api/Controllers/Admin/UsersController.cs`, `Api/Controllers/Admin/StepsController.cs`, `Api/Controllers/Admin/TermsController.cs`, `Api/Controllers/Admin/ApiChecksController.cs`, `Api/Services/JsParse.cs (new)`

**Verification after fixing:** `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2 && dotnet build Api/Api.csproj && dotnet test`


### backend-controllers #1 — NULL is_active read four contradictory ways with no comment saying which convention is canonical

*Location:* `Api/Services/QueryHelpers.cs:10`


MERGED from 2 findings; VERIFIED. ActiveStepFilter treats NULL as inactive (all admin analytics/exports, ApiCheckRunner.cs:414); student-facing StepsController.cs uses (is_active = 1 OR is_active IS NULL) at lines 78/87/90/99/102 (NULL = active); IntegrationsController.cs:130/140 uses COALESCE(s.is_active, 1). All are faithful ports, but a maintainer writing the next query must pick between contradictory precedents. Do NOT change schema or predicates — name and document the two conventions.


**Fix:** In QueryHelpers add a second named constant: public const string StudentVisibleStepFilter = "(is_active = 1 OR is_active IS NULL)" with a WHY comment: "student-facing reads treat legacy NULL is_active as active (ported from the old steps.ts); admin/analytics treat NULL as inactive (ActiveStepFilter, ported from the old ACTIVE_STEP_FILTER)". Use it to replace the inline copies in Controllers/StepsController.cs, and extend the ActiveStepFilter comment to point at the difference (note ApiCheckRunner and IntegrationsController read paths in the comment; their files are owned by other batches and need no edit).


### backend-controllers #2 — Orphaned comments from deleted helpers now misdocument AsString and CollectBodyKeys

*Location:* `Api/Controllers/Admin/TermsController.cs:267`


MERGED from 3 findings; VERIFIED at lines 267-271 and 291-293. The dedup sweep (b76faff) removed local NullIfEmpty/TryGetProperty/IsTruthy but left their comments stacked above AsString and CollectBodyKeys. A reader will conclude AsString collapses "" to null — it does not — which is exactly the wrong assumption that causes a future bug.


**Fix:** Delete the NullIfEmpty and TryGetProperty comment lines (267-270), keeping only "// String value of a JSON property, treating JSON null as SQL/C# null." above AsString. Delete the IsTruthy line (291), keeping the Object.keys comment above CollectBodyKeys.


### backend-controllers #3 — Two private classes both named DynamicSqlParams with different APIs; four mechanisms for one partial-UPDATE job

*Location:* `Api/Controllers/Admin/UsersController.cs:189`


MERGED from 2 findings; VERIFIED. UsersController.cs:189 (positional Next()/ToObject) and Admin/StepsController.cs:531 (keyed Add()/ToDynamicParameters, with an inaccurate 'Tiny ordered param bag' comment over an unordered Dictionary) share a name but not a shape. StudentsController.UpdateProfile passes a plain Dictionary straight to Dapper; TermsController uses DynamicParameters. Greping the name finds two unrelated classes, and the wrappers imply Dapper can't take a dictionary, which StudentsController disproves.


**Fix:** Standardize on the plain Dictionary<string, object?> that StudentsController already uses (Dapper binds IDictionary natively): delete both nested DynamicSqlParams classes, replace UsersController's Next() calls with explicitly named parameters into a dictionary, and replace StepsController's wrapper with the same dictionary pattern. Add a one-line comment at one site noting Dapper accepts IDictionary directly.


### backend-controllers #4 — JS-parseInt mimic implemented twice with different overflow behavior

*Location:* `Api/Controllers/Admin/StepsController.cs:488`


ParseLeadingInt (Admin/StepsController.cs:488-506, silently wraps on overflow via (int)(sign * long)) and ParseIntPrefix (IntegrationsController.cs:416-438, returns null on overflow) are two ports of the same 'JS parseInt(x, 10)' concept with near-identical comments and divergent edge behavior — the drift class the prior audits were cleaning up.


**Fix:** Create Api/Services/JsParse.cs with one static helper (keep the safer null-on-overflow semantics of the IntegrationsController version, plus its existing WHY comment about mirroring JS parseInt), call it from both controllers, and delete both private copies. Overflow inputs are unreachable in practice, so behavior is preserved for all real data.


### backend-controllers #5 — GetSteps repeats the identical active-term fallback block, justified by diffing against a file not in this repo

*Location:* `Api/Controllers/StepsController.cs:63`


VERIFIED: the inner else (83-91) and outer else (95-102) are byte-identical, and server/routes/steps.ts does not exist anywhere in this repository (no server/ directory), so the class comment's 'mirrors that route file 1:1 so the two diff cleanly' rationale can no longer be exercised.


**Fix:** Resolve the term once: int? termId = student?.term_id ?? await _db.QueryOneAsync<int?>("SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC"); then one termId-conditional steps query (use the new QueryHelpers.StudentVisibleStepFilter from this batch). Behavior identical. Update the class comment to drop or correct the 'diff cleanly' rationale.


### backend-controllers #6 — Contradictory comment pair about how the audit actor is resolved in student UpdateStatus

*Location:* `Api/Controllers/StepsController.cs:193`


Lines 193-195 claim Audit.ResolveActor uses the stashed studentEmail, but the actual call passes an explicit actor (display_name falling back to email). This is the only Audit.LogAsync site not using ResolveActor; a maintainer trusting the first comment and 'simplifying' would silently change the audit actor.


**Fix:** Delete the misleading comment and strengthen the remaining one: "Deliberately NOT Audit.ResolveActor (which would yield the token email): the old app logs the student's display name, falling back to email."


### backend-controllers #7 — UncompleteStep tolerates unknown student/step with 200 where sibling CompleteStep 404s — asymmetry uncommented

*Location:* `Api/Controllers/Admin/StudentsController.cs:99`


VERIFIED: UncompleteStep runs the same two lookups as CompleteStep but ignores null results (stepRow?.title, student?.display_name in the audit payload) and returns 200/'noop'. Plausibly intentional idempotent DELETE, but nothing says so, 60 lines from an endpoint doing the opposite.


**Fix:** Comment-only (do not add 404s without parity verification): above the lookups add "// Unlike CompleteStep, missing student/step is NOT a 404 here: the old API treats uncomplete as idempotent (deleting nothing is success); the lookups exist only to enrich the audit entry."


### backend-controllers #8 — Some integration failures are stored for idempotent replay, others are not — undocumented contract asymmetry

*Location:* `Api/Controllers/IntegrationsController.cs:169`


ProcessCompletionItemAsync stores resolution failures via FinalizeOutcomeAsync (lines 181-197) so replays return the original outcome, but invalid status (169-176) and invalid completed_at (212-219) return WITHOUT storing, so those retries re-execute. The class comment describes replay but not which failures participate; an integration partner cannot tell deliberate contract from oversight.


**Fix:** Comment-only: above the invalid_status early-return add "// Validation failures (bad status / bad completed_at) are deliberately NOT recorded in integration_events — the caller can retry the same source_event_id with a corrected payload; resolution failures ARE recorded so a replay returns the original outcome." Verify the asymmetry against the old route before wording it as deliberate.


### backend-controllers #9 — Per-action try/catch-500 wrappers in exactly two controllers duplicate the global error middleware

*Location:* `Api/Controllers/Admin/ApiChecksController.cs:50`


Every action in ApiChecksController and RoadmapApiChecksController wraps in try/catch returning StatusCode(500, { error = "Internal server error" }), which Program.cs's outermost middleware already does. No other controller does this, so a reader wonders whether the other ten endpoints leak errors (they don't). Only difference: a labeled log message.


**Fix:** Either remove the wrappers from both controllers (identical response body via the global handler; the action name is already in the ASP.NET request log scope) or, if the labeled log lines are wanted, keep them and add one comment at the top of each controller: "Actions catch locally so the log line names the operation; the global handler in Program.cs would log it generically." Pick one approach for both files.


### backend-controllers #10 — run-api-checks replies status:'started' even when it did not start a run

*Location:* `Api/Controllers/RoadmapApiChecksController.cs:56`


When TryBeginRun fails because a run is already in flight, the endpoint returns the same { status = "started" } body as the success path. Correct for the polling client, but the literal response reads like a lie.


**Fix:** Comment-only: above line 56 add "// a run is already in flight — report 'started' anyway so the client polls check-status (same contract as the old server)."


### backend-controllers #11 — Audit log endpoint method is named Audit_ with a trailing underscore

*Location:* `Api/Controllers/Admin/StudentsController.cs:360`


The underscore exists only to dodge the collision with the Api.Services.Audit class; it reads like a typo and is the only such name in the codebase. Action method names don't affect the [HttpGet("audit")] route.


**Fix:** Rename the method to GetAuditLog; no other call sites exist.


### backend-controllers #12 — deadline_date compared as a raw string in ListStudents with no note that the column is ISO text

*Location:* `Api/Controllers/Admin/StudentsController.cs:302`


The overdue subquery compares st.deadline_date < CONVERT(varchar(10), CAST(SYSUTCDATETIME() AS date), 23) lexicographically — only correct because the column stores ISO yyyy-MM-dd text, which nothing states. (The analytics-side TRY_CAST counterpart is handled in the analytics batch.)


**Fix:** Add a one-line comment: "// deadline_date is ISO yyyy-MM-dd text, so string compare orders correctly and tolerates legacy free-text values (the analytics endpoint uses TRY_CAST for the same column)."


### backend-controllers #13 — Admin step Delete neither 404s nor checks existence, unlike every other mutation in the file

*Location:* `Api/Controllers/Admin/StepsController.cs:317`


Delete looks up only the title (tolerating null), soft-deletes unconditionally, and can write an audit entry with title = null for a nonexistent id, returning 200. Update and Duplicate both 404 for the same situation. Likely idempotent-delete parity, but uncommented.


**Fix:** Add above line 319: "// No 404 on purpose: the old API treats delete as idempotent; the title lookup is only for the audit entry." Optionally skip the audit write when step is null so phantom step_delete entries with a null title can't appear.


### backend-controllers #14 — INSERT INTO steps binds 15 column-matched snake_case params plus two odd ones (@order, @termId)

*Location:* `Api/Controllers/Admin/StepsController.cs:118`


Both 17-column INSERTs (lines 117-119 and 350-352) use column-matched names except @order (sort_order) and @termId (term_id), while TermsController.Clone inserts the same column list fully column-matched (@sort_order, @term_id). Verifying the 17-slot VALUES alignment requires special-casing the two odd names.


**Fix:** Rename to @sort_order and @term_id in both INSERTs (anonymous-object keys sort_order = order, term_id = termId.Value), matching TermsController.Clone.


### backend-controllers #15 — Two uppercase AS column aliases in IntegrationsController vs lowercase 'as' everywhere else

*Location:* `Api/Controllers/IntegrationsController.cs:130`


The step-catalog queries (lines 130, 140) are the lone uppercase column-alias outliers (t.name AS term_name, COALESCE(s.is_active, 1) AS is_active) among ~60 lowercase 'as' aliases; the lone deviation makes a reader wonder if it signals something.


**Fix:** Lowercase the two AS aliases to match the codebase convention. (Trivial — fold into the same commit as this file's idempotency comment.)


---

## Batch: backend-services-data (15 items)

**Files:** `Api/Services/ApiCheckRunner.cs`, `Api/Services/Progress.cs`, `Api/Data/Db.cs`, `Api/Data/Seeder.cs`, `Api/Data/schema.sql`, `Api/Auth/AdminAuthAttribute.cs`, `Api/Auth/IntegrationAuthAttribute.cs`, `Api/Auth/StudentAuthAttribute.cs`, `tests/Api.IntegrationTests/HelperTests.cs (new)`

**Verification after fixing:** `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2 && dotnet build Api/Api.csproj && dotnet test`


### backend-services-data #1 — ApiCheckRunner.Stopwatch() is wall-clock unix ms misleadingly named after the monotonic Stopwatch class

*Location:* `Api/Services/ApiCheckRunner.cs:546`


MERGED from 2 findings; VERIFIED: private static long Stopwatch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), used for the 15s run cap at lines 419/424 where a reader assumes monotonic timing (clock jumps can lengthen or end runs early). The name actively suggests the wrong primitive.


**Fix:** Use the real thing for the elapsed check: var sw = System.Diagnostics.Stopwatch.StartNew(); if (sw.ElapsedMilliseconds > RunBudgetMs) — and delete the private helper. At minimum rename it NowUnixMs() with: "// Wall-clock unix ms — same representation as RunState.startedAt (not a Stopwatch; the old Node code used Date.now())."


### backend-services-data #2 — 5-second per-request timeout and 15-second run budget are duplicated magic literals

*Location:* `Api/Services/ApiCheckRunner.cs:361`


VERIFIED: CancellationTokenSource(TimeSpan.FromMilliseconds(5000)) appears at lines 361 and 477; the 15_000 cap at 424 is a third bare literal. The relationship (5s per request bounds how far one slow API overshoots the 15s budget) is never stated.


**Fix:** Introduce private const int PerRequestTimeoutMs = 5000; and private const int RunBudgetMs = 15_000; with "// 5s per request keeps one slow upstream from eating most of the 15s run budget (same values as the old Node runner)", and use them at all three sites.


### backend-services-data #3 — existing.status == "not_completed" check looks unreachable; waived-skip rationale unstated

*Location:* `Api/Services/ApiCheckRunner.cs:500`


Under Progress.ApplyAsync semantics a not_completed change deletes the row, so a stored 'not_completed' row should never exist via this codebase; a reader can't tell dead code from legacy-data defense, nor why 'waived' rows are skipped rather than clobbered.


**Fix:** Add: "// existing rows normally never have status 'not_completed' (ApplyAsync deletes them) — this also tolerates legacy rows from the old Node app. 'completed'/'waived' rows are left alone so an API check never clobbers a manual waive."


### backend-services-data #4 — Load-bearing doc.RootElement.Clone() has no comment explaining why Clone is required

*Location:* `Api/Services/ApiCheckRunner.cs:369`


Both extraction sites (369, 483) depend on Clone() because the extracted JsonElement outlives the using-scoped JsonDocument; removing it would throw ObjectDisposedException at the deferred reads. A tidy-minded maintainer could delete it and pass the happy path inside the using block.


**Fix:** Add at line 369 (and reference at 483): "// Clone() detaches the element from the JsonDocument — `extracted` is read after `doc` is disposed; an uncloned element would throw ObjectDisposedException."


### backend-services-data #5 — Run-state cleanup TTL comment states the what, not that the 5-minute net un-sticks a crashed 'running' claim

*Location:* `Api/Services/ApiCheckRunner.cs:94`


TryBeginRun refuses to start while a state is 'running'; if a background run dies without SetRunState, the 5-minute TTL is the only thing releasing the slot (and it dovetails with the 5-min throttle in RoadmapApiChecksController.cs:44). None of that is stated.


**Fix:** Extend the comment: "// 2 min keeps results around long enough for the client to poll; 5 min is the safety net that releases a 'running' claim if the background task died without SetRunState (and matches the 5-min run throttle)."


### backend-services-data #6 — Run query selects five columns the loop never reads; IntegrationAuth selects is_active it already pins

*Location:* `Api/Services/ApiCheckRunner.cs:405`


MERGED from 2 findings. RunApiChecksForStudentAsync selects sac.id, created_at, updated_at, s.id AS s_id (duplicating sac.step_id), and sort_order AS sort_order (only needed in ORDER BY); StepApiCheckWithSort carries matching unread properties. IntegrationAuthAttribute.cs:35/54 selects is_active that both queries pin with WHERE is_active = 1 and never read.


**Fix:** Trim the SELECT to the columns the loop uses, delete the unread properties (optionally rename the row class StepApiCheckToRun), keep ORDER BY s.sort_order. In IntegrationAuthAttribute drop is_active from both SELECTs and from IntegrationClientRow.


### backend-services-data #7 — `(current.note ?? null)` no-op and unexplained sameCompletedAt logic in the noop check

*Location:* `Api/Services/Progress.cs:103`


MERGED from 2 findings; VERIFIED at lines 102-106. x ?? null is exactly x for a nullable string — a literal translation of the JS (current.note || null) that no longer does anything (notes are normalized to null on write at line 70, so empty-string DB notes don't occur via this code). sameCompletedAt encodes 'caller omitted completed_at means keep' but reads as 'null matches anything'.


**Fix:** Replace (current.note ?? null) with current.note, and add above the sameCompletedAt line: "// completed_at omitted by the caller means 'keep the stored value' — only an explicit value can differ."


### backend-services-data #8 — ApplyAsync coerces any unrecognized status (including null) to 'completed' without saying callers validate

*Location:* `Api/Services/Progress.cs:67`


Lines 67-69 map anything that isn't waived/not_completed to 'completed'. Callers do validate (IntegrationsController rejects bad statuses; admin pre-coerces), but the function read in isolation suggests invalid statuses can silently mark steps complete.


**Fix:** Add: "// callers validate status; anything else (including null) means 'completed' — the default action, matching the old progress.ts."


### backend-services-data #9 — emplid lookup is trim-only while the schema's emplid_norm is LOWER(LTRIM(RTRIM(...)))

*Location:* `Api/Services/Progress.cs:30`


ResolveStudentByIdNumberAsync matches WHERE LTRIM(RTRIM(COALESCE(emplid, ''))) = @normalized — no LOWER, no emplid_norm — so case-insensitivity silently depends on DB collation while the unique index guarantees it regardless. The asymmetry looks accidental and the inline expression can't use the emplid_norm index.


**Fix:** Preferred: change the lookup to WHERE emplid_norm = LOWER(@normalized) (matches the schema's definition of emplid identity; @normalized is already trimmed in C#) — verify the integration tests still pass. Minimum: add "// trim-only compare ports the old server; case-insensitivity comes from the server's CI collation, and emplid_norm guarantees no case-variant duplicates exist."


### backend-services-data #10 — Db retry: unexplained `ex is TimeoutException` arm and bare backoff constants

*Location:* `Api/Data/Db.cs:174`


MERGED from 2 findings. Lines 183/188 add `|| ex is TimeoutException` though SqlClient command timeouts arrive as SqlException -2 (already in the transient list) — a reader can't tell defensive from unreachable. MaxAttempts = 4 and inline 200 * 2^(attempt-1) (~1.4s worst case) have no stated budget.


**Fix:** Add a comment naming the case the TimeoutException arm covers (or remove it if it was speculative — confirm by checking what Open/pool paths throw). Name the base delay: private const int BaseRetryDelayMs = 200; with "// 200/400/800ms — ~1.4s worst case, well under the client request timeout."


### backend-services-data #11 — Seeder's ?? defaults never fire under compose (env passes "" not null), unlike every other optional setting

*Location:* `Api/Data/Seeder.cs:123`


VERIFIED: var name = config["Integration:DefaultName"] ?? "PeopleSoft Dev". Compose passes unset vars as empty strings, and the rest of the codebase uses IsNullOrEmpty for this convention (Program.cs Cors, AdminAuthController LocalLogin, AzureAdTokenValidator) — running compose with the key set but the name unset seeds a client whose name is "".


**Fix:** Treat empty as missing, consistent with the rest of the app: var name = config["Integration:DefaultName"]; if (string.IsNullOrEmpty(name)) name = "PeopleSoft Dev"; same for the key variable (the Production guard already short-circuits an empty key).


### backend-services-data #12 — 'The active term' is selected with opposite ORDER BY directions in Seeder vs runtime endpoints

*Location:* `Api/Data/Seeder.cs:43`


VERIFIED: Seeder.ActiveTermIdAsync uses ORDER BY id (oldest active) while AuthController and StepsController use ORDER BY id DESC (newest). Both are faithful ports; they only diverge if more than one term is active, which nothing prevents via seed or direct DB edits.


**Fix:** Comment-only: on ActiveTermIdAsync add "// ORDER BY id (oldest active) mirrors the old init.ts; the runtime endpoints pick the newest active term (ORDER BY id DESC) — only differs if more than one term is active."


### backend-services-data #13 — students.term_id and steps.term_id deliberately lack FOREIGN KEYs but the schema doesn't say so

*Location:* `Api/Data/schema.sql:44`


MERGED from 2 findings. student_progress, integration_events, and step_api_checks all declare FKs; the two term_id columns (lines 44, 91) do not. Integrity is application-managed (TermsController.Delete guards and cleans up in a transaction), mirroring the old Postgres schema, but the otherwise thorough translation notes never mention it.


**Fix:** Add next to each term_id column (or in the header translation notes): "-- no FK to terms on purpose: mirrors the old schema; terms are deleted via the guarded admin flow (TermsController.Delete blocks when students are assigned and removes the term's steps/progress itself)."


### backend-services-data #14 — Indentation artifacts from the Error-helper removal in all three auth filter attributes

*Location:* `Api/Auth/AdminAuthAttribute.cs:73`


MERGED from 2 findings. AdminAuthAttribute.cs:73 and IntegrationAuthAttribute.cs:85 declare nested row classes at 8-space indent; StudentAuthAttribute.cs ends with a stray blank line and a 4-space-indented closing brace. Same glitch in three sibling auth files looks like an unfinished edit.


**Fix:** Re-indent the two nested class declarations to 4 spaces and move StudentAuthAttribute's closing brace to column 0 (or run dotnet format over Api/Auth).


### backend-services-data #15 — No direct tests for TryBeginRun (CAS loop) or the Json JS-truthiness helpers

*Location:* `Api/Services/ApiCheckRunner.cs:79`


The client's parseMaybeJson got a dedicated unit test file but the backend helpers from the same fix wave did not: TryBeginRun (the race fix, pure in-memory) has zero references under tests/, and Json.IsTruthy/TryGetProperty/NullIfEmpty encode subtle JS-mirroring semantics exercised only indirectly.


**Fix:** Add tests/Api.IntegrationTests/HelperTests.cs (plain xUnit, no WebAppFixture): assert Json.IsTruthy over the JS-boundary values (true/false/null/0/1/""/"0"/object/array), Json.TryGetProperty on a non-object body, and that two TryBeginRun calls for one student yield exactly one true while status is 'running'.


---

## Batch: frontend (components + state + types) (30 items)

**Files:** `client/src/pages/admin/AdminLogin.vue`, `client/src/pages/admin/AdminLocalLogin.vue`, `client/src/stores/auth.ts`, `client/src/stores/toast.ts`, `client/src/pages/admin/TermStepsTab.vue`, `client/src/pages/admin/StepRow.vue (new)`, `client/src/pages/admin/StepForm.vue`, `client/src/pages/admin/ApiCheckConfig.vue`, `client/src/pages/admin/TermHeader.vue`, `client/src/pages/admin/CloneTermModal.vue`, `client/src/pages/admin/ExportButton.vue`, `client/src/pages/admin/StudentsTab.vue`, `client/src/pages/admin/StudentDetail.vue`, `client/src/pages/admin/StudentDrillDown.vue`, `client/src/pages/admin/AdminUsersTab.vue`, `client/src/pages/admin/AnalyticsTab.vue`, `client/src/pages/admin/SummaryStats.vue`, `client/src/pages/admin/AuditTimeline.vue`, `client/src/pages/admin/AuditLogTab.vue`, `client/src/pages/admin/AdminPage.vue`, `client/src/pages/admin/charts/chartTheme.ts`, `client/src/pages/admin/charts/types.ts (new)`, `client/src/pages/admin/charts/StepCompletionChart.vue`, `client/src/pages/admin/charts/CompletionTrendChart.vue`, `client/src/pages/admin/charts/BottleneckChart.vue`, `client/src/pages/admin/charts/CohortDistributionChart.vue`, `client/src/pages/admin/charts/CohortComparisonChart.vue`, `client/src/pages/admin/charts/CompletionVelocityChart.vue`, `client/src/pages/admin/charts/DeadlineRiskChart.vue`, `client/src/pages/admin/charts/StalledStudentsChart.vue`, `client/src/pages/RoadmapPage.vue`, `client/src/components/PublicRoadmapPreview.vue`, `client/src/components/SignInCard.vue (new)`, `client/src/components/HighContrastToggle.vue`, `client/src/components/StepDetailPanel.vue`, `client/src/composables/useAdminApi.ts`, `client/src/composables/useProgress.ts`, `client/src/types/api.ts`, `client/src/utils/json.ts`, `client/src/utils/json.test.ts`, `client/src/utils/errors.ts (new)`, `client/src/utils/initials.ts (new)`, `client/eslint.config.js`, `client/tailwind.config.js`

**Verification after fixing:** `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2/client && npm run test && npm run lint && npm run build`


### frontend (components + state + types) #1 — AdminLogin's popup-to-redirect fallback misses the stuck-spinner guard its twin in stores/auth.ts already has

*Location:* `client/src/pages/admin/AdminLogin.vue:85`


VERIFIED by reading both. stores/auth.ts wraps loginRedirect in try/catch that resets redirecting = false with a WHY comment; AdminLogin.vue does redirecting = true; await loginRedirect; return with no guard — if loginRedirect throws, finally sees redirecting=true and never clears ssoLoading, leaving the admin button stuck on 'Signing in...' forever. A diagnosed-and-fixed bug surviving in its duplicate.


**Fix:** Mirror the store's fix: wrap loginRedirect in try/catch, set redirecting = false before re-throwing, copy the WHY comment. Add a cross-reference comment at the top of both functions: "NOTE: kept in sync with stores/auth.ts ssoLogin — admin and student sessions are deliberately separate."


### frontend (components + state + types) #2 — moveStep indexes the full step list with an index from the filtered visible list — arrows can swap the wrong rows

*Location:* `client/src/pages/admin/TermStepsTab.vue:147`


VERIFIED by reading the code. The template passes the draggableSteps (= visibleSteps, which hides inactive steps by default) index to moveStep, but moveStep sorts ALL steps ([...steps.value].sort(...)) and swaps order[index]/order[swapIndex]. With hidden inactive steps interleaved, the visible index points at the wrong element, swapping sort_order with a hidden step. The down-arrow disabled check uses visibleSteps.length while moveStep bounds-checks sorted.length — two universes in one interaction.


**Fix:** Make moveStep operate on the visible list: accept step.id, find it and its neighbor in the sort_order-sorted visibleSteps array, swap those two steps' sort_order values, and send the same payload shape to PUT /steps/reorder. Bounds check and template disabled check then agree by construction.


### frontend (components + state + types) #3 — Failed admin data loads silently render misleading empty states across six components

*Location:* `client/src/pages/admin/StudentsTab.vue:89`


VERIFIED in StudentsTab: fetchStudents ends in catch { // ignore }, so an API failure shows 'No students in this term yet.' — an affirmative wrong statement. Same pattern: StudentDrillDown.vue:52/:73, AnalyticsTab.vue:60 (four blank charts), AuditLogTab.vue:104, AdminUsersTab.vue:37, StudentDetail.vue:115/123/134. Contradicts the project's own stated policy in AdminPage.vue:134 ('Surface it — silently swallowing leaves tabs stuck...').


**Fix:** In each listed catch, import useToastStore and call toast.error with the established voice: 'Could not load students. Please try again.', 'Could not load analytics. Please try again.', 'Could not load the audit log. Please try again.', 'Could not load admin users. Please try again.', 'Could not load student details. Please try again.'


### frontend (components + state + types) #4 — TermStepsTab mixes alert(), silent catches, and nothing for failures; ExportButton alerts; preflight wording differs from server 409

*Location:* `client/src/pages/admin/TermStepsTab.vue:112`


MERGED from 3 findings. alert(err.message) for save/create/delete term (112, 231, 244, 255) and bare catch { // ignore } for deactivate/restore/duplicate/moveStep/bulk status (122, 132, 142, 160, 216) — a failed Duplicate gives zero feedback. ExportButton.vue:27 also alerts. Line 244's client-side preflight rewords the server's 409 'Cannot delete a term that still has students assigned'. Codebase standard is the toast store (StepToggle, StudentDetail, AdminPage).


**Fix:** Import useToastStore; replace the alert() calls and each empty catch with toast.error using the 'Could not <verb> <object>. Please try again.' voice ('Could not deactivate that step...', 'Could not restore that step...', 'Could not duplicate that step...', 'Could not reorder steps...', 'Could not update the selected steps...'). For the delete-term preflight, reuse one sentence for client and server paths (or drop the preflight and surface the 409 body). Same change in ExportButton.vue. Keep confirm()/prompt() flows; the debounced drag-reorder self-heal at :177 and the commented unmount flush stay silent.


### frontend (components + state + types) #5 — StudentsTab fires duplicate racing fetches (watcher + explicit handler calls); 'overrides' is a React setState leftover

*Location:* `client/src/pages/admin/StudentsTab.vue:104`


VERIFIED by reading the code: a watcher on [page, sortBy, overdueOnly, termId] calls fetchStudents(), and handleSearch/handleSearchClear/handleSortChange/handleOverdueToggle mutate those refs AND call fetchStudents({overrides}) — two identical HTTP requests racing to set students.value. Vue refs update synchronously, so the overrides param solves a React-timing problem that doesn't exist. Also: fetchStudents response is `const data: any` despite the existing StudentListItem interface.


**Fix:** One trigger path: delete the overrides parameter; handlers only mutate refs (the watcher fetches). Since search is Enter-triggered and unwatched, handleSearch does `if (page.value === 1) fetchStudents(); else page.value = 1`. Type the fetch: props.api.get<{ students: StudentListItem[]; total: number }>(url).


### frontend (components + state + types) #6 — Student-facing optional-step toggle fails with no feedback; thrown Error message is constructed then discarded

*Location:* `client/src/pages/RoadmapPage.vue:167`


handleOptionalStepStatusChange throws new Error('Unable to update optional step') then swallows it with catch { // ignore for now } — dead message text plus a stale-reading 'for now'. The admin-side twin (StepToggle.vue:55-58) toasts with a comment explaining rollback.


**Fix:** Import useToastStore and replace the silent catch with toast.error('Could not update that step. Please try again.') matching StepToggle.vue verbatim; delete the 'for now' comment.


### frontend (components + state + types) #7 — Network-level fetch failures surface raw browser text ('Failed to fetch') in admin error slots

*Location:* `client/src/composables/useAdminApi.ts:33`


useAdminApi normalizes HTTP error bodies but a network rejection propagates the raw TypeError, so consumers rendering err.message show engine-specific text ('Failed to fetch' / 'Load failed' / 'NetworkError...'). The login screens already map this condition to 'Cannot connect to server.' — the chosen wording just isn't applied past login.


**Fix:** In useAdminApi.request wrap the fetch: let res: Response; try { res = await fetch(...) } catch { throw new Error('Cannot connect to server.') }. One change normalizes every consumer.


### frontend (components + state + types) #8 — Login screen displays raw MSAL exception messages verbatim; fallback period mismatches backend string

*Location:* `client/src/pages/admin/AdminLogin.vue:66`


MERGED from 2 findings. Lines 66/98 render err.message from MSAL — long AADSTS/technical strings on an unauthenticated screen, the only place third-party exception text reaches users unfiltered. Separately, AdminLogin.vue:119 and AdminLocalLogin.vue:26 fall back to 'Invalid credentials.' while the backend returns 'Invalid credentials' (no period), so the same form flickers between two near-identical strings.


**Fix:** console.error(err) for diagnostics and show fixed strings: 'SSO initialization failed. Please try again or contact IT.' / 'SSO login failed. Please try again or contact IT.' (keep the user_cancelled special case). Change both 'Invalid credentials.' fallbacks to 'Invalid credentials' so the fallback is byte-identical to the contract string it stands in for.


### frontend (components + state + types) #9 — catch (err: any) idiom at 14 sites instead of one narrowing helper

*Location:* `client/src/utils/errors.ts:1`


14 lint warnings are the same pattern: catch (err: any) { error.value = err.message } at AdminLogin.vue 65/80/94, AdminUsersTab.vue 69/86, ApiCheckConfig.vue 149/170/184, CloneTermModal.vue 70/113, TermHeader.vue 56, TermStepsTab.vue 111/230/254. All errors reaching them are Error instances from useAdminApi or fetch.


**Fix:** Create client/src/utils/errors.ts: export function errorMessage(err: unknown, fallback = 'Request failed'): string { return err instanceof Error && err.message ? err.message : fallback } with a WHY comment (all client-thrown request errors are Error instances from useAdminApi). Convert every site to catch (err) { error.value = errorMessage(err, '<existing per-site fallback>') }. AdminLogin.vue:80 already narrows with instanceof BrowserAuthError — just drop the : any; line 94-95 becomes err instanceof BrowserAuthError && err.errorCode === 'user_cancelled'.


### frontend (components + state + types) #10 — Local any-typed AdminApi interfaces in StepForm and ApiCheckConfig shadow the real typed one (18 of 67 warnings)

*Location:* `client/src/pages/admin/ApiCheckConfig.vue:4`


MERGED from 2 findings; VERIFIED both declarations exist (ApiCheckConfig.vue:4, StepForm.vue:10) with all-any signatures while 16 sibling components import the typed AdminApi from composables/useAdminApi.ts. Silently disables type checking on every API call in these two files and implies a different API client.


**Fix:** Delete both local interfaces; import type { AdminApi } from '../../composables/useAdminApi'. In ApiCheckConfig use explicit generics at the call sites: props.api.get<ApiCheckConfigData>(...) (line ~70) and props.api.post<TestResultData>(...) (line ~180). StepForm only forwards api, so no call-site changes.


### frontend (components + state + types) #11 — ApiCheckConfig: bare '••••••••' sentinel x8, extractedValue: any, and a hand-rolled success banner beside an unused toast.success

*Location:* `client/src/pages/admin/ApiCheckConfig.vue:79`


MERGED from 3 findings. The masked-credentials sentinel the keep-stored-credentials protocol hinges on appears as a raw literal in fetchConfig and handleSave; a typo in any occurrence silently breaks it. TestResultData.extractedValue is any but only ever JSON.stringify'd. ApiCheckConfig also reimplements success feedback with setTimeout(3000) while toast.ts exports success/info that nothing in the repo calls.


**Fix:** Hoist const MASKED_CREDENTIALS = '••••••••' // sentinel the server returns/accepts for 'unchanged' and use it at all eight sites. Change extractedValue?: any to extractedValue?: unknown. Either switch the two success messages to toast.success and delete the local timer plumbing, or keep the inline banner with a one-line WHY ('inline so the confirmation appears next to the form being edited') — and in stores/toast.ts remove or comment the then-unused info() export.


### frontend (components + state + types) #12 — Step summary card duplicated ~65 lines between the draggable and read-only branches

*Location:* `client/src/pages/admin/TermStepsTab.vue:541`


The step row body (icon box, title + badges, description/deadline, tag chips with mode pills) appears verbatim at lines 440-496 and 550-606 including identical conditional card classes. Any rendering change must be made twice; this duplication is most of why the file is the largest component in the codebase. Deduplication, not layering — consistent with the prior audits' duplicate-function cleanup.


**Fix:** Extract the shared body into a small presentational pages/admin/StepRow.vue taking a step prop (reusing the existing parseTags helper). Draggable branch wraps it with grip/checkbox/arrows/actions; the viewer branch renders it bare. No behavior change.


### frontend (components + state + types) #13 — Entire sign-in card (~110 lines) duplicated inside PublicRoadmapPreview

*Location:* `client/src/components/PublicRoadmapPreview.vue:287`


The sign-in card (lock header, SSO button + spinner, ssoError, divider, dev-login form, loginError) is written out at lines 135-245 and 287-400, differing only in input ids while both reuse id="login-error" — the mutual exclusivity that prevents id collision is invisible, and the next edit to one copy will drift.


**Fix:** Extract components/SignInCard.vue holding the markup and its own loginName/loginEmail/loginError/loggingIn state, taking onLogin as a prop (or re-emitting); pass an idSuffix prop in case both ever co-render. PublicRoadmapPreview keeps only placement logic.


### frontend (components + state + types) #14 — Shared charts/types.ts: DrillDownPayload declared 10 times with filterValue: any; AnalyticsTab holds chart payloads in ref<any>

*Location:* `client/src/pages/admin/AnalyticsTab.vue:26`


MERGED from 2 findings. The two-field DrillDownPayload { filterType: string; filterValue: any } is declared in all 8 chart SFCs, again in AnalyticsTab (DrillDownState), and as filterValue?: any in StudentDrillDown — but every producer passes a step id (number) or bucket/tag/date string, serialized into a query string the API reads as string. AnalyticsTab.vue:26-29 keeps stepCompletion/trend/bottlenecks/cohort as ref<any>, voiding the charts' typed data props; the only reason is that script-setup types can't be exported.


**Fix:** Create client/src/pages/admin/charts/types.ts (one concept: analytics chart data contracts) exporting DrillDownPayload { filterType: string; filterValue: string | number }, StepCompletionData, TrendPoint, BottleneckData, ProgressBucket per the chart prop shapes. Import in the 8 charts (deleting local declarations), type StudentDrillDown's prop as filterValue?: string | number, and type AnalyticsTab refs (ref<StepCompletionData | null> etc.) with props.api.get<T> generics in the Promise.all.


### frontend (components + state + types) #15 — Audit log entry/details shapes duplicated in three components behind Record<string, any>; details vocabulary undocumented

*Location:* `client/src/pages/admin/AuditTimeline.vue:9`


MERGED from 2 findings. AuditLog declared twice (AuditTimeline.vue:3-10, AuditLogTab.vue:6-13); StudentDetail uses ref<any[]>. The actual vocabulary of details keys — the contract the server writes — exists only implicitly in getSummary/getDetailRows's 19-key if-chain. Additionally the template calls parseDetails(log.details) three times and getDetailRows twice per log per render.


**Fix:** Add AuditLogDetails (the 19 optional keys) and AuditLogEntry { id, entity_type, action, changed_by, created_at, details: string | AuditLogDetails } to src/types/api.ts (its header already says it holds shared API response shapes). Import in AuditTimeline, AuditLogTab, and StudentDetail (ref<AuditLogEntry[]>, get<{ logs: AuditLogEntry[]; total?: number }>). In AuditTimeline add a rows computed mapping each log to { log, summary, detailRows } once and iterate over that.


### frontend (components + state + types) #16 — StepForm save payload is Record<string, any> on both ends; emoji callback typed any despite library types

*Location:* `client/src/pages/admin/StepForm.vue:44`


MERGED from 2 findings. StepForm emits (e: 'save', data: Record<string, any>) and TermStepsTab.handleSaveStep(data: any) PUT/POSTs it — yet handleSubmit builds a fixed 15-field object, the exact kind of shape types/api.ts exists to name. Also StepForm.vue:143 types the emoji-picker callback as any although vue3-emoji-picker exports EmojiExt with the i: string field read here.


**Fix:** Add StepSavePayload to src/types/api.ts with the 15 fields exactly as handleSubmit builds them (title, icon, description, deadline, deadline_date, guide_content, links: null, required_tags, required_tag_mode: 'any' | 'all', excluded_tags, sort_order?, contact_info, term_id, is_public: 0 | 1, is_optional: 0 | 1); use it in the emit declaration and in TermStepsTab.handleSaveStep. Import type { EmojiExt } from 'vue3-emoji-picker' for onEmojiSelect.


### frontend (components + state + types) #17 — TermStepsTab discards the typed contracts its children declare (handleSaveTerm: any, handleCloned: any)

*Location:* `client/src/pages/admin/TermStepsTab.vue:235`


TermHeader types its save callback and CloneTermModal types its cloned emit, but the parent handlers take any (lines 235, 259), so vue-tsc never verifies the parent matches the child and a reader can't see result.term/result.steps shapes.


**Fix:** Type the handlers to mirror the children: handleSaveTerm(termId: number, data: { name: string; start_date: string; end_date: string }) and handleCloned(result: { term: TermItem; steps: StepItem[] }) — both shapes already exist as local interfaces.


### frontend (components + state + types) #18 — StudentDetail fetches three responses as any, types the profile as Record<string, any>, and inlines an 8-line initials chain in the template

*Location:* `client/src/pages/admin/StudentDetail.vue:87`


MERGED from 2 findings. The /students/{id}/progress shape is fixed ({ student, manualTags, derivedTags, mergedTags, progress }) and the template reads nine specific profile fields — with Record<string, any>, a typo renders silently blank. Lines 205-212 also contain the worst of three getInitials copies: an 8-line method chain inside a mustache, the only copy without a '?' fallback.


**Fix:** Declare local StudentProfile and StudentProgressResponse interfaces per the API shape and use props.api.get<StudentProgressResponse>; type PROFILE_FIELDS as { key: keyof StudentProfile; label: string }[]; type the two audit fetches with AuditLogEntry. Replace the inline initials chain with the shared util from the initials item.


### frontend (components + state + types) #19 — getInitials logic written three times across admin components

*Location:* `client/src/utils/initials.ts:1`


The split/map/join/slice/toUpperCase chain exists in StudentsTab.vue (getInitials, ~line 126), inline in StudentDetail.vue's template, and as initials in AdminUsersTab.vue. Mirrors how links.ts/json.ts host tiny shared helpers, so a shared util fits the house style.


**Fix:** Create client/src/utils/initials.ts with one implementation including the '?' empty-name fallback; import it in StudentsTab, AdminUsersTab, and StudentDetail.


### frontend (components + state + types) #20 — StudentDrillDown ignores the get<T> generic for a three-field response it already half-types

*Location:* `client/src/pages/admin/StudentDrillDown.vue:47`


DrillDownStudent is defined but both fetches use any (.then((data: any)) at 47, const data: any at 64) for the same { students, title, total } shape.


**Fix:** Add interface DrillDownResponse { students: DrillDownStudent[]; title: string; total: number } and call props.api.get<DrillDownResponse>(...) at both sites; drop the any annotations. (filterValue prop becomes string | number via charts/types.ts.)


### frontend (components + state + types) #21 — Mixed emit vs callback-prop conventions; :onClose bound to a declared emit

*Location:* `client/src/pages/admin/AnalyticsTab.vue:179`


StudentDrillDown declares a close emit but AnalyticsTab binds :onClose="() => (drillDown = null)" — works only via Vue's @close→onClose compilation, which a reader must know to trust. TermHeader declares onSave/onDelete function props while TermStepsTab binds them as @save/@delete; charts take onDrillDown callback props; StepDetailPanel mixes both. Scoped fix: make the misleading binding conventional and label the deliberate exceptions — no sweeping refactor.


**Fix:** Change AnalyticsTab line 179 to @close="drillDown = null". In TermHeader keep the callback props (they're awaited for the saving spinner — legitimate) but bind them as :on-save/:on-delete with a one-line comment explaining why a callback prop is used; add the same comment to the charts' onDrillDown and StepDetailPanel's onOptionalStepStatusChange.


### frontend (components + state + types) #22 — SummaryStats builds throwaway CardDef objects inline so getColor can string-match a label

*Location:* `client/src/pages/admin/SummaryStats.vue:41`


getColor only does something when card.label === 'Avg. Completion' and the average is 0; the template constructs three inline object literals (with a never-read value field) purely to feed it — residue of an abandoned card-array refactor.


**Fix:** Delete getColor and CardDef; hardcode the static classes on the Total Students and Active Steps cards; add const avgColor = computed(() => stats.value?.avgCompletionPercent === 0 ? 'text-csub-gray' : 'text-csub-gold') keeping the existing 'suppress gold at 0%' comment.


### frontend (components + state + types) #23 — Identical chart.js tooltip/borderRadius/onHover boilerplate repeated across seven chart components

*Location:* `client/src/pages/admin/charts/chartTheme.ts:1`


Seven charts repeat the exact tooltip style block (including a hardcoded '#374151' that isn't in the theme), the BAR_RADIUS expansion, and the pointer-cursor onHover. chartTheme.ts exists precisely for shared styling but stops at colors. Shared constants, not an abstraction — charter-compatible.


**Fix:** Export TOOLTIP_STYLE (spread as tooltip: { ...TOOLTIP_STYLE, callbacks }), BAR_BORDER_RADIUS (the pre-expanded object), and pointerOnHover from chartTheme.ts alongside the existing color constants; use them in the seven charts.


### frontend (components + state + types) #24 — parseMaybeJson returns parsed null instead of the fallback, diverging from the server's Json.SafeParse it claims to mirror

*Location:* `client/src/utils/json.ts:8`


VERIFIED by reading the file: return JSON.parse(value) as T with no null-coalesce, so parseMaybeJson('null', []) returns null typed as an array — and the render-path computeds (CurrentStepCallout links.value.length, TimelineStep primaryAction) dereference it: exactly the mid-render crash class the migration was sold as eliminating. Server's Json.SafeParse does Deserialize<T>(value) ?? fallback. No test covers the 'null' input.


**Fix:** Change line 8 to return (JSON.parse(value) as T) ?? fallback (nullish coalescing preserves legitimate false/0 results) and add a regression case to client/src/utils/json.test.ts: expect(parseMaybeJson('null', [])).toEqual([]).


### frontend (components + state + types) #25 — Unguarded JSON.parse of sessionStorage in AdminPage missed by the parseMaybeJson sweep

*Location:* `client/src/pages/admin/AdminPage.vue:77`


return stored ? JSON.parse(stored) : null reads csub_admin_user during <script setup>; a corrupted value throws during setup and breaks the entire /admin page until storage is cleared. Inconsistent even within the file (the token read goes through a try/catch'd parse).


**Fix:** Replace with return parseMaybeJson<AdminUser | null>(stored, null) importing from '../../utils/json'; optionally remove the bad key when the parse yields the fallback.


### frontend (components + state + types) #26 — useProgress: duplicate completed_at state, value-restating POLL_INTERVAL comment, undocumented NULL-status default, and an inaccurate 'same fallback' claim

*Location:* `client/src/composables/useProgress.ts:97`


MERGED from 4 findings. (1) fetchProgress builds both progressMap (Map<number,...>) and completedDates (Record<string,...>) from the same response — apparent vestigial duplication that could drift. (2) POLL_INTERVAL's comment translates ms to seconds without the reason. (3) Line 92's (p.status || 'completed') default is explained only in schema.sql. (4) The stepApplies comment claims the client uses 'the same fallback' as the server, but the server falls back to [] while the client uses null (outcomes agree; literal values don't).


**Fix:** (1) Either derive completedDates as a computed from progressMap, or add: '// duplicate of progressMap's completed_at, kept as a plain Record because the timeline components take it as a prop (ported API).' (2) Comment: '// 30s: fast enough that integration/API-check completions show up while a student watches the page, slow enough to keep polling load trivial (same cadence as the old hook).' (3) Add: '// NULL/empty status is legacy data and means completed (schema default — see schema.sql student_progress.status).' (4) Reword to state the semantic: 'Malformed tag JSON degrades to no-rule — the server does the same (Json.SafeParse falls back to an empty list), so client and server stay in agreement.'


### frontend (components + state + types) #27 — Client-authored failure strings use three competing voices

*Location:* `client/src/pages/admin/TermHeader.vue:57`


Toasts say 'Could not X. Please try again.'; alerts say 'Failed to X.'; inline fallbacks say 'Failed to save term' (no period); useProgress mixes 'Failed to load checklist steps.' with 'Unable to connect. Please try again later.' Scope: client-authored fallbacks only — backend contract strings are displayed verbatim and must NOT be edited.


**Fix:** Standardize remaining client-authored fallbacks on the dominant toast voice: TermHeader 'Could not save the term. Please try again.', CloneTermModal 'Could not load steps. Please try again.' / 'Could not clone the term. Please try again.', useProgress 'Could not load checklist steps. Please try again.' Add a short comment in stores/toast.ts stating the convention (sentence case, 'Could not <verb> <object>. Please try again.'; backend contract strings shown verbatim) to prevent regression.


### frontend (components + state + types) #28 — HighContrastToggle renders a ternary with identical branches

*Location:* `client/src/components/HighContrastToggle.vue:38`


VERIFIED: {{ enabled ? 'HC' : 'HC' }} at line 33 (finding said 38). A reader stops to figure out whether a state-dependent label was lost in the port.


**Fix:** Replace the expression with the plain text HC.


### frontend (components + state + types) #29 — selectedStepList computed is filteredSteps plus a dead empty-array branch

*Location:* `client/src/pages/RoadmapPage.vue:124`


Every consumer only runs while selectedStep is non-null, so the [] branch is unreachable; a reader diffs it against filteredSteps looking for a distinction that doesn't exist.


**Fix:** Delete the computed and use filteredSteps directly in the four places; selectedStepIndex already returns -1 when nothing is selected, preserving behavior.


### frontend (components + state + types) #30 — tailwind.config.js mixes ESM export with CJS require(); an eslint override exists solely to permit it — then fix the misattributing eslint comment and promote no-explicit-any

*Location:* `client/eslint.config.js:22`


MERGED from 2 findings; VERIFIED both. tailwind.config.js uses export default with plugins: [require('@tailwindcss/typography')] under "type": "module" — it only runs because Tailwind loads configs through jiti, and eslint.config.js's app/tooling-configs block exists only to permit that one require. Separately, the no-explicit-any justification ('Chart.js payloads are intentionally dynamically shaped') is no longer true — all 8 charts are fully typed with ChartData/ChartOptions; after this batch's type fixes the leave-as-any list is empty.


**Fix:** In tailwind.config.js: import typography from '@tailwindcss/typography' and plugins: [typography]; delete the app/tooling-configs block from eslint.config.js. After the other type items in this batch land, rewrite the rule-tweaks comment (e.g. 'no-explicit-any is enforced; types/api.ts and charts/types.ts hold the shared response shapes') and promote '@typescript-eslint/no-explicit-any' to 'error' — do this LAST in the batch so npm run lint stays green.


---

## Batch: config-and-docs (9 items)

**Files:** `docs/SETUP.md`, `docs/API-GUIDE.md`, `.env.example`, `client/.env.example`, `client/vite.config.ts`, `Api/Properties/launchSettings.json`, `Api/appsettings.Development.json`, `Api/Program.cs`

**Verification after fixing:** `cd /Users/aburt1/Desktop/roadmap/CSUB-Runner-Roadmap-V2 && dotnet build Api/Api.csproj && dotnet test && cd client && npm run lint && npm run build`


### config-and-docs #1 — SETUP.md claims MSSQL_SA_PASSWORD has a compose default; compose makes it required with no default

*Location:* `docs/SETUP.md:534`


SETUP.md:534-535 says the sqlserver service is configured with a default Csub_Local_Dev_2026!, contradicting docker-compose.yml:86 (${MSSQL_SA_PASSWORD:?...} — hard-required) and SETUP.md's own lines 220-221. The value is actually the hardcoded host-process dev password (appsettings.Development.json, WebAppFixture.cs). An operator would assume docker compose up works without setting it.


**Fix:** Reword to: "The sqlserver service requires MSSQL_SA_PASSWORD (no default — set it in .env; use Csub_Local_Dev_2026! locally so dotnet run/dotnet test match), plus ACCEPT_EULA=Y and MSSQL_PID=Developer."


### config-and-docs #2 — SETUP.md env table claims Integration defaults that do not apply to the compose stack the table documents

*Location:* `docs/SETUP.md:524`


The table says Integration__DefaultName/DefaultKey have defaults, but the api container runs ASPNETCORE_ENVIRONMENT=Production and Seeder skips seeding any integration client in Production when the key is unset — so a user sending X-Integration-Key: dev-integration-key gets unexplained 401s. The defaults only apply to host-process dotnet run (Development).


**Fix:** Amend the two rows: "Seeded integration client name/key. No default in either compose stack — in Production the client is only seeded when Integration__DefaultKey is explicitly set (both name and key, per .env.example). The PeopleSoft Dev / dev-integration-key defaults apply only to local dotnet run (Development)."


### config-and-docs #3 — API-GUIDE still says 'All three' health endpoints after the legacy GET /api/health was removed

*Location:* `docs/API-GUIDE.md:685`


VERIFIED by grep: line 671 says 'two unauthenticated health endpoints ... all mounted under /api/health' and line 685 says 'All three are anonymous' — a reader will hunt for a third endpoint that no longer exists.


**Fix:** Line 685: 'All three are anonymous' → 'Both are anonymous'. Line 671: 'all mounted under /api/health' → 'both mounted under /api/health'.


### config-and-docs #4 — .env.example groups dev-only and both-stacks variables under the 'Production stack only' banner

*Location:* `.env.example:38`


Of the variables under the prod-only banner, only PROD_CONNECTION_STRING and WEB_PUBLISH_PORT are prod-only. VITE_AZURE_AD_* are build args in BOTH compose files; VITE_ALLOW_DEV_LOGIN is hardcoded to false in the prod compose so the .env value only affects dev; WEB_API_URL is consumed only by docker-compose.yml (prod hardcodes API_URL) so a prod operator setting it is silently ignored.


**Fix:** Restructure the tail: keep only PROD_CONNECTION_STRING and WEB_PUBLISH_PORT under 'Production stack only'; move VITE_AZURE_AD_* into a 'Both compose stacks (build-time)' section; annotate VITE_ALLOW_DEV_LOGIN with '(dev compose only — the prod compose file pins this to false)' and WEB_API_URL with '(docker-compose.yml only — the prod file hardcodes http://api:8080)'.


### config-and-docs #5 — .env.example invites changing MSSQL_SA_PASSWORD without mentioning the hardcoded host-dev coupling

*Location:* `.env.example:6`


A dev who picks a custom password and then runs dotnet run/dotnet test against the compose sqlserver gets connection failures, because appsettings.Development.json and WebAppFixture.cs hardcode Csub_Local_Dev_2026!. SETUP.md covers this, but not the file a person actually edits at that moment.


**Fix:** Add one comment under MSSQL_SA_PASSWORD: "# For host-process dev (dotnet run / dotnet test against this container) use Csub_Local_Dev_2026! — appsettings.Development.json and the test fixture hardcode it (docs/SETUP.md, Database Setup)."


### config-and-docs #6 — Dead 'https' launch profile advertises ports 7201/5293 that nothing uses; dev URL defined in two places

*Location:* `Api/Properties/launchSettings.json:13`


VERIFIED by reading the file: the https profile is template scaffolding (no doc/script references those ports), and appsettings.Development.json's Urls shadows applicationUrl under minimal hosting, so selecting the https profile still yields http://localhost:3001 — the profile doesn't do what it says. The http profile's applicationUrl is a redundant second definition of the URL SETUP.md attributes to appsettings.


**Fix:** Delete the https profile and remove applicationUrl from the http profile (keep ASPNETCORE_ENVIRONMENT), leaving appsettings.Development.json's Urls as the single source — matching SETUP.md.


### config-and-docs #7 — appsettings.Development.json restates in-code defaults verbatim with no marker of which copy is load-bearing

*Location:* `Api/appsettings.Development.json:15`


Cors:Origin, Admin:DefaultEmail, and Integration:DefaultName/DefaultKey duplicate code fallbacks with identical values (Program.cs, Seeder.cs); a reader changing one copy can't tell whether the other masks the edit. ASP.NET Core's JSON config reader accepts // comments.


**Fix:** Add one comment line above the relevant entries: "// These mirror the in-code dev fallbacks (Program.cs / Seeder.cs); the JSON wins while present, the code fallback takes over if a key is removed. Kept here so the full dev config is visible in one file."


### config-and-docs #8 — client/.env.example omits VITE_ALLOW_DEV_LOGIN and the shell-only VITE_API_PROXY_TARGET trap

*Location:* `client/.env.example:1`


Only the three VITE_AZURE_AD_* vars are documented. VITE_ALLOW_DEV_LOGIN controls built bundles (import.meta.env.DEV short-circuits it under npm run dev). vite.config.ts reads process.env.VITE_API_PROXY_TARGET before Vite loads .env files, so putting it in client/.env silently does nothing — a trap nothing warns about.


**Fix:** Append two commented entries: "# VITE_ALLOW_DEV_LOGIN=false — only affects built bundles; npm run dev always shows the dev login" and "# VITE_API_PROXY_TARGET must be set in the SHELL (VITE_API_PROXY_TARGET=... npm run dev) — vite.config.ts reads process.env before this file is loaded, so setting it here has no effect." Add the same one-line warning to the comment block in vite.config.ts:4-8.


### config-and-docs #9 — Two hand-maintained CSP strings differ with no cross-pointer; single-process fallback comment overstates what works

*Location:* `Api/Program.cs:151`


nginx.conf.template's CSP allows connect-src login.microsoftonline.com (needed by MSAL); Program.cs's CSP is connect-src 'self' with no pointer back. Program.cs:176-178 claims the order is kept so a single-process wwwroot deployment also works — but in that mode index.html gets the API's CSP, which blocks MSAL, so Azure SSO would fail mysteriously.


**Fix:** Comment-only: extend the Program.cs CSP comment: "A second, SPA-specific CSP lives in client/nginx.conf.template (it additionally allows connect-src login.microsoftonline.com for MSAL) — keep the two in sync. Consequently the single-process wwwroot fallback below works only without Azure SSO."


---

## Dropped / merged during triage (for the record)

- **Backend wording disagreement: "Azure AD SSO is not configured" vs "Azure AD is not configured" for the same 501 case** — Changes an API response body — out of scope for this pass (rules exclude API behavior/contract changes). The two strings live in different controllers serving different clients; a reader-facing question this small does not justify a contract edit.
- **Backend casing disagreement: "Name is required" vs "name is required" for the same field on the same resource** — Both strings are pinned by integration tests (AdminTermsTests.cs:112/:228) — changing one is a deliberate contract change plus a test edit, which the audit rules exclude. The inconsistency is enshrined intentionally enough that it needs an owner decision, not a quality fix.
- **Vestigial 'as count' alias on scalar COUNT queries, inconsistently applied** — Bikeshedding/churn: the alias is harmless, Dapper ignores it, and removing it touches 15+ files across every backend batch for no comprehension gain; it also aids old-vs-new diffability, which the port comments value.
- **Deadline-risk summary counts 'waived' as at-risk but the drilldown excludes it (comprehensibility duplicate)** — Duplicate of the deadline-risk waived-mismatch item in the backend-analytics batch (merged).
- **BuildFilterAsync returns `null!` for unknown filter types instead of using its own exception channel (comprehensibility duplicate)** — Duplicate of the BuildFilterAsync null! item in the backend-analytics batch (merged).
- **Orphaned comments from deleted helpers attached to the wrong functions (comprehensibility duplicate)** — Duplicate of the TermsController orphaned-comments item in the backend-controllers batch (merged; the recent-changes version with the AsString misread consequence was kept).
- **Orphaned comment fragments from deleted helpers stacked above AsString and CollectBodyKeys (readability duplicate)** — Duplicate of the same TermsController item (merged).
- **Method named Stopwatch() returns wall-clock unix milliseconds, not a stopwatch (comprehensibility duplicate)** — Duplicate of the ApiCheckRunner Stopwatch item in the backend-services-data batch (merged).
- **Two private classes both named DynamicSqlParams with different APIs; four mechanisms for the same partial-UPDATE job (sql-consistency duplicate)** — Duplicate of the DynamicSqlParams item in the backend-controllers batch (merged; the Dictionary standardization fix was kept over the rename-only fix).
- **Cohort drilldown bucket boundaries (0.251/0.501/0.751) silently mismatch the summary's (comprehensibility duplicate)** — Duplicate of the cohort bucket-edges item in the backend-analytics batch (merged).
- **Cohort divisor is string-interpolated into SQL while neighboring values are bound parameters (sql-consistency duplicate)** — Duplicate of the {divisor} item in the backend-analytics batch (merged; kept the bind-it fix since the old server bound it, making inlining a port deviation).
- **schema.sql: term_id columns are the only relationships without FOREIGN KEYs, and the omission is uncommented (sql-consistency duplicate)** — Duplicate of the schema.sql term_id FK comment item in the backend-services-data batch (merged).
- **Indentation glitches left behind by the Error-helper removal in all three auth filters (recent-changes duplicate)** — Duplicate of the auth-filter indentation item in the backend-services-data batch (merged).
- **Duplicate any-typed AdminApi interface shadows the real, fully-typed one (type-quality duplicate)** — Duplicate of the local AdminApi interfaces item in the frontend batch (merged; kept the type-quality version's call-site generics detail).
- **Three contradictory 'active step' predicates with no comment explaining when NULL counts as active (sql-consistency duplicate)** — Duplicate of the QueryHelpers is_active item in the backend-controllers batch (merged; its StudentVisibleStepFilter fix was adopted).
- **Three different SQL definitions of 'completed' inside AnalyticsController, none commented (sql-consistency duplicate)** — Duplicate of the three-definitions-of-done item in the backend-analytics batch (merged, together with the Stats()-counts-any-status finding).
- **Stats() average counts progress rows of ANY status while CohortSummary filters to completed/waived (comprehensibility duplicate)** — Merged into the three-definitions-of-done item in the backend-analytics batch — same root observation, comment-only fix.
- **Analytics endpoints silently alternate between 'completed-only', 'completed+waived', and 'any progress row' (readability duplicate)** — Merged into the three-definitions-of-done item in the backend-analytics batch.
- **deadline_date compared as a raw string in ListStudents but CAST to date everywhere else (readability duplicate)** — Merged with the sql-consistency deadline_date finding and split per batch ownership: TRY_CAST + comment in AnalyticsController (backend-analytics), explanatory comment in StudentsController (backend-controllers).
- **Query selects s.id AS s_id into a mapped property nobody reads (readability duplicate)** — Merged into the dead-columns item in the backend-services-data batch (the sql-consistency version also covering IntegrationAuthAttribute was kept).
- **alert() used for errors in admin UI that has a toast store (error-consistency duplicate)** — Merged into the single TermStepsTab error-reporting item in the frontend batch (alerts + silent catches + ExportButton + preflight wording).
- **Mutation failures silently swallowed in TermStepsTab while sibling actions in the same file alert (error-consistency duplicate)** — Merged into the same TermStepsTab error-reporting item.
- **Admin step/term actions swallow errors silently or use alert() (frontend-readability duplicate)** — Merged into the same TermStepsTab error-reporting item; its RoadmapPage observation became the standalone RoadmapPage toast item.
- **sameCompletedAt null-means-keep logic and a no-op `?? null` make the noop check hard to follow (comprehensibility duplicate)** — Merged with the `current.note ?? null` no-op finding into one Progress.cs item in the backend-services-data batch.
- **DrillDownPayload.filterValue typed `any` in 10 places (standalone)** — Not dropped on merit — merged into the charts/types.ts item in the frontend batch together with the AnalyticsTab ref<any> finding, since both are fixed by the same new shared types file.
- **AuditTimeline re-parses details JSON and rebuilds detail rows multiple times per log (standalone)** — Not dropped on merit — merged into the AuditLogEntry types item in the frontend batch; the rows computed and the shared types touch the same files and land together.
- **"Invalid credentials." (frontend fallback) vs "Invalid credentials" (backend) flicker on the same login form** — Merged into the AdminLogin raw-MSAL-messages item in the frontend batch (same files, both are login-screen message hygiene); fix keeps the backend string untouched.
- **Toast store exposes success/info that nothing uses while ApiCheckConfig hand-rolls its own success notification** — Merged into the consolidated ApiCheckConfig item in the frontend batch (sentinel + extractedValue + success banner), which also covers the stores/toast.ts cleanup.
- **POLL_INTERVAL comment restates the value instead of the reason / stepApplies 'same fallback' claim** — Merged into the single useProgress.ts comments item in the frontend batch along with the completedDates duplication and the NULL-status client comment — four small fixes, one file.
- **eslint.config.js comment misattributes the `any` debt to Chart.js (standalone)** — Not dropped on merit — merged with the tailwind require() finding into one frontend-batch item because both edit client/eslint.config.js, which may appear in only one batch; the warn→error promotion is sequenced last so lint stays green.
