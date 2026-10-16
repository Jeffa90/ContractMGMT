# Performance baseline (register, search, views, exports, configuration)

Status: **Measured on SQLite only. SQL Server: NOT VERIFIED.** This page records what was measured and how to measure again. It states **no service-level target**: a baseline is evidence about one machine and one engine, not a promise.

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
* It does **not** show SQL Server performance. Index choice, the filtered indexes (`WHERE ... IS NOT NULL`), statistics, parameter sniffing and row-level security predicates are SQL Server behaviours that need their own run. Run the harness against the disposable SQL Server (`tools/sqlserver-up.sh`, then `dotnet test --filter "Category=Performance&Category=SqlServer"`) and record the result here next to this one. **Until that is done, SQL Server performance is NOT VERIFIED.**
* It does not include HTTP, TLS, rendering or browser time; it does not test concurrency (many users at once), 100,000 contracts, or cold start.
* The Debug build is slower than Release; the numbers are a floor on confidence, not a ceiling.

## Index and query strategy (summary)

* Core columns: organisation, status, dates, reference are indexed; access predicates are translated to the database.
* Configured values: six filtered typed-column indexes plus tenant/organisation/field and batch indexes (see SEARCH_AND_VIEWS.md).
* Configuration: the latest release per tenant, organisation and kind is cached for five minutes with a generation guard; the parsed definitions are cached per release number (a release never changes). A publish invalidates before and after commit; a cache that fails is bypassed (reads fall back to the database) and a failed invalidation fails the publish. The warm/cold rows above show the cost of a miss.
* Not done, deliberately: materialised search tables, full-text indexes, a search service, or caching of contract data. They are not justified by this baseline.
