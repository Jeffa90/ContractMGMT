# Testing Strategy

Status: **strategy and test catalogue; no tests exist yet.** Every ID below is a *planned* test. A task in `TASKS.md` may be marked complete only when its implementing code and the listed tests exist and pass (`CLAUDE.md`: "Run restore/build/migrations/tests and fix failures before claiming completion"; `prompts/03-CONTINUE-IMPLEMENTATION.md`: "only mark complete when functional code and tests exist"). Test IDs are referenced from `docs/THREAT_MODEL.md`, `docs/DOMAIN_MODEL.md`, `docs/SECURITY.md` and `requirements/`.

## 1. Principles and gates

1. Tests assert behaviour required by `requirements/`, not the current implementation. A failing test is never "fixed" by editing the requirement (`.claude/skills/*`: "Never change requirements merely to match defective implementation").
2. Security and isolation tests run on **every build**; they are not optional suites.
3. Data-driven where a requirement file is the source of truth: the authorisation matrix from `role-permission-matrix.csv` (AUT-001), workflow transitions from `status-transitions*.csv` (WFL-001), field types from `custom-field-types.csv` (NCD-012), reference lookups from `reference-lookups.csv`, migration samples from `migration-samples/`. If a requirement file changes, tests change with it automatically or fail loudly.
4. Negative tests are first-class: each control has at least one test that proves the bad input is refused.
5. Deterministic: `FakeClock`, seeded ids where needed, no sleeps, no network except opt-in provider tests.
6. Synthetic data only. Hostile files (zip bombs, traversal archives, macro workbooks) are **generated in memory by test code**, never committed.
7. Honest reporting: tests that could not run (for example no SQL Server or container engine available) are reported as *not run*, never as passed, and block the *implemented* label for the affected capability.
8. Milestone exit gates are defined in `docs/IMPLEMENTATION_PLAN.md` by test-ID sets.

## 2. Layers and projects (planned)

| Layer | Purpose | Project | Needs |
|---|---|---|---|
| Unit | Domain invariants, interpreters, validators, parsers, value objects | `tests/<Module>.Tests` | Nothing |
| Architecture | Module boundaries, banned APIs, filters, endpoint annotation | `tests/Ecp.Architecture.Tests` | Nothing |
| Integration | EF mappings, migrations, RLS, concurrency, outbox, audit, storage | `tests/Ecp.Integration.Tests` | Real SQL Server (section 3) |
| API / authorisation | In-process host (`WebApplicationFactory`) against real SQL Server | `tests/Ecp.Api.Tests` | SQL Server |
| End-to-end | Browser flows incl. wizards, designers, migration; accessibility | `tests/Ecp.E2E.Tests` (planned, not yet created) (Playwright + axe-core) | SQL Server, Chromium (present at `/opt/pw-browsers`) |
| Contract | Adapter interface suites run against mocks and real providers | `tests/Ecp.Adapters.ContractTests` (planned, not yet created) | Providers only for opt-in runs |
| Deployment | Container build/run, static validation of IIS/Azure artefacts | `tests/Ecp.Deployment.Tests` (planned, not yet created) + scripts | Container engine for runtime part |
| Performance | Scale and budgets | `tests/Ecp.Performance.Tests` (planned, not yet created) | SQL Server, larger data |

Categories (xUnit traits): `Unit`, `Architecture`, `Integration`, `E2E`, `Contract`, `Deployment`, `Performance`, `Security`, `Isolation`, `Migration`, `A11y`. CI target selection by trait.

### 2.1 Architecture tests (`Category=Architecture`)

| ID | Rule enforced |
|---|---|
| ARC-001 | Module dependency tiers and the same-tier edges in `docs/ARCHITECTURE.md` section 4; no cycles |
| ARC-002 | A module references only other modules' public contract namespaces |
| ARC-003 | Every tenant-owned entity implements `ITenantOwned`, has the global query filter and a Row-Level Security policy (generated from the model, compared with the database) |
| ARC-004 | No data access outside a tenant scope (unscoped context use throws; domain code has no `DbContext` reference) |
| ARC-005 | `IgnoreQueryFilters` and raw SQL appear only in an allowlist |
| ARC-006 | Cache access only through the tenant-keyed helper |
| ARC-007 | Banned APIs in application code: Roslyn scripting, Jint or other script engines, `Assembly.Load*` from data, `Process.Start`, dynamic expression compilation of administrator input, string-concatenated SQL |
| ARC-008 | Every endpoint and page has an authorisation requirement or is on the explicit anonymous allowlist |
| ARC-009 | Domain layer has no infrastructure references; workflow-controlled properties (`status`, lock flags, `registeredDate`) have no public setter |
| ARC-010 | No JBS or other business-pack names in compiled code (PRT-001) |

## 3. Environment, SQL Server and reporting policy (RISK-01; decision record D4)

Policy confirmed in DR-2026-10-15 D4:

1. **Provider-independent tests** (domain, validators, interpreters, parsers, access evaluator logic) need no database and always run.
2. A **lightweight test provider** (SQLite in-memory with foreign keys enabled) may be used for *limited application tests* (for example filter wiring, service flows). Results from it are labelled **"provider-independent (not SQL Server proof)"** in the report and are **never** counted as evidence of SQL Server behaviour.
3. **Real SQL Server integration tests** cover migrations, constraints, composite foreign keys, Row-Level Security, `rowversion` concurrency, application locks, audit permissions, reporting views and any other provider-specific behaviour. They are tagged `SqlServer`.
4. SQL Server tests are skipped **only** when no SQL Server connection is available. The connection comes from the environment variable `ECP_TEST_SQL` (an external or CI SQL Server, or a local disposable one started by `tools/` scripts). A skipped SQL Server test is reported **NOT VERIFIED**, never Passed.
5. **Report counters.** Every test run prints four counters: **Passed, Failed, Skipped, NOT VERIFIED**. Skipped = deliberately excluded (reason printed). NOT VERIFIED = needs an environment that was unavailable (SQL Server, container engine, IIS, Azure, live provider). A capability whose critical tests are NOT VERIFIED may not be labelled *implemented*; it is labelled *Implemented, not verified*.
6. Docker, IIS and Azure artefacts that cannot be run in the workspace are labelled **Scaffolded / Not Verified**.
7. Production architecture is not weakened to suit the sandbox. The lightweight provider is a test and Demo-on-sandbox convenience only; Production refuses it (R-DB-01).
8. A **CI workflow** (`.github/workflows/ci.yml`) runs the SQL Server suite against a disposable SQL Server container with a generated password, subject to runner availability and repository policy.

Environment findings are recorded in `docs/ENVIRONMENT.md` (task T0.1). SQL Server for local runs, in order of preference: (A) container engine (`dockerd`) if it can run; (B) native `mssql-server` installed from the Microsoft package feed in the workspace; (C) external SQL Server via `ECP_TEST_SQL`. Outcome (T0.1): option A worked in the cloud workspace (container engine without bridge networking, `--network host`), so the SQL Server suite ran for real; see `docs/ENVIRONMENT.md`. The test reporter is `tools/test-report.sh` (parses `.trx`; counts a skip whose reason starts with `NOT VERIFIED` separately from Skipped).

M1 result (2026-10-16, Release, warnings as errors): with SQL Server available 163 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED (102 unit, 33 integration of which 16 SQL Server, 13 architecture, 15 API). Without `ECP_TEST_SQL`: 147 Passed, 0 Failed, 0 Skipped, 16 NOT VERIFIED. The `NotVerified` mechanism is implemented by the `SqlServerFact` and `SqlServerTheory` attributes in the test infrastructure project.

Result after the secure application and setup slice (2026-10-16, Release, warnings as errors, `dotnet restore --locked-mode` clean): **with** a real SQL Server 2022 Developer container (`ECP_TEST_SQL`): **453 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED** (249 unit, 13 architecture, 130 integration of which 64 run against SQL Server, 61 API). **Without** `ECP_TEST_SQL`: **389 Passed, 0 Failed, 0 Skipped, 64 NOT VERIFIED** (the 64 SQL Server tests; they are never counted as Passed). The earlier 163 tests all still pass. SQLite-run tests are labelled `SqliteNotSqlServerProof` and prove nothing about SQL Server; the same scenarios run on both providers (`AccessScenarioTests_*`, `SetupScenarioTests_*`, `OnboardingScenarioTests_*`), so SQL Server behaviour (migrations including the `setup` schema, RLS on `setup.SetupRecord` and `setup.OnboardingItem`, composite keys) is evidenced only by the `_SqlServer` runs. The API tests use SQLite and an in-process mock OpenID Connect provider. GitHub-hosted CI (commit 975c5d3, run 6) independently ran the build, all non-SQL tests (250 unit, 13 architecture, 66 integration, 61 API), the repository and tooling checks, the SBOM/vulnerability/licence scan, the SQL Server tests against a service container, and the full container build with `deploy/docker/verify-image.sh`: all green. Not verified anywhere: live Entra, live SMTP, IIS, Azure, accessibility automation, performance.

Added in that slice: identity and access (`AccessEvaluatorTests`, `IdentityRulesTests`, `OidcSignInTests`, `ViewerScopeTests`, `ClassificationOverrideTests`, `UiAccessTests`, `AntiforgeryTests`, `AccessScenarioTests_*`), localisation (`LocaleFormatterTests`, `TimeZoneTests`, `LocalisationApiTests`), supply chain (`tools/tests`), setup (`SetupScenarios` 13 per provider; `SetupHostTests` 10: gate, token, session cookie flags, antiforgery, end-to-end wizard, restart midway and after completion, step-up reopening, the `setup-token` command in a separate process, remote callers; `SetupStageTests` 11 including Production refusals), onboarding (`OnboardingScenarios` 7 per provider plus a service-graph cycle guard, `OnboardingHostTests` 4, `OnboardingCsvTests`, `OnboardingValidatorTests`, `OnboardingSimulatorTests`).

Result after the migration slice (2026-10-16, Release, warnings as errors, `dotnet restore --locked-mode` clean): **with** a real SQL Server 2022 Developer container (`ECP_TEST_SQL`): **546 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED** (297 unit, 13 architecture, 174 integration of which 86 run against SQL Server, 62 API). **Without** `ECP_TEST_SQL`: **460 Passed, 0 Failed, 0 Skipped, 86 NOT VERIFIED** (the SQL Server copies of the integration scenarios; never counted as Passed). The migration scenarios (22) run on both providers; the migration unit tests, the 22 service scenarios per provider and the real-page flow (`MigrationUiTests`, SQLite) are in section 7.1. A scripted run over HTTP against the real host (`tools/migration-demo.py`) is recorded in `docs/MIGRATION.md` section 12.1.

Result after the approval matrix slice (WFE-02, 2026-10-16, Release, warnings as errors, `dotnet restore --locked-mode` clean, `tools/check-repo.py`, `tools/supply-chain.sh`, tooling tests, `tools/docker-runtime-smoke.sh` and `tools/compose-smoke.sh` clean): **with** a real SQL Server 2022 Developer container (`ECP_TEST_SQL`): **776 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED** (380 unit, 13 architecture, 71 API, 312 integration of which 157 ran on SQL Server). **Without** `ECP_TEST_SQL`: **619 Passed, 0 Failed, 0 Skipped, 157 NOT VERIFIED** (the SQL Server wrappers are reported NOT VERIFIED, never as passed). The slice added 101 tests: 49 unit (matrix definition and validator, evaluator, plan evaluation, delegation rules and cycle detection), 48 integration (23 scenarios on SQLite and on SQL Server, plus two SQL-Server-only race scenarios) and 4 API tests through the real pages. A defect found by this pass and fixed: the approval pages opened on the first organisation in the tenant rather than one the person may use, which returned 404 to a scoped administrator. Previous result: Result after the lifecycle slice (2026-10-16, Release, warnings as errors, `dotnet restore --locked-mode` clean, `tools/check-repo.py` and `tools/supply-chain.sh` clean): **with** a real SQL Server 2022 Developer container (`ECP_TEST_SQL`): **675 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED** (331 unit, 13 architecture, 67 API, 264 integration of which 132 ran on SQL Server). **Without** `ECP_TEST_SQL`: **543 Passed, 0 Failed, 0 Skipped, 132 NOT VERIFIED** (the 132 SQL Server wrappers are reported NOT VERIFIED, never as passed). The lifecycle slice added 129 tests over the migration slice's 546: 34 unit (workflow definition validator, notification policy validator, renderer, email and signature providers, permission counts), the lifecycle, Legal, execution, register authorisation, Variation, renewal, unlock, notification, reminder, dashboard, classification and elevation scenarios (each on SQLite and SQL Server) and the API tests through the real pages. A defect found by this pass: the lifecycle SQL Server wrappers were missing the `Category=SqlServer` trait, so the CI SQL Server job would not have run them; fixed. Docker: the runtime stage `tools/docker-runtime-smoke.sh` and `tools/compose-smoke.sh` passed in the workspace (the SDK build stage runs in CI only; see `deploy/STATUS.md`).

GitHub-hosted CI, run 10 on commit `ba712aba` (the migration slice): all three jobs green: build, unit, architecture, API and lightweight-provider tests; container image build and smoke test; SQL Server integration (including the 22 migration scenarios against SQL Server). IIS, Azure and the compose files remain Scaffolded / Not Verified (`deploy/STATUS.md`).

Commands (implemented in M1; `dotnet test` without `ECP_TEST_SQL` reports the SQL Server tests as NOT VERIFIED):

```
dotnet restore --locked-mode
dotnet build -c Release -warnaserror
dotnet test --filter "Category!=SqlServer"                       # provider-independent
ECP_TEST_SQL="<connection string>" dotnet test                  # includes SQL Server suite
tools/test-report.sh                                            # prints Passed / Failed / Skipped / NOT VERIFIED
npx playwright test                                              # E2E + a11y (later milestone)
docker compose -f deploy/docker/compose.yml up -d --wait         # needs a container engine
```

## 4. Test data and fixtures

| Fixture | Source |
|---|---|
| Two tenants, three organisations, sites | `sample-data/demo-organisation-structure.json` |
| Users and roles (including `viewer.a2`, a same-tenant different-organisation user) | `sample-data/demo-users-and-roles.json` |
| Categories and assignments (including an ended assignment) | `sample-data/demo-categories-and-assignments.json` |
| Approval matrix, custom fields | `sample-data/synthetic-generic-value-approval-matrix.json`, `demo-custom-fields.json` |
| Migration sample | `migration-samples/contracts-import.csv`, `attachment-manifest.csv`, `field-mapping-profile.csv` (original) and `field-mapping-profile-complete.csv`, `status-lookup-map.csv`; placeholder attachment files created by the fixture builder per `migration-samples/attachments/README.md` |
| Generated hostile and scale data | Builders in test code: zip-slip, bomb, macro workbook, polyglot, duplicates, unmatched, bad dates, cross-tenant references, 100k contracts, 25k contracts + 50k files |
| Clock | `FakeClock` (UTC), organisation time zones from fixtures |
| Providers | Mock implementations for every adapter (they pass the same contract suite as real ones) |

## 5. Tenant-isolation suite (`Category=Isolation`)

Two tenants (A, B) and, inside A, two organisations. Each surface has at least a read test and a write test unless stated. Fixture data exists in both tenants with overlapping codes (for example category `PACKAGING` in both) to catch confusion by code.

| ID | Surface |
|---|---|
| TEN-001 | Record reads across tenants for **every** tenant-owned entity (generated from the entity list) |
| TEN-002 | Create/update/delete across tenants, including via foreign-key ids embedded in payloads |
| TEN-003 | Document download across tenants (by id, by storage key, by manifest) |
| TEN-004 | Upload attached to another tenant's contract |
| TEN-005 | Background jobs and outbox: messages run in the correct scope; a job without scope fails closed |
| TEN-006 | Caches: keys carry tenant; cross-tenant cache hit impossible |
| TEN-007 | Search, autocomplete, saved filters, counts |
| TEN-008 | Reports, projections and reporting views |
| TEN-009 | Exports (records, documents, audit, configuration) |
| TEN-010 | Imports/migration: references, staging rows, file linking, matching |
| TEN-011 | Packages: export, import, pack application |
| TEN-012 | Notification and email content and recipients |
| TEN-013 | Webhook subscriptions, integration settings, secrets references |
| TEN-014 | Audit queries and evidence exports |
| TEN-015 | Logs and support bundles contain no other tenant's data |
| TEN-016 | Configuration drafts, releases, terminology, branding |
| TEN-017 | API error messages, timing and counts reveal nothing about other tenants |
| TEN-018 | Organisation isolation inside one tenant (user `viewer.a2` cannot see org A1; tenant-level Auditor can per matrix) |
| TEN-019 | Tenant resolution tampering: header, claim, host, route, body value |
| TEN-020 | Suspended tenant is denied everywhere |
| TEN-021 | Content de-duplication does not link across tenants |
| TEN-030 | Layer-off test: EF filter disabled → RLS still blocks |
| TEN-031 | Layer-off test: RLS disabled → EF filter still blocks |
| TEN-032 | Composite foreign keys reject cross-tenant parent/child |
| TEN-033 | Storage prefix enforcement rejects keys outside the tenant |
| TEN-040 | Raw SQL wrapper requires scope and parameters |

## 6. Authorisation suite

| ID | Test |
|---|---|
| AUT-001 | **Matrix generated from `role-permission-matrix.csv`**: for each role × permission, assert allowed / scoped / denied against the real evaluator and API |
| AUT-002 | Tenant layer denies |
| AUT-003 | Organisation layer denies |
| AUT-004 | Permission layer denies |
| AUT-005 | Relationship layer: an **ended** category assignment grants no edit; a new one grants edit immediately; edits only in assigned categories |
| AUT-006 | Classification ceiling hides records in lists, counts, search and exports |
| AUT-007 | Lifecycle/lock layer: locked record refuses edits by every role |
| AUT-008 | Segregation of duties: submitter cannot approve; configurable |
| AUT-009 | Delegation cannot exceed grantor rights; cannot delegate `setup.run` etc. without holding it |
| AUT-010 | Catalogue consistency: every permission code used by workflow CSVs, policies and endpoints exists in `permission-catalogue.csv`; every role in `roles-and-permissions.csv` has a matrix column |
| AUT-011 | Last Tenant Administrator / System Administrator cannot be removed |
| AUT-012 | Elevation requires reason, expires, is visible, audited |
| AUT-013 | Emergency unlock requires reason and expiry, edits carry unlock id, auto-relocks |
| AUT-014 | **IDOR sweep**: every route with an id parameter is enumerated by reflection and exercised with foreign-tenant and foreign-organisation ids |
| AUT-015 | Every endpoint carries an authorisation requirement (also ARC-008) |
| AUT-016 | Group-overage or unresolved group claims fail closed |
| AUT-017 | **Scope intersection:** a record is visible only if its organisation, business unit, site, category and classification all fall inside the assignment's scopes; scopes of separate assignments are never merged; an assignment with no organisational scope is refused |
| AUT-018 | Viewer kinds: All Contracts, Category, Business Unit, Site and Restricted viewers each see exactly their scope (fixtures `viewer.*@a.example.test`); Viewer is never a global read role |
| AUT-019 | Category Owner reads all within organisation and classification scope but edits only currently assigned categories; Legal, Approver, Procurement Administrator, administrators and Auditor behave per `role-permission-matrix.csv` (no contract edits by administrators or Legal on locked metadata) |
| AUT-020 | Procurement Administrator cannot approve their own submission unless separately assigned an approval role and policy permits; Approver cannot edit the submitted record; Backup Approver actions record the actual individual |

## 7. Migration suite (`Category=Migration`) (Phase 1 mandatory)

Master's nine critical tests are MIG-001 to MIG-009.

| ID | Test |
|---|---|
| MIG-001 | **Dry run changes no target data**: snapshot of every non-staging table, blob area, outbox, notifications, reminders identical before and after |
| MIG-002 | **Batch resume does not duplicate** records or files (kill/resume at each phase boundary and mid-chunk) |
| MIG-003 | **Manifest matching is deterministic** (same inputs, any order, same proposals; ties never auto-broken) |
| MIG-004 | Duplicate and source-ID policies: each strategy in `reference-lookups.csv` `DuplicateStrategy` behaves as specified; `agreementReference` collisions are tenant-wide; `sourceAgreementReference` is always preserved |
| MIG-005 | **Unmatched files remain visible** and are never silently dropped; discard is an audited action |
| MIG-006 | **Rollback removes only the selected batch's reversible changes**; pre-existing records and other batches untouched |
| MIG-007 | **Hash and provenance persist** (source id, system, hash, extraction timestamp, source status, batch id, profile version) |
| MIG-008 | **Cross-tenant migration is impossible** (references, files, keys) |
| MIG-009 | **Error and reconciliation totals equal processed input totals** (identities in `MIGRATION_SUPPLEMENT.md` section 11) |
| MIG-010 | UTF-8 BOM, UTF-16, quoted newlines, ragged rows; ambiguous date format requires explicit format |
| MIG-011 | XLSX: cached formula values, hidden sheets flagged, `.xlsm` rejected |
| MIG-012 | Archive path traversal, absolute, drive, UNC, reserved names, symlinks rejected (`MIG-F001`) |
| MIG-013 | Decompression bomb, entry-count and size limits enforced while streaming |
| MIG-014 | Mapping preview shows original/transformed values and affected-row counts |
| MIG-015 | Legacy load: policy required; second-person confirmation; `isLegacyLoad`; no overwrite of locked records without explicit policy |
| MIG-016 | Rollback keeps shared suppliers/categories still referenced outside the batch |
| MIG-017 | Process killed mid-commit; resume completes; totals balance |
| MIG-018 | **End-to-end sample**: 2 contracts, 3 manifest rows, ZIP built from the fixture builder, dry run, commit, reconcile |
| MIG-019 | Hostile naming-rule regex (ReDoS) terminated by timeout, batch continues |
| MIG-020 | No truncation: over-length value is `MIG-E009`, never cut |
| MIG-021 | Error workbook neutralises formula injection |
| MIG-022 | After acceptance rollback is refused; compensating archival path works |
| MIG-023 | Batch captures mapping profile version and configuration release; later changes do not alter the batch |
| MIG-024 | Re-running the same file with Skip existing is a no-op; Update mapped fields changes only mapped fields and stores before-images |
| MIG-025 | Document duplicate options (reject hash, link existing, add version, keep separate with warning, manual review) |
| MIG-026 | Cutover freeze window recorded; target freeze blocks non-migration contract creation when enabled |
| MIG-027 | Legal/approval/Variation historical slice (when Q-12 is answered): read-only, no fabricated rounds, in-flight items reported not recreated |

### 7.1 Implementation status of the migration suite (2026-10-16)

Implemented and passing on SQLite (lightweight; not SQL Server proof) and on a disposable SQL Server 2022 container (integration scenarios, `tests/Ecp.Integration.Tests/MigrationScenarios*.cs`); unit tests (`tests/Ecp.UnitTests/Migration*.cs`); the whole flow through the real pages (`tests/Ecp.Api.Tests/MigrationUiTests.cs`); and a scripted run over HTTP against the real host (`tools/migration-demo.py`). Without `ECP_TEST_SQL` the SQL Server copies are skipped and reported **NOT VERIFIED**.

| ID | Where it is covered |
|---|---|
| MIG-001 | `A_dry_run_writes_nothing_to_the_target` (snapshot of records, change stamps, counters, blob area); outbox, notifications and reminders do not exist yet |
| MIG-002, MIG-017 | `A_stopped_commit_resumes_without_losing_or_duplicating_anything` (eight kill and failure points) |
| MIG-003 | unit: `The_same_inputs_in_any_order_give_exactly_the_same_proposals`, `Ties_are_never_broken_automatically...` |
| MIG-004, MIG-024 | `Each_duplicate_strategy_behaves_as_specified`, `The_reference_policy_can_generate_every_target_reference...`, `Re_running_a_file_is_idempotent...` |
| MIG-005 | `Unmatched_files_wait_in_a_queue_until_a_person_resolves_or_discards_them` |
| MIG-006, MIG-016 | `Rollback_removes_only_the_selected_batch...`, `A_record_that_has_moved_on_is_archived_not_deleted` |
| MIG-007 | `The_sample_contracts_manifest_and_archive_load_end_to_end` (ledger, hash, source identity, batch id on audit) |
| MIG-008 | `Another_tenant_and_people_without_the_permission_cannot_see_or_run_a_migration` |
| MIG-009 | `Error_and_reconciliation_totals_equal_the_processed_input...` |
| MIG-010, MIG-011, MIG-012, MIG-013 | unit format tests; `Unsafe_archive_entries_and_infected_files_never_reach_the_target`; `Spreadsheet_and_UTF16_sources_load_exactly_like_CSV` |
| MIG-014, MIG-020 | `The_mapping_preview_shows_original_and_transformed_values...`; unit mapping tests |
| MIG-015 | `A_legacy_load_needs_a_policy_a_second_person_and_exactly_what_was_validated`, `The_source_registered_date...` |
| MIG-018 | `The_sample_contracts_manifest_and_archive_load_end_to_end`; UI and scripted runs use the committed sample ZIP |
| MIG-019 | unit: `A_naming_rule_runs_on_the_safe_engine...`; `Attachments_match_in_a_fixed_order...` |
| MIG-021 | unit and integration: formula-neutralised CSV and workbook |
| MIG-022 | `After_acceptance_rollback_is_refused_and_compensating_archival_keeps_everything` |
| MIG-023 | `A_batch_keeps_the_profile_version_it_was_created_with` |
| MIG-025 | unit: `Each_duplicate_option_behaves_as_specified`, `Content_already_in_the_target...` |
| MIG-026 | `The_cutover_freeze_stops_people_creating_contracts_but_not_the_migration` |
| MIG-027 | not built (MGR-12, Future) |

## 8. Workflow, approval and lifecycle suite

| ID | Test |
|---|---|
| WFL-001 | Every transition in `status-transitions.csv` and (when accepted) `status-transitions-supplemental.csv`: allowed for the holder, denied for others, guard failures explained |
| WFL-002 | Each guard in `guard-vocabulary.csv` true/false |
| WFL-003 | **Workflow validator rejects the original CSV's dead ends** (`Legal Rework Required`, `Variation in Progress`) and accepts the CSV plus supplemental |
| WFL-004 | `Approve`: approval record, Registered Date stamped (UTC), lock set, reminders generated, audit and outbox in one transaction; rollback leaves nothing |
| WFL-005 | Registered Date set exactly once; later edits refused |
| WFL-006 | `status` cannot be set via API, import, rule action or mass assignment |
| WFL-007 | Pinned workflow/matrix version: publishing a new release does not change in-flight instances |
| WFL-008 | Legal rounds: open, rework, resubmit, round 2, clear; closed rounds immutable |
| WFL-009 | Variation lifecycle: create, one open only, restricted fields, apply/cancel/reject, parent returns to Effective, Registered Date unchanged |
| WFL-010 | Reminders: Evergreen/Critical Notice offsets in organisation time zone across DST; dedupe; recompute on Variation |
| WFL-011 | Emergency unlock lifecycle |
| WFL-012 | Withdraw from Draft/Initial Creation/Rework; no hard delete |
| WFL-013 | Idempotent transition by `clientRequestId` |
| APR-001 | Matrix ranges are half-open `[min,max)`; boundary values at 99,999.99 / 100,000 / 500,000 resolve as specified |
| APR-002 | Publish rejects gaps |
| APR-003 | Publish rejects overlaps and priority ties |
| APR-004 | No matching row blocks the submission with explanation (fail closed) |
| APR-005 | Sequential, parallel and first-response models |
| APR-006 | Backup approver and delegation windows; "on behalf of" recorded |
| APR-007 | Approver identity comes from the session; submitter cannot approve |
| APR-008 | `A_plan_is_pinned_to_the_release_it_was_made_under` | |
| APR-009 | `The_simulator_and_a_real_submission_resolve_to_the_same_steps_and_people`; `The_simulator_can_try_a_numbered_release_or_an_unpublished_draft_before_anyone_relies_on_it` | |
| APR-010 | Unit `Currency_needs_a_rate_in_force_on_the_date_and_is_never_assumed`, `Currency_codes_and_rate_tables_are_checked`; integration `Resolution_chooses_the_row_names_the_people_and_explains_every_choice` | |
| WFE-02 (additional) | Effective and future dates: `Effective_dates_decide_which_rows_rates_and_people_apply_on_a_given_day`, `Effective_dates_apply_to_the_matrix_and_to_each_row_inclusively`; classification: `The_submitter_and_people_who_cannot_open_the_record_are_never_approvers`, `Row_conditions_on_classification_and_exceptions_shape_who_approves`; tenant isolation: `Matrices_delegations_and_resolutions_belong_to_one_tenant`; concurrency (SQL Server): `The_last_response_is_decided_once_even_when_two_approvers_answer_at_the_same_moment`; pages: `ApprovalMatrixUiTests` (4 API tests) | |
| APR-008 | Matrix version pinned on submission |
| APR-009 | Simulator output equals runtime resolution for the same inputs |
| APR-010 | Currency mismatch without rate table blocks (no implicit conversion) |

### 8.1 Implementation status of the lifecycle suite (lifecycle slice, 2026-10-16)

Scenarios live in `tests/Ecp.Integration.Tests/Lifecycle*.cs`, `NotificationScenarios*.cs`, `ClassificationScenarios.cs`, `ElevationScenarios.cs` and `MigrationClassificationScenario.cs`; each runs on SQLite (lightweight, **not** SQL Server proof) and on a real SQL Server 2022 container through the wrappers in `LifecycleScenarioTests.cs` (traits `Category=Lifecycle` and, for SQL Server, `Category=SqlServer`). Unit tests: `WorkflowDefinitionTests.cs`, `NotificationDeliveryTests.cs`, `SignatureProviderTests.cs`. API tests (real host, real pages): `LifecycleUiTests.cs`, `ElevationUiTests.cs`.

| ID | Status | Where it is covered |
|---|---|---|
| WFL-001 | Covered | `Every_state_offers_exactly_its_transitions_and_every_other_step_is_refused`; `A_locked_contract_refuses_every_crafted_step_and_edit_whoever_asks`; `A_contract_goes_from_draft_to_effective_through_every_stage` |
| WFL-002 | **Partial**: guards are exercised through the scenarios that need them (executed copy, submitter-not-approver, comment and reason required, open Variation, Legal required, final document); there is no table-driven true/false test for every guard in the vocabulary | |
| WFL-003 | Covered by validator unit tests (`Dead_ends_unknown_references_and_vocabulary_breaches_are_found`, `Approval_must_keep_its_guards…`, `A_transition_cannot_unlock_a_locked_record`) and by publication of the shipped pack | |
| WFL-004, WFL-005 | `Register_authorisation_rules_self_approval_backup_rejection_and_registered_date`; `A_Variation_changes_an_effective_contract_only_when_approved…` (Registered Date not re-stamped) | |
| WFL-006 | `A_locked_contract_refuses_every_crafted_step_and_edit_whoever_asks` (no status setter on any API; crafted `Step` POST through the real page in `LifecycleUiTests`) | |
| WFL-007 | `A_published_workflow_applies_to_new_contracts_while_existing_ones_stay_pinned_and_an_invalid_one_is_refused` | |
| WFL-008 | `Legal_rounds_are_kept_and_a_privileged_review_is_invisible…`; `A_Variation_that_needs_Legal_goes_through_Legal_rounds…` | |
| WFL-009 | `LifecycleScenarios3` Variation scenarios (apply, cancel, reject, one open, restricted fields, Legal path) | |
| WFL-010 | **Partial**: reminder windows, Evergreen, Critical Notice, dedupe and ownership are covered (`NotificationScenarios`); the organisation's time zone is used but there is no daylight-saving-boundary test, and "recompute on Variation" is covered only in that a changed date opens new windows | |
| WFL-011 | `A_force_unlock_needs_permission_recent_sign_in_reason_and_a_time_limit_and_is_fully_audited`; `Emergency_access_adds_only_account_recovery_and_can_never_alter_an_effective_record` | |
| WFL-012 | Withdraw paths in `Every_state_offers_exactly_its_transitions…`; no user-facing hard delete exists | |
| WFL-013 | `The_same_step_taken_twice_at_once_happens_once` | |
| APR-001 | Unit `Value_ranges_are_half_open_at_every_boundary`, `Value_ranges_are_half_open_so_adjacent_tiers_are_valid_and_overlapping_ones_are_not` | |
| APR-002 | Unit `Gaps_between_ranges_are_errors_and_open_ends_are_warnings`, `Gaps_and_overlaps_are_judged_in_each_period_a_row_is_in_force`; integration `Publication_refuses_a_matrix_with_gaps_overlaps_or_ties_and_one_a_workflow_still_needs` | |
| APR-003 | Unit `Rows_that_can_match_the_same_record_at_the_same_priority_are_a_tie_and_different_priorities_are_not`, `Ranges_are_compared_only_among_rows_that_share_every_other_condition`, `Category_hierarchy_sharpens_tie_detection_when_it_is_known` | |
| APR-004 | Unit `No_match_blocks_with_an_explanation_and_never_defaults`, `A_priority_tie_at_run_time_blocks_rather_than_picking_one`; integration `A_submission_nobody_can_approve_is_blocked_with_the_reason_and_can_be_resubmitted_once_fixed` | |
| APR-005 | All four models: unit `Sequential_rows_make_one_stage_per_step_and_parallel_rows_one_stage_for_all`, `A_first_response_step_is_settled_by_one_approval_from_any_listed_person`, `An_all_required_step_needs_every_named_primary_and_reports_who_is_outstanding`; integration `A_two_stage_approval_opens_each_stage_in_turn_and_registers_only_at_the_end`, `Parallel_steps_open_together_an_all_required_step_needs_every_member_and_a_delegate_covers_one`, `A_rejection_at_any_open_step_returns_the_contract_and_ends_the_plan`; the reference model's first-response behaviour is `The_reference_model_is_unchanged_when_no_matrix_is_named` | |
| APR-006 | `Backup_approvers_and_backup_groups_stand_in_where_the_step_allows_it`, `Delegation_is_bounded_audited_non_transitive_and_never_circular`, `A_backup_without_a_delegation_cannot_complete_an_all_required_step`; "on behalf of" recorded (`Placement_records_on_behalf_of_for_backups_and_delegates_and_refuses_repeats`); API shows it on the approval page | |
| APR-007 | `Register_authorisation_rules_self_approval_backup_rejection_and_registered_date`; API `Register_authorisation_is_refused_to_everyone_but_the_assigned_approvers_and_to_the_second_responder` | |
| CNC-002 | `Two_people_deciding_at_once_produce_one_decision_and_one_registration` (SQL Server) | |
| NTF-001 | `A_workflow_event_tells_the_right_people_once_and_a_second_run_changes_nothing` | |
| NTF-002 | `Failed_email_is_retried_with_a_back_off_then_dead_lettered…` | |
| NTF-003 | **Partial**: idempotent re-runs are tested; a kill in the middle of a send is not | |
| NTF-004 | `Reminders_are_sent_once_per_window_and_follow_current_ownership` | |
| NTF-005 | `Failed_email_is_retried_with_a_back_off_then_dead_lettered…`; `Background_job_runs_are_recorded_and_visible_only_to_administrators` | |
| NTF-006 | `Notification_content_never_leaks_a_classified_record…`; `A_title_is_withheld_from_the_message_at_the_restricted_level…`; `A_person_who_loses_access…`; unit `NotificationRendererTests` | |
| NTF-007 | Not built (no translated templates; en-AU only) | |
| ADP-001, ADP-002 | **Partial**: back-off, dead-letter, visibility and retry for email deliveries; replay of lifecycle outbox events beyond the ten-attempt park is not built | |
| ADP-003 to ADP-005 | Not built for external adapters (no live adapter); provider fail-closed behaviour is unit-tested (`SignatureProviderTests`, `EmailProviderTests`) | |
| MIG-024 (suppression) | `A_legacy_load_causes_no_workflow_notifications_and_only_the_reminders_it_is_due` | |
| Classification | `ClassificationScenarios` (7 scenarios) and `Migrated_records_keep_their_classification_and_are_protected_by_it` | |
| Elevation (IDN-03) | `ElevationScenarios` (6 scenarios) and `ElevationUiTests` | |
| Cross-tenant | `Another_tenants_records_do_not_exist_for_any_lifecycle_call`; `Notification_administrators_see_only_their_own_organisations_deliveries` | |
| Dashboards | `Dashboards_never_show_or_count_a_record_the_reader_could_not_open`; `A_reminder_is_acknowledged_by_a_current_owner_and_the_dashboard_follows`; API `The_new_pages_need_sign_in_and_the_permissions_they_name` | |
| Signature boundary | `The_signature_boundary_stores_envelope_status_and_a_completed_envelope_only_supplies_the_executed_copy`; unit `SignatureProviderTests` | |

Not tested by design or not possible here: SMTP and Graph against live services; a real e-signature service; browser-level accessibility of the new pages (ACC-01 remains open); daylight-saving boundaries for reminders; performance of the dashboards with large registers (PER-001 baseline is not measured).

### 8.2 Implementation status of the metadata, search, views, exports, Studio and migration scenarios (CFG-03, DOM-03, STU-01; 2026-10-16)

Each scenario runs on SQLite (lightweight, **not** SQL Server proof) and, through the `SqlServer` wrappers, on a real SQL Server when `ECP_TEST_SQL` is set. **In this workspace the SQL Server wrappers did not run (the container daemon could not be started; the start command was denied), so they are NOT VERIFIED locally. On GitHub-hosted CI (run 21, commit 797aa55, and run 22, commit 5d695eb) the `sqlserver-integration` job ran them against SQL Server 2022: 183 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED.** SQLite results remain lightweight checks, not SQL Server proof.

| Area | Where it is covered |
|---|---|
| Field model, parser, validator, impact, writer | unit `CustomFieldTests` (56), `StudioModelTests` (5); `WorkflowDefinitionTests` (`cf.` names) |
| Values: every type, validation, defaults, requiredness, role/state/type rules, classification and privileged hiding, locks, versions and retirement, tenant isolation and cache, failed publish | `CustomFieldScenarios` (8 scenarios), `CacheFailureScenarios`, `VariationFieldScenarios` |
| Studio service (draft, edit, order, validate, impact, publish, retire, rollback, permissions, audit) | `StudioScenarios`; pages: `StudioUiTests` (real host, antiforgery) |
| Typed search: filters, sorts, paging, limits, grammar refusals, scope, hidden fields, tenant | `RegisterScenarios` (2) |
| Views: 11 shipped views, personal (private, validated, unique, deletable), shared (configuration lifecycle, per-viewer access, retired fields) | `RegisterViewScenarios` |
| Exports: columns, filters, formula safety, limits, large export reason, audit; reporting schema | `RegisterViewScenarios` (2) |
| Contract change history and hidden values | `ChangeHistoryScenarios` |
| Approval facts: live and simulated parity; only usable fields | `ApprovalFactScenarios` |
| Migration into configured and core fields: mapping, dry run, explicit choice mapping, retired and unsupported fields, provenance, idempotent rerun, update, rollback | `MigrationCustomFieldScenarios`; unit `The_custom_field_sample_columns_map_…` |
| Architecture: tiers for Formats and Metadata, same-tier edge, no cross-module persistence, no expression compilation | `ArchitectureTests` |
| Performance baseline (10,000 contracts) and shape guard | `PerformanceBaselineTests_Sqlite`; `PerformanceBaselineTests_SqlServer` (Passed in CI run 22; numbers in `PERFORMANCE.md`) |

Not covered: browser-level accessibility of the Studio and register pages (ACC-01 remains open); concurrency of two administrators editing one draft beyond the conflict message; keyset paging (not built); SQL Server index plans for `ContractCustomValue` and `SavedView` were reviewed afterwards (PER-002, section 8.4); 100,000-contract scale is not measured.

### 8.3 Pack resolution and the Blank pack (portability slice S1, ADR-0024; PRT-001, first step of PRT-002)

| Area | Where it is covered |
|---|---|
| Each organisation of one tenant works from its own pack; a Blank-pack contract is created in `Open`, activated, locked, and no JBS state code appears in its state path or history; the installed Blank definition names the neutral approver group and no JBS group; the JBS organisation is unaffected | `PackResolutionScenarios` (`Own_pack`), SQLite and SQL Server |
| A Blank organisation resolves its Variation workflow and notification policy and can open a Variation on an active contract (the parent moves to `Amending`); no Legal-review rule exists in its policy | `PackResolutionScenarios` (`Variation_and_policy`) |
| An organisation without a pack uses the installation default (the test hosts set it to the reference pack) | `PackResolutionScenarios` (`Installation_default`) |
| With no installation default (the shipped configuration) an organisation without a pack is refused with `no_solution_pack` for both the workflow and the policy, nothing is installed, and an organisation with a pack of its own is unaffected | `PackResolutionScenarios` (`No_installation_default`) |
| The neutral lifecycle, Variation workflow and notification policy validate through the normal validators, share no state code with the JBS lifecycle, and name no business | unit `BlankPackTests` (4) |
| The business-name guard (ARC-010, stage A) matches every spelling, including a lowercase pack id and a name inside an identifier | `ArchitectureTests` `The_business_name_guard_recognises_every_spelling` (7 cases) and `No_business_pack_names_or_workflow_state_literals_in_compiled_code` |

PRT-002 as a whole (a second business with its own fields, rules and approvals) stays **NOT VERIFIED**; it needs slice S9.

### 8.4 PER-002: query-plan review and search regression guards (SQL Server)

| Area | Where it is covered |
|---|---|
| The access subquery is left out for a field every grant allows, and kept otherwise: the open/closed decision, including empty grants and case | unit `LevelVisibilityTests` (4) |
| A text search, configured filter and configured sort read the `Contract` table at most twice (before the change: seven reads; the test fails against the old code) | `SearchPlanShapeScenarios` `Open_fields_skip_the_access_subquery`, SQLite and SQL Server |
| The number of SELECT statements for a page does not grow with the rows on it, within 40 (34 measured) | `SearchPlanShapeScenarios` `Statements_do_not_grow_with_rows`, SQLite and SQL Server |
| Results and visibility unchanged: hidden fields absent and unusable in filters, sorts and text; another tenant; role-hidden field | existing `RegisterScenarios`, `RegisterViewScenarios`, `CustomFieldScenarios`, `ApprovalFactScenarios`, `MigrationCustomFieldScenarios` on both providers |
| Baseline and plan report on every CI run | `PerformanceBaselineTests_SqlServer` (separate CI step; prints summary, JSON and plans) |

No time limit is asserted (runner variance is up to 2.4 times). Result: `docs/PERFORMANCE_REVIEW.md` section 9.

### 8.5 Totals at the end of the PER-002 / S1 pass (superseded by 8.7)

Without `ECP_TEST_SQL`: **730 Passed, 0 Failed, 0 Skipped, 189 NOT VERIFIED** (451 unit, 20 architecture, 72 API, 187 integration on SQLite). CI with SQL Server 2022 (run 32, commit a0293ee): **189 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED** for the SQL Server suite; the performance baseline is a separate step; the other two jobs also succeeded.

### 8.6 Reminder fall-back and migrated initial status (portability slice S2, ADR-0025; P-02a and P-08)

| Area | Where it is covered |
|---|---|
| The policy field `reminders.unownedFallback`: absent reads as the legacy fall-back, empty means nobody, unknown and repeated rules are refused, the JBS pack states `OrganisationAdministrators` and the Blank pack states none | unit `ReminderFallbackPolicyTests` (4) |
| A Blank organisation never resolves JBS reminder recipients: with the category unowned, an administrator holding the old fall-back role is not told and the reminder is held (counted as skipped, no window used); with ownership restored the owner is reminded once and the administrator still is not; the test fails against the old fall-back | `ReminderFallbackScenarios` (`Blank_policy`), SQLite and SQL Server |
| JBS behaviour unchanged: the reference policy states the fall-back and the administrator still receives the unowned reminder | `ReminderFallbackScenarios` (`Reference_policy`), SQLite and SQL Server |
| A migrated contract starts in the initial state of its own organisation's lifecycle (JBS `Draft`, Blank `Open`); an organisation with no lifecycle is refused with `no_solution_pack` and nothing falls back to a configured status | `MigrationInitialStatusScenarios` (`Own_lifecycle`, `No_lifecycle`), SQLite and SQL Server |
| Every migration message code is documented (now including `MIG-E026`) | unit `MigrationCatalogueTests` |

### 8.7 Totals at the end of the S2 pass

Without `ECP_TEST_SQL`: **738 Passed, 0 Failed, 0 Skipped, 193 NOT VERIFIED** (455 unit, 20 architecture, 72 API, 191 integration on SQLite). CI with SQL Server 2022 (run 35, commit 3245079): **193 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED** (192 SQL Server tests plus the performance baseline); the other two jobs also succeeded.

### 8.8 External solution packs (slice S7, ADR-0026; PKG-001 to PKG-012 in part, PRT-002 first mechanism, PRT-007, PRT-008)

| Area | Where it is covered |
|---|---|
| Package format: safe archive (traversal, absolute paths, backslashes, duplicates, bombs, ratio, entry count, unlisted and disallowed files), canonical JSON, manifest strictness, hash verification, content scan, compatibility (platform range, unknown required/optional kinds, schema versions), deterministic build | unit `PackFormatTests` |
| Signing and trust: ES256 round trip, tamper detection, wrong key, unknown, revoked, expired and not-yet-valid keys, unsupported algorithm, Production versus Demo/Test policy, unsigned synthetic only when marked, allowed by the manifest **and** enabled by the environment, a manifest cannot weaken the policy, command line (build, validate, hash, sign, verify, inspect, compare, keygen) with no key material in output | unit `PackSigningTests` |
| Upload, check, preview, install; idempotent re-upload; same key and version with different bytes refused; spent token; audit of upload, install and rejection | integration `PackManagerScenarios` `Install_flow` (SQLite; SQL Server wrapper in CI) |
| Permissions follow the confirmed matrix; trust-key administration not held by the business configuration administrator | `Permissions`; UI `Only_people_who_hold_the_pack_permissions_can_open_each_page` |
| Tenant isolation of packs, keys and uploads; an upload is usable only by its uploader; the package cache is keyed by tenant and package | `Isolation`, `Cache` |
| Assignment creates drafts only, never overwrites a published release, keeps an open draft, refuses a retired pack | `Assignment` |
| **Third pack the repository has never seen** installed from bytes and run end to end with no source change | `Third_pack` |
| Production refuses unsigned and unknown-key packs; trusting a publisher key admits the same file; revocation withdraws it; key administration audited with no key material | `Production_trust` (hosts built in Production mode with the process-local test key) |
| Concurrent uploads and installs of one pack leave exactly one copy | `Concurrency` |
| Hostile packages (not a zip, empty, traversal, unexpected file, tampered content) give plain messages, no server path, no stack trace, and store nothing | `Hostile` |
| CSV report defuses formulas | `Csv` |
| Report-only diagnostics for unresolved packs and fallback definitions change nothing | `Diagnostics` |
| Pages: access by role, shipped catalogue and unsigned warnings, upload/check/install with confirmation and CSRF refusal, other person's upload not found, step-up after ten minutes, XSS encoding of pack text, private key refused and never echoed, readiness (healthy, missing folder unhealthy, unsigned-in-Production degraded) | API `PackManagerUiTests` |
| Onboarding offers loader packs with publisher and signature, allows staying on Blank, refuses an unavailable pack plainly | API `OnboardingHostTests` |
| Architecture: no embedded pack documents (projects and assemblies); the reader only in the pack library, tool and loader folder; the loader names no pack; sources and built packages separate; every shipped source builds reproducibly; no private key in the working tree; `Ecp.Packs` at tier 0 | `PackRuleTests` and `ModuleDependencyTests` |
| CI (`tools/check-packs.sh`): two builds are byte-identical, every pack validates, the host output carries the packs, signing and verifying with an ephemeral key works, unsigned, wrong-key and tampered packs are refused as Production would, no embedded pack and no tracked private key | CI step "Solution packs" |

Not covered: a browser accessibility audit of the new pages (ACC-01), SQL Server row-level security of the three new tables beyond the existing "every mapped table has a policy" test (verified in CI only), a penetration test, and the container smoke of the new `solution-packs` readiness line (run by CI; the container engine cannot be started in this workspace).

### 8.9 Totals at the end of the S7 pass

Without `ECP_TEST_SQL`: **854 Passed, 0 Failed, 0 Skipped, 203 NOT VERIFIED** (545 unit, 27 architecture, 80 API, 202 integration on SQLite). The 203 are the SQL Server wrappers, reported NOT VERIFIED rather than passed. The S7 CI result is recorded in `deploy/STATUS.md`.

## 9. No-code, packages and portability suite

| ID | Test |
|---|---|
| NCD-001 | Creating fields/forms/rules/workflow/matrix changes no database schema (also PRT-003) |
| NCD-002 | Labels, help and terminology are encoded; XSS corpus |
| NCD-003 | Closed vocabulary: unknown operator, action, function, node type rejected at validation and runtime |
| NCD-004 | Pattern engine: length cap, non-backtracking, timeout |
| NCD-005 | Template placeholders validated against the event allowlist |
| NCD-006 | Rule pass cap; non-terminating rule set rejected by validation and stopped safely at runtime |
| NCD-007 | Safe-change rules block unsafe edits of in-use definitions |
| NCD-008 | Draft → validate → preview → simulate → publish → version history → rollback → audit end-to-end |
| NCD-009 | Draft concurrency and three-way rebase |
| NCD-010 | Calculated-display limits (nodes, functions, no cross-record access) |
| NCD-011 | Conditional visibility/required/read-only on forms by role, state, type |
| NCD-012 | All 17 field types: validation, storage, search, reporting projection |
| NCD-013 | Preview performs no writes |
| NCD-014 | Simulation performs no writes and matches production decisions |
| NCD-015 | Feature flags enable only existing capabilities |
| PKG-001 | Export → import round trip yields identical release hash |
| PKG-002 | Dry run writes nothing to the target |
| PKG-003 | Conflict resolution: keep target, replace, keep both, merge lookup values |
| PKG-004 | Hash mismatch / tampered file rejected |
| PKG-005 | Signature required in Production for catalogue packs; audited override path |
| PKG-006 | Traversal, bomb, macro, executable, symlink, oversize rejected |
| PKG-007 | Missing dependency / incompatible platform version rejected |
| PKG-008 | No secrets, users, assignments or privilege grants accepted |
| PKG-009 | Three-way pack upgrade preserves local changes |
| PKG-010 | Apply produces a **draft**, never a published release |
| PKG-011 | Compatibility metadata respected |
| PKG-012 | Agreement Reference conflicts (overlapping prefix or range, seed reference already used in the tenant) are detected in the dry run before any commit |
| REF-001 | Unique `(TenantId, AgreementReference)`: a duplicate in another organisation of the same tenant is refused without disclosing that record; the same reference in another tenant is allowed; numbering allocation is concurrent-safe and never reuses numbers; `sourceAgreementReference` is not unique-constrained |
| PRT-001 … PRT-015 | As defined in `requirements/08-portability/PORTABILITY_ACCEPTANCE_CRITERIA.md`; PRT-002 uses a second, non-JBS pack |

## 10. Security regression suite (`Category=Security`)

| ID | Test |
|---|---|
| SEC-001 | Security headers and CSP on every response type; no `unsafe-inline`/`unsafe-eval` |
| SEC-002 | CSRF: state-changing form posts without token rejected |
| SEC-003 | Cookie flags; session timeout; rotation on privilege change |
| SEC-004 | Open redirect and OIDC state/nonce/replay |
| SEC-005 | XSS corpus in every free-text input (names, labels, help, comments, terminology, template text) |
| SEC-006 | SQL injection corpus on filters, sort, search |
| SEC-007 | SSRF corpus (private ranges, metadata address, DNS rebinding, redirect, IPv6, decimal IP) for webhooks, website retrieval, catalogue URLs |
| SEC-008 | Setup requires the token; wrong/expired token 404; remote origin refused by default |
| SEC-009 | After completion setup routes do not exist |
| SEC-010 | Production refuses Demo identity, default credentials, ephemeral storage, exposed secrets (start-up aborts) |
| SEC-011 | File type allowlist and content-signature check |
| SEC-012 | Hostile filenames never become paths |
| SEC-013 | Size, count and ratio limits |
| SEC-014 | XXE and macro documents rejected |
| SEC-015 | Polyglot files (PDF/HTML) handled |
| SEC-016 | Scan status Pending/Infected/Error blocks download |
| SEC-017 | SVG branding sanitised; no custom CSS/HTML |
| SEC-018 | Mass assignment: unknown and protected properties ignored or rejected |
| SEC-019 | Errors are generic with correlation id; no stack traces, SQL or paths |
| SEC-020 | Rate limits and lockouts on auth, setup, import, export, webhooks |
| SEC-021 | No user-enumeration or timing oracle |
| SEC-030 | **Support bundle redaction**: seeded canary secrets and PII fields absent from the bundle |
| SEC-031 | Log redaction filter |
| SEC-040 | Reporting principal for tenant A cannot read tenant B through views |
| SEC-041 | Reporting principal cannot read base tables |
| SEC-042 | Reporting views honour clearance and masking |
| SEC-043 | Reporting principal is read-only |
| SEC-044 | API export and file drop honour the same rules |
| SEC-045 | Secret rotation revokes access |
| SEC-050 | AI disabled by default; no network call without configuration |
| SEC-051 | AI principal cannot write; proposals require human acceptance and pass validation, rules and locks |
| SEC-052 | Prompt-injection corpus in documents cannot trigger actions or exfiltration; output schema-validated |
| SEC-053 | AI prompts exclude sensitive fields and above-clearance data |
| SEC-060 | Vulnerable-package gate fails the build above threshold |
| SEC-061 | SBOM generated and archived |
| SEC-062 | Dependency licence allowlist enforced |

## 11. Accessibility suite (`Category=A11y`)

Automated axe-core checks plus scripted keyboard tests on every page type; manual screen-reader and zoom checklists at each milestone exit (evidence stored in `docs/`).

| ID | Test |
|---|---|
| A11Y-001 | axe-core: zero serious/critical violations on every page type (generated from the route list) |
| A11Y-002 | Keyboard-only completion of: setup wizard, onboarding wizard, create/edit contract, transitions, approvals, migration wizard |
| A11Y-003 | Dialog focus trap, Escape, focus return |
| A11Y-004 | Skip link, landmarks, one `h1`, meaningful `title` |
| A11Y-005 | Form errors summarised, associated, focus moved |
| A11Y-006 | Table semantics (caption, scoped headers), sort announcements |
| A11Y-007 | Live-region announcements for save, publish, job progress |
| A11Y-008 | Designers: keyboard operation, move up/down/into controls, outline equivalent |
| A11Y-009 | Wizard progress as text, resume, back/forward state |
| A11Y-010 | 320 CSS px reflow and 200%/400% zoom |
| A11Y-011 | Contrast for default and pack branding; refusal of low-contrast branding |
| A11Y-012 | Reduced-motion respected |
| A11Y-013 | Target size minimum |
| A11Y-014 | Terminology aliases keep accessible names meaningful |
| A11Y-015 | Accessible names for controls, icons, status chips |
| A11Y-016 | Session-timeout warning is accessible and extendable |
| A11Y-017 | File upload and drag/drop have keyboard alternatives |
| A11Y-018 | Error pages and 404/403 content |
| A11Y-019 | Print views |
| A11Y-020 | Localised (en-AU, en-NZ) formats announced correctly |

## 12. Audit, adapters, notifications, concurrency, retention

| ID | Test |
|---|---|
| AUD-001 | Runtime principal cannot UPDATE/DELETE audit rows (database-level) |
| AUD-002 | Hash chain verifies; tampering detected at the exact event |
| AUD-003 | Gap-free sequence under concurrent writers |
| AUD-004 | Sensitive field values redacted in old/new |
| AUD-005 | Change and audit commit together; failure rolls both back |
| AUD-006 | Elevation/unlock id present on events; denials audited with rate limiting |
| AUD-007 | Evidence export manifest hashes and HMAC verify; export is audited |
| ADP-001 | Outbox retry with back-off and jitter |
| ADP-002 | Dead-letter visible; replay works; manual fallback available |
| ADP-003 | Contract suite passes for every mock and each real adapter (opt-in) |
| ADP-004 | Inbound webhook signature, timestamp window |
| ADP-005 | Replay of an inbound event is a no-op (inbox) |
| ADP-006 | Scanner hook gates promotion; Production without scanner fails readiness |
| ADP-007 | Egress guard (also SEC-007) |
| ADP-008 | Health and `DescribeAsync` reporting |
| NTF-001 | Duplicate event sends one message |
| NTF-002 | Retry after failure sends one message |
| NTF-003 | Restart mid-send sends one message |
| NTF-004 | Reminder dedupe across job re-runs |
| NTF-005 | Persistent failure dead-letters and surfaces |
| NTF-006 | Users without read access receive no message naming the record |
| NTF-007 | Localisation fallback explicit and logged |
| CNC-001 | ETag / `If-Match` mismatch returns 412 |
| CNC-002 | Concurrent double approval: exactly one succeeds |
| CNC-003 | Concurrent draft edits produce a conflict |
| CNC-004 | Outbox claim contention: each message processed once |
| IMP-001 | Records export honours visibility, masks sensitive fields |
| IMP-002 | Above-clearance records excluded |
| IMP-003 | Formula injection neutralised |
| IMP-004 | Export links authorised, user-bound, expire |
| IMP-005 | Documents export manifest re-ingests via migration |
| IMP-006 | Full tenant export imports into an empty tenant with matching counts and hashes (PRT-011) |
| RET-001 | Default retention is Indefinite |
| RET-002 | Archive precedes deletion; users cannot hard-delete |
| RET-003 | Legal hold blocks archive/disposition |
| RET-004 | Disposition requires approval and retention checks |
| RET-005 | No user-facing hard-delete endpoint exists (ARC check) |
| RET-006 | Tombstone and disposition evidence |

## 13. Operations and deployment suite

| ID | Test |
|---|---|
| BRS-001 | Backup procedure produces a restorable artefact (database + documents + key ring + configuration) |
| BRS-002 | Restore into a clean instance; validation script compares counts, hashes, document SHA-256, audit chain, release id |
| BRS-003 | Restore-test evidence recorded and shown on the operations dashboard |
| BRS-004 | Maintenance mode blocks writes, allows health and admin |
| UPG-001 | N-1 → N database migration with populated data |
| UPG-002 | Pre-flight detects blockers (version, free space, pending jobs) |
| UPG-003 | Rollback by restore returns to N-1 state |
| UPG-004 | Pack upgrade and configuration compatibility |
| DEP-001 | Compose stack starts, becomes ready, setup wizard reachable only with token |
| DEP-002 | Restart persistence: database, documents and key ring survive; sessions still valid |
| DEP-003 | IIS publish output and `web.config` validated statically (*scaffold, unverified*) |
| DEP-004 | Azure Bicep validates (*scaffold, unverified*; requires tooling) |
| DEP-005 | Image hardening: non-root, ICU and time-zone data present, no secrets, pinned digest |
| DEP-006 | Health endpoints behave per mode and reveal no detail unauthenticated |
| DEP-007 | Readiness checks R-* behave per mode (Production fails closed) |
| DEP-008 | Shared behaviour suite parameterised by environment (PRT-005) |

## 14. Performance (initial budgets, adjust with Q-13)

| ID | Test |
|---|---|
| PER-001 | Contract list/search p95 < 500 ms at 100k contracts. **Partly measured**: a baseline at 10,000 contracts on SQLite is in `PERFORMANCE.md`; SQL Server measured at 10,000 contracts in CI (`PERFORMANCE.md`); 100,000 contracts and the plan review are NOT DONE |
| PER-002 | Search by custom field with 50 fields per entity uses indexes (query plan check). **Done at 10,000 contracts and 35,000 values** on SQL Server 2022 in CI (plans in `PERFORMANCE_REVIEW.md`; configured-field text search 599 to 417 ms median). Not verified at 50 fields per entity or 100,000 contracts |
| PER-003 | Migration of 25k contracts and 50k documents completes resumably within budget; memory bounded |
| PER-004 | Export/report/import under concurrent load stays within rate limits and does not starve interactive users |
| PER-005 | Row-Level Security overhead measured and within budget |
| PER-006 | Audit append contention under concurrency |

## 15. Pipeline and reporting

1. Pre-merge gate: restore (locked), build with warnings as errors, Unit + Architecture + Security + Isolation + Migration (needs SQL Server), vulnerable/licence scans, SBOM, OpenAPI diff, link check for docs.
2. Nightly: E2E + A11y, Performance, Deployment, backup/restore and upgrade smoke.
3. Milestone exit: the gate in `IMPLEMENTATION_PLAN.md`, plus skill-based reviews (`.claude/skills/`), plus updated threat model.
4. Every run produces a test report listing run / not run / failed by ID. `TASKS.md` references report IDs, not opinions.

## 16. Traceability to Master "Testing" and the Definition of Done

| Master test family | IDs |
|---|---|
| Unit, integration | Layers in section 2 |
| Authorisation | AUT-001 to AUT-016 |
| Tenant isolation | TEN-001 to TEN-040 |
| Migration, attachment matching | MIG-001 to MIG-027 |
| No-code builder | NCD-001 to NCD-015 |
| Workflow, approval | WFL-001 to WFL-013, APR-001 to APR-010 |
| Import/export, package | IMP-001 to IMP-006, PKG-001 to PKG-011, PRT-* |
| Concurrency | CNC-001 to CNC-004 |
| Notification idempotency | NTF-001 to NTF-007 |
| Accessibility | A11Y-001 to A11Y-020 |
| Backup/restore smoke, upgrade | BRS-001 to BRS-004, UPG-001 to UPG-004 |
| Security regression | SEC-001 to SEC-062, AUD-*, ADP-*, RET-* |

The Master **Definition of Done** scenario (clean demo install → onboard → configure without code → import synthetic contracts/attachments through dry run and commit → resolve unmatched → enforce tenant/role security → Legal and approval lifecycle → lock → Variation → config pack round trip → reporting/API → audit evidence → automated tests across deployment artefacts) is implemented as one scripted E2E journey, `E2E-DOD-001`, composed of the tests above, run nightly and at release readiness.
