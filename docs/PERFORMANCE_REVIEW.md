# Performance Review (PER-002): configured-field search on SQL Server

Status: **PER-002 query-plan review done on SQL Server; two evidence-based changes made; target result in section 9.** This document records the evidence, the reasoning and the limits. It states no service-level target: the figure the owner asked for (configured-field text search below 500 ms median) is treated as a target to report against, not a promise. Measurements for the register overall are in `docs/PERFORMANCE.md`.

## 1. Scope and method

* **Subject:** the register search of `Ecp.Contracts` for the SQL Server provider, concentrating on configured-field text search ("free text matching a searchable additional field") and the other configured-field operations that share its code.
* **Why CI:** this workspace has no SQL Server (the container daemon could not be started and that was not worked around). The only SQL Server available is the disposable SQL Server 2022 container of the GitHub-hosted `sqlserver-integration` job.
* **Instrument:** `tests/Ecp.Integration.Tests/QueryPlanReport.cs`, called from the performance harness (`tests/Ecp.Integration.Tests/PerformanceBaseline.cs`). For each operation it clears the plan cache of the test server, runs the operation once, then reads `sys.dm_exec_query_stats` with `sys.dm_exec_query_plan_stats` (the last **actual** plan, with actual rows, executions and logical reads per operator; `LAST_QUERY_PLAN_STATS` is enabled on the test database). It prints a digest: statements sent, repeated statements, elapsed, CPU, logical reads, an operator tree with estimated and actual rows, the object read, residual predicates, missing-index suggestions, warnings and compiled parameter values. It also runs a parameter-sensitivity probe. It asserts nothing.
* **Data:** the synthetic tenant of the baseline: 10,000 contracts, 40 suppliers, 35,000 configured values (service tier, score, memo on every contract; renewal date on half). The text-search operation is "note 42" (two words) against the searchable `memo` and `service_tier` fields; 463 contracts match.
* **Runs used:** CI run 22 (first baseline), run 24 (plans captured, before any change), run 26 (change 1, with the earlier commit rebuilt and re-run **on the same runner** for a like-for-like comparison) and run 27 (changes 1 and 2, same comparison). The temporary same-runner comparison step is in `.github/workflows/ci.yml` and is removed once this review is accepted.
* **Environment:** Ubuntu 24.04 on an Azure-hosted GitHub runner, 4 logical cores, about 16 GB, .NET 10.0.13, Release build, SQL Server 2022 Developer in a container on the same machine with default settings. Runners differ between runs by a factor of up to about 2.4 for identical code (the unchanged text search measured 1,719 ms in run 22, 714 ms in run 24 and 872 ms in the run 26 baseline), so **only same-runner comparisons and logical reads are used to judge a change.** Logical reads do not depend on the machine.

## 2. Current search architecture

`RegisterQueryEngine.SearchCoreAsync` in `src/Modules/Ecp.Contracts/Register/RegisterEngine.cs`:

1. **Reach and catalogue:** which organisations the caller may read, and which core and configured fields they may show, filter and sort on (configured fields above the caller's classification level or hidden by role are not offered at all).
2. **Access first:** the caller's grants become one predicate over `Contract` (organisation, business unit, site, category, classification, relationship). Everything after this is applied to contracts the caller can already read.
3. **Typed filters:** each filter is a predicate on the contract query or the supplier query. A **configured-field filter** is an uncorrelated `Contract.Id IN (SELECT ContractId FROM ContractCustomValue WHERE FieldKey = ... AND <typed column test>)`, hitting the field's own filtered index. Before change 1 each such condition was also wrapped in an access test ("the contract is above the field's classification level, or its id is in the set the caller's level-allowing grants cover"), described in section 5.
4. **Join:** contracts, suppliers and categories are joined on `(TenantId, id)`.
5. **Free text:** every word (up to five) must match somewhere readable: reference, title, supplier name, vendor number, ABN, SAP number, or a searchable configured field the caller may see. A word matches a configured field when `UPPER(TextValue) LIKE %word%` (or the choice code whose label contains the word) for text, choice, lookup, email and URL fields, or `UPPER(LongTextValue) LIKE %word%`.
6. **Count, sort, page:** one `COUNT(*)` over the whole filtered join, then the ordered page (ordered by the chosen sorts with the reference and id as tie-breakers, then offset and fetch).
7. **Cells:** `BuildRowsAsync` loads the page's configured values in one query (not one per row), the tenancy structure names, and, only when a column needs it, category owners.

Cost shape: a search is two big statements (count and page) plus about 30 small reads of identity, configuration and tenancy data. The small reads do not grow with the rows on the page (guarded by a test).

## 3. Plans observed before any change (run 24)

All figures are the **last actual plan** for one cold run on a 10,000-contract tenant.

| Statement | Elapsed | Logical reads | Shape |
|---|---:|---:|---|
| Text search, `COUNT(*)` | 624 ms | **289,235** | Hash join of supplier and contract, then for every one of 10,005 contracts a **nested loop probe**: the custom-value match and the access subtree re-executed per row; four `Index Spool` operators each reading all 10,007 contracts |
| Text search, page | 72 ms | 33,106 | Same structure, but the ordered seek stops after 25 rows, so only 1,240 contracts were probed |
| Configured filter (tier, score range, date; sort by score), page | 303 ms | 85,653 | Nested-loop semi join into `IX_CustomValue_Date`: **1,901,859 rows** read in 1,683 executions |
| Configured filter, `COUNT(*)` | 71 ms | 58,971 | Clustered scan of `Contract`, access subtree per row |
| Saved view, `COUNT(*)` | 50 ms | 36,363 | Same pattern |
| Export: page, then `COUNT(*)` | 61 ms, then 50 ms | 41,092, then 41,088 | Same pattern |
| Core filtered search, count | 88 ms | 56,375 | Clustered scan of `Contract`; missing-index suggestion (below) |

No plan used more than one thread (`dop=1`), none had a spill, and none had a key lookup.

## 4. Analysis against the checklist

| Concern | Verdict | Evidence |
|---|---|---|
| **Missing indexes** | One suggestion, **not applied** | SQL Server suggests `Contract (TenantId, OrganisationId, IsArchived [, other equality columns]) INCLUDE (...)` for the register `COUNT` and page statements (several plans carry it or an `UnmatchedIndexes` warning). It would cut the full clustered scans of `Contract` for core filters (about 56,000 reads, 88 ms at 10,000 rows). It does not touch the 1.7-second operation, grows write cost on the main table and changes the schema, so it is recorded for the 100,000-contract pass (PRF-01), not added speculatively. The configured-value filtered indexes were used correctly by the filters (`IX_CustomValue_Number`, `IX_CustomValue_Text`, `IX_CustomValue_Date`). |
| **Table scans** | Present, expected | `PK_Contract` is scanned for the count (10,007 rows) and `PK_Supplier` (44 rows). The text match is a substring search (`LIKE '%word%'`) and cannot seek on any index; the scan of `ContractCustomValue` for it covers 35,000 narrow rows and is cheap once it runs once. |
| **Key lookups** | None | No key or RID lookup operator in any plan. |
| **Parameter sniffing** | **Not a cause** | Probe: plan compiled for the rare value then used for the common one, and the reverse. Text search "note 4242" (rare) and "note" (common): 1,318 and 428 ms with the plan compiled for the rare value; 410 and 1,243 ms compiled for the common value. The slower case follows the **number of words** (two words cost more than one), not the compiled value. Score filter (a narrow and a wide range): 166 to 205 ms on the first, cold run (this includes compiling), 22 to 24 ms on every warm run for both values. |
| **N+1 query patterns** | **None over rows**; some repeated small reads | Statement counts do not grow with the page size (test `Statements_do_not_grow_with_rows`: 34 SELECTs for a page of 50 rows, no more than for a page of 2). The same tenancy statements (organisation, division, business unit, site, legal entity) are read three to five times in one request. Each takes about 0.1 ms (`last_elapsed_time`), so the total is below 3 ms: noted, not changed. Query Store maintenance statements appear in the capture (SQL Server's own, not the application's). |
| **Sort spills** | None | No spill warning in any plan. The `TopN Sort` operators are the sort of the two-key `IN` list inside each probe. |
| **Hash spills** | None | Memory grants of 1 to 6 MB; no spill warning. |
| **Inefficient joins** | **Yes, the root cause** | Nested loop semi joins that re-run an independent subquery for every outer row (section 5). |
| **Excessive projection** | Minor, not changed | The page statement selects every column of `Contract`, `Supplier` and `Category` for 25 rows (the entity is materialised although the grid shows a handful). It costs transfer, not plan work: the page statement is dominated by the filter, not the select list. No evidence it matters at 25 rows. |
| **Cardinality estimation** | **Yes, the trigger** | The optimiser estimated **one row** for the correlated sub-trees where 10,000 to 19,238 rows came back (`est=1`, `actual=10000`), and 119 rows for the configured filter against 1,683. A one-row estimate makes a nested loop look free. The estimate comes from the shape (an `OR` that contains a subquery, and `UPPER(...) LIKE @p` on a parameter), not from stale statistics. |

## 5. Root cause analysis

Two independent constructions, each written in good faith to be correct and secure, together produced the plan above.

### 5.1 RC-1: a redundant access test on every configured-field condition

For every configured field the engine added a test so that a value could never reveal itself to someone who may not read it:

```
contract.Classification is above the field's level
  OR  contract.Id IN (ids the caller's grants allowing that level cover)
```

The second set is the whole contract access filter again, as a subquery. It was written once per filter clause, once per text word, once per sorted field, and once more per displayed column. SQL Server cannot turn "A OR id IN (subquery)" into a join; it evaluates the subquery per row through a `Concatenation` of the two branches. With a one-row estimate it chose nested loops and built an eager index spool over all 10,007 contracts for each use. That is the `Index Spool (Eager Spool) ... actual=10005 exec=10005` repeated four times in the text-search count, and it is why the page statement and the count both cost tens of thousands of reads.

The test is **true for every contract already in the query** whenever *every* grant the caller holds also allows the field's classification level: the caller's contracts are the contracts covered by their grants, and every grant covers that level. For the reference users (and any administrator) that is the normal case. The test was therefore computing a constant.

### 5.2 RC-2: an OR between an independent set and the row predicate

Free text was `row matches the word  OR  contract id IN (configured-field matches)`, per word. The configured-field set does not depend on the row. Written as an `OR`, SQL Server could not hash-join it either, and ran the set for each of the 10,005 contracts as a separate probe: a seek into `PK_ContractCustomValue` per contract, each preceded by a sort of the field-key list (`Merge Interval` over a `TopN Sort`). The actual-rows column shows it: `exec=10005`, `exec=9701` for the second word, and 19,238 seeks for the second word alone.

### 5.3 Why the optimiser was misled

Both constructions hide an independent set behind an `OR` or an expression of a parameter. The estimate for such a subtree is a fixed guess of about one row, regardless of data, and nested loops over a one-row inner side is the cheapest plan on paper. The parameter-sensitivity probe shows that this is not parameter sniffing: the same plan shape was chosen whichever value it was compiled for.

## 6. Fixes applied (only where the evidence points)

| # | Change | Evidence it answers | Where |
|---|---|---|---|
| 1 | **Leave the access test out when it is a constant.** `LevelVisibility.IsOpen(level)` is true when every grant allows the level; then the filter, the text match, the sort and the cell visibility skip the test. Otherwise the test is exactly as before. | RC-1: eager spools, estimate of one row, 289,235 reads | `src/Modules/Ecp.Contracts/Register/RegisterEngine.cs`, `RegisterFilters.cs` |
| 2 | **Combine the two match sets as `id IN (row matches UNION ALL configured matches)`**, an uncorrelated set the optimiser can hash. | RC-2: per-contract probes | `src/Modules/Ecp.Contracts/Register/RegisterEngine.cs` |

Behaviour is unchanged: the same rows come back, no field becomes visible to anyone who could not see it before. The decision in change 1 is a pure function with its own unit tests; the existing hidden-field scenarios (the owner who cannot read a Restricted field, a field hidden by role, another tenant) run on both providers.

### 6.1 Considered and not done

| Idea | Why not |
|---|---|
| Index on `Contract` for the missing-index suggestion | Not the cause; schema and write-cost change; revisit at 100,000 contracts (PRF-01) |
| Remove `UPPER()` and rely on the database collation | Provider-dependent (SQLite is case-sensitive), changes behaviour; the scan it affects is 35,000 narrow rows |
| Full-text index for the configured text search | Not needed to meet the target; a new capability and a new operational dependency |
| Materialise matching ids in the application and send them back | Unbounded list, two round trips, a race between them |
| Cache the tenancy structure per request | Saves under 3 ms per search; not evidence-based |
| A `OPTION (HASH JOIN)` or other query hint | Hides the problem, ties the code to one engine, and was not needed |
| Narrow the page projection | No measurable cost at 25 rows |

## 7. Risks

* **Change 1 skips a security test in a defined case.** The risk is a wrong "open" decision. The rule is exact and narrow (a grant list that is non-empty and in which every grant lists the level by exact, case-sensitive match), is a pure function with unit tests for the open, closed, empty and case cases, and the closed path is untouched. The existing hidden-field scenarios cover the closed path on both providers. A future change that adds a new kind of grant (for example a deny rule) must revisit `LevelVisibility.IsOpen`; its comment says so.
* **Change 2 reads the joined rows twice per word** that also searches a configured field (once for the page, once inside the set). That is linear in the rows and measured below; at 100,000 contracts it is a cost to watch (PRF-01).
* **Plan choice stays the optimiser's.** Nothing forces a hash join. A different data distribution or a statistics change could choose another plan. The shape guards fail if the structure that caused this comes back; they cannot promise a plan. The plan report is repeatable in CI for the next review.
* **The measurements are from one shared runner each**, untuned, with the container on the same machine. They are evidence of direction and order of magnitude, not a capacity statement.
* **The "before" columns have wide noise** between runs (up to about 2.4 times). The same-runner comparisons and logical reads are the evidence; single numbers are not.

## 8. Regression protection added

| Guard | What it catches |
|---|---|
| `SearchPlanShapeTests_*.Open_fields_skip_the_access_subquery` (SQLite and SQL Server) | Records the SQL a text search, configured filter and configured sort send and asserts the `Contract` table is read at most once plus once per text word. Before the change the same query read it seven times, and the test fails against the old code |
| `SearchPlanShapeTests_*.Statements_do_not_grow_with_rows` | The number of SELECT statements for a page does not grow with the rows on it (N+1), and stays within 40 (34 measured) |
| `LevelVisibilityTests` (unit) | The open/closed decision |
| Existing `RegisterScenarios`, `RegisterViewScenarios`, `CustomFieldScenarios`, `ApprovalFactScenarios`, `MigrationCustomFieldScenarios` | Results and visibility are unchanged, on SQLite and SQL Server |
| `PerformanceBaselineTests_SqlServer` | Prints the baseline and the plan report on every CI run; keeps its coarse shape assertion (a configured-field search must not cost orders of magnitude more than a core-field one) |

A time limit is not asserted: a time depends on the runner (section 1).

## 9. Results

@@RESULTS@@

## 10. Remaining bottlenecks

@@REMAINING@@

## 11. Reproducing

CI prints, in the step "Print performance baseline and plan report" of the `sqlserver-integration` job, the summary table, the full baseline JSON and the plan report. Locally, with a SQL Server (`tools/sqlserver-up.sh`):

```
export ECP_TEST_SQL="Server=127.0.0.1,14330;User Id=sa;Password=<yours>;TrustServerCertificate=True;Encrypt=True"
dotnet test src/Ecp.sln -c Release --filter "Category=SqlServer&Category=Performance"
cat artifacts/performance/sqlserver.json artifacts/performance/plans-sqlserver.txt
```
