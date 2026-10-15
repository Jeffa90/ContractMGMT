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
| End-to-end | Browser flows incl. wizards, designers, migration; accessibility | `tests/Ecp.E2E.Tests` (Playwright + axe-core) | SQL Server, Chromium (present at `/opt/pw-browsers`) |
| Contract | Adapter interface suites run against mocks and real providers | `tests/Ecp.Adapters.ContractTests` | Providers only for opt-in runs |
| Deployment | Container build/run, static validation of IIS/Azure artefacts | `tests/Ecp.Deployment.Tests` + scripts | Container engine for runtime part |
| Performance | Scale and budgets | `tests/Ecp.Performance.Tests` | SQL Server, larger data |

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

Environment findings are recorded in `docs/ENVIRONMENT.md` (task T0.1). SQL Server for local runs, in order of preference: (A) container engine (`dockerd`) if it can run; (B) native `mssql-server` installed from the Microsoft package feed in the workspace; (C) external SQL Server via `ECP_TEST_SQL`. The `NotVerified` mechanism is implemented by the `SqlServerFact` and `SqlServerTheory` attributes in the test infrastructure project.

Planned commands:

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
| APR-008 | Matrix version pinned on submission |
| APR-009 | Simulator output equals runtime resolution for the same inputs |
| APR-010 | Currency mismatch without rate table blocks (no implicit conversion) |

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
| PER-001 | Contract list/search p95 < 500 ms at 100k contracts |
| PER-002 | Search by custom field with 50 fields per entity uses indexes (query plan check) |
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
