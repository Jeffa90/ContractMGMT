# Portability Audit

Status: **audit only.** No code, configuration or pack was changed to produce this document. It answers one question: *does the platform layer contain JBS-specific business logic, and where does JBS (or any named business or vendor) appear at all?*

Method, scope and limits are in section 1; the verdict and the violations are in sections 2 and 3; every occurrence is in sections 5 to 9.

## 0. Verdict

**The platform layer does not yet meet PRT-001 and PRT-013.** The scan found no vendor name (Medius, Qlik, DocuSign) and no `JBS` string in compiled platform code, and the existing architecture rule ARC-010 passes. But ARC-010 only looks for a narrow set of spellings in `*.cs`. Reading the hits that rule cannot see shows five places where JBS business behaviour or vocabulary is compiled into the platform (section 3, findings P-01 to P-05), and the one that matters most is observable at run time:

* **P-01 (High): any organisation that has published no workflow gets the JBS lifecycle.** The fallback installs the pack named by a code default `"jbs-procurement"`, not the organisation's own pack. An organisation on the `blank` pack therefore receives the 17-state JBS contract lifecycle, the JBS Variation workflow, the approver group `GroupHeadProcurement` and the JBS notification policy the first time it needs one. The host passes only the installation mode to the lifecycle and notifications modules (`src/Ecp.Host/Program.cs:64`-`65`), so the default can only be changed by editing code. This is established by reading the code path (`WorkflowDefinitionStore.CurrentAsync`, `NotificationPolicyStore.CurrentAsync`) and by finding no override anywhere; no test exercises a `blank`-pack organisation, so it has not been demonstrated at run time.
* **P-02 (High / Medium): the role catalogue is compiled in and one role is named for the procurement function**, and a reminder fall-back routes to that role by name.
* **P-03 (High): a second business pack cannot be installed without a source change**, because packs are embedded into module assemblies by `.csproj` lines and looked up by resource name. This is the opposite of PRT-002.

Everything else is Low or Medium (labels, examples, comments, locale defaults). Nothing is a security issue.

**Not verifiable today:** PRT-002 (a second, non-JBS business walked through a lifecycle). Only the `blank` pack exists besides JBS, and `blank` has no workflow of its own (P-01), so the check cannot pass yet. It is reported **NOT VERIFIED**, not passed.

## 0.1 Status after remediation slices S1 and S2

Slices S1 and S2 of `docs/PORTABILITY_REMEDIATION_PLAN.md` were implemented after this audit (ADR-0024, ADR-0025). The registers below describe the repository **as audited** and are not regenerated; this table records what changed.

| Finding | Status |
|---|---|
| **P-01** JBS pack selected by a code default | **Fixed.** The pack comes from the organisation's own `SolutionPackKey`; an organisation with none uses the installation default `Ecp:DefaultPackId`, which ships unset; with neither the request is refused (`no_solution_pack`). The two `DefaultPackId = "jbs-procurement"` lines and their uses are gone from `src/` |
| **P-09** ARC-010 too narrow | **Partly fixed (stage A).** Case-insensitive, inside identifiers, all `src` C# files, names `JBS`, `Medius`, `Qlik`, `DocuSign`. Razor, project and JSON files, `Procurement`, `Group Head` and role names remain for stages B and C |
| **P-02a** reminder fall-back routes to a JBS-named role | **Fixed in S2 (ADR-0025).** Who is told when nobody owns the category is the policy's `reminders.unownedFallback` (closed recipient vocabulary, one new rule `OrganisationAdministrators`). The JBS policy states it (today's recipient); the Blank policy states none; a policy published before the field existed reads as it behaved. `Reminders.cs` no longer names a role. The role identifier that `OrganisationAdministrators` resolves to is still compiled in `RecipientResolver`: that is P-02b (Medium), slices S5 and S8 |
| **P-08** initial status of migrated contracts is a host setting naming a JBS state | **Fixed in S2 (ADR-0025).** The initial status comes from the organisation's lifecycle definition through the same source the contract pages use; `Contracts:InitialStatusCode` is gone from `appsettings.json`; the dry run reports `MIG-E026` for an organisation with no lifecycle |
| P-02b, P-04, P-05, P-06, P-07, R-01 to R-06 | **Open, unchanged** (other slices). P-03 was closed by S7 (section 0.2) and P-10 by S3 (section 0.3) |
| **PRT-002** | Still **NOT VERIFIED** as a whole. Its first step passes: a Blank-pack organisation creates a contract, activates it and opens a Variation, and none of the 17 JBS states appears (`tests/Ecp.Integration.Tests/PackResolutionScenarios.cs`, SQLite and SQL Server) |
| Verification | Locally: 730 Passed, 0 Failed, 0 Skipped, 189 NOT VERIFIED (the SQL Server wrappers). CI (run 31 commit f3cddb4 and run 32 commit a0293ee, SQL Server 2022): 189 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED; Release build with warnings as errors, repository, supply-chain and container-image jobs succeeded. A fresh scan after S1: no occurrence of `jbs`, `medius`, `qlik` or `docusign` in any spelling in `src` C# files; no `jbs-procurement` in any `src` `.cs` or `.cshtml` file; the only `src` files that name `jbs-procurement` are the embedding lines of two project files (P-03, open). No EF migration changed since this audit |
| New | The Blank pack now ships a neutral lifecycle, Variation workflow and notification policy as data (`templates/packs/blank/`), still embedded by project files (P-03 is open) |

## 0.2 Status after slice S7 (external solution packs; ADR-0026)

**P-03 is closed for pack content, with the evidence below; what remains is stated precisely.** The registers in sections 3 to 9 still describe the repository as audited and are not regenerated.

| Question | Evidence (2026-10-17) |
|---|---|
| Is any pack an embedded resource or found by an assembly resource name? | No. The eight `EmbeddedResource` entries in `Ecp.Configuration`, `Ecp.Lifecycle` and `Ecp.Notifications` are gone, together with `WorkflowKinds.ResourceName` and the reader classes. Architecture tests `PackRuleTests` (`No_project_embeds_a_solution_pack_document`, `No_compiled_assembly_carries_a_pack_document_as_a_resource`) and the CI step `tools/check-packs.sh` fail the build if one returns. |
| Where do the Blank and JBS packs come from now? | `templates/packs/<key>/pack-source.json` (sources) built by `Ecp.PackTool` into `<output>/packs/*.ecpkg` (read-only external assets, in the container under `/app/packs`). They are ordinary packages loaded by the same loader as any tenant-installed pack. |
| Can a second business be installed with no source change? | Yes. `A_third_pack_the_repository_has_never_seen_runs_a_contract_with_no_source_change` builds a pack at test time (a lifecycle whose initial state, `Intake`, exists nowhere in the code or the shipped packs), uploads and installs it through the Pack Manager service as a tenant administrator, assigns it, publishes the drafts, creates a contract that starts in `Intake` and activates it, on SQLite (and SQL Server in CI). No rebuild, no redeploy. |
| Does the loader name any business or pack? | No. `The_pack_loader_names_no_pack_and_makes_no_decision_by_pack_key` and ARC-010 (stage A, all `src` C#). |
| Do Blank and JBS organisations behave as before? | Yes. `Organisation.SolutionPackKey` is unchanged; `PackResolutionScenarios`, the lifecycle, notification and migration scenarios pass unchanged against the external packs. Existing published releases and pinned contracts are not touched. |
| Production | Refuses unsigned, wrongly signed, unknown-key, revoked-key, expired-key and tampered packs (`PackSigningTests`, `Production_refuses_unsigned_and_untrusted_packs_until_the_publisher_key_is_trusted`). The shipped packs are built **unsigned** because no private key is committed, so a Production operator must sign them or install signed packs of their own and trust the key; readiness says so (Degraded). This is the intended behaviour and it is operational, not a defect. |

**What P-03's closure does not claim.** Pack *content kinds* that exist today (lifecycle, Variation workflow, notification policy, solution-pack catalogue entry) are external; the other packable kinds (approval matrix, classification policy, custom fields, list views, branding) are accepted by the format but no shipped pack carries them yet, and the *vocabulary* findings (P-02b role catalogue, P-04 to P-07, R-01 to R-06) are other slices and remain **Open**. PRT-002 (a second business end to end with its own fields, rules and approvals) is still **NOT VERIFIED** as a whole (S9); the mechanism it needs is now in place. A report-only diagnostic lists organisations whose pack cannot be resolved or that earlier received another pack's starting definitions through the old fallback; nothing is rewritten (`docs/PACK_MANAGER.md` section 3).

## 0.3 Status after slice S3 (provider-neutral signature boundary; ADR-0027)

**P-10 is fixed.** The registers in sections 3 to 10 still describe the repository as first audited and are not regenerated. This section re-searches every tracked and untracked file (source, tests, migrations, pages, routes, configuration, resource keys, documentation) for signature-provider-shaped vocabulary: `envelope`, `envelope id`, `DocuSign`, `Connect`, `recipient`, `signing ceremony`, `signature tabs`, and classifies every remaining occurrence.

| Class | Where it remains (after S3) | Why it is acceptable |
|---|---|---|
| **Generic platform violation** | **None.** Before S3: `IESignatureProvider.CreateEnvelopeAsync`, `EnvelopeRequest`, `EnvelopeResult`, `ExecutionRecord.EnvelopeId`, the `EnvelopeId` column and its filtered index, `RegisterSnapshot.EnvelopeId`, the "Envelope" label in `_Steps.cshtml`, "envelope" in `src/Ecp.Host/Pages/Approvals/Details.cshtml`, audit JSON key `envelopeId`, and comments in `WorkflowDefinition.cs` | Replaced by Signature Request, Provider Request ID and the neutral interfaces. A test (`The_signature_boundary_uses_no_signature_vendor_vocabulary_in_any_source_file`) now scans every C#, Razor, project, JSON, resource, CSS and script file under `src/` |
| **Provider-adapter vocabulary** | `requirements/07-integrations/DOCUSIGN_ADAPTER_NOTES.md` (envelope, envelope id, Connect, recipients, tabs, certificate of completion, status words) | The one place vendor words may live, for the future adapter. No adapter code exists. The file is labelled Future |
| **Compatibility reference** | `src/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs`: one read-only property named `envelopeId` so frozen register-authorisation snapshots written before S3 still read; `tests/Ecp.UnitTests/SignatureVocabularyTests.cs` proves it | Snapshots are evidence and are never rewritten. New snapshots do not write the old key. The architecture test allows exactly this line |
| **Historical migration reference** | `src/Modules/Ecp.Lifecycle/Persistence/Migrations/20261016060002_Initial.cs`, its `.Designer.cs`, `20261016074947_AddApprovalPlans.Designer.cs`, and the S3 migration `20261017040140_SignatureRequestVocabulary.cs` (which renames from the old names) | Migrations already applied must not be edited, and the renaming migration has to name the old columns. The current model snapshot contains none of the old names |
| **Decision and pack reference** | `requirements/01-authoritative/CONFIRMED_DECISIONS.md` (D2), `MASTER_BUILD_INSTRUCTIONS.md`, `GLOSSARY.md`, `WORKFLOW_SPEC.md`, `CONFIGURATION_STUDIO_SPEC.md`, `PORTABILITY_ACCEPTANCE_CRITERIA.md`, ADR-0022, ADR-0024, `docs/APPROVAL_MATRIX.md`, `docs/IMPLEMENTATION_PLAN.md`, `docs/REPOSITORY_REVIEW.md`, `TASKS.md` | They record that DocuSign is the **JBS pack's chosen provider** (a business decision), or discuss the rule that forbids the name. They define no platform entity, column, interface, route, key or label |
| **Documentation example** | This audit, `docs/PORTABILITY_REMEDIATION_PLAN.md`, `docs/PROJECT_STATUS.md`, `docs/DOCUMENT_INDEX.md`, `PACKAGE_MANIFEST.md`, `.claude/skills/portability-review/SKILL.md`, `ESIGNATURE_PROVIDER_SPEC.md` (which says the words are *not* platform terms) | Describe the finding or the rule |
| **Synthetic test fixture** | `tests/Ecp.Integration.Tests/SignatureVocabularyMigrationTests.cs` (inserts a row under the old column name before the migration), `tests/Ecp.Integration.Tests/SignatureRequestScenarios.cs` (asserts the word does not appear in audit), `tests/Ecp.Architecture.Tests/ArchitectureTests.cs` and `tests/Ecp.UnitTests/BlankPackTests.cs` (the guard's own patterns) | Tests need the words to prove their absence |
| **Not a signature concept** | `src/Modules/Ecp.DataMigration/MigrationReconciler.cs` had a private record called `Envelope` (a JSON wrapper); `Recipient` in Notifications (a person who receives a notification); "Connect" in OpenID Connect | Unrelated meanings. The wrapper was renamed `ReconciliationPayload` so the guard needs no exception. Notification recipients and OpenID Connect are correct and stay |

Also checked and clean: pack sources under `templates/packs` (no occurrence), `appsettings.json` and every configuration key (none; the only new key is the Demo/Test `MockSignatureCallbackKey`), routes (no signature route exists yet; the planned one is `/api/v1/integrations/signature/{providerKey}/callback`), resource keys (the Razor labels are plain strings: "Provider request ID", "Sent for signature", "Provider status"), the DocuSign-named requirements file (renamed `ESIGNATURE_PROVIDER_SPEC.md`; vendor content moved to the adapter notes).

What S3 does **not** claim: no live adapter exists, so nothing provider-specific has been verified against a real service; the callback endpoint is not built; the S3 migration has not been run against SQL Server in this workspace (no SQL Server is available here); see `docs/TESTING.md` section 8.10 for what is proved where.

## 0.4 Status after slice S4 (terminology and guard stage B; ADR-0028)

**P-05, P-06 and P-07 are fixed; P-09 stage B is done.** The registers in sections 3 to 10 still describe the repository as first audited and are not regenerated. This section re-searches `src/`, `deploy/`, `.github/`, `tools/` and the root props files (C#, Razor, resources, JSON, project and props files, appsettings, deployment files, workflows, scripts, CSS and JavaScript), case-insensitively, for the business and vendor names, the fixed business labels and the fixed regional defaults listed below.

New classes used in this section: **Stable machine identifier** (a role, permission, state, audit-action, configuration-kind or pack code, a route, a form field, a database object: not renamed, not a label), **Terminology key** (a catalogue key whose label is data), **Neutral label** (the platform's own wording, naming no business), **Pack-provided label** (wording in a pack's terminology document), **Regional default** (a locale, time zone or currency default: S10, not terminology).

| Class | Where it remains (after S4) | Why it is acceptable |
|---|---|---|
| **Generic platform violation** | **None** for business labels. Before S4 the pages, messages, notifications, dashboards, register sets, built-in views, exports and role names spoke procurement in every organisation (P-05, P-06, P-07, R-05, R-06) | Replaced by terminology keys and tokens (`docs/TERMINOLOGY.md`). Guard stage B fails the build on a fixed label or a capitalised business word in page text |
| **Stable machine identifier** | `RoleCodes.ProcurementAdministrator`, `RoleCodes.CategoryOwner`, `RoleCodes.Legal`, `Approver`, `BackupApprover`; permission codes such as `suppliers.manage`, `categories.manage`; workflow state codes; routes `/Suppliers`, `/Admin/Categories`; form fields `supplierId`, `categoryId`; database tables `Supplier`, `Category`; the legacy contract CSV header | Renaming them is a migration with risk and no benefit (owner decision, ADR-0028 D2). Labels shown to people come from terminology; administrator pages show the code beside the label. The guard matches words, not these identifiers |
| **Pack-provided label** | `templates/packs/jbs-procurement/terminology/terminology.json` (Supplier, Category, Subcategory, Legal Review, Procurement Administrator, Category Owner, Group Head of Procurement, Procurement Contract Register) and the tokenised notification policy | The only place the procurement wording lives; a pack, not platform code. The Blank pack is neutral |
| **Neutral label** | `src/Ecp.SharedKernel/Terminology/TermCatalogue.cs` (Counterparty, Contract Administrator, Contract Owner, Reviewer, Registration Approver, Backup Approver, Contract Type, Contract Portfolio, Contract Register, External Execution, Review, Approval, Organisation, Business Unit, Site) | Names no business |
| **Regional default (S10)** | `appsettings.json` (`en-AU`, Brisbane, `AUD`), `LocaleFormatter` defaults, `OrgLocale`/`MigrationPageModel` fallbacks, `CustomFields` fallback culture, `GlobalisationProbe` | Locale, time zone and currency are not terminology. They are named, per file, in the guard's allow-list with reason "S10" and are the only fixed regional defaults left |
| **Demo and tool data** | `src/Ecp.Host/DemoSeeder.cs` (role names in the sample CSV), `tools/migration-demo.py` (the Medius migration demonstration) | Sample-data loading and a demonstration of a migration compatibility profile; allowed by name |
| **Documentation example, historical finding** | This audit, the remediation plan, ADRs, `docs/TERMINOLOGY.md` | Describe the findings; not scanned |
| **Provider-adapter vocabulary** | `requirements/07-integrations/DOCUSIGN_ADAPTER_NOTES.md` | Unchanged from S3 |

**Fixed in S4:** P-05 (dashboards, register sets, legal-review labels, built-in views), P-06 (the approvals page text naming the Group Head of Procurement), P-07 (onboarding and setup examples: now neutral), the label parts of R-05 and R-06, and P-09 stage B. **Still open:** P-02b (compiled role catalogue, S5/S8), P-04 (pack feature flags, S6), the regional defaults (S10), R-01 to R-04.

**The guard.** `VocabularyGuardTests` scans every shipped text file case-insensitively for `JBS`, `JBSAUS`, `Medius`, `Qlik`, `DocuSign`, `Group Procurement`, `Group Head of Procurement`, `Procurement Administrator`, `Procurement Contract Register`, `Category Owner`, `en-AU`, an Australian time-zone identifier, `Brisbane`, `AUD` (as a currency code), and for capitalised business words in page text. Its allow-list names single files with a reason, rejects stale entries, is capped, and is proved narrow by tests. Scan result at the end of S4: no hit outside the allow-list; every allowance is exercised.

## 0.5 Status after slice S5 (stable role and permission identifiers; ADR-0029)

**P-02b is closed by policy, not by rename.** The registers in sections 3 to 10 still describe the repository as first audited and are not regenerated. The owner decided (Option A+) that role codes, permission codes and approver-group keys are permanent identifiers (`docs/ROLE_CATALOGUE_ADR.md`).

| Class | Where it remains | Why it is acceptable |
|---|---|---|
| **Stable machine identifier** | `RoleCodes.ProcurementAdministrator`, `CategoryOwner`, `Legal`, `Approver`, `BackupApprover` and the other seven; permission codes such as `categories.manage`, `suppliers.manage`; the approver-group key `GroupHeadProcurement` in the JBS pack | Frozen by `FrozenIdentifierTests`; persisted in assignments, mappings, audit and immutable configuration; the label people read comes from terminology; the guard matches words, not these identifiers |
| **Presentation of identifiers** | Approver-group display names and role visibility in the terminology document | Presentation only; no authority change |

**Closed:** P-02b. **Superseded:** the plan's slice S8 (rename with aliases). **Still open:** P-04 (pack feature flags, S6), regional defaults (S10), R-01 to R-04.

## 1. Scope, method and classification

### 1.1 What was scanned

Every text file in the repository outside generated and local directories (`bin/`, `obj/`, `TestResults/`, `artifacts/`, `.git/`): C# and Razor sources, project files, JSON, CSV, Markdown, shell and Python tools, workflows, deployment files. Binary files, `packages.lock.json` and the generated EF migrations designer files were not read as sources (migrations were scanned and contain no hits).

### 1.2 What was searched for

Named terms (case-insensitive): `JBS` (including `JBSAUS` and the pack id `jbs-procurement`), `Procurement`, `Group Head (of) Procurement` (including the approver group key `GroupHeadProcurement`), `Group Procurement`, `Medius`, `Qlik`, `DocuSign`.

Vocabulary of the reference configuration (so that a JBS name written without the word JBS is not missed):

* **JBS lifecycle names:** the 17 states of the reference contract lifecycle (`Draft`, `AwaitingLegal`, `InLegalReview`, `LegalReworkRequired`, `LegalCleared`, `InitialCreation`, `OutForSignature`, `AwaitingApproval`, `Effective`, `VariationInProgress`, `RenewalInProgress`, `Suspended`, `Terminated`, `Ended`, `FlaggedForDisposition`, `DisposedArchived`, `Withdrawn`) as code, as quoted literal and as display text ("Awaiting Legal", "Initial Creation", ...).
* **JBS category names:** `Packaging`, `Flexible Packaging`, `Energy`, `Electricity`, `Logistics`, `Facilities` and their codes (`PACKAGING`, `PACKAGING-FLEX`, `ENERGY`, `ENERGY-ELEC`, `LOGISTICS`, `FACILITIES`).
* **Role names of the reference model:** `ProcurementAdministrator`, `CategoryOwner`, `BackupApprover` (added because the role matrix is where the procurement function leaks into code).
* **DocuSign-specific assumptions** (not only the name): envelope vocabulary, HMAC callback handling, vendor identifiers. **Qlik-specific assumptions:** reporting hooks or schemas shaped for one tool. **Medius-specific assumptions:** source-system fields or formats hard-wired to one system. Each was searched for by name and by shape (section 7).

The scan is a text scan with the limits of a text scan: a rule written without any of these words is not found by it. Section 6 covers structural findings found by reading the code around the hits.

### 1.3 Classification

Each occurrence is placed in one of the five classes asked for. Test projects are not one of the five; they are reported as **Platform Code (test project)** and judged against PRT-001, which allows business names in "test fixtures".

| Class | Where it lives | Rule |
|---|---|---|
| 1. Platform Code | `src/` (all projects), including the `.csproj` lines that embed files into assemblies | Must not contain JBS-specific business logic or names. A hit is a violation unless it is a generic word. |
| 2. Configuration | `src/Ecp.Host/appsettings*.json`, `deploy/`, `.github/`, `sample-data/*approval-matrix*` | May name a business value, but a platform default that selects JBS behaviour is a finding. |
| 3. Solution Pack | `templates/packs/*`, `sample-data/jbs-procurement-solution-pack.json`, `sample-data/demo-*.json` | The allowed home of JBS names (PRT-001). Not a violation. |
| 4. Migration Sample | `migration-samples/`, `tools/migration-demo.py` | The allowed home of source-system names such as Medius. Not a violation. |
| 5. Documentation | `docs/`, `requirements/`, `decisions/`, `prompts/`, `.claude/`, root `*.md` | Describes the reference configuration. Not a violation; drift is Low. |

"Violates portability?" uses: **Yes** (platform code carries JBS behaviour, a JBS name or a JBS default), **Partly** (the reference is incidental or a label), **No** (permitted location or a generic word).

## 2. Summary of counts

Counts are matching lines (one line with two terms counts once per term).

### 2.1 Named terms by class

| Class | JBS | Procurement | Group Head Procurement | Group Procurement | Medius | Qlik | DocuSign |
|---|---:|---:|---:|---:|---:|---:|---:|
| Platform Code | 6 | 20 | 2 | 0 | 0 | 0 | 0 |
| Platform Code (test project) | 9 | 46 | 49 | 0 | 12 | 1 | 1 |
| Configuration | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Configuration (sample) | 2 | 2 | 0 | 0 | 0 | 0 | 0 |
| Solution Pack | 8 | 16 | 7 | 0 | 0 | 2 | 0 |
| Migration Sample | 0 | 0 | 0 | 0 | 2 | 0 | 0 |
| Documentation | 97 | 103 | 20 | 4 | 29 | 37 | 44 |
| **Total** | **122** | **187** | **78** | **4** | **43** | **40** | **45** |

Note on the columns: *Group Head Procurement* counts the role phrase and the approver-group key `GroupHeadProcurement`; *Procurement* also counts every line that carries either of those, so the columns overlap by design.

### 2.2 Reference-configuration vocabulary by class

| Class | Lifecycle names | Category names | Role names |
|---|---:|---:|---:|
| Platform Code | 30 | 4 | 53 |
| Platform Code (test project) | 146 | 112 | 61 |
| Configuration | 1 | 0 | 0 |
| Configuration (sample) | 0 | 2 | 0 |
| Solution Pack | 155 | 44 | 29 |
| Migration Sample | 0 | 8 | 0 |
| Documentation | 103 | 4 | 62 |

Generic words (`Ended`, `Effective`, `Draft` used as column headings in admin pages) are excluded from the lifecycle count in the platform row; see section 5.2 for the lines.

### 2.3 Where the platform layer is clean

* **Medius, Qlik, DocuSign:** no occurrence in `src/` at all, by name. Source system is free text (`SourceSystem`) in the migration and contracts modules; the reporting schema is the neutral `cf_<key>` projection; the signature boundary is an interface with a manual, a mock and a not-connected provider.
* **`JBS`, `JBSAUS`:** no occurrence in `src/` as a word. The only appearances are the lowercase pack id `jbs-procurement` (P-01, P-03).
* **Approver group key `GroupHeadProcurement`:** not in compiled code. The register-authorisation engine reads `approverGroupKey` from the workflow definition. It appears in compiled code only as a comment and one UI sentence (P-06).
* **Workflow state codes:** the contract engine treats status as an opaque code taken from the published definition (`Guard.NotBlank(n.InitialStatusCode ...)`); no domain rule branches on `"Effective"` or `"AwaitingApproval"`. The state names that do appear in code belong to the Legal review sub-process and to labels (P-05).

## 3. Findings (violations and near-violations)

Severity follows the portability-review skill: **High** is business logic hard-coded in compiled code or a pack that cannot be replaced without source change; **Medium** is business vocabulary or a pack-specific default in compiled code; **Low** is a label, example, comment or drift. Each finding names the requirement it conflicts with. Fixes are recommendations only; none has been made.

| ID | Severity | Class | Location | What | Why it violates | Recommended fix (not done) |
|---|---|---|---|---|---|---|
| P-01 | **High** (fixed in S1, ADR-0024) | Platform Code | `src/Modules/Ecp.Lifecycle/Definitions/DefinitionStore.cs:14`, `:87`; `src/Modules/Ecp.Notifications/NotificationsApi.cs:15`; `src/Modules/Ecp.Notifications/Policy/PolicyStore.cs:46` | `DefaultPackId` defaults to `"jbs-procurement"` and is used whenever an organisation has published no definition. The organisation's own `SolutionPackKey` is never consulted, and the host never sets it (`src/Ecp.Host/Program.cs:64`-`65` passes only the installation mode). | A `blank`-pack organisation silently gets the JBS 17-state lifecycle, the JBS Variation workflow, approver group `GroupHeadProcurement` and the JBS notification policy. PRT-001 (pack name in compiled code), PRT-002 (second business cannot run), PRT-013. | Resolve the pack from the organisation's `SolutionPackKey`; refuse (or install an empty/neutral definition) when the pack carries none; remove the code default. |
| P-02a | **High** | Platform Code | `src/Modules/Ecp.Notifications/Reminders.cs:94`-`95` | Reminder fall-back: when nobody owns the category, notify `RoleCodes.ProcurementAdministrator`. | Routing rule keyed to a JBS-named role in domain logic. PRT-013. | Make the fall-back recipient a notification-policy rule (the policy already has a closed recipient vocabulary), not a role constant. |
| P-02b | Medium | Platform Code | `src/Modules/Ecp.Identity/Domain/Roles.cs:11`, `:22`; `src/Modules/Ecp.Identity/Domain/RoleAssignment.cs:15`; `src/Modules/Ecp.Identity/Access/AccessEvaluator.cs:199`; `src/Modules/Ecp.Setup/Onboarding/OnboardingRules.cs:25`, `:145`; `src/Ecp.Host/DemoSeeder.cs:31` | The 12-role catalogue is a compiled constant list (`RoleCodes.All`), including `ProcurementAdministrator`; grant rules (`DelegationPolicy`) and onboarding rules are written against it. The role-to-permission matrix is embedded from the reference CSV (`src/Modules/Ecp.Identity/Ecp.Identity.csproj:13`-`14`). | Recorded decision D1 fixed the twelve roles, but PRT-013 requires role names to come from definitions and the catalogue, and PRT-004 requires renaming to work. A business that calls the role something else, or has no such role, cannot configure that. Needs an ADR that reconciles D1 with PRT-013 (for example: keep stable role *codes*, move the function-flavoured names and any role-specific behaviour into terminology and permissions data). | Rename the code to a neutral name (for example an organisation-administration role), or load the catalogue from configuration; keep the matrix as pack data. |
| P-03 | **High** | Platform Code | `src/Modules/Ecp.Configuration/Ecp.Configuration.csproj:13`-`14`; `src/Modules/Ecp.Lifecycle/Ecp.Lifecycle.csproj:19`-`20`; `src/Modules/Ecp.Notifications/Ecp.Notifications.csproj:18`; `WorkflowKinds.ResourceName` at `src/Modules/Ecp.Lifecycle/Definitions/DefinitionStore.cs:41` | The JBS pack, its two workflows and its notification policy are embedded into module assemblies as named resources, and looked up by `Ecp.Workflow.{packId}...`. | Adding a pack means editing a `.csproj` and rebuilding: a source change. PRT-002 requires a second pack to install with no source change; CFG-04/PKM-01 (pack loader) is not built. | Load packs from the pack store (the `PKM-01` package format) and keep the embedded copies only as the installer's seed. |
| P-04 | Medium | Platform Code | `src/Modules/Ecp.Configuration/PacksAndBranding.cs:19` | Pack metadata `features` (`LegalReview`, `Variations`, `QlikExternalReporting`, `CriticalNotice`), `ownershipModel` and `approvalModel` are parsed and displayed but nothing reads them. Legal review, Variations, category ownership and register authorisation are always on, whatever the pack says. | The pack cannot switch off JBS behaviour, so "JBS is a shipped reference configuration, not universal hard-coded logic" is only partly true. PRT-015 (flags enable existing capabilities). | Implement the feature flags (CFG-02) and make the compiled capabilities conditional on them. |
| P-05 | Medium | Platform Code | `src/Modules/Ecp.Notifications/Dashboards.cs:23`, `:32`, `:96`, `:103`-`:121`; `src/Modules/Ecp.Lifecycle/RegisterSets.cs:30`-`34`; `src/Modules/Ecp.Lifecycle/LegalReviews.cs:93`, `:127`-`:158`; `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:50`-`125`; `src/Ecp.Host/Pages/Approvals/Details.cshtml.cs:26`; `src/Modules/Ecp.Contracts/Register/system-views.json:10` | JBS lifecycle names compiled in: dashboard cards ("Awaiting Legal", "Pending Approval", "Renewals in Progress"), an embedded system view named "Awaiting Legal", the Legal-review status enum and its labels, and a flash message that says a rejected agreement "returns to Initial Creation". | The display name of a state belongs to the workflow definition (`DisplayOf`) and to terminology. A pack with different state names shows JBS wording on dashboards and messages. PRT-004, PRT-013. The Legal-review enum is a compiled sub-process; acceptable only if Legal review is declared a platform capability, in which case its labels must still come from terminology. | Take state labels from the definition; move dashboard card titles and the system view to the pack; keep the Legal-review enum but render its labels from terminology. |
| P-06 | Low | Platform Code | `src/Ecp.Host/Pages/Approvals/Index.cshtml:5`; `src/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs:48` | A page paragraph names "the Group Head of Procurement or a backup"; a doc comment does the same. | A platform page shows a JBS role title to every business. The engine itself reads the approver group from the definition. PRT-004. | Say "the configured approver group"; use terminology for the title. |
| P-07 | Low | Platform Code | `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:17`-`35`; `src/Modules/Ecp.Setup/Stages/SetupStages.cs:185` | Help examples use JBS category and role names ("Packaging", "Flexible packaging", "Energy", "Procurement-Viewers", "Procurement Administrator"); the pack selector says the reference pack is "a synthetic example of a procurement organisation". | Examples are not behaviour, but they ship in every business's UI. | Use neutral examples ("Category A") or take them from the selected pack. |
| P-08 | Medium | Configuration | `src/Ecp.Host/appsettings.json:23` | `"Contracts": { "InitialStatusCode": "Draft" }`: the platform default names a JBS state. | With P-01 resolved, a pack whose first state is not `Draft` would fail to create a contract unless the host is edited. | Take the initial state from the published workflow definition. |
| P-09 | Medium (stage A done in S1) | Platform Code (guard) | `tests/Ecp.Architecture.Tests/ArchitectureTests.cs:191`-`196` | ARC-010 scans `*.cs` only, case-sensitively, for `JBS|JBSAUS|Medius|Qlik|DocuSign` and a list of 12 quoted state codes. | It cannot see `.cshtml`, `.csproj`, `.json`, the lowercase pack id, `Procurement`, role names or `Group Head`. That is why P-01 to P-07 pass CI. | Widen the rule (case-insensitive, all source extensions, role and group names), with an explicit allow-list for the adapter projects. |
| P-10 | Low | Platform Code | `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:278`; `src/Modules/Ecp.Lifecycle/Persistence/LifecycleDbContext.cs:126`, `:131`; `src/Modules/Ecp.Lifecycle/Execution.cs:16`-`84` | The signature boundary is shaped like DocuSign: `IESignatureProvider.CreateEnvelopeAsync`, `EnvelopeRequest` and `EnvelopeResult`, an `EnvelopeId` column and index, an `ExternalEnvelopeProvider` placeholder. No vendor name appears. | "Envelope" is DocuSign's term; the persisted column name carries it into every installation. Other providers (Adobe Sign, internal workflows) have no envelope. Low because the interface is otherwise neutral and no vendor code exists. | Name the concept neutrally (signature request) before the first adapter is written, while the column is cheap to rename. |

### 3.1 Related findings outside the named terms

These were found by reading around the hits. They are not in the list of terms asked for, but they are the same kind of problem.

| ID | Severity | Location | What | Requirement |
|---|---|---|---|---|
| R-01 | Medium | `src/Modules/Ecp.Metadata/FieldModel.cs:152`, `:174`; `src/Modules/Ecp.Contracts/Domain/Contracts.cs:187`, `:505`, `:656`; `src/Ecp.Host/Pages/Suppliers/Create.cshtml:15`; `src/Ecp.Host/Pages/Shared/_ContractFields.cshtml:49` | Core fields and validation specific to one country and one ERP: Supplier **ABN** (eleven digits) and **SAP outline agreement number** are fixed core columns, and an invalid ABN is refused by the domain. | PRT-013; the field engine (CFG-03) could carry both as pack fields. |
| R-02 | Medium | `src/Modules/Ecp.Contracts/Domain/Contracts.cs:15`; the same constant at `src/Ecp.SharedKernel/DomainErrors.cs:57`; `src/Modules/Ecp.Notifications/Policy/NotificationPolicy.cs:107`; `src/Modules/Ecp.Configuration/ConfigurationApi.cs:315` | The classification level name `"Legal Privileged"` is a compiled constant and a default, although the scale is configured by the pack. | PRT-013. |
| R-03 | Low | `src/Ecp.SharedKernel/Localisation/LocaleFormatter.cs:8`-`11`; `src/Ecp.Host/appsettings.json:11`-`14`; `src/Ecp.Host/Pages/OrgLocale.cs:12`; `src/Ecp.Host/Pages/Migration/MigrationPageModel.cs:14`; `src/Modules/Ecp.Contracts/CustomFields.cs:110` | Australian defaults (the `en-AU` culture, the Brisbane time zone and the `AUD` currency) as platform fall-backs when an organisation has no setting. The per-organisation setting exists and is used. | PRT-012: acceptable as a fall-back, but a fall-back should be neutral (UTC, invariant, no currency). |
| R-04 | Low | `src/Modules/Ecp.Notifications/Reminders.cs:63`; `src/Modules/Ecp.Notifications/ReminderSettings.cs:22` | The literal `"Evergreen"` (an extension type of the reference configuration) is compared in reminder code, while a constant `ExtensionTypes.Evergreen` exists in the contracts module. | PRT-013. |
| R-05 | Medium | `src/Modules/Ecp.Lifecycle/LegalReviews.cs`, `src/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs` (modules, not single lines) | The Legal-review sub-process and the register-authorisation step (with its Registered Date stamping) are compiled capabilities of the JBS operating model. They are driven by the workflow definition, but a business without Legal review or a register cannot remove them. | PRT-015; links to P-04. |
| R-06 | Info | `src/Modules/Ecp.Contracts/Register/system-views.json` | Eleven shipped list views are embedded; one is named for a JBS state (P-05). The rest are generic ("All Contracts", "Expiring Soon"). | PRT-004. |

### 3.2 What was checked and found compliant

* Approver groups, roles for approval and the approval matrix are data: `approverGroupKey` and matrix rows come from published releases; the resolver has no group name in code.
* Reference numbering (`JBSAUS-000042`) is a per-organisation prefix rule from the pack (`referencePrefix`); the only `JBSAUS` strings are in unit tests and the pack.
* Configuration, matrices, notification policy, views and custom fields are stored as published releases; creating fields changes no schema (typed value rows).
* The classification scale, contract types and categories are pack defaults applied at setup, not code.

## 4. Test coverage of portability (what is and is not verified)

| Criterion | State | Evidence |
|---|---|---|
| PRT-001 static scan | **Partly verified.** ARC-010 passes, but it is too narrow (P-09). | `tests/Ecp.Architecture.Tests/ArchitectureTests.cs:191` |
| PRT-002 second business | **NOT VERIFIED.** No non-JBS pack with a lifecycle exists; `blank` has none. | `templates/packs/` holds `blank` and `jbs-procurement` only; PKM-04 is open. |
| PRT-003 no schema change per field | Verified (typed value rows; custom-field scenarios). | `docs/CUSTOM_FIELDS.md`, `tests/Ecp.Integration.Tests/CustomFieldScenarios.cs` |
| PRT-004 terminology aliases | **NOT VERIFIED.** The terminology service (CFG-02) is not built; labels are resource strings. | `TASKS.md` CFG-02 |
| PRT-006/PRT-011 round trips | **NOT VERIFIED.** Export/import packages (PKM-01, EXP-01) are not built. | `TASKS.md` |
| PRT-012 per-organisation locale | Partly verified (formatting, zones, culture per organisation). | `tests/Ecp.UnitTests/LocalisationTests.cs` |
| PRT-013 no role/state/permission literals | **Not met** (P-02, P-05) and **no renamed-role or renamed-state test exists.** | this audit |
| PRT-015 feature flags | **Not met** (P-04). | this audit |

## 5. Occurrence register: platform code (`src/`)

Every occurrence in `src/`, one row each. "Reason" says why it is there; "Violates?" is the judgement in section 1.3. The finding column links the row to section 3.

### 5.1 Named terms in `src/`

| Location | Terms | Class | Finding | Reason | Violates? | Line |
|---|---|---|---|---|---|---|
| `src/Ecp.Host/DemoSeeder.cs:31` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `["Business Configuration Administrator"] = RoleCodes.BusinessConfigurationAdministrator, ["Procurement Adminis` |
| `src/Ecp.Host/Pages/Approvals/Index.cshtml:5` | Group Head (of) Procurement, Procurement | Platform Code | P-06 | Page text names the JBS approver title for every business | Yes (label) | `<p class="lede">@L["Executed agreements waiting for the Group Head of Procurement or a backup. Whoever respond` |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:33` | Procurement | Platform Code | P-07 | Example rows in onboarding help use JBS names | Partly (example) | `["The group is the value the provider sends in its groups claim (a name or an object id).", "Onboarding can ma` |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:35` | Procurement | Platform Code | P-07 | Example rows in onboarding help use JBS names | Partly (example) | `OnboardingCsv.MappingHeader, "corp-sso,Procurement-Viewers,Viewer,BusinessUnit,,PKG,,Internal\ncorp-sso,Legal-` |
| `src/Modules/Ecp.Configuration/Ecp.Configuration.csproj:14` | JBS, Procurement | Platform Code | P-03 | Build wiring embeds a JBS pack file into the platform assembly under a pack-specific resource name | Yes | `<EmbeddedResource Include="../../../templates/packs/jbs-procurement/pack.json" LogicalName="Ecp.Pack.jbs-procu` |
| `src/Modules/Ecp.Identity/Access/AccessEvaluator.cs:199` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `RoleCodes.ProcurementAdministrator, RoleCodes.CategoryOwner, RoleCodes.Legal, RoleCodes.Approver, RoleCodes.Ba` |
| `src/Modules/Ecp.Identity/Domain/RoleAssignment.cs:15` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `[RoleCodes.ProcurementAdministrator] = [ScopeLevel.Organisation],` |
| `src/Modules/Ecp.Identity/Domain/Roles.cs:11` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `public const string ProcurementAdministrator = "ProcurementAdministrator";` |
| `src/Modules/Ecp.Identity/Domain/Roles.cs:22` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `ProcurementAdministrator, CategoryOwner, Legal, Approver, BackupApprover, Auditor, Viewer` |
| `src/Modules/Ecp.Lifecycle/Definitions/DefinitionStore.cs:14` | JBS, Procurement | Platform Code | P-01 | Code default selects the JBS pack for every organisation that has published nothing | Yes | `public string DefaultPackId { get; set; } = "jbs-procurement";` |
| `src/Modules/Ecp.Lifecycle/Ecp.Lifecycle.csproj:19` | JBS, Procurement | Platform Code | P-03 | Build wiring embeds a JBS pack file into the platform assembly under a pack-specific resource name | Yes | `<EmbeddedResource Include="../../../templates/packs/jbs-procurement/workflows/contract-lifecycle.json" Logical` |
| `src/Modules/Ecp.Lifecycle/Ecp.Lifecycle.csproj:20` | JBS, Procurement | Platform Code | P-03 | Build wiring embeds a JBS pack file into the platform assembly under a pack-specific resource name | Yes | `<EmbeddedResource Include="../../../templates/packs/jbs-procurement/workflows/variation.json" LogicalName="Ecp` |
| `src/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs:48` | Group Head (of) Procurement, Procurement | Platform Code | P-06 | Doc comment names the JBS approver; the code reads the approver group from the definition | Partly (comment) | `/// Register authorisation: the Group Head of Procurement (or a configured backup) approves or rejects an exec` |
| `src/Modules/Ecp.Notifications/Ecp.Notifications.csproj:18` | JBS, Procurement | Platform Code | P-03 | Build wiring embeds a JBS pack file into the platform assembly under a pack-specific resource name | Yes | `<EmbeddedResource Include="../../../templates/packs/jbs-procurement/notifications/notification-policy.json" Lo` |
| `src/Modules/Ecp.Notifications/NotificationsApi.cs:15` | JBS, Procurement | Platform Code | P-01 | Code default selects the JBS pack for every organisation that has published nothing | Yes | `public string DefaultPackId { get; set; } = "jbs-procurement";` |
| `src/Modules/Ecp.Notifications/Reminders.cs:94` | Procurement | Platform Code | P-02a | Comment describing the fall-back to the Procurement Administrator role | Partly | `// Nobody currently owns the category (or could open the record): fall back to the organisation's Procurement ` |
| `src/Modules/Ecp.Notifications/Reminders.cs:95` | Procurement | Platform Code | P-02a | Fall-back recipient is a JBS-named role | Yes | `recipients = await resolver.FilterByReadAccessAsync(await directory.HoldersOfRoleAsync(RoleCodes.ProcurementAd` |
| `src/Modules/Ecp.Setup/Onboarding/OnboardingRules.cs:25` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `RoleCodes.Viewer, RoleCodes.CategoryOwner, RoleCodes.Legal, RoleCodes.Auditor, RoleCodes.ProcurementAdministra` |
| `src/Modules/Ecp.Setup/Onboarding/OnboardingRules.cs:145` | Procurement | Platform Code | P-02b | Compiled role catalogue / grant rule names the procurement-function role | Yes (D1 vs PRT-013) | `if (g.RoleCode is RoleCodes.ProcurementAdministrator or RoleCodes.DivisionSiteAdministrator)` |
| `src/Modules/Ecp.Setup/Stages/SetupStages.cs:185` | Procurement | Platform Code | P-07 | Pack selector help text calls the reference pack "a procurement organisation" | Partly (help text) | `=> [new("packId", "Solution pack", FieldKind.Select, true, "Blank starts with nothing. The reference pack is a` |

### 5.2 Reference-configuration lifecycle and category names in `src/`

| Location | Match | Kind | Class | Finding | Reason | Violates? |
|---|---|---|---|---|---|---|
| `src/Ecp.Host/Pages/Admin/Approvals/Delegations.cshtml:52` | `"Ended"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Admin/Approvals/Delegations.cshtml:77` | `"Ended"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Admin/Approvals/Index.cshtml:32` | `"Effective"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Admin/Configuration/Index.cshtml:22` | `"Draft"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Admin/Elevations.cshtml:34` | `"Ended"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Admin/Users/Details.cshtml:12` | `"Effective"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Admin/Users/Details.cshtml:30` | `"Ended"` | lifecycle | Platform Code | - | Generic word in a column heading or badge, not a lifecycle reference | No |
| `src/Ecp.Host/Pages/Approvals/Details.cshtml:91` | `LegalCleared` | lifecycle | Platform Code | R-05 | Property name of the Legal-review summary | Partly |
| `src/Ecp.Host/Pages/Approvals/Details.cshtml.cs:26` | `Initial Creation` | lifecycle | Platform Code | P-05 | Flash message hard-codes the JBS state a rejection returns to | Yes |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:19` | `Packaging` | category | Platform Code | P-07 | Example CSV rows use JBS category names | Partly (example) |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:22` | `PACKAGING` | category | Platform Code | P-07 | Example CSV rows use JBS category names | Partly (example) |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:27` | `PACKAGING` | category | Platform Code | P-07 | Example CSV rows use JBS category names | Partly (example) |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs:31` | `PACKAGING` | category | Platform Code | P-07 | Example CSV rows use JBS category names | Partly (example) |
| `src/Ecp.Host/appsettings.json:23` | `"Draft"` | lifecycle | Configuration | P-08 | Platform default initial status names a JBS state | Yes |
| `src/Modules/Ecp.Contracts/Domain/Contracts.cs:283` | `Awaiting Legal` | lifecycle | Platform Code | P-05 | Doc comment lists JBS states as examples of frozen content | Partly (comment) |
| `src/Modules/Ecp.Contracts/Register/system-views.json:10` | `Awaiting Legal` | lifecycle | Platform Code | P-05 | Embedded system view is named for a JBS state | Yes |
| `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:50` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:85` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:89` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:99` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:112` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/Domain/LifecycleRecords.cs:125` | `"Withdrawn"` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/Engine/Support.cs:26` | `LegalCleared` | lifecycle | Platform Code | R-05 | Event type / field of the compiled Legal-review step | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:93` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:127` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:149` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:153` | `"Withdrawn"` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:158` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:312` | `"Withdrawn"` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LegalReviews.cs:492` | `LegalCleared` | lifecycle | Platform Code | P-05 | Legal-review sub-process status and labels compiled in (R-05) | Partly |
| `src/Modules/Ecp.Lifecycle/LifecycleApi.cs:86` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Register set provider named for a JBS state, built on the compiled Legal-review status | Partly |
| `src/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs:29` | `LegalCleared` | lifecycle | Platform Code | R-05 | Event type / field of the compiled Legal-review step | Partly |
| `src/Modules/Ecp.Lifecycle/RegisterSets.cs:30` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Register set provider named for a JBS state, built on the compiled Legal-review status | Partly |
| `src/Modules/Ecp.Lifecycle/RegisterSets.cs:34` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Register set provider named for a JBS state, built on the compiled Legal-review status | Partly |
| `src/Modules/Ecp.Notifications/Dashboards.cs:23` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:32` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:96` | `Awaiting Approval` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:103` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:105` | `AwaitingLegal` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:106` | `Awaiting Legal` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:111` | `Awaiting Approval` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |
| `src/Modules/Ecp.Notifications/Dashboards.cs:121` | `Renewal in Progress` | lifecycle | Platform Code | P-05 | Dashboard card key or title uses a JBS lifecycle name | Yes |

### 5.3 Role names in `src/` (grouped by file)

Role-name matches are not among the terms asked for; they are listed so that every JBS-flavoured name in compiled code is in one place. `CategoryOwner`, `Approver` and `BackupApprover` are generic role concepts of the reference model (P-02b); `ProcurementAdministrator` is the function-named one.

| File | Lines | Names | Finding |
|---|---|---|---|
| `src/Ecp.Host/DemoSeeder.cs` | 31, 32 | Category Owner, Procurement Administrator | P-02b |
| `src/Ecp.Host/Pages/Admin/Categories.cshtml.cs` | 84 | Category Owner | P-02b |
| `src/Ecp.Host/Pages/Onboarding/Index.cshtml.cs` | 36 | CategoryOwner | P-02b |
| `src/Ecp.Host/Pages/Onboarding/OnboardingHelp.cs` | 26, 33 | Category Owner | P-02b |
| `src/Modules/Ecp.Contracts/ContractAccess.cs` | 56, 57, 680 | CategoryOwner | P-02b |
| `src/Modules/Ecp.Contracts/ContractAccessFilter.cs` | 67 | CategoryOwner | P-02b |
| `src/Modules/Ecp.Identity/Access/AccessEvaluator.cs` | 12, 24, 25, 100, 111, 135, 170, 199, 202 | BackupApprover, Category Owner, CategoryOwner, ProcurementAdministrator | P-02b |
| `src/Modules/Ecp.Identity/Access/AccessModel.cs` | 55, 94, 95 | Category Owner, CategoryOwner | P-02b |
| `src/Modules/Ecp.Identity/Domain/RoleAssignment.cs` | 15, 16, 19, 40 | BackupApprover, CategoryOwner, ProcurementAdministrator | P-02b |
| `src/Modules/Ecp.Identity/Domain/Roles.cs` | 11, 12, 15, 22 | BackupApprover, CategoryOwner, ProcurementAdministrator | P-02b |
| `src/Modules/Ecp.Identity/IdentityApi.cs` | 677 | BackupApprover | P-02b |
| `src/Modules/Ecp.Identity/PeopleDirectory.cs` | 25, 88, 91 | Category Owner, CategoryOwner | P-02b |
| `src/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs` | 72 | BackupApprover | P-02b |
| `src/Modules/Ecp.Notifications/Policy/NotificationPolicy.cs` | 13 | CategoryOwner | P-02b |
| `src/Modules/Ecp.Notifications/Processing.cs` | 76 | CategoryOwner | P-02b |
| `src/Modules/Ecp.Notifications/Reminders.cs` | 94, 95 | Procurement Administrator, ProcurementAdministrator | P-02a |
| `src/Modules/Ecp.Setup/Onboarding/OnboardingApplier.cs` | 99, 107 | CategoryOwner | P-02b |
| `src/Modules/Ecp.Setup/Onboarding/OnboardingModel.cs` | 30, 60 | CategoryOwner | P-02b |
| `src/Modules/Ecp.Setup/Onboarding/OnboardingRules.cs` | 25, 98, 100, 105, 145, 310 | CategoryOwner, ProcurementAdministrator | P-02b |
| `src/Modules/Ecp.Setup/Onboarding/OnboardingService.cs` | 104, 167, 191, 207 | CategoryOwner | P-02b |

## 6. Structural findings (no matching word)

Found by reading the code that surrounds the hits; a text scan cannot see them.

* **The pack does not drive behaviour (P-04).** `PackDefaults` is applied once at setup (culture, time zone, currency, reference prefix, classification scale, contract types, categories). `features`, `ownershipModel` and `approvalModel` are read into a record and shown; no code branches on them.
* **Workflow selection ignores the organisation (P-01).** `WorkflowDefinitionStore.CurrentAsync` and `NotificationPolicyStore.CurrentAsync` use the process-wide option. The organisation record carries `SolutionPackKey`, and setup and onboarding validate it against the catalogue (`OnboardingRules.cs:42`), but lifecycle and notifications do not read it.
* **Packs are compiled in (P-03).** The only packs that can ever be installed are the ones named in three `.csproj` files.
* **Guard blind spots (P-09).** See finding.
* **The role matrix is reference data compiled into the Identity assembly (P-02b):** `src/Modules/Ecp.Identity/Ecp.Identity.csproj` embeds the two reference CSVs from `requirements/02-reference/`. Those files are documentation-folder content that the build treats as code.

## 7. DocuSign, Qlik and Medius: specific assumptions

| Subject | In `src/` | Assumption found | Verdict |
|---|---|---|---|
| **DocuSign** | No occurrence of the name. | Envelope-centric interface and column (P-10). No HMAC callback code exists yet (INT-03 is open), so no webhook format is assumed. | Low; fix the naming before the adapter exists. |
| **Qlik** | No occurrence. | None in code. The pack feature name `QlikExternalReporting` is in the solution pack only. The reporting projection is the neutral `cf_<key>` schema (`docs/EXPORTS.md`). | Compliant. |
| **Medius** | No occurrence. | None in code. `SourceSystem` is free text. The Medius import profile is requirements text and a migration sample. | Compliant. |

Tests that use the names (allowed as fixtures): section 8.

## 8. Occurrence register: test projects (`tests/`)

Test fixtures are a permitted home for these names (PRT-001). Rows are per file with every line number. "Reason" is what the test needs the name for. The pattern worth noting is not any single line but that **every lifecycle, approval and notification scenario is written against the JBS reference pack**; there is no scenario against a second pack (PRT-002 gap, section 4).

### 8.1 Named terms

| File | Terms and lines | Reason | Violates? |
|---|---|---|---|
| `tests/Ecp.Api.Tests/ApprovalMatrixUiTests.cs` | Group Head (of) Procurement: 28, 30, 102, 123, 131, 134, 167, 168, 169, 170, 174, 202, 215; Procurement: 28, 30, 99, 123, 131, 148, 174, 215 | Approver group key of the JBS reference workflow / Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Api.Tests/ElevationUiTests.cs` | Procurement: 53, 54, 55 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Api.Tests/LifecycleUiTests.cs` | Group Head (of) Procurement: 60, 133, 134 | Approver group key of the JBS reference workflow | No (fixture) |
| `tests/Ecp.Api.Tests/MigrationUiTests.cs` | Medius: 66, 73 | Source-system label for migration fixtures; the migration module treats it as free text | No (fixture) |
| `tests/Ecp.Api.Tests/OidcTests.cs` | Procurement: 149 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Api.Tests/SetupHostTests.cs` | JBS: 43; Procurement: 43 | Role or user name of the reference model (Procurement Administrator) used as fixture / Selects the JBS pack in a setup scenario | No (fixture) |
| `tests/Ecp.Api.Tests/UiTests.cs` | Procurement: 234 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Architecture.Tests/ArchitectureTests.cs` | DocuSign: 193; JBS: 193; Medius: 193; Qlik: 193 | Source-system label for migration fixtures; the migration module treats it as free text / The ARC-010 rule that looks for the name / The ARC-010 rule that looks for the name (P-09) | No (fixture) |
| `tests/Ecp.Integration.Tests/AccessScenarioTests.cs` | Procurement: 54, 89 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/AccessScenarios.cs` | Procurement: 49 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/AccessWorld.cs` | Procurement: 118 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/ApprovalMatrixScenarios.cs` | Procurement: 236 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/ApprovalMatrixScenarios2.cs` | Group Head (of) Procurement: 66, 279, 292, 294, 301 | Approver group key of the JBS reference workflow | No (fixture) |
| `tests/Ecp.Integration.Tests/ApprovalMatrixScenarios3.cs` | Group Head (of) Procurement: 63; Procurement: 95 | Approver group key of the JBS reference workflow / Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/ClassificationScenarios.cs` | Procurement: 50, 161, 191 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/CustomFieldScenarios.cs` | Group Head (of) Procurement: 301, 306; Procurement: 112, 120 | Approver group key of the JBS reference workflow / Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/LifecycleScenarios.cs` | Group Head (of) Procurement: 55 | Approver group key of the JBS reference workflow | No (fixture) |
| `tests/Ecp.Integration.Tests/LifecycleScenarios2.cs` | Group Head (of) Procurement: 30, 75, 110, 127, 151, 156 | Approver group key of the JBS reference workflow | No (fixture) |
| `tests/Ecp.Integration.Tests/LifecycleScenarios3.cs` | Group Head (of) Procurement: 115, 159, 160, 204, 248, 287, 305 | Approver group key of the JBS reference workflow | No (fixture) |
| `tests/Ecp.Integration.Tests/LifecycleScenarios4.cs` | Group Head (of) Procurement: 55, 61 | Approver group key of the JBS reference workflow | No (fixture) |
| `tests/Ecp.Integration.Tests/LifecycleWorld.cs` | Group Head (of) Procurement: 20, 33, 106, 115, 152, 161; Procurement: 20 | Approver group key of the JBS reference workflow / Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/LightweightProviderTests.cs` | Medius: 195, 293, 304, 309 | Source-system label for migration fixtures; the migration module treats it as free text | No (fixture) |
| `tests/Ecp.Integration.Tests/MigrationScenarios.Safety.cs` | Procurement: 185 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/MigrationScenarios.cs` | Medius: 42 | Source-system label for migration fixtures; the migration module treats it as free text | No (fixture) |
| `tests/Ecp.Integration.Tests/MigrationWorld.cs` | Medius: 29; Procurement: 16, 38, 41, 42 | Role or user name of the reference model (Procurement Administrator) used as fixture / Source-system label for migration fixtures; the migration module treats it as free text | No (fixture) |
| `tests/Ecp.Integration.Tests/NotificationScenarios2.cs` | Group Head (of) Procurement: 53, 62, 90; Procurement: 78 | Approver group key of the JBS reference workflow / Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/OnboardingScenarios.cs` | Procurement: 45, 86 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.Integration.Tests/RegisterViewScenarios.cs` | Procurement: 180 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.UnitTests/AccessEvaluatorTests.cs` | Procurement: 71, 79, 187, 383, 385 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.UnitTests/IdentityRulesTests.cs` | Procurement: 166, 174 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.UnitTests/MigrationAuditAndGuardTests.cs` | Medius: 14 | Source-system label for migration fixtures; the migration module treats it as free text | No (fixture) |
| `tests/Ecp.UnitTests/OnboardingRulesTests.cs` | Procurement: 83, 209, 274 | Role or user name of the reference model (Procurement Administrator) used as fixture | No (fixture) |
| `tests/Ecp.UnitTests/TenancyAndContractDomainTests.cs` | JBS: 13, 66, 73, 74; Medius: 133, 143 | Reference-number prefix rule is exercised with the pack prefix / Source-system label for migration fixtures; the migration module treats it as free text | No (fixture) |
| `tests/Ecp.UnitTests/WorkflowDefinitionTests.cs` | JBS: 14, 45, 211; Procurement: 14 | Role or user name of the reference model (Procurement Administrator) used as fixture / Validates the shipped JBS pack files | No (fixture) |

### 8.2 Lifecycle, category and role vocabulary (per file counts)

| File | Lifecycle | Category | Role |
|---|---:|---:|---:|
| `tests/Ecp.Api.Tests/ApprovalMatrixUiTests.cs` | 1 | 1 | 4 |
| `tests/Ecp.Api.Tests/ElevationUiTests.cs` | 1 | 0 | 0 |
| `tests/Ecp.Api.Tests/LifecycleUiTests.cs` | 7 | 1 | 0 |
| `tests/Ecp.Api.Tests/UiTests.cs` | 0 | 19 | 0 |
| `tests/Ecp.Architecture.Tests/ArchitectureTests.cs` | 1 | 0 | 0 |
| `tests/Ecp.Integration.Tests/AccessScenarios.cs` | 1 | 13 | 2 |
| `tests/Ecp.Integration.Tests/AccessWorld.cs` | 1 | 12 | 3 |
| `tests/Ecp.Integration.Tests/ApprovalFactScenarios.cs` | 1 | 0 | 0 |
| `tests/Ecp.Integration.Tests/ApprovalMatrixScenarios.cs` | 0 | 0 | 1 |
| `tests/Ecp.Integration.Tests/ApprovalMatrixScenarios2.cs` | 28 | 4 | 1 |
| `tests/Ecp.Integration.Tests/ApprovalMatrixScenarios3.cs` | 0 | 0 | 1 |
| `tests/Ecp.Integration.Tests/ApprovalMatrixWorld.cs` | 0 | 1 | 1 |
| `tests/Ecp.Integration.Tests/ClassificationScenarios.cs` | 0 | 1 | 9 |
| `tests/Ecp.Integration.Tests/CustomFieldScenarios.cs` | 0 | 0 | 2 |
| `tests/Ecp.Integration.Tests/LifecycleScenarios.cs` | 12 | 0 | 0 |
| `tests/Ecp.Integration.Tests/LifecycleScenarios2.cs` | 7 | 0 | 3 |
| `tests/Ecp.Integration.Tests/LifecycleScenarios3.cs` | 17 | 0 | 0 |
| `tests/Ecp.Integration.Tests/LifecycleScenarios4.cs` | 9 | 0 | 0 |
| `tests/Ecp.Integration.Tests/LifecycleWorld.cs` | 22 | 1 | 2 |
| `tests/Ecp.Integration.Tests/LightweightProviderTests.cs` | 3 | 0 | 0 |
| `tests/Ecp.Integration.Tests/MigrationCustomFieldScenarios.cs` | 1 | 0 | 0 |
| `tests/Ecp.Integration.Tests/MigrationScenarios.Commit.cs` | 2 | 0 | 0 |
| `tests/Ecp.Integration.Tests/MigrationScenarios.Core.cs` | 3 | 3 | 0 |
| `tests/Ecp.Integration.Tests/MigrationScenarios.cs` | 2 | 2 | 0 |
| `tests/Ecp.Integration.Tests/MigrationWorld.cs` | 2 | 3 | 1 |
| `tests/Ecp.Integration.Tests/NotificationScenarios.cs` | 0 | 0 | 2 |
| `tests/Ecp.Integration.Tests/NotificationScenarios2.cs` | 1 | 0 | 1 |
| `tests/Ecp.Integration.Tests/OnboardingScenarios.cs` | 0 | 18 | 1 |
| `tests/Ecp.Integration.Tests/PerformanceBaseline.cs` | 1 | 0 | 0 |
| `tests/Ecp.Integration.Tests/RegisterScenarios.cs` | 0 | 1 | 0 |
| `tests/Ecp.Integration.Tests/TestData.cs` | 0 | 2 | 0 |
| `tests/Ecp.Tests.Common/TestSupport.cs` | 1 | 0 | 0 |
| `tests/Ecp.UnitTests/AccessEvaluatorTests.cs` | 0 | 2 | 19 |
| `tests/Ecp.UnitTests/ApprovalMatrixDefinitionTests.cs` | 0 | 6 | 0 |
| `tests/Ecp.UnitTests/ApprovalMatrixResolutionTests.cs` | 0 | 5 | 0 |
| `tests/Ecp.UnitTests/CustomFieldTests.cs` | 6 | 0 | 0 |
| `tests/Ecp.UnitTests/IdentityRulesTests.cs` | 0 | 0 | 3 |
| `tests/Ecp.UnitTests/MigrationDomainTests.cs` | 4 | 0 | 0 |
| `tests/Ecp.UnitTests/MigrationMappingTests.cs` | 4 | 1 | 0 |
| `tests/Ecp.UnitTests/OnboardingRulesTests.cs` | 0 | 14 | 5 |
| `tests/Ecp.UnitTests/StudioModelTests.cs` | 2 | 0 | 0 |
| `tests/Ecp.UnitTests/TenancyAndContractDomainTests.cs` | 5 | 2 | 0 |
| `tests/Ecp.UnitTests/WorkflowDefinitionTests.cs` | 1 | 0 | 0 |

All are fixture data for the reference pack (state codes in lifecycle scenarios, `PACKAGING`/`ENERGY` categories in access and register scenarios, role codes in access scenarios). **Violates? No**, with the coverage note above.

## 9. Occurrence register: packs, samples, configuration, tools

### 9.1 Solution Pack (`templates/`, `sample-data/`)

Permitted (PRT-001). Not violations. `sample-data/demo-*.json` is the demonstration seed of the JBS pack; the Host project embeds four of those files (`src/Ecp.Host/Ecp.Host.csproj:30`-`33`) for Demonstration mode only, which is the one place a pack's sample data is compiled into a platform assembly (Low).

| Location | Term | Reason |
|---|---|---|
| `sample-data/demo-organisation-structure.json:12` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/demo-organisation-structure.json:16` | JBS | Demonstration seed of the JBS pack |
| `sample-data/demo-organisation-structure.json:16` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/demo-users-and-roles.json:77` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/demo-users-and-roles.json:91` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/demo-users-and-roles.json:143` | Group Head (of) Procurement | Approver group key of the reference workflow |
| `sample-data/demo-users-and-roles.json:152` | Group Head (of) Procurement | Approver group key of the reference workflow |
| `sample-data/demo-users-and-roles.json:152` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/demo-users-and-roles.json:158` | Group Head (of) Procurement | Approver group key of the reference workflow |
| `sample-data/demo-users-and-roles.json:167` | Group Head (of) Procurement | Approver group key of the reference workflow |
| `sample-data/demo-users-and-roles.json:167` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/demo-users-and-roles.json:311` | Procurement | Demonstration seed of the JBS pack |
| `sample-data/jbs-procurement-solution-pack.json:2` | JBS | Pack identity and description |
| `sample-data/jbs-procurement-solution-pack.json:2` | Procurement | Pack identity and description |
| `sample-data/jbs-procurement-solution-pack.json:8` | Qlik | Pack identity and description |
| `templates/packs/jbs-procurement/notifications/notification-policy.json:3` | JBS | Reference workflow / policy description |
| `templates/packs/jbs-procurement/notifications/notification-policy.json:3` | Procurement | Reference workflow / policy description |
| `templates/packs/jbs-procurement/pack.json:2` | JBS | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:2` | Procurement | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:3` | JBS | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:3` | Procurement | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:7` | Group Head (of) Procurement | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:7` | Procurement | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:8` | Qlik | Pack identity and description |
| `templates/packs/jbs-procurement/pack.json:15` | JBS | Pack identity and description |
| `templates/packs/jbs-procurement/workflows/contract-lifecycle.json:5` | JBS | Reference workflow / policy description |
| `templates/packs/jbs-procurement/workflows/contract-lifecycle.json:5` | Procurement | Reference workflow / policy description |
| `templates/packs/jbs-procurement/workflows/contract-lifecycle.json:23` | Group Head (of) Procurement | Approver group key of the reference workflow |
| `templates/packs/jbs-procurement/workflows/contract-lifecycle.json:23` | Procurement | Reference workflow / policy description |
| `templates/packs/jbs-procurement/workflows/variation.json:5` | JBS | Reference workflow / policy description |
| `templates/packs/jbs-procurement/workflows/variation.json:5` | Procurement | Reference workflow / policy description |
| `templates/packs/jbs-procurement/workflows/variation.json:16` | Group Head (of) Procurement | Approver group key of the reference workflow |
| `templates/packs/jbs-procurement/workflows/variation.json:16` | Procurement | Reference workflow / policy description |

### 9.2 Configuration (sample) (`sample-data/*approval-matrix*`)

These two files describe themselves as not part of the JBS default; the JBS mention is a disclaimer. No violation.

| Location | Term |
|---|---|
| `sample-data/synthetic-approval-matrix-advanced.json:7` | JBS |
| `sample-data/synthetic-approval-matrix-advanced.json:7` | Procurement |
| `sample-data/synthetic-generic-value-approval-matrix.json:7` | JBS |
| `sample-data/synthetic-generic-value-approval-matrix.json:7` | Procurement |

### 9.3 Configuration (`src/Ecp.Host/appsettings.json`)

No named-term hit. One lifecycle name (P-08); the locale defaults on lines 11 to 14 of the same file are R-03:

* `src/Ecp.Host/appsettings.json:23`: `"Draft"` (P-08, Yes)

`deploy/` and `.github/` contain no occurrence of any term.

### 9.4 Migration Sample (`migration-samples/`, `tools/`)

Permitted. `migration-samples/` has no named-term hit; it carries JBS category and state vocabulary as data:

| Location | Match | Kind |
|---|---|---|
| `migration-samples/contracts-import-custom-fields.csv:2` | `Packaging` | category |
| `migration-samples/contracts-import-custom-fields.csv:3` | `Packaging` | category |
| `migration-samples/contracts-import.csv:2` | `Packaging` | category |
| `migration-samples/contracts-import.csv:3` | `Energy` | category |
| `migration-samples/status-lookup-map.csv:8` | `Packaging` | category |
| `migration-samples/status-lookup-map.csv:9` | `Flexible Packaging` | category |
| `migration-samples/status-lookup-map.csv:10` | `Energy` | category |
| `migration-samples/status-lookup-map.csv:11` | `Electricity` | category |

| Location | Term | Reason |
|---|---|---|
| `tools/migration-demo.py:100` | Medius | Source-system value of the scripted migration demonstration |
| `tools/migration-demo.py:105` | Medius | Source-system value of the scripted migration demonstration |

## 10. Occurrence register: documentation

Documentation describes the reference configuration and the confirmed decisions, so the names are expected. **Violates? No** for every row; a document that presents a JBS rule as a platform rule is Low drift and is called out below. Per file with every line number.

| File | Terms and lines |
|---|---|
| `.claude/skills/contract-domain-review/SKILL.md` | Group Head (of) Procurement: 27; JBS: 27; Procurement: 27, 31 |
| `.claude/skills/migration-review/SKILL.md` | Medius: 11 |
| `.claude/skills/no-code-config-review/SKILL.md` | Group Head (of) Procurement: 26; JBS: 26; Procurement: 26 |
| `.claude/skills/portability-review/SKILL.md` | DocuSign: 3, 14; JBS: 3, 14, 15, 28; Medius: 3, 14; Qlik: 3, 14 |
| `CLAUDE.md` | JBS: 13, 20 |
| `PACKAGE_MANIFEST.md` | DocuSign: 97; JBS: 126; Medius: 111; Procurement: 126; Qlik: 101 |
| `README.md` | Group Procurement: 3; JBS: 3; Procurement: 3 |
| `TASKS.md` | DocuSign: 25, 170; Group Head (of) Procurement: 25, 31, 167; JBS: 25, 31, 106, 107, 121, 167, 199, 201, 266; Medius: 138; Procurement: 25, 31, 167, 266; Qlik: 15, 172, 212 |
| `decisions/ADR-0002-metadata-extensibility.md` | Qlik: 27 |
| `decisions/ADR-0018-migration-slice-design.md` | Procurement: 20 |
| `decisions/ADR-0019-classification-privileged-access-and-elevation.md` | Procurement: 12 |
| `decisions/ADR-0020-lifecycle-engine-and-workflow-as-configuration.md` | Group Head (of) Procurement: 17; JBS: 7, 24; Procurement: 17 |
| `decisions/ADR-0021-notifications-reminders-and-dashboards.md` | Qlik: 17 |
| `decisions/ADR-0022-approval-matrix-engine.md` | DocuSign: 7; Group Head (of) Procurement: 7; JBS: 7; Procurement: 7 |
| `decisions/ADR-0023-configurable-metadata-typed-search-and-studio.md` | JBS: 7, 24, 33 |
| `docs/ACCESS_AND_IDENTITY.md` | Procurement: 40 |
| `docs/APPROVAL_MATRIX.md` | DocuSign: 11; Group Head (of) Procurement: 11; JBS: 3, 9, 11; Procurement: 11, 69 |
| `docs/ARCHITECTURE.md` | JBS: 118, 130, 154; Procurement: 130; Qlik: 171 |
| `docs/DOCUMENT_INDEX.md` | DocuSign: 38; JBS: 73; Procurement: 73; Qlik: 38 |
| `docs/DOMAIN_MODEL.md` | JBS: 205, 222; Procurement: 212 |
| `docs/EXPORTS.md` | Procurement: 7; Qlik: 3, 25 |
| `docs/IMPLEMENTATION_PLAN.md` | DocuSign: 25, 110, 111, 167; JBS: 8, 21, 62, 95, 100, 120, 167, 184, 192; Medius: 105; Qlik: 125 |
| `docs/LIFECYCLE_AND_NOTIFICATIONS.md` | Group Head (of) Procurement: 13, 65; JBS: 9, 10, 101; Procurement: 9, 10, 13, 65, 72, 87, 89, 101; Qlik: 22, 105 |
| `docs/MIGRATION.md` | Procurement: 9, 117 |
| `docs/PERFORMANCE.md` | Procurement: 25, 27, 28, 29, 30, 31, 32, 34, 46, 48, 49, 50, 51, 52, 53, 55 |
| `docs/REPOSITORY_REVIEW.md` | DocuSign: 60, 81, 140, 164, 172, 194, 210; Group Head (of) Procurement: 140; JBS: 59, 65, 67, 81, 96, 140, 151, 164, 182, 183; Medius: 64, 80, 88; Procurement: 59, 81, 93, 140, 168; Qlik: 60, 117, 173 |
| `docs/SECURITY.md` | DocuSign: 159; Procurement: 136; Qlik: 159 |
| `docs/TESTING.md` | JBS: 44, 345; Procurement: 153, 154 |
| `docs/THREAT_MODEL.md` | DocuSign: 17, 159, 177; Qlik: 12, 102, 163 |
| `docs/USER_GUIDE_LIFECYCLE.md` | Group Head (of) Procurement: 26; JBS: 3; Procurement: 3, 26, 48, 73 |
| `prompts/01-INITIAL-BUILD-PROMPT.md` | Group Procurement: 5; JBS: 5; Procurement: 5 |
| `requirements/01-authoritative/CONFIRMED_DECISIONS.md` | DocuSign: 8, 43, 45, 46; Group Head (of) Procurement: 47; JBS: 3, 13, 43, 48, 54; Procurement: 3, 30, 41, 43, 47, 91, 92, 94; Qlik: 13 |
| `requirements/01-authoritative/MASTER_BUILD_INSTRUCTIONS.md` | DocuSign: 95; Group Procurement: 5; JBS: 5, 43, 90, 129, 170; Procurement: 5, 43, 90, 129; Qlik: 101 |
| `requirements/02-reference/GLOSSARY.md` | DocuSign: 43; Group Procurement: 11; JBS: 11, 62, 64; Medius: 15, 46; Procurement: 11, 62 |
| `requirements/02-reference/README.md` | JBS: 10 |
| `requirements/02-reference/permission-catalogue.csv` | Procurement: 3, 13, 18, 63; Qlik: 55 |
| `requirements/02-reference/reference-lookups.csv` | DocuSign: 29; Medius: 24; Procurement: 107 |
| `requirements/02-reference/role-permission-matrix.csv` | Procurement: 1, 3, 70 |
| `requirements/03-data-dictionary/README.md` | Medius: 9 |
| `requirements/03-data-dictionary/contracts-extended.csv` | Medius: 6, 7, 8, 9, 14, 17, 22 |
| `requirements/03-data-dictionary/identity.csv` | Group Head (of) Procurement: 21, 30; JBS: 30; Procurement: 21, 30 |
| `requirements/03-data-dictionary/lifecycle-records.csv` | Group Head (of) Procurement: 23; Procurement: 23 |
| `requirements/03-data-dictionary/migration-entities.csv` | Medius: 6 |
| `requirements/03-data-dictionary/org-structure.csv` | JBS: 12, 13, 15, 16 |
| `requirements/03-data-dictionary/suppliers.csv` | Medius: 8 |
| `requirements/04-workflows/WORKFLOW_SPEC.md` | DocuSign: 17; Group Head (of) Procurement: 47; JBS: 5, 47; Procurement: 5, 47, 89 |
| `requirements/05-security/ACCESS_CONTROL_SPEC.md` | Procurement: 46, 59, 65, 69, 84, 92; Qlik: 97 |
| `requirements/05-security/RETENTION_AND_CLASSIFICATION_SPEC.md` | JBS: 20 |
| `requirements/05-security/roles-and-permissions.csv` | Procurement: 7 |
| `requirements/06-design/ACCESSIBILITY_AND_LOCALISATION.md` | JBS: 28 |
| `requirements/07-integrations/ADAPTER_CATALOGUE.md` | DocuSign: 10; JBS: 9; Medius: 16; Qlik: 9 |
| `requirements/07-integrations/API_AND_WEBHOOKS_SPEC.md` | Qlik: 38 |
| `requirements/07-integrations/ESIGNATURE_PROVIDER_SPEC.md` | DocuSign: 1, 3, 9, 10, 14, 29, 39, 44, 48; Group Head (of) Procurement: 3, 12; JBS: 3, 12; Procurement: 3, 12 |
| `requirements/07-integrations/IDENTITY_SPEC.md` | Qlik: 37 |
| `requirements/07-integrations/README.md` | DocuSign: 9; JBS: 10; Qlik: 10 |
| `requirements/07-integrations/REPORTING_QLIK_SPEC.md` | JBS: 3; Qlik: 1, 3, 7, 19, 38 |
| `requirements/08-portability/CONFIGURATION_PACKAGE_SPEC.md` | JBS: 21, 69, 75; Procurement: 21, 69, 75 |
| `requirements/08-portability/CONFIGURATION_STUDIO_SPEC.md` | DocuSign: 99; Group Head (of) Procurement: 99; JBS: 99, 107; Procurement: 93, 99; Qlik: 106 |
| `requirements/08-portability/PORTABILITY_ACCEPTANCE_CRITERIA.md` | DocuSign: 7; JBS: 7, 8; Medius: 7; Qlik: 7 |
| `requirements/09-migration/MEDIUS_IMPORT_PROFILE.md` | Medius: 1, 3 |
| `requirements/09-migration/MIGRATION_REQUIREMENTS.md` | Medius: 10 |
| `requirements/09-migration/MIGRATION_SUPPLEMENT.md` | Medius: 3, 87 |
| `requirements/10-solution-packs/SOLUTION_PACK_SPEC.md` | JBS: 3; Procurement: 3 |
| `requirements/12-operations/SETUP_AND_READINESS_SPEC.md` | JBS: 38; Procurement: 38 |

### 10.1 Documentation vocabulary counts (lifecycle / category / role) per file

| File | Lifecycle | Category | Role |
|---|---:|---:|---:|
| `.claude/skills/contract-domain-review/SKILL.md` | 1 | 0 | 2 |
| `.claude/skills/no-code-config-review/SKILL.md` | 1 | 0 | 0 |
| `.claude/skills/security-review/SKILL.md` | 0 | 0 | 1 |
| `.claude/skills/tenant-isolation-review/SKILL.md` | 0 | 1 | 0 |
| `TASKS.md` | 2 | 0 | 1 |
| `decisions/ADR-0018-migration-slice-design.md` | 0 | 0 | 1 |
| `decisions/ADR-0019-classification-privileged-access-and-elevation.md` | 0 | 0 | 1 |
| `decisions/ADR-0020-lifecycle-engine-and-workflow-as-configuration.md` | 1 | 0 | 0 |
| `decisions/ADR-0021-notifications-reminders-and-dashboards.md` | 1 | 0 | 0 |
| `docs/ACCESS_AND_IDENTITY.md` | 0 | 0 | 1 |
| `docs/APPROVAL_MATRIX.md` | 0 | 0 | 1 |
| `docs/ARCHITECTURE.md` | 0 | 1 | 0 |
| `docs/DOMAIN_MODEL.md` | 15 | 0 | 2 |
| `docs/EXPORTS.md` | 0 | 0 | 1 |
| `docs/LIFECYCLE_AND_NOTIFICATIONS.md` | 9 | 0 | 4 |
| `docs/MIGRATION.md` | 0 | 1 | 2 |
| `docs/REPOSITORY_REVIEW.md` | 4 | 0 | 2 |
| `docs/SEARCH_AND_VIEWS.md` | 1 | 0 | 0 |
| `docs/SECURITY.md` | 0 | 0 | 1 |
| `docs/TESTING.md` | 2 | 1 | 2 |
| `docs/THREAT_MODEL.md` | 1 | 0 | 0 |
| `docs/USER_GUIDE_LIFECYCLE.md` | 3 | 0 | 2 |
| `requirements/01-authoritative/CONFIRMED_DECISIONS.md` | 2 | 0 | 7 |
| `requirements/02-reference/permission-catalogue.csv` | 0 | 0 | 5 |
| `requirements/02-reference/reference-lookups.csv` | 11 | 0 | 3 |
| `requirements/02-reference/role-permission-matrix.csv` | 0 | 0 | 3 |
| `requirements/03-data-dictionary/identity.csv` | 0 | 0 | 3 |
| `requirements/03-data-dictionary/lifecycle-records.csv` | 0 | 0 | 1 |
| `requirements/04-workflows/WORKFLOW_SPEC.md` | 15 | 0 | 2 |
| `requirements/04-workflows/guard-vocabulary.csv` | 4 | 0 | 0 |
| `requirements/04-workflows/status-transitions-supplemental.csv` | 17 | 0 | 0 |
| `requirements/04-workflows/status-transitions.csv` | 11 | 0 | 0 |
| `requirements/05-security/ACCESS_CONTROL_SPEC.md` | 0 | 0 | 9 |
| `requirements/05-security/RETENTION_AND_CLASSIFICATION_SPEC.md` | 1 | 0 | 0 |
| `requirements/05-security/roles-and-permissions.csv` | 0 | 0 | 2 |
| `requirements/07-integrations/ESIGNATURE_PROVIDER_SPEC.md` | 1 | 0 | 1 |
| `requirements/08-portability/CONFIGURATION_STUDIO_SPEC.md` | 0 | 0 | 2 |

### 10.2 Documentation drift (Low)

* `docs/ARCHITECTURE.md`, `docs/DOMAIN_MODEL.md` and `requirements/` present the twelve roles, the Legal review and the register authorisation as the platform's model. That is accurate to the code, and it is the same statement the portability criteria forbid (P-02b, P-04, R-05). The documents should say which parts are platform capabilities and which are the reference pack.
* `TASKS.md` marks WFE-02 and LIF-01 as done for "the JBS pack" and records a second pack as open (PKM-04); no document says that a `blank` organisation currently receives the JBS lifecycle (P-01).

## 11. Recommended order of work (not started)

1. **P-01**: resolve the pack from the organisation; add the test that a `blank` organisation does not receive JBS states. Smallest change, highest value, and it makes PRT-002 testable.
2. **P-09**: widen ARC-010 so the rest cannot regress; it will fail on P-02 to P-07 until they are fixed, so land it with the fixes or with an explicit, dated allow-list.
3. **P-03 / PKM-01 / CFG-04**: pack loader, then a second pack (PKM-04) and the PRT-002 end-to-end test.
4. **P-02a**, then **P-02b** with an ADR reconciling decision D1 and PRT-013.
5. **P-05, P-06, P-07, P-08**: labels and defaults from the definition and terminology (CFG-02).
6. **P-04**: feature flags that actually switch capabilities.
7. **R-01 to R-04, P-10**: neutral naming and pack-owned fields while they are cheap to change.

## 12. Reproducing this audit

```
python3 -I tools/check-repo.py                         # repository consistency (this document is link-checked)
grep -rniE "jbs|procurement|medius|qlik|docu ?sign" src # the named-term scan of platform code
```

The occurrence tables were generated from the scan output, not typed. A re-run after the fixes should show section 5 reduced to the generic words only.

