# Performance baseline (register, search, views, exports, configuration)

Status: **Measured on SQLite (workspace) and on SQL Server 2022 (GitHub-hosted runner; first baseline CI run 22, PER-002 before/after CI runs 24 to 32).** PER-002 (the SQL Server query-plan review) is **done**; the full evidence is in `docs/PERFORMANCE_REVIEW.md`. This page records what was measured and how to measure again. It states **no service-level target**: the owner's figure for PER-002 (configured-field text search below 500 ms median) was treated as a target to report against and is **met** on the benchmark operation (section "PER-002: before and after"). A baseline is evidence about one machine and one engine, not a promise.

## What was measured

A synthetic tenant (no real names or amounts) with **10,000 contracts**, 40 suppliers, two categories, and **35,000 configured-field values** (service tier, score, memo on every contract; renewal date on half). Operations run in-process through the application services (no HTTP, no network), as the signed-in person, so the access rules are applied exactly as in the application. Each operation is run once unmeasured, then 15 times; the table gives the median and the 95th percentile in milliseconds.

The harness is `tests/Ecp.Integration.Tests/PerformanceBaseline.cs` (`PerformanceBaselineTests_Sqlite`, and `PerformanceBaselineTests_SqlServer` which runs when `ECP_TEST_SQL` is set). It writes `artifacts/performance/<provider>.json` (git-ignored). Environment variables: `ECP_PERF_CONTRACTS` (at least 1,000; default 10,000), `ECP_PERF_RUNS` (default 15), `ECP_PERF_DIAG=1` (adds single-clause diagnostics).

## Environment (this run)

| | |
|---|---|
| Engine | **SQLite** (in-memory shared cache, via EF Core). Not SQL Server: planner, locking, row-level security and index behaviour differ |
| Host | Ubuntu 24.04, X64, 4 logical cores, 16 GB, workstation GC |
| Runtime | .NET 10.0.12 (SDK 10.0.112), Debug build of the test assembly |
| Data | 10,000 contracts; 35,000 configured values; seeded in 6.1 s |
| Date | 2026-10-16 (UTC) |

## Results (SQLite, 10,000 contracts)

| Operation | User | Median ms | P95 ms | Rows returned | Total matching |
|---|---|---:|---:|---:|---:|
| Register first page (All Contracts, 25 rows) | procurement administrator | 25.2 | 75.1 | 25 | 10,005 |
| Register first page (All Contracts, 25 rows) | category owner (scoped) | 21.4 | 24.6 | 25 | 10,005 |
| Register page 40 (offset paging) | procurement administrator | 20.6 | 25.3 | 25 | 10,005 |
| Filtered search (value range, type, risk, supplier text) | procurement administrator | 100.0 | 120.2 | 25 | 194 |
| Configured-field filter (tier + score range + date, sorted by score) | procurement administrator | 238.3 | 295.1 | 25 | 204 |
| Configured-field text search (searchable field) | procurement administrator | 175.1 | 198.9 | 25 | 463 |
| Saved view load (personal view, configured columns and filter, 50 rows) | procurement administrator | 73.5 | 88.6 | 50 | 6,667 |
| Shipped view: Expiring Soon | procurement administrator | 6.8 | 9.3 | 25 | 283 |
| Contract detail load (details + configured fields) | category owner | 4.7 | 6.2 | n/a | n/a |
| Export initiation (CSV of a 363-row search) | procurement administrator | 163.7 | 231.8 | 363 | 363 |
| Configuration resolution, warm cache | system | 0.1 | 0.2 | n/a | n/a |
| Configuration resolution, cold (after invalidation) | system | 0.5 | 0.7 | n/a | n/a |

Single-clause diagnostics (same data): tier filter 49.9 ms, score filter 91.0 ms, renewal-date filter 40.7 ms, sort by score 55.1 ms, sort by a core field 36.5 ms.

## Results (SQL Server 2022, 10,000 contracts, GitHub-hosted runner)

Measured by `PerformanceBaselineTests_SqlServer` in CI [run 22](https://github.com/Jeffa90/ContractMGMT/actions/runs/38915166774) (commit 5d695eb), printed from `artifacts/performance/sqlserver.json`. Environment: Ubuntu 24.04.5 LTS (kernel 6.17 Azure), X64, 4 logical cores, about 16 GB, workstation GC, .NET 10.0.13, **Release** build, SQL Server 2022 Developer in a container on the same runner (127.0.0.1, default settings, no tuning), 15 runs per operation after one unmeasured run, seeding 54.1 s, measured 2026-10-16 14:29 UTC. A shared CI runner is noisy; treat these as one sample, not a distribution.

| Operation | User | Median ms | P95 ms | Rows returned | Total matching |
|---|---|---:|---:|---:|---:|
| Register first page (All Contracts, 25 rows) | procurement administrator | 74.5 | 109.4 | 25 | 10,005 |
| Register first page (All Contracts, 25 rows) | category owner (scoped) | 64.7 | 89.4 | 25 | 10,005 |
| Register page 40 (offset paging) | procurement administrator | 74.0 | 93.6 | 25 | 10,005 |
| Filtered search (value range, type, risk, supplier text) | procurement administrator | 356.4 | 420.9 | 25 | 194 |
| Configured-field filter (tier + score range + date, sorted by score) | procurement administrator | 781.0 | 892.3 | 25 | 204 |
| Configured-field text search (searchable field) | procurement administrator | **1,719.1** | **1,859.8** | 25 | 463 |
| Saved view load (personal view, configured columns and filter, 50 rows) | procurement administrator | 220.0 | 241.3 | 50 | 6,667 |
| Shipped view: Expiring Soon | procurement administrator | 58.0 | 76.8 | 25 | 283 |
| Contract detail load (details + configured fields) | category owner | 29.3 | 50.3 | n/a | n/a |
| Export initiation (CSV of a 363-row search) | procurement administrator | 314.5 | 390.8 | 363 | 363 |
| Configuration resolution, warm cache | system | 0.3 | 0.3 | n/a | n/a |
| Configuration resolution, cold (after invalidation) | system | 2.7 | 5.2 | n/a | n/a |

Finding at the time: on SQL Server the **configured-field text search (about 1.7 s) and the configured-field filter (about 0.8 s)** were the slowest operations and 5 to 10 times slower than on SQLite. This first sample came from a slower runner than later runs (the same code measured 0.6 to 0.9 s on other runners), so the table above is **not** the "before" of PER-002; that comparison is made on one runner in the next section. Nothing was tuned for this run.

## PER-002: before and after (SQL Server, same runner)

CI run 31 built three variants of the code on **one** runner and measured each (10,000 contracts, 35,000 configured values, 4 cores, .NET 10.0.13, Release). Medians in milliseconds, 95th percentile in brackets. Runner-to-runner variance is up to 2.4 times for identical code, so only this same-runner comparison is used to judge a change.

| Operation | Before | After change 1 (retained) | Changes 1 + 2 (change 2 rejected) |
|---|---:|---:|---:|
| **Configured-field text search** | **599.2** (609) | **417.1** (426) | 449.9 (453) |
| Configured-field text search, scoped owner | n/a | 408.2 (411) | 417.8 (571) |
| Configured-field filter (tier, score range, date, sorted) | 339.5 | 31.7 | 31.4 |
| Saved view load | 96.5 | 63.3 | 62.9 |
| Export initiation (500-row search) | 123.9 | 46.1 | 43.2 |
| Core-field filtered search | 134 | 87 | 168 |

Final code, run 32 (a different runner; not comparable with the table above): text search 410.4 ms (p95 420), scoped owner 402.8, configured filter 30.1, saved view 61.9, export 42.0, core filtered search 86.3, register first page 33.0, Expiring Soon 23.8.

Logical reads (machine-independent), before (run 24) to after (run 32): text search count 289,235 to 92,128 and page 33,106 to 20,828; configured filter page 85,653 to 4,034 and count 58,971 to 2,216; saved view count 36,363 to 1,174; export page 41,092 to 2,709; core filtered search count 56,375 to 9,172.

| Change | Retained? | Why |
|---|---|---|
| 1. Skip the per-field access test when every grant allows the field's classification (`LevelVisibility.IsOpen`) | **Yes** | Removed the eager index spools and the one-row estimates; reads fell by 3 to 70 times; text search 599 to 417 ms |
| 2. Combine row and configured text matches as `id IN (... UNION ALL ...)` | **No, reverted** | Count reads fell (91,935 to 66,231) but the page statement grew (20,753 to 25,403) and time rose on the same runner (417 to 450 ms; core search 87 to 168 ms) |

**Target met:** the benchmark operation is under 500 ms median (417 ms same-runner, 410 ms on the final-code run; about 17% margin). **Not covered:** a selective second word (for example `note 4242`) still takes 0.77 to 0.85 s because the configured-field set is evaluated per contract in the OR form (root cause RC-2, not fixed; change 2 did not help). Other remaining bottlenecks: one clustered scan of Contract per statement (linear in contracts, no 100,000-contract measurement), small repeated tenancy reads, runner variance. See `docs/PERFORMANCE_REVIEW.md` sections 9 and 10.

## What the measurement found and fixed

The first run showed a **defect**, not a tuning opportunity: a configured-field filter was a correlated lookup per contract, costing O(contracts x matches).

| Operation | Before | After |
|---|---:|---:|
| Configured-field filter (3 clauses + sort) | **27,788 ms** | 238 ms |
| Tier filter alone (3,334 matches) | 11,428 ms | 50 ms |
| Renewal-date filter alone | 5,662 ms | 41 ms |
| Export of a 363-row configured-field search | 5,005 ms | 164 ms |

The filter is now an uncorrelated `ContractId IN (matching values)` subquery (the `Matching` and `NotMatching` helpers in the register filters). A regression guard in the harness fails if a configured-field filter or a 500-row export costs more than max(5 s, 50 x a core-field search): it asserts the *shape* of the cost, not a number.

## What this does and does not show

* It shows that on 10,000 contracts no register operation is slow enough to need caching beyond the published-configuration cache, and that the cost of a configured-field search follows the number of matching values.
* The SQL Server runs are samples on shared 4-core runners with an untuned container. The execution plans, statistics, parameter sensitivity and the row-level security predicate were inspected for the PER-002 review (`docs/PERFORMANCE_REVIEW.md`); the plan report is printed on every CI run. Re-run locally with `tools/sqlserver-up.sh`, then `dotnet test --filter "Category=Performance&Category=SqlServer"`.
* It does not include HTTP, TLS, rendering or browser time; it does not test concurrency (many users at once), 100,000 contracts, or cold start.
* The SQLite numbers come from a Debug build and the SQL Server numbers from a Release build, so the two tables are not a like-for-like comparison of engines.

## Index and query strategy (summary)

* Core columns: organisation, status, dates, reference are indexed; access predicates are translated to the database.
* Configured values: six filtered typed-column indexes plus tenant/organisation/field and batch indexes (see SEARCH_AND_VIEWS.md).
* Configuration: the latest release per tenant, organisation and kind is cached for five minutes with a generation guard; the parsed definitions are cached per release number (a release never changes). A publish invalidates before and after commit; a cache that fails is bypassed (reads fall back to the database) and a failed invalidation fails the publish. The warm/cold rows above show the cost of a miss.
* Not done, deliberately: materialised search tables, full-text indexes, a search service, the `Contract` missing-index suggestion, or caching of contract data. They are not justified by the measurements so far; revisit the index at 100,000 contracts (PRF-01).
