# Portability Remediation Plan

Status: **S1 and S2 implemented (see sections 8.6 and 8.9); every other slice is still a plan.** The plan itself changed no code, configuration, pack or data. It turns the findings of `docs/PORTABILITY_AUDIT.md` (P-01 to P-10, R-01 to R-06) into ordered, testable work. Finding IDs below are the audit's. Every fact about current behaviour was checked in the code and is cited by file and line; where something is a judgement it says so.

The target is the rule in `CLAUDE.md` and `requirements/01-authoritative/PORTABILITY_AND_NO_CODE.md`: JBS is a shipped reference configuration, not platform logic, and a second business must install and run with no source change (PRT-002, PRT-013).

## 1. Confirmed platform-level JBS dependencies

"Confirmed" means the dependency was traced to the behaviour a user or a second business would see, not only to a matching word. They are grouped by what kind of dependency they are, because the fix differs.

### 1.1 JBS behaviour selected or run by platform code

| ID | Dependency | Confirmed by |
|---|---|---|
| P-01 | **Every organisation that has published no workflow or notification policy gets the JBS ones**, whatever its own pack is. The fallback reads a process-wide option `DefaultPackId` whose code default is `"jbs-procurement"` and never looks at the organisation's `SolutionPackKey`. | `src/Modules/Ecp.Lifecycle/Definitions/DefinitionStore.cs:14`, `:84`-`:89`; `src/Modules/Ecp.Notifications/NotificationsApi.cs:15`; `src/Modules/Ecp.Notifications/Policy/PolicyStore.cs:44`-`:47`. The host sets only `Mode` (`src/Ecp.Host/Program.cs:64`-`:65`). Organisations created by setup do carry their pack (`src/Modules/Ecp.Setup/Stages/SetupStages.cs:368`, stored at `src/Modules/Ecp.Tenancy/Domain/Tenancy.cs:76`), so the information exists and is unused. |
| P-02a | Reminder fall-back routes to the JBS-named role `ProcurementAdministrator` when nobody owns the category. | `src/Modules/Ecp.Notifications/Reminders.cs:94`-`:95` |
| P-08 | The initial status of **migrated (non-legacy) contracts** is `Contracts:InitialStatusCode`, which ships as `"Draft"`, not the status the organisation's workflow defines. New contracts created through the pages use the workflow's initial state; the migration path does not. | `src/Modules/Ecp.Contracts/MigrationPort.cs:160`, `:162`; `src/Modules/Ecp.Contracts/ContractsApi.cs:120`; `src/Ecp.Host/appsettings.json:23` |
| R-05 / P-04 | Legal review and register authorisation (with Registered Date stamping) are compiled capabilities, always on. The pack fields that look like switches (`features`, `ownershipModel`, `approvalModel`) are read into a record and never consulted. | `src/Modules/Ecp.Configuration/PacksAndBranding.cs:18`; no other reader (searched `src/`) |

### 1.2 Compiled structures that make a pack impossible to replace without a rebuild

| ID | Dependency | Confirmed by |
|---|---|---|
| P-03 | The JBS pack, its two workflows and its notification policy are embedded into module assemblies by project files and found by resource name. Adding a business means editing a `.csproj` and rebuilding. | `src/Modules/Ecp.Configuration/Ecp.Configuration.csproj:13`-`:14`; `src/Modules/Ecp.Lifecycle/Ecp.Lifecycle.csproj:19`-`:20`; `src/Modules/Ecp.Notifications/Ecp.Notifications.csproj:18`; `src/Modules/Ecp.Lifecycle/Definitions/DefinitionStore.cs:30`, `:111` |
| P-02b | The role catalogue (twelve roles, one named for the procurement function) is a compiled constant list; grant rules, onboarding rules and scope rules are written against it; the role-to-permission matrix is embedded from the reference CSV. | `src/Modules/Ecp.Identity/Domain/Roles.cs:11`, `:22`; `src/Modules/Ecp.Identity/Access/AccessEvaluator.cs:199`; `src/Modules/Ecp.Identity/Domain/RoleAssignment.cs:15`; `src/Modules/Ecp.Identity/Ecp.Identity.csproj:13`-`:14` |

### 1.3 JBS vocabulary shown to every business

| ID | Dependency | Confirmed by |
|---|---|---|
| P-05 | JBS state names in dashboard cards, an embedded system view, a flash message, and Legal-review labels | `src/Modules/Ecp.Notifications/Dashboards.cs:23`, `:96`-`:121`; `src/Modules/Ecp.Contracts/Register/system-views.json:10`; `src/Ecp.Host/Pages/Approvals/Details.cshtml.cs:26`; `src/Modules/Ecp.Lifecycle/LegalReviews.cs:149`-`:158` |
| P-06, P-07 | Page and help text naming the Group Head of Procurement, "Packaging", "Procurement-Viewers" | `src/Ecp.Host/Pages/Approvals/Index.cshtml:5`; `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:17`-`:35`; `src/Modules/Ecp.Setup/Stages/SetupStages.cs:185` |
| Terminology | A terminology map exists in the branding record (validated and stored) but **nothing in the UI reads it**; labels are resource strings. So the lever PRT-004 relies on exists on the write side only. | `src/Modules/Ecp.Configuration/PacksAndBranding.cs:67`-`:104`; no reader in `src/Ecp.Host` |

### 1.4 Generic concepts with one business's shape

| ID | Dependency | Confirmed by |
|---|---|---|
| P-10 | The signature boundary is "envelope" shaped (DocuSign's concept), down to a persisted column and index | `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:278`; `src/Modules/Ecp.Lifecycle/Persistence/LifecycleDbContext.cs:126`, `:131` |
| R-01 | Supplier ABN (eleven digits, refused otherwise) and SAP outline agreement number are fixed core columns | `src/Modules/Ecp.Contracts/Domain/Contracts.cs:187`, `:505`; `src/Modules/Ecp.Metadata/FieldModel.cs:152`, `:174` |
| R-02 to R-04 | `"Legal Privileged"` constant, Australian locale fall-backs, `"Evergreen"` literal | listed in the audit, section 3.1 |

### 1.5 The guard that should have caught these

P-09: the architecture rule ARC-010 scans `*.cs` only, case-sensitively, for five names and twelve quoted state codes (`tests/Ecp.Architecture.Tests/ArchitectureTests.cs:191`-`:196`). It cannot see lowercase `jbs-procurement`, Razor, project files, JSON, `Procurement` or role names. It is the reason P-01 to P-07 pass CI today.

### 1.6 Not dependencies (checked, clean)

Medius, Qlik and DocuSign are not named anywhere in `src/`. The approver group key `GroupHeadProcurement` is not in compiled logic (the engine reads `approverGroupKey` from the published definition). Workflow state codes are opaque strings to the contract aggregate. Reference numbering is an organisation prefix rule.

## 2. Solution-pack responsibilities

The principle: **the platform owns mechanisms and closed vocabularies; a pack owns every business choice made with them.** Today the split is right for most things and wrong at the edges, which is where the findings are.

| Concern | Owner | Today | Gap |
|---|---|---|---|
| Workflow engine, guards vocabulary, publish-time validation | Platform | Correct | none |
| The lifecycle itself (states, transitions, who may do what, approver group keys) | **Pack** | Pack data, but selected wrongly (P-01) and only for JBS | Neutral lifecycle for the Blank pack; selection by organisation |
| Notification policy (events, recipients, templates, reminder windows, fall-back recipient) | **Pack** | Pack data, selected wrongly (P-01); fall-back recipient is code (P-02a) | Fall-back as a policy rule |
| Approval matrices | **Pack / organisation** | Configuration kind | none |
| Classification scale (levels, default, privileged level) | **Pack** | Pack defaults, but `"Legal Privileged"` is a compiled constant (R-02) | Read the privileged level from the policy everywhere |
| Roles: codes and permission grants | Split: **platform** owns the permission codes and the evaluator; the **pack** owns which roles exist and what each is granted | All compiled (P-02b) | Role catalogue as data (or stable neutral codes plus terminology) |
| Role and state *names shown to people* | **Pack (terminology)** | Resource strings; terminology map stored but unread | Read side of terminology |
| Dashboard cards, shipped list views | **Pack** (they name states and sets) | Compiled (Dashboards.cs) and one embedded view (P-05) | Move titles and view names to pack data |
| Categories, contract types, reference prefix, locale, currency, time zone | **Pack defaults, then the organisation** | Correct; fall-backs are Australian (R-03) | Neutral fall-backs |
| Additional fields (ABN, SAP number, tiers) | **Pack** | ABN and SAP are core columns (R-01); the field engine exists | Decision: keep as optional core, make validation a rule (section 6) |
| Optional capabilities (Legal review, Variations, register authorisation, critical notice, reporting) | **Pack, switched by feature flags** | Always on; flags unread (P-04, R-05) | Flags that gate (CFG-02) |
| Source-system profiles (Medius and similar) | **Migration sample / pack data** | Correct: no name in compiled code | none |
| Demonstration data | **Pack** | Embedded into the host for Demonstration mode only | Acceptable; load from the pack store later |
| Pack discovery, validation, install, upgrade, signing | Platform | Embedded catalogue of two packs (P-03) | Pack loader and store (PKM-01, CFG-04) |

What a complete pack manifest has to carry, beyond today's `pack.json` (identity, defaults, three advisory fields): `workflows` (lifecycle, Variation), `notificationPolicy`, `roles` (codes, display names, grants), `terminology`, `dashboards` and `systemViews`, `fields` and `lookups`, `capabilities` (the feature flags that gate compiled capabilities), `demo` (optional). Each part goes through the same draft, validate, publish path as any configuration, so a pack is nothing more than a signed bundle of releases.

## 3. Remediation actions

| # | Action | Fixes | Class |
|---|---|---|---|
| **R1** | Resolve the pack from the organisation; remove the JBS code default; give the Blank pack a neutral lifecycle, Variation workflow and notification policy | P-01 | Behaviour |
| R2 | Take the initial status for migrated and non-lifecycle creation from the organisation's workflow; remove the `"Draft"` setting | P-08 | Behaviour |
| R3 | Make the reminder fall-back recipient a notification-policy rule | P-02a | Behaviour |
| R4 | Widen ARC-010 in three stages, each landing with the fixes that make it pass | P-09 | Guard |
| R5 | Take state, card and view names from definitions and terminology; wire the terminology map to the UI; neutral help examples | P-05, P-06, P-07, R-06 | Vocabulary |
| R6 | Name the signature concept neutrally before the first adapter exists | P-10 | Naming |
| R7 | Pack loader and pack store; remove the embedded JBS resources | P-03 | Structure |
| R8 | Role catalogue: decide, then implement (ADR first) | P-02b | Structure |
| R9 | Feature flags that gate Legal review, Variations, register authorisation | P-04, R-05 | Structure |
| R10 | Country- and ERP-specific core fields, privileged-level constant, locale fall-backs, Evergreen literal | R-01 to R-04 | Cleanup |
| R11 | Second pack and the portability tests (PRT-002, PRT-004, PRT-013) | verification | Proof |

## 4. Ranking

Three views, as asked. "Architectural importance" is how much of the portability principle the item carries. "Business risk" is the risk of **leaving it**, to a pilot or to a second business. "Difficulty" is the effort and the risk of **changing it** (S small, M medium, L large). The detailed reasoning for each follows the tables.

### 4.1 By architectural importance (most important first)

| Rank | Action | Why |
|---|---|---|
| 1 | R1 pack resolved by organisation | Makes "pack" a runtime concept at all; every other fix assumes it |
| 2 | R7 pack loader and store | Without it a second business always needs a rebuild |
| 3 | R8 role catalogue | The widest compiled assumption; touches security, setup and storage |
| 4 | R9 feature flags | Lets a pack say what it does and does not use |
| 5 | R11 second pack and tests | Proves the above; turns PRT-002 from NOT VERIFIED into a gate |
| 6 | R4 guard | Keeps the fixed things fixed |
| 7 | R5 vocabulary and terminology read side | Completes PRT-004 |
| 8 | R2 initial status | Removes the last place the workflow is bypassed |
| 9 | R3 reminder fall-back | A single rule, but it is business routing in code |
| 10 | R6 signature naming | Prevents a vendor's concept becoming schema |
| 11 | R10 cleanup | Defaults and optional columns |

### 4.2 By business risk (highest first)

| Rank | Action | Risk if left |
|---|---|---|
| 1 | R1 | **High.** A non-JBS organisation silently gets the JBS lifecycle, approver group and notification policy, and contracts created under it are pinned to it. Fixing it later means changing the workflow of live contracts, which the platform cannot yet do (pinned contracts cannot be moved to another workflow release; recorded as Future in the lifecycle slice) |
| 2 | R2 | **High to Medium.** Migrated contracts in any pack whose first state is not `Draft` get a status the workflow does not contain |
| 3 | R3 | Medium. Reminders for unowned categories go to a role that may not exist in the business, or to nobody (counted as skipped for lack of a recipient) |
| 4 | R10 | Medium. A non-Australian supplier's tax number is refused (ABN must be eleven digits) when entered |
| 5 | R5 | Medium to Low. Wrong wording on dashboards and messages; no data effect |
| 6 | R8 | Medium. A business cannot rename or drop a role; data effect only if it is renamed later, after production use |
| 7 | R7, R9, R11 | Low for a single-business pilot; blocking for a second business |
| 8 | R6 | Low now; rises to Medium once an adapter and data exist |
| 9 | R4 | Low (process), but it is what stops regression |

### 4.3 By difficulty (easiest first)

| Rank | Action | Size | Why |
|---|---|---|---|
| 1 | R3 | S | One rule and a policy field; no data |
| 2 | R2 | S | One call site per path; the lookup already exists in the creation policy |
| 3 | R4 stage A | S | A regular expression and an allow-list; lands with R1 |
| 4 | R6 | S to M | Rename a column and an interface while there is no adapter; one EF migration |
| 5 | R5 | M | Many small edits; needs the terminology read side and moves pack data |
| 6 | R1 | M | Few lines of logic, but needs neutral Blank-pack definitions and a change to every test world |
| 7 | R10 | M | Validation as a rule is small; moving the columns into fields is large (section 6) |
| 8 | R9 | M to L | Gating compiled capabilities touches Lifecycle, Notifications, pages and migration |
| 9 | R7 | L | New loader, trust and packaging decisions (PKM-01, PKM-02) |
| 10 | R8 | L (rename) or M (terminology only) | Persisted codes, grants, mappings, imports |
| 11 | R11 | L | A whole second pack with fields, rules and approvals |

### 4.4 How the three views combine

The order in section 5 follows one rule: **do what is both important and cheap first, and do what is dangerous to change while it is still cheap.** R1 is first on importance and on risk and only medium in difficulty. R2, R3 and R4 stage A are cheap and remove behaviour that depends on R1's outcome. R6 is cheap now and expensive after the first adapter. R8 is the one that gets *more* expensive with every production install: the argument for deciding it early, even if it is implemented late.

## 5. Safe refactoring order

Each slice is shippable on its own, leaves the JBS pack behaving exactly as today, and ends with its tests green on both providers and on CI's SQL Server.

| Slice | Contents | Depends on | Exit evidence |
|---|---|---|---|
| **S1 (first), done** | R1, R4 stage A, a minimal PRT-002 test | none | section 8.6 |
| **S2, done** | R2 and R3 | S1 | Migrated contracts take the workflow's initial status; unowned-category reminder fall-back comes from the policy; JBS pack states the same recipient it has today |
| S3 | R6 | none (can run in parallel with S2) | Signature request vocabulary neutral; EF migration renames the column and filtered index; no data loss. **Done 2026-10-17 (section 8.11, ADR-0027).** |
| S4 | R5 and the terminology read side (the part of CFG-02 PRT-004 needs); R4 stage B (Razor, JSON, project files) | S1 | Render test with a renamed vocabulary shows no JBS term; guard passes on all source extensions. **Done 2026-10-17 (section 8.12, ADR-0028).** |
| S5 | R8 step 1: the ADR deciding the role catalogue (no code) | S1 | ADR accepted by the owner; decision D1 reconciled with PRT-013. **Done 2026-10-17: Option A+ accepted (section 8.13, ADR-0029, `docs/ROLE_CATALOGUE_ADR.md`).** |
| S6 | R9 feature flags that gate (CFG-02) | S4 | A pack without Legal review installs and runs; JBS unchanged. **Done 2026-10-17 (section 8.14, ADR-0030): a business can operate with Legal Review, Register Authorisation, Variations, Renewals and Signature Requests all off.** |
| S7 | R7 pack loader and store (CFG-04, PKM-01); remove embedded JBS resources; R4 stage C | S1, S5, S6 | A pack installs from the store with no rebuild; the three `.csproj` embeds are gone. **Done 2026-10-17 for S7a to S7c plus the Pack Manager, setup and onboarding integration (section 8.10); S7d to S7f are future.** The order dependency on S5 and S6 was waived by the owner's instruction. |
| S8 | R8 step 2: implement the ADR | S5, S7 | **Retired 2026-10-17, superseded by S4 (ADR-0028) and ADR-0029.** The accepted decision needs no rename; its follow-ups were delivered in section 8.13. |
| S9 | R11: second pack (PKM-04) with PRT-002, PRT-004, PRT-013 end-to-end | S1 to S8 | PRT-002 passes. **Designed 2026-10-17, not built: `docs/SECOND_BUSINESS_PROOF.md` (construction subcontracts; 19 acceptance criteria)** |
| S10 | R10 cleanup (neutral fall-backs; validation as rule; field relocation only if justified) | any | Section 6.3 |

Why this order and not another:

* **R7 before R8's implementation** because a role catalogue as pack data needs somewhere to live; **R8's ADR before R7** because it decides what the pack format has to carry.
* **R5 before R9** because flags are meaningless to a user if the pages that switch off still show the old vocabulary.
* **R4 stage C last** because the full guard (role names, `Procurement`) can only pass after R8.
* **R6 is independent** and is cheapest immediately, before the e-signature adapter (INT-03) adds data and a second implementation.

## 6. Compatibility risks

### 6.1 Per action

| Action | Risk | Mitigation |
|---|---|---|
| R1 | **Every test world creates organisations with no pack key** (`tests/Ecp.Integration.Tests/AccessWorld.cs:60` and the others pass none) and relies on the JBS fall-back. Removing the code default breaks the whole lifecycle, notification, approval and register suites at once | Test support sets the installation default pack explicitly (`tests/Ecp.Tests.Common/TestSupport.cs:150`-`:151`, where the module options are already configured); production reads it from host configuration and ships it unset. A null pack key with no installation default is a clear error, never a silent JBS |
| R1 | Blank pack has no workflow today, so a Blank organisation would get `workflow_pack_missing` on its first contract, and `Variations.cs` and the Variation page call the Variation definition (`src/Modules/Ecp.Lifecycle/Variations.cs:79`, `:120`), so a missing Variation workflow can break more than the Variation page | Ship neutral lifecycle, Variation and notification-policy definitions in the Blank pack in the same slice; a notification policy needs only a valid schema, lead days, content levels and delivery settings (zero events are valid; `src/Modules/Ecp.Notifications/Policy/NotificationPolicy.cs:118`-`:150`) |
| R1 | The seeded Demonstration organisation `org-a2` is on the Blank pack and currently shows the JBS lifecycle (`sample-data/demo-organisation-structure.json`) | Update demonstration scenarios and the demo page tests to the neutral lifecycle, or move that organisation to the JBS pack. Decide explicitly; do not leave it to chance |
| R1 | Organisations that already have releases | None changes: the fall-back applies only where nothing is published. See 7.1 for those that got JBS by the fall-back |
| R2 | A pack with no lifecycle (or a failing lookup) now blocks migration of non-legacy records | Same neutral definitions as R1; the dry run must report "no workflow for this organisation" as a blocker, not fail at commit |
| R3 | Changing who gets the fall-back reminder is user-visible | The JBS policy states today's recipient (the Procurement Administrators) so JBS behaviour is byte-for-byte the same; the closed recipient vocabulary gets one new rule; the policy validator rejects an unknown one |
| R4 | A widened guard fails the build | Land each stage with the fixes that make it pass, or with a dated allow-list that names the finding and the slice that removes it |
| R5 | Renaming a dashboard card key or a system-view key breaks bookmarks | **Change names, never keys**: `awaiting-legal` and its siblings (`src/Modules/Ecp.Notifications/Dashboards.cs:23`, `src/Modules/Ecp.Contracts/Register/system-views.json:10`) stay |
| R5 | Existing tests assert English strings | Strings change only where the vocabulary is replaced; JBS pack terminology keeps the old wording |
| R6 | Renaming a persisted column | EF `RenameColumn` and recreate the filtered index; single migration; no JBS data affected |
| R7 | A runtime pack loader reads files: path traversal, size bombs, unsigned packs in Production | Reuse the closed-vocabulary validators and the package rules of PKM-01 and PKM-02; the Docker image must carry the pack directory (the Dockerfile already copies `templates/packs`) |
| R8 | A rename touches security | See 6.2 |
| R9 | A flag that gates a capability can orphan data (a contract in "Awaiting Legal" in a pack that turns Legal off) | A flag change is a configuration publish with an impact analysis; refuse to turn a capability off while records are in its states |
| R10 | Moving ABN and SAP number into fields changes search, export, Variations (`cf.`), migration mapping targets and the reporting projection | Do not move them by default (6.3) |

### 6.2 R8 in detail (the riskiest)

Decision D1 fixed the twelve role codes and the matrix, and the evaluator, delegation policy and onboarding rules are written against those constants. Two options, to be chosen in the ADR (S5):

| | Option A: keep codes, move names to terminology | Option B: neutral codes, aliases for the old ones |
|---|---|---|
| What changes | Display names and help text come from pack terminology; code `ProcurementAdministrator` stays | Code renamed (for example to an organisation-administration code); old code accepted on input |
| Persisted data | None | `RoleCode` in role assignments and provider group mappings (both 50-character columns, `src/Modules/Ecp.Identity/Persistence/IdentityDbContext.cs:67`, `:113`); onboarding documents; customers' identity-provider mappings and import CSVs that name the role |
| Audit | None | Audit events are append-only and hash-chained, so **historical events keep the old code and are never rewritten**; readers must accept both |
| Risk | Low; leaves a procurement-flavoured *identifier* in code | Medium to high; touches grants and every role check |
| Reversibility | Easy | A data migration plus an alias period |
| Recommended | **Yes, now** | Only if the ADR finds a business that cannot accept the identifier; cheapest before any production install |

The judgement behind the recommendation: a code is an identifier, not a name. Users see names. If names come from terminology (S4), the remaining objection is aesthetic and architectural (PRT-013 text), which an ADR can resolve by naming the code as a stable platform identifier. If the owner wants the code changed anyway, do it before the first production install, as Option B with a one-time idempotent migration and an alias.

### 6.3 R10 in detail

* **Validation, not columns.** The harm in ABN and SAP is not the column but the rule that refuses a non-eleven-digit ABN. Making the supplier tax identifier a configured rule (format by country, optional) removes the harm with no data move. Relocating the columns into additional fields is a large change (migration of values, search, export, Variations, mapping profiles) with little portability gain, and is **not recommended** unless a business needs to remove the column.
* **Locale fall-backs** (`en-AU`, Brisbane, `AUD`) become neutral (UTC, invariant, no default currency) with the organisation's setting always winning; only places that run without an organisation are affected.
* **`"Legal Privileged"` and `"Evergreen"`** read from the classification policy and the existing constant respectively.

## 7. Data migration impacts

No remediation requires rewriting contract data. The impacts are on configuration rows, a few persisted strings, and one column.

| Action | Rows or columns affected | Migration needed | Notes |
|---|---|---|---|
| R1 | None for organisations that already have releases. New: Blank-pack definitions (data files) | No schema change | See 7.1 |
| R2 | None | No | Behaviour on the migration path only |
| R3 | The JBS policy release gains one field | A new release, published by the normal path; organisations that never republish keep the old release and the **code must treat an absent field as today's behaviour** until they do | Backward-compatible read |
| R4 | None | No | |
| R5 | Pack data (dashboard and view names); terminology read side | No schema change | Keys are stable (6.1) |
| R6 | `lifecycle` execution record: column `EnvelopeId` and its filtered index | One EF migration: rename column, recreate index | Data preserved by the rename; no running mixed versions (single process, forward-only migrations) |
| R7 | None if packs are stored as `SolutionPack` configuration releases (the kind already exists, tenant-level: `src/Modules/Ecp.Configuration/PacksAndBranding.cs:46`-`:64`); a new table only if the pack store needs one | Possibly one new table | Decide in the PKM-01 design |
| R8 (Option A) | None | No | |
| R8 (Option B) | Role assignments, provider group mappings, onboarding documents; embedded permission matrix columns | Idempotent `UPDATE` of the two role tables per tenant inside the migration runner; matrix and importer accept both codes for a stated period | Audit untouched (6.2) |
| R9 | Pack `capabilities`; per-organisation flag releases | No schema change | Refuse to switch off with records in the capability's states |
| R10 | None unless fields are relocated | No (recommended) | |

Enum-valued strings that look like candidates for renaming are **not to be renamed**: the Legal-review status is stored as its member name (`HasConversion<string>` at `src/Modules/Ecp.Lifecycle/Persistence/LifecycleDbContext.cs:52`), so renaming `AwaitingLegal` would be a data migration for a label problem. Fix the label (R5), keep the member.

### 7.1 Organisations that already received JBS by the fall-back

A deployment that has an organisation on a non-JBS pack and has used it holds JBS releases for it, and contracts with JBS statuses pinned to them. The plan never changes these automatically:

1. **Detect (read-only):** organisations whose `SolutionPackKey` is not `jbs-procurement` and whose published workflow, Variation or notification-policy release was installed by the system with the label "Installed the jbs-procurement reference ..." (set at `src/Modules/Ecp.Lifecycle/Definitions/DefinitionStore.cs:88`). A diagnostic query or readiness check (`R-*`) can list them.
2. **Report** them to the administrator with the contract counts per status.
3. **Decide per organisation:** keep the JBS definitions (they are now that organisation's own, editable configuration), or publish a replacement. A replacement must map every existing status, and the platform cannot move pinned contracts to a new workflow release yet, so this is a deliberate, separate piece of work, not part of S1.

On a pilot that has not used a non-JBS organisation, this set is empty.

## 8. The first remediation slice: S1

**Name:** Pack resolved by organisation, with a neutral Blank pack and a first portability test.

**Why this one:** it is first on architectural importance and on business risk (section 4), it is a medium change, it changes nothing for JBS, and it is the precondition for proving PRT-002 at all. Doing it first also surfaces, cheaply, how many tests silently depend on the JBS fall-back.

### 8.1 Scope

1. **Resolve the pack from the organisation.** `WorkflowDefinitionStore.CurrentAsync` and `NotificationPolicyStore.CurrentAsync` take the pack from the organisation's `SolutionPackKey`; where the key is null they use an **installation default** that is host configuration, unset in the shipped `appsettings.json`. If neither names a pack, they refuse with a clear message ("This organisation has no solution pack and no installation default is configured"). No pack identifier remains in compiled code.
2. **Neutral Blank pack.** Add a minimal contract lifecycle (for example Draft, Active, Closed with the transitions between them), a minimal Variation workflow, and a minimal notification policy to the Blank pack as data under `templates/packs/blank/`. They use only the existing closed vocabularies and no approver group. Still embedded for now (S7 removes the embedding).
3. **Test infrastructure** sets the installation default to the JBS pack explicitly, in the one place the test host configures the modules, so existing suites keep their behaviour. The Demonstration seed keeps the JBS pack on `org-a1`; `org-a2` (Blank) is moved to the decision in section 6.1.
4. **Guard, stage A:** ARC-010 gains a case-insensitive scan of `*.cs` for `jbs`, `medius`, `qlik`, `docusign` (project and Razor files wait for S4). After step 1 the only `*.cs` hits are gone.
5. **The first PRT-002 test (small):** an organisation on the Blank pack creates a contract, takes it through one transition, and the test asserts that none of the 17 JBS state codes and no JBS approver group appears in its history, and that the JBS organisation next to it still has the JBS lifecycle.

### 8.2 Out of scope for S1

Initial status for migrated contracts (S2), the reminder fall-back (S2), vocabulary (S4), pack loader (S7), roles (S5, S8), any change to the JBS pack data, any schema change.

### 8.3 Tests (on SQLite and SQL Server)

| Test | Proves |
|---|---|
| Organisation on `jbs-procurement` and organisation on `blank`, same tenant: each resolves its own lifecycle and policy | Selection by organisation |
| Null pack key with an installation default: uses the default. Without: refuses with the stated message | No silent fall-back |
| Blank organisation: create contract, transition, history contains only Blank states | PRT-002 (first step) |
| Blank organisation: the Variation and notification-policy paths do not throw | Neutral definitions are complete |
| Existing lifecycle, approval, notification, register and migration scenarios | JBS behaviour unchanged |
| ARC-010 stage A | No pack or vendor name in compiled code, any spelling |

### 8.4 Exit criteria

* All existing suites pass unchanged on SQLite and on CI's SQL Server job (every existing `SqlServer` test plus the new ones).
* The three new definitions validate through the normal publish validators (no special path).
* `docs/PORTABILITY_AUDIT.md` P-01 is marked fixed with the commit; PRT-002 is still reported **NOT VERIFIED** (a full second business is S9), with the first step passing.
* No data migration, no schema change, no change to a published release.

### 8.5 Rollback

Revert the commit. Nothing persisted depends on it: organisations that already have releases are untouched, and organisations that installed the Blank definitions by the new path would simply re-resolve on the next start (their releases remain valid configuration).


### 8.6 Outcome of S1

Implemented as defined, with no change to role identifiers (`ProcurementAdministrator` and the other eleven are untouched, and no Option B migration was done) and no other remediation action. Record: ADR-0024 (`decisions/ADR-0024-solution-pack-resolved-by-organisation.md`).

| Plan item | What was done |
|---|---|
| Resolve the pack from the organisation | `PackSelection.ResolveAsync` (`src/Modules/Ecp.Tenancy/PackSelection.cs`), used by `WorkflowDefinitionStore` and `NotificationPolicyStore` |
| Remove the JBS code default | `DefaultPackId` is now `string?` with no default in both option classes |
| Installation default as host configuration, unset | `Ecp:DefaultPackId` read in `src/Ecp.Host/Program.cs`, key present and empty in `src/Ecp.Host/appsettings.json`; refusal code `no_solution_pack` when neither names a pack |
| Neutral lifecycle, Variation workflow and notification policy | `templates/packs/blank/workflows/contract-lifecycle.json`, `templates/packs/blank/workflows/variation.json`, `templates/packs/blank/notifications/notification-policy.json`, embedded like the JBS ones (S7 will remove the embedding) |
| Test infrastructure names the pack it relies on | `tests/Ecp.Tests.Common/TestSupport.cs`: `TestPacks.Reference` as the installation default of the test hosts |
| Guard stage A | `tests/Ecp.Architecture.Tests/ArchitectureTests.cs`: case-insensitive, inside identifiers, plus a theory proving each previously invisible spelling is now seen |
| First PRT-002 proof | `tests/Ecp.Integration.Tests/PackResolutionScenarios.cs` (four scenarios on SQLite and SQL Server) and `tests/Ecp.UnitTests/BlankPackTests.cs` (the neutral definitions validate, name no JBS state or business) |

Two deviations from the plan text, both found in the code while implementing:

* **The guard needed no word boundaries.** The plan said "case-insensitive"; a class named after a vendor (`DocuSignClient`) would still have passed a whole-word match, so the pattern is a substring match. Nothing in `src/` is affected.
* **The neutral approver group is required, not optional.** The workflow validator refuses a definition with no `approverGroupKey`, so the Blank workflows name the neutral group `Approvers`.

Decisions taken inside the slice (the plan left them open): the seeded `org-a2` stays on the Blank pack and now has the neutral lifecycle (the cheapest permanent non-JBS organisation); the Blank lifecycle has no Legal review, signature or approval step, so a Blank organisation's contracts do not get a Registered Date until a pack adds one.

### 8.7 Verification of S1

| Check | Result |
|---|---|
| A Blank organisation resolves by organisation | Passed: `PackResolutionScenarios` (SQLite local; SQL Server in CI runs 31 and 32) |
| The JBS pack resolves only when the organisation selects it or the host sets `Ecp:DefaultPackId` | Passed: `Installation_default` and `No_installation_default` scenarios; the shipped `appsettings.json` leaves the key empty |
| No compiled platform code silently defaults to `jbs-procurement` | Passed: the property is `string?` with no default in both option classes; a scan of `src` finds `jbs-procurement` in no `.cs` or `.cshtml` file; only two project-file embedding lines remain (P-03, open) |
| Blank-pack contracts use only Blank states and policies | Passed: the contract's state path and history contain no JBS state; the notification policy has no Legal-review rule |
| JBS-pack behaviour unchanged | Passed: no JBS pack file changed; the existing lifecycle, notification, approval and migration scenarios pass unchanged on both providers (test hosts name `TestPacks.Reference` as installation default, which is how the shipped JBS configuration is selected) |
| Existing published releases and migrated contracts remain compatible | Passed by construction: resolution applies only when an organisation has published nothing; a published release is read as before and contracts stay pinned to their workflow release. No EF migration, schema or data change (`git diff` of the migrations since the audit is empty) |
| ARC-010 | Passed (20 architecture tests, including the spelling theory) |
| Release build with warnings as errors | Passed locally and on CI (runs 31 and 32) |
| Repository, supply-chain and Docker checks | Repository, tooling and supply-chain checks passed locally and on CI; the container image build and smoke passed on CI; Docker smoke was not run in the workspace (no daemon) |
| All existing and new portability tests on SQL Server | Passed: 189 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED (run 32, commit a0293ee) |

Remaining high-severity findings after S1 (superseded by section 8.9): **P-03** (packs embedded in assemblies by project files; no loader) and **P-02a** (the reminder fall-back for an unowned category names a JBS role). **First incomplete remediation task: S2.** PRT-002 as a whole remains NOT VERIFIED.

### 8.8 Slice S2: R2 and R3 (P-08 and P-02a)

Implemented as defined (ADR-0025); no role identifier changed, no other remediation action touched.

| Plan item | What was done |
|---|---|
| R3: reminder fall-back recipient as a policy rule | `reminders.unownedFallback` in the notification policy (a list from the closed recipient vocabulary, plus the new rule `OrganisationAdministrators`); `ReminderScanner` resolves the policy's list through the same `RecipientResolver` and access filter as before; the validator refuses an unknown or repeated rule; the JBS policy states `["OrganisationAdministrators"]`, the Blank policy `[]`; an absent field reads as the old behaviour so every published release is unchanged |
| R2: initial status for migrated contracts from the organisation's workflow | `IContractMigrationPort.InitialStatusAsync(organisationId)` replaces `InitialStatusCode`; it uses `IContractCreationPolicy` (the lifecycle definition in force), is used by the dry run's context and by the commit, and `Contracts:InitialStatusCode` is removed from the shipped `appsettings.json`; `MIG-E026` blocks a dry run for an organisation with no lifecycle |

Deviation to note: the plan said an absent field must read as today's behaviour for existing releases, which also applies to a Blank-pack release published between S1 and S2 (no deployment had one). A fresh Blank installation states `[]`; republishing corrects any older Blank release (recorded as a reversible assumption in ADR-0025).

### 8.9 Verification of S2

| Check | Result |
|---|---|
| A Blank organisation never resolves JBS reminder recipients | Passed: `ReminderFallbackScenarios` (SQLite locally; SQL Server in CI). An administrator holding the old fall-back role in the Blank organisation is never told; the category owner is, once, when ownership is restored; the test fails against the old fall-back |
| JBS reminder behaviour unchanged | Passed: the reference policy states `OrganisationAdministrators`; with the category unowned the administrator receives the reminder as before; the existing reminder scenarios pass unchanged |
| Policy compatibility | Passed: unit `ReminderFallbackPolicyTests` (absent reads as legacy, empty means nobody, unknown and repeated rules refused, both shipped packs state the field) |
| Migrated contracts take the organisation's initial status | Passed: `MigrationInitialStatusScenarios` (JBS organisation `Draft`, Blank organisation `Open`, no lifecycle refused); the existing migration scenarios pass unchanged |
| ARC-010 and the rest of the architecture tests | Passed (20) |
| Remaining high-severity findings | **P-03** only (packs embedded by project files, no loader). P-02a is fixed; P-02b (compiled role catalogue, including the role that `OrganisationAdministrators` resolves to) remains Medium |
| First incomplete remediation task | **S3** (R6: neutral signature-request vocabulary, with an EF migration), then S4 to S10 |

### 8.10 Slice S7: external solution packs (ADR-0026)

Delivered, as one slice, on the owner's instruction (the Enterprise Platform path; JBS is one optional pack):

* **S7a external pack loading.** `Ecp.Packs` (tier 0, no dependencies): the `.ecpkg` format (closed ZIP, canonical manifest, SHA-256 per document), safe reader and deterministic writer, content scan, compatibility check, builder and `ecp-pack` tool (`src/Ecp.PackTool`). `IPackLoader` in `Ecp.Configuration` reads shipped files and tenant-installed packs alike. The eight embedded entries, the resource-name conventions and the reader classes are removed; `IOrganisationPackContent` replaces them in the lifecycle and notification stores.
* **S7b verification and signing.** ES256 (ECDSA P-256), `IPackageSigner`, `IPackageSignatureVerifier`, `IPackTrustStore`/`IPackKeyResolver`, Production trust policy, unsigned synthetic packs only in Demo and Test behind `Ecp:Packs:AllowUnsignedSyntheticPacks`. No private key in the repository.
* **S7c tenant storage and installation.** Tables `InstalledPack`, `PackAssignment`, `TrustedPublisherKey` (migration `AddSolutionPacks`, row-level security), a storage-provider abstraction (file system implementation, outside any public path), quarantine, idempotent re-upload, conflict on different bytes, audit of upload, validation, install, retire, assign and trust changes.
* **Pack Manager** (form-based, accessible), eight `packs.*` permissions with a documented role allocation, setup and onboarding integration, readiness check, report-only organisation diagnostics, CI checks.

**Exit criteria.** "A pack installs from the store with no rebuild": met (third-pack scenario). "The embeds are gone": met (architecture tests). **Rollback**: the migration is additive; restore the pre-upgrade database backup and the matching application version. Pack files are inert without the loader.

**Deferred (not started, by design):** S7d upgrade workflow across versions and per-item merge, S7e pack and tenant export, S7f marketplace and catalogue, billing, graphical designer, AI-generated packs; the other packable kinds in shipped packs; the Razor and JSON stages (B, C) of the ARC-010 guard (P-09) which belong to later slices.

**First incomplete remediation task after S7:** S3 (R6, neutral signature-request vocabulary with an EF migration), then S4, S5, S6, S8 to S10. **Remaining high-severity portability findings:** none from the original audit's High list; P-02b (compiled role catalogue) remains Medium.

### 8.11 Slice S3: provider-neutral signature boundary (ADR-0027)

Delivered on the owner's instruction (R6, finding P-10):

* **Vocabulary.** Signature Request, Signature Provider, Provider Request ID, Signature Request Status (Sent for signature, Delivered, Completed, Declined, Voided, Expired, Failed), Executed Document, Provider Event, Provider Callback, External Execution. Envelope, Envelope ID, DocuSign Envelope, DocuSign Status, Recipient Tabs and DocuSign Connect are not platform concepts; they live only in `requirements/07-integrations/DOCUSIGN_ADAPTER_NOTES.md`.
* **Model.** `ExecutionRecord` becomes `SignatureRequest` (provider key, optional Provider Request ID, platform status, provider-native status kept apart, requested by/when, last provider event, completed, executed-document reference, correlation id, idempotency key, failure code and message). New `SignatureProviderEvent` is the idempotent inbox record.
* **Interfaces.** `ISignatureProvider`, `ISignatureRequestService`, `ISignatureStatusMapper`, `ISignatureCallbackVerifier` (with the `HmacCallbackVerifier` base), `ISignatureEventInbox`, `IExecutedDocumentRetriever`, `ISignatureProviderFactory`. `IESignatureProvider` and its envelope types are gone. A test adapter with its own words, headers, secret and payload runs end to end with no change to the lifecycle.
* **Behaviour.** Provider words are mapped explicitly; unknown words and unknown requests fail safely and stay visible; each provider event id is processed once; a provider cannot move or make Effective a contract; completed execution stores the executed document through the normal document rules; every change is audited; mock provider, verifier and simulated events are refused in Production; manual upload still works.
* **Migration `SignatureRequestVocabulary`.** Renames the table, four columns, indexes and primary key in place, re-creates the filtered index and the row-level-security policy, adds six empty columns and the inbox table. No value is rewritten. Contract state, approvals, executed-document references and audit records are untouched. Frozen register-authorisation snapshots written under the old key still read.
* **Guard.** An architecture test scans all of `src/` for the vendor words (only the one compatibility name is allowed; applied migrations are history).

**Exit criteria.** "No vendor word in the platform": met (architecture test). "No data loss": proved by a SQL Server migration test that runs in CI; **NOT VERIFIED in this workspace**. **Rollback**: restore the pre-upgrade backup; a development `Down` exists.

**Not done:** live adapter, callback endpoint, provider configuration page, reporting, operations, role catalogue, feature flags, another pack slice. **First incomplete remediation task after S3:** S4 (vocabulary and terminology read side, guard stage B), then S5, S6, S8 to S10.

### 8.12 Slice S4: terminology and guard stage B (ADR-0028)

Delivered on the owner's instruction (R5, R4 stage B; findings P-05, P-06, P-07, P-09 stage B):

* **Model.** Stable key versus label; resolution order organisation, pack, neutral, key as diagnostic; 42 catalogue keys (32 required); neutral wording names no business. Role codes, permission codes, state codes, audit action codes, configuration-kind keys, routes, form fields, database objects and pack ids are not renamed.
* **Configuration.** The `Terminology` kind uses the existing lifecycle (draft, validate, preview, publish, history, rollback, audit, second person, step-up); plain text only; limits; duplicate prevention; tenant and organisation isolation; five `terminology.*` permissions (decision D7b).
* **Resolver and cache.** One `ITerminologyResolver`; cache key tenant, organisation, locale and published version; invalidated on publish and rollback; falls back to persistence if the cache fails; fails safe to neutral wording; no label values in diagnostics.
* **Packs.** Optional signed pack terminology document (content type `Terminology`); installed as a draft only; never overwrites published terminology; preview on review and assign pages; older packs stay compatible. Blank 1.2.0 neutral; JBS 2.2.0 procurement wording; a synthetic third pack with different wording.
* **Read side.** Pages through `TermLocalizer` and `[[token]]` strings; domain messages through `DomainException.RawMessage`; notification templates validated and expanded per record organisation; dashboards, register labels, built-in views, migration text and reports, export headings and worksheet name.
* **Administration.** Form-based Overview, Edit, Preview, History and Audit; setup and onboarding show pack wording and prepare a draft.
* **Guard stage B.** `VocabularyGuardTests` over every shipped text file, with a single-file allow-list for S10 locale defaults, the demo seeder and the Medius demonstration script.

**Exit criteria.** "A renamed vocabulary shows no JBS term": met (Blank organisation pages, onboarding and exports; a third pack's pages). "The guard passes on all source extensions": met. **Compatibility.** No migration: terminology is a new configuration kind in the existing release tables. Contracts, documents, workflow state, approvals, audit entries and action codes, saved views, release hashes and user access are unchanged; stored migration text written before S4 is plain and is left as written. **Rollback.** Unpublish by rolling the terminology back or deleting the organisation's draft; revert the application version; no data to restore.

**Not done:** role-catalogue replacement (S5/S8), feature flags (S6), another pack-loader slice, regional defaults (S10), live e-signature, reporting, operations, API work, AI, marketplace, visual designers. **First incomplete remediation task after S4:** S5 (the role-catalogue decision), then S6, S8 to S10.

### 8.13 Slice S5: stable role and permission identifiers (ADR-0029)

Delivered on the owner's decision (Option A+): design in `docs/ROLE_CATALOGUE_ADR.md`, decision in `decisions/ADR-0029-stable-role-and-permission-identifiers.md`.

* **Decisions.** Role codes, permission codes and approver-group keys are permanent identifiers; PRT-013 is satisfied by terminology and display-layer abstraction (requirement text amended; decision D7c); S8 retired and recorded as superseded; no identifier renamed; no alias migration.
* **Follow-ups built (no schema change).** Frozen-identifier tests (`FrozenIdentifierTests`, 10); approver-group display names (optional `groups` section of the terminology document, resolved organisation over pack over key, shown beside the key on administrator pages, edited on the Terminology edit form; Blank 1.3.0 and JBS 2.3.0 name their groups); role visibility support for S6 (optional `roles` section, hides a role from the role picker only, never the system, tenant or organisation administrator roles, never revokes access).
* **Exit criteria.** "ADR accepted, D1 reconciled with PRT-013": met. "A business can show its own role and group names, and hide a role it does not use, with no rename": met (tests through the real host on SQLite; SQL Server in CI).

**Not done:** a capability or feature-flag mechanism (S6), a data-driven role catalogue (Future), regional defaults (S10), another pack slice. **First incomplete remediation task after S5:** S6 (feature flags that gate capabilities), then S9 and S10.

### 8.14 Slice S6: configurable capabilities (ADR-0030)

Delivered on the owner's S6 instruction. Design and matrix in `docs/CAPABILITIES.md`; decision in `decisions/ADR-0030-configurable-capabilities.md`.

* **Catalogue (Implemented).** Five capabilities with permanent keys bound to the closed workflow vocabularies, register fields and built-in list and card keys (`src/Ecp.SharedKernel/Capabilities/CapabilityCatalogue.cs`).
* **Feature-flag configuration kind (Implemented).** The `Capabilities` kind with draft, validate, preview, publish, history, rollback and audit; five permissions (`capabilities.read`, `.draft`, `.publish`, `.rollback`, `.audit.read`; decision D7d).
* **Pack defaults (Implemented).** A packable `Capabilities` content kind; Blank 1.4.0 turns Legal Review, Renewals and Signature Requests off, the reference pack 2.4.0 leaves all on. Assigning a pack applies its defaults without a draft.
* **Organisation overrides and resolver (Implemented).** Organisation over pack over the platform default of all on; cached by tenant, organisation and version; remembered per request; fails open.
* **Gate (Implemented).** One chokepoint in the lifecycle engine refuses new starts and hides the step; work in flight can finish.
* **Capability-aware navigation, pages, register, lists, dashboards (Implemented).**
* **Onboarding and setup (Implemented).** The pack step shows each pack's optional steps; a mapping to the Legal role while Legal Review is off gets a warning.
* **Readiness and compatibility (Implemented).** Errors for a capability on without one it needs, a published workflow using one that is off, turning one off with work in flight and an incoherent pack assignment; the readiness check `capabilities` covers the shipped packs.
* **Guard (Implemented).** `CapabilityGuardTests` (keys only through the constants, the gate present and early, navigation consults the resolver, only the resolver reads the kind, packs state every capability, documents are data).
* **Exit criteria.** "A pack without Legal review installs and runs; JBS unchanged": met (Blank organisation proof; the reference pack has all on and the existing suites pass unchanged). "A business can operate with all five off with no code change": met (`A_business_can_operate_with_all_five_capabilities_switched_off_with_no_code_change`, SQLite locally and SQL Server in CI).

**Found while testing, fixed:** the first version resolved the configuration service from the resolver's constructor and made the kind graph circular (the test host crashed); the resolver now resolves its services on first use.

**Not done, by design:** capabilities beyond the five (category ownership, critical notice, external reporting; Future), hiding pages by direct address (work in flight must stay reachable), reporting, DocuSign, operations, API work, AI, marketplace, role renaming. **First incomplete remediation task after S6:** S9 (second pack end to end), then S10 (regional defaults).

## 9. Open decisions for the owner

| Decision | Needed by | Recommendation |
|---|---|---|
| Role catalogue: Option A (names from terminology, codes stay) or Option B (rename with alias) | S5 | **Decided 2026-10-17: Option A+ (ADR-0029).** B and a data-driven catalogue are not pursued |
| Is Legal review a platform capability (flag-gated) or a JBS pack feature (removable)? | S6 | **Decided and built 2026-10-17 (ADR-0030): a flag-gated platform capability.** The data model and pages stay; the pack proposes and the organisation decides whether to use them |
| `org-a2` in the Demonstration seed: keep on Blank (and accept the neutral lifecycle) or move to the JBS pack | S1 | Keep on Blank; it is the cheapest permanent test of a non-JBS organisation |
| Move ABN and SAP number into fields? | S10 | No; make the validation a rule |
| Who owns the second pack (PKM-04) and which business should it model? | S9 | A construction or facilities example already named in the plan; synthetic, like JBS |

## 10. What this plan does not claim

* It does not claim any of this is done. Every finding in the audit is open.
* It does not claim PRT-002 passes after S1; only a full second business (S9) does.
* The difficulty sizes are relative, not estimates in days.
* The detection query in 7.1 is described, not written or run.
