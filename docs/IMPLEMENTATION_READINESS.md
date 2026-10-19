# Implementation readiness: gap register and closure report

Status of this document: **the register as confirmed by reading the code, and what the Implementation Readiness Gap Closure slice changed.** It is evidence for a company-implementation rehearsal. It is not an approval to go live: production approval is external governance (CLAUDE.md, working rules).

The objective of the slice: a trained non-developer Business Administrator can perform the supported company-implementation journey through the application's pages without editing JSON, running SQL, changing source code or using Git. The gaps came from `docs/IMPLEMENTATION_PLAYBOOK.md`, Appendix D (G-01 to G-30). Each was re-checked against the code at the start of this slice; nothing was implemented for a gap that could not be confirmed.

## 1. How to read the register

Status values:

| Status | Meaning |
|---|---|
| **Fixed** | Changed in this slice. Tests named in the last column; where a SQL Server run exists it is named. |
| **Fixed (documentation)** | The behaviour is a technical task or a limit; a guide now says so plainly. |
| **Partly confirmed** | Some of the playbook's statement is true, some not; what is true is recorded. |
| **By design** | The behaviour is intended; the reason is recorded. |
| **Deferred** | Confirmed, not fixed in this slice; a workaround and a long-term resolution are recorded. Deferred items that block a company rehearsal are called out in section 4. |
| **Not reproducible** | Could not be reproduced from the code. |

Verification labels: **Verified** = a passing test named here. **Not Verified** = no test or no environment. SQLite runs are labelled `SqliteNotSqlServerProof`; the SQL Server column says whether the real-database run passed in this workspace.

## 2. Register

Columns for every row: page or service; reproduction; users affected; environments; severity; workaround; long-term resolution; required test; status and evidence.

### 2.1 Fixed in this slice

| ID | Page / service | Reproduction (as confirmed) | Users | Environments | Severity | Workaround before | Resolution and evidence |
|---|---|---|---|---|---|---|---|
| **G-01** | Configuration, Terminology, Capabilities, Regional, Studio pages; `ConfigurationService` | A page saved the draft as the clicking user then published, so the publisher was always the last editor and second-person publication always refused. Rollback skipped the rule. | Every Business Administrator in Production | Production (second person on) | High | None for form pages | **Fixed.** Content-hash-aware saves (an unchanged save changes nothing); explicit Submit; a submission holds the hash; any edit voids it; the hash must match at publication; creator, last editor, submitter, publisher and times are kept; self-publication refused; refusals audited; rollback now follows the rule and needs a recent sign-in and a reason and creates a new release. Tests: `ConfigurationGovernanceScenarios`, `ConfigurationRollbackKindsScenarios` (every kind), `ConfigurationReviewUiTests`; SQL Server variants passed. |
| **G-02** | `ITenantProvisioningService`; new `IOrganisationCreation` | No screen added an organisation or widened a single-organisation tenant. | Tenant Administrators | All | High | Developer | **Fixed.** `/Admin/Organisations`, `/New`, `/Setup`: Check then Create, field-level problems, duplicate name and key refused, structure rows, initial administrator scoped to the new organisation, pack recorded, nothing published, widen once with a reason (never narrowed), reset of an unused set-up, step-up, audit, tenant isolation. Tests: `OrganisationCreationScenarios` (6), `OrganisationAndGroupUiTests`; SQL Server variants passed; the no-code rehearsal steps 1-4. |
| **G-03** | Workflow, approval matrix, notification policy editing | Edited only as JSON in a text box. | Business Administrators | All | High | Developer / pack author | **Fixed.** `/Admin/Forms/Edit`: one schema-driven form editor for the four kinds; closed vocabularies; add / remove / move up / move down buttons; validation placed on the field with an error summary; preview and impact analysis; ordinary draft, second-person publication, history and rollback. Raw JSON is a read-only diagnostic in Production. Every shipped pack document round-trips through its form with its meaning unchanged. Tests: `FormEditorTests` (unit), `FormEditorScenarios` (SQLite and SQL Server), `FormEditorUiTests`, rehearsal steps 9-11. |
| **G-04** | Approver groups | Groups existed only as a text key on a role assignment. | Organisation Administrators | All | High | Developer | **Fixed.** `/Admin/ApproverGroups` registry: key, description, organisation scope, delegation eligibility, primary and backup members with dates, preview of who can approve before and after, in-use and last-member protections, retire/reactivate, no deletion while referenced, audit. Tests: `ApproverGroupScenarios` (SQLite and SQL Server), `OrganisationAndGroupUiTests`. |
| **G-19** | Regional page | Publish and rollback needed no recent sign-in, unlike terminology and capabilities. | Business Administrators | All | Low | None | **Fixed.** Publish and rollback on Regional, Studio, Onboarding review and Configuration pages require a recent sign-in. Test: `ConfigurationReviewUiTests`, `ConfigurationGovernanceScenarios`. |
| **G-20** | Organisation creation | Only a prefix could be entered; the format was fixed. | Tenant Administrators | All | Medium | None | **Fixed** for new organisations: separator and digit count are fields on `/Admin/Organisations/New`, validated field by field. Existing organisations keep the format they have (no screen changes it; **Deferred**). Test: `OrganisationCreationScenarios`. |
| **G-21** | Onboarding validation | A structure key with a full stop passed the check and failed at publish. | Business Administrators | All | Medium | Avoid full stops | **Fixed.** The check uses the same key shape as publication. Test: `OrganisationCreationScenarios` (structure rows) and `OnboardingRulesTests`. |

### 2.2 Verified or partly addressed, with a limit that remains

| ID | Page / service | What was confirmed | Status | Remaining limit and evidence |
|---|---|---|---|---|
| **G-05** | Onboarding access | A Tenant Administrator may start onboarding but holds no organisation-level configuration right by design (a tenant-level right is not an organisation-level one). | **By design** | The organisation wizard names an Organisation Administrator and that person drafts and submits the rest; the set-up checklist names who does each step. Test: `OrganisationCreationScenarios`; rehearsal steps 2-4. |
| **G-07** | Packs | Signing needs the pack tool and a trusted key. Packs cannot be signed inside the application, deliberately. | **By design; journey documented and guarded** | `docs/PACK_IMPLEMENTATION_GUIDE.md` separates the Business Administrator, Pack Publisher, System Administrator and key custody; the Packs page says who does what; an architecture test proves the application creates, holds and asks for no private key (`No_private_signing_key_is_created_held_or_asked_for_by_the_web_application_or_any_module`). Real signing and key custody remain outside the product (**Not Verified** in any customer environment). |
| **G-25** | Documents / migration | No real scanner is integrated; skipped scans are held. | **Gate implemented; real scanner Deferred** | Scan attempts recorded and audited; fail-closed; bounded repeat-safe retry job; infected files quarantined; admin page `/Admin/Documents/Scanning`; Production readiness check R-DOC-01 fails without a real connected scanner. A provider adapter does not exist (**Future, Not Verified**). Attachments cannot be used in Production until one is supplied. Tests: `DocumentScanningScenarios` (SQLite and SQL Server), `DocumentScanningUiTests`. See `docs/DOCUMENT_SCANNING.md`. |
| **G-13** | Installation | There was no installation guide. | **Fixed (documentation)** | `docs/INSTALLATION_GUIDE.md`. Not Verified by a non-developer on a clean machine. |
| **G-15** | Container | Fixed user id undocumented. | **Fixed (documentation)** | Stated in the installation guide. |

### 2.3 Confirmed and deferred

| ID | Area | Severity | Workaround | Long-term resolution | Required test when fixed |
|---|---|---|---|---|---|
| **G-06** | Users: invitation only records | Medium | Tell each person; or use the identity provider's own onboarding | Send the invitation through the mail provider; bulk invite | Integration: invitation produces one outbox message |
| **G-08** | Onboarding pack step records the choice but does not assign | Medium | Assign the pack from `/Admin/Packs/Assign` (the organisation wizard's checklist names the step) | Assign from the wizard or relabel the step | UI: choosing a pack in onboarding assigns it |
| **G-09** | Setup: mode, binding and token are environment settings | Medium | Installation guide (System Administrator) | Guided pre-check page | Host test of the pre-check |
| **G-10** | SSO page does not show redirect addresses | Medium | Build them as the installation guide describes | Show the exact addresses | UI: addresses shown after saving |
| **G-11** | Key ring loss: no recovery procedure; no fallback to the database mirror | Medium | Back up the key ring with the data directory | Document and add to the backup set | Recovery test with a lost key ring |
| **G-12** | E-mail: the setup page does not set the delivery provider; the readiness check can pass while mail fails; port 465 unsupported | **High** | Set `Notifications__EmailProvider` and the mail settings as a technical task; test-send before go-live | Make the page authoritative and the readiness check real | Integration: wizard settings change delivery; readiness fails on a bad SMTP setting |
| **G-14** | Key Vault / DPAPI notations accepted but unresolved (blocks only in Production) | Medium | Use `env:` or `file:` references | Reject or implement | Unit: unsupported notation refused at save |
| **G-16** | No least-privilege database login script | Medium | The `ecp_runtime` role exists; a DBA creates the login | Provide the grants script | SQL Server test under the restricted login |
| **G-17** | Sign-in re-check minutes fixed at ten in some places | Low | None needed | Wire the setting | Unit |
| **G-22** | Contract types recorded, used by nothing | Low | Help text says so | Use the list or hide it | UI |
| **G-23** | Migration cannot set owners, legal entity, business unit, site | Medium | Set by hand after load | Add mapping targets | Migration scenarios |
| **G-24** | No mapping designer; CSV profile only | Medium | Coached first profile; the guide describes it | Guided mapping screen | UI |
| **G-26** | Freeze window entered in UTC | Low | State UTC in the guide | Accept local time with the zone shown | UI |
| **G-27** | No backup schedule | Medium | The organisation's scheduler runs the backup command; the dashboard warns of a stale backup (R-BKP-01) | A simple schedule | Operations scenarios |
| **G-28** | Counts differ between documents | Low | This document and the status documents were updated for this slice | One generated reference | Repo check |
| **G-29** | Escalation of an unacknowledged reminder is recorded only; no test-send | Low | Test with a test contract | Test-send; escalation | Notification scenarios |
| **G-30** | Business Administrators cannot see operations health | Low | Ask the System Administrator | Read-only business summary | UI |

### 2.4 By design

| ID | Why |
|---|---|
| **G-18** | Terminology keys are a closed catalogue so that every page, notification and export can be translated without code; adding a key means adding a page that uses it. |
| **G-05** | See 2.2. |

### 2.5 Not reproducible

None. Every gap in Appendix D was reproduced in the code except where the table says "Partly confirmed" (G-11: the database-mirror fallback; G-14: only the Production block).

## 3. What the slice added beyond the register

| Item | Result | Evidence |
|---|---|---|
| Configuration Reviews inbox (`/Admin/Reviews`) | Implemented: waiting for me / mine / returned; plain-language summary of what would change; approve and publish; return or reject with a comment; the author never sees the approve button | `ConfigurationReviewUiTests` |
| Rollback governance for every kind | Implemented: permission, recent sign-in, reason, second person where required, new release, immutable history, audit | `ConfigurationRollbackKindsScenarios` (every kind of the second business's configuration) |
| Organisation set-up checklist | Implemented, read from what is published and drafted | `OrganisationCreationScenarios` |
| Eight click-by-click guides | Written (section 5) | Not Verified by an unaided non-developer |
| 23-step no-code rehearsal (was 20 steps) | **Passed over real HTTP on the lightweight provider and on real SQL Server** | `docs/NO_CODE_REHEARSAL.md` |

## 4. What still stands between this product and a company-implementation rehearsal

1. **G-12 (closed as readiness, section 8):** email is configured once on `/Admin/Operations/Email` and the setup email stage, and readiness is proved by a test message; live delivery to a customer's mail service is still Not Verified.
2. **G-25 (boundary closed, section 8):** no real scanner exists; attachments cannot be released in Production.
3. **Identity provider and secrets** (G-09, G-10, G-14, G-16): technical tasks that the System Administrator performs from the installation guide.
4. **Backups** are proved by the SQL Server recovery tests; the rehearsal opens the page but does not repeat a restore.
5. **R-DOC-01 in Production (found while writing the guides; read from the code, not run in Production mode).** With no real scanner connected, the platform check is Unhealthy; `/health/ready` should then answer 503, and the setup wizard treats an Unhealthy platform check as blocking, so "Finish setup" would be refused in Production. This is the intended fail-closed result, and it is the same blocker as G-25.
6. **The guides have not been followed by an unaided non-developer**; the rehearsal proves the pages work, not that the wording is sufficient.
7. **Production approval** is external governance and is not declared anywhere.

## 5. Guides created

`docs/INSTALLATION_GUIDE.md`, `docs/SETUP_WIZARD_GUIDE.md`, `docs/ORGANISATION_ONBOARDING_GUIDE.md`, `docs/BUSINESS_ADMIN_GUIDE.md`, `docs/PACK_IMPLEMENTATION_GUIDE.md`, `docs/MIGRATION_EXECUTION_GUIDE.md`, `docs/UAT_AND_CUTOVER_GUIDE.md`, `docs/OPERATIONS_QUICK_GUIDE.md`. Each screen's entry lists the navigation path, who may use it, the permission, every field, recommended and example input, validation, the consequence of a wrong value, what save / check / submit / publish do, the success result, common errors and evidence. Technical interventions are labelled.

## 6. Decisions and assumptions recorded

See `decisions/ADR-0037-implementation-readiness-no-code-journey.md` and `requirements/01-authoritative/CONFIRMED_DECISIONS.md` (D13).

- Document scanning administration reuses `notifications.administer` (background-job status and retry), scoped to the organisations the person administers, rather than adding a permission. Reversible: a dedicated permission can replace it with a catalogue change.
- An organisation is never deleted; "reset set-up" discards open drafts and ends the structure only while nothing has been recorded against it. Reversible.
- The organisation wizard assigns the pack and records the choice; the drafts an organisation's administrator is entitled to make are made by that administrator, because a Tenant Administrator holds no organisation-level configuration right by design.
- Terminology tokens such as `[[organisation]]` are expanded on every new page.

## 7. Findings recorded while writing and checking the guides

Each was read from the code; none was run in a browser. They are limits or contradictions, recorded rather than silently resolved.

- **Who registers a trusted publisher key.** The permission `packs.trust.manage` is held by the Tenant Administrator only (role matrix). The Packs page said a system administrator registers the key. The page text is corrected in this slice; the System Administrator route is host configuration (`Ecp__Packs__TrustFile`), which works before any tenant exists. No permission was changed.
- **Document scanning administration.** The System Administrator role does not hold `notifications.administer`, so cannot open `/Admin/Documents/Scanning`; the Organisation Administrator can. Nothing notifies anyone when a file is quarantined or a scan errors (the page and the dashboard show it). Deferred.
- **Skipped scans.** Only a Clean result releases a file unless `Documents:AllowSkippedScan` is true; it defaults to false and R-DOC-01 flags it.
- **Audit for the System Administrator.** The role matrix gives that role neither `audit.read` nor `audit.read_admin`; how it reads operations audit events on screen is Not Verified.
- **Time zone.** The shipped JBS pack proposes Australia/Brisbane in its regional profile; the guides' JBS-style examples use Australia/Sydney as the brief asked, and say so.
- **Harborline date format.** the month-first date format is not covered by a shipped migration sample; the guide tells the reader to confirm the date in the dry-run sample.
- **Playbook text that was stale** (batch routes, labels, the set-up wizard's tenancy help text, the reference-prefix help text, the SMTP provider setting) is corrected in the guides; the playbook carries a banner pointing to them.
- **Review buttons.** The on-screen labels are "Approve and publish", "Send back for changes", "Reject" and "Withdraw my submission".
- **Behaviours worth knowing.** Only people who have signed in once are offered as an initial administrator; after a step-up redirect the Create form comes back empty; Add / Move / Remove in the form editors change the screen only until Save draft; choosing a pack in the onboarding wizard does not assign it (G-08); the Organisation Administrator has no menu entry for the set-up checklist, so the Tenant Administrator passes on the link; the onboarding wizard is not among the 20 rehearsal steps.

## 8. Findings from the greenfield rehearsal (G-31 to G-37), and their closure

Found while writing `docs/IMPLEMENTATION_REHEARSAL.md` on 2026-10-18, most of them by driving a Production-mode host over HTTP. They were closed by the Company Rehearsal Enablement slice on 2026-10-19 (ADR-0038); the rehearsal document, section 9, has the evidence.

| ID | Finding | Severity | Status | How it was closed |
|---|---|---|---|---|
| **G-31** | The second-person rule was on only in Production and had no setting, so a Demo or Test rehearsal could not exercise it | High for rehearsals | **Fixed** | `Ecp:Governance:SecondPerson` (`Default` or `Enforced`). Production always enforces; Test can enforce the Production rule; Demo ignores the setting. The effective mode is shown on the setup report, `/Admin/Health` (check R-GOV-01) and the operations dashboard, and is recorded on configuration audit events and releases |
| **G-32** | Terminology tokens were shown raw in the setup wizard | Low | **Fixed** | Titles and messages expanded at their origin; a response guard replaces a leftover token with a neutral marker and logs a stable code; a render sweep over three packs asserts none reaches a reader |
| **G-33** | `/Setup` in Production behind a proxy that does not forward the scheme returned a bare 500 | Medium | **Fixed** (a real reverse proxy product is **Not Verified**) | Plain-language page with `ECP-NET-001` or `ECP-NET-002`, readiness check R-NET-01, a setup check, `docs/REVERSE_PROXY.md` |
| **G-34** | Signing a pack needed a repository checkout and the .NET SDK | Medium | **Fixed** locally; the CI step is Scaffolded until it has run on a runner | `ecp-pack` .NET tool package with key references, deterministic builds, a sanitised report and exit codes; `PackToolArtefactTests` installs that artefact alone |
| **G-35** | No aid for masking data and no way to tell masked from real | Medium | **Fixed as a checkpoint, not a detector** | Masked-data declaration (owner, source, method, fields, approval, expiry, permitted users) gates commit of a batch marked Masked Company Data in Test. The platform does not mask, detect or de-identify and never states the data is safe |
| **G-36** | Guides disagreed with the application and with each other | Low | **Fixed** | All implementation guides reconciled; `tools/check-docs.py` (in CI) fails when a guide names a page or permission that does not exist, repeats a retired statement or tells a business role to run a command |
| **G-37** | A Blocked pre-flight was recorded as a pass | Low | **Fixed** | One evidence-semantics rule (Ready = Passed; Ready with warnings = Passed only when accepted on the record; Blocked/Failed = Failed; Not Verified stays Not Verified); the rehearsal has 23 steps and judges steps 21 to 23 by it |
| **G-12** | Email setup did not equal live delivery | High | **Fixed as readiness; live delivery Not Verified** | One installation email configuration shared by setup and Administration; readiness is a connectivity probe plus a synthetic test message; unreachable is never Healthy; results go stale. SMTP verified only against a loopback test relay; Graph is a scaffold |
| **G-25** | No real scanner | High | **Boundary Implemented; real scanner still absent (Production Blocked)** | Provider configuration, provider-neutral self-test, Scanner page, labelled mock only in Demo/Test, acceptance on the record for masked-data rehearsals, alerts. No real scanner is claimed |

**What still stands between this product and Production** (unchanged by this slice): a real scanner adapter (R-DOC-01 stays Unhealthy in Production without one), a Production-mode rehearsal in a non-production environment with the customer's identity provider and mail relay, signed packs with a custodied key, a least-privilege database login, and one unaided run by a trained non-developer. Production approval is external governance and is not declared.
