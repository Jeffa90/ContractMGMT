# Guided administration: implementation status

Status: **Implemented in slices UX-1 to UX-10 and verified (final acceptance review 2026-10-29, section 5A; the three gaps it found were closed by UX-9 on 2026-10-30, section 5).** UX-10 (guided Fields, section 5B) followed on 2026-10-30. A moderated usability test and the items in section 6 remain open. Branch ux/guided-administration (from claude/stoic-volta-dvszm5 at commit 5cdf9f7; that branch is unchanged). Design: `docs/UX_REDESIGN.md`. Criteria: `docs/UX_ACCEPTANCE_CRITERIA.md`. Decisions: `decisions/ADR-0039-guided-administration-access-profiles.md`. Production approval is external governance and nothing here declares it.

Labels: **Implemented** (built and covered by a named automated test), **Partial** (built, with the stated gap), **Not built**, **Not Verified** (needs a person, a browser or an environment this work did not have). "SQLite" tests are provider-independent behaviour, not SQL Server proof; "SQL Server" means the same scenario ran on a real SQL Server 2022 container.

## 1. What each slice added

| Slice | Screens | Backend |
|---|---|---|
| UX-1 Navigation, Home, Administration | Short everyday navigation; Administration menu and landing page grouped in eight cards; Home with **+ New contract**, My Actions, My Contracts, Expiring Soon, Reviews Due; Actions and Reports hubs; "Support & emergency access log" | None (presentation over existing services) |
| UX-2 Access foundation | **My access** in plain English (what you can do, where, why; what you cannot and what would give it; details underneath) | New module `Ecp.Access` (tier 8, schema `access`); profile catalogue (8 profiles); ability catalogue (all 94 permissions, `abilities.csv`, enforced by a test); effective-access report from the real evaluator; atomic profile application with dry-run preview; provenance table `ProfileApplication` |
| UX-3 People & Access | **People** list; **Add a person / Change access** wizard (up to seven steps, review in plain English); person page with effective access, provenance and **End this access**; **Allow demonstration sign-in** for synthetic test people; **Preview as this user** with permanent banner and **Exit user preview** | `IPersonAccessService` (add person plus access in one transaction), `IPreviewService`, `PreviewSession` table, `ecp:preview` session claim re-validated on every request, `PreviewContext` and a database write guard, `ActorType.Preview`, audit actions `preview.started`, `preview.write_refused`, `preview.ended` and `access.test_person.created` |
| UX-4 First administrator | Set-up stage asks "what will this person do?" (five choices); **Set up my access** page with **Give me full Test-mode access** (explained first, TEST ONLY, removable) | `IAdministratorBootstrap`, `AssignmentSource.TestBootstrap`, audit actions `access.test_bootstrap`, `access.test_bootstrap.ended` and `access.bootstrap.setup` |
| UX-5 Onboarding | **Set-up checklist** (13 business-outcome items, Required/Recommended/Optional, computed from facts) above the existing draft sections; Publish blocked, with links, while a Required item is open | `IOnboardingReadiness`; packs may carry `onboardingRequirements` (data) in their solution-pack document, validated against a closed vocabulary; the platform insists only on organisation, review and publish |
| UX-6 Category ownership | **Categories and owners**: primary and backup per category, assign-all with a preview, bulk selection, search, "needs an owner" filter, owner role added in the same save | `ICategoryOwnership` |
| UX-7 Advanced | **Identity group mappings** (Advanced): in words, previewed in a sentence, closed safe role catalogue | `IGroupMappingAdministration` (requires `rolemappings.manage`, delegation policy, recent sign-in, no high ceiling, org-filtered); `GroupMappingPolicy.SafeRoles` enforced at the store as well |

| UX-10 Fields | **Fields** landing (Field, Type, Section, Used on, Required, Status; **+ Add field**, search, Edit, Preview, Retire/Reinstate, Version history); a guided **Add/Edit field** page (basic information, type picker, only that type's settings, choice editor, placement, standard and Advanced rules, preview, plain-English review, Save draft, Publish or Submit for publication); Retire and Version history pages; export option **Include the additional fields** | None to the field engine (ADR-0041). One contracts-module defect fixed: Create now applies the classification gate that Details, Edit, Save and the register already applied |

## 2. Security behaviour preserved

* Role codes, permission codes and the access evaluator are unchanged. Profiles only issue ordinary grants through the services that enforce the delegation policy, scope containment, ceiling rules, self-grant refusal and last-administrator protection (tests: delegation, self-grant, rollback, parity with the evaluator).
* Effective access is computed from real grants; the provenance table explains and never decides (test: deleting every provenance row changes no permission).
* Changing roles, scope, ceiling, ownership, approver membership or mappings needs a recent sign-in (tests: `Changing_access_needs_a_recent_sign_in` and the mapping and test-access equivalents).
* Test/Demo-only tools are refused **in the service** in Production, not only hidden: demonstration sign-in, Preview, Give me full Test-mode access (tests run against an installation whose mode is Production).
* Preview runs as the target's real access, never the administrator's; is read-only at the request layer and at the database layer (only audit and its own record may be written); is bounded to 30 minutes; is re-checked on every request; cannot cross a tenant; requires the previewer to be able to give everything the target holds; does not touch the target's account (an Invited person stays Invited).
* Full Test-mode access never grants System or Tenant Administrator, is audited, removable, and does not weaken self-approval prevention (evaluator test: a person holding every Test-mode role still cannot approve their own submission).
* Identity group mappings cannot carry administrator or approval roles, at the screen or at the store.
* Two new tables (`ProfileApplication`, `PreviewSession`) have row-level security; the upgrade manifest for 1.1.0 was regenerated and verifies.

## 3. Deviations from the design, recorded

1. **New audit actions** were added (`access.profile.applied`, `access.profile.ended`, `access.test_person.created`, `access.test_bootstrap`, `access.test_bootstrap.ended`, `access.bootstrap.setup`, `access.bootstrap.applied`, and the three `preview` actions) beyond the two the design listed; PPL-007's "no new ones" and SEC-001 are therefore met only for existing actions. The existing `role.granted` and `category.ownership_assigned` events are still written for every grant.
2. **Three new tables**, not one (SEC-009's "single new table"): `ProfileApplication`, `PreviewSession` and, from UX-9, `CategoryOwnershipPolicy` (the standing "own all current and future categories" instruction; ADR-0040).
3. **Pack requirements** live in the existing solution-pack document (`onboardingRequirements`), not a new pack kind; this avoids changing the pack format. A pack can raise or relax anything except the platform's three universal items.
4. **A tenant administrator alone cannot assign category ownership** (existing rule); the Test-mode shortcut and the ownership editor are used by an organisation administrator, and the screens say so in words instead of failing silently.
6. **Set-up has 15 stages**: UX-9 adds "Administrator access" (review and confirm) after notifications, so choosing access is no longer giving it. The first-administrator grants moved there from the organisation stage.
7. **Standing ownership supersedes Q-7** ("current categories only"): the owner decided on 2026-10-30 that "own all categories" means current and future (ADR-0040). Category authorisation is unchanged; a policy keeps ordinary assignments in step.
5. The Preview refusal page has no banner (it is not rendered through the layout); it names the previewed person and links back.

## 4. Criteria status

Evidence names are test classes under `tests/`. "Journey" is `GuidedAdministrationJourneyTests`.

| Area | Implemented | Partial or not built |
|---|---|---|
| **NAV** | NAV-001..005, 007..009 (UX-1 tests; breadcrumbs name the group and page for every page in the navigation catalogue, including the People pages); NAV-006 disclosure menu | NAV-001 lists no Documents entry because no cross-contract document list exists (UXG-09) |
| **HOME** | HOME-001..003, 006..008 (UX-1) | HOME-004 Setup & administration card: **Partial** (shown only to administrators and links to Administration, People & Access and My access; listing up to three open Required items is not built); HOME-001 "Recent contracts" **Not built** |
| **PRF** | PRF-001..005, 007, 008 (`AccessProfile*` scenarios, SQLite and SQL Server) | PRF-006 capability hiding: Reviewer and Approval Authority are marked unavailable when the step is off; admin-role non-hiding **Implemented**; PRF-009 "Other access" label: grants without provenance show their source text, not the literal words |
| **PPL** | PPL-003, 005, 008, 012 (wizard, Journey); PPL-011 Advanced links | PPL-001 search and paging **Not built**; PPL-002 "why am I asked this" disclosures **Not built** (hints are shown); PPL-004 Back preserves answers (hidden fields; not asserted by a test); PPL-006 sign-in return is built (answers travel in the address) but only the service rule is tested; PPL-009/010 unchanged existing behaviour |
| **EFF** | EFF-001..003, 005..008 (My access, person page, parity and catalogue tests) | EFF-002 is proved at ability level (nothing shown that the evaluator denies and the reverse for every world user); a per-permission differential is **Not built**; EFF-004 ended/future grants appear in the details table with their status, not a separate "Past and future access" list |
| **BOOT** | BOOT-001..003, 005..008 (set-up scenarios, `BootstrapAndOwnershipUiTests`, SQLite and SQL Server) | BOOT-004: **Implemented** by UX-9 as a standing policy (a category created later is owned automatically); BOOT-009: the page explains the separation-of-duties limit and the evaluator test proves it; an **Add a test colleague** button is **Not built** (use Add a person with demonstration sign-in and the Approval Authority profile) |
| **PRV** | PRV-002 (service), 003..009, 011..016, 018 in effect (`AccessProfile*` preview scenarios, `PeopleAccessUiTests`) | PRV-001 the control is on the person page only (not the list or the old Users page); PRV-003 banner is on every layout page, not the refusal page; PRV-010 the one GET that wrote (lazy installation of a reference notification policy or workflow) is handled; a full inventory sweep of every GET page is **Not Verified**; PRV-015 each end condition is checked on every request, expiry and disabling are tested, sign-out/mode change are not tested separately; PRV-017 **Not Verified** |
| **DSI** | DSI-001..003, 005, 006, 008 | DSI-004 the sign-in page empty-state sentence **Not built**; DSI-007 the Test person badge is in the People list only; DSI-009 switching an Invited person **Not built** (offered at creation only) |
| **ONB** | ONB-001..008 (`ReadinessScenarios`, `OnboardingHostTests`, SQLite and SQL Server) | ONB-009 the CSV sections are labelled "Draft sections" with an explanation, not "Bulk import (Advanced)"; ONB-010 plurals handled in the new text only |
| **OWN** | OWN-001..005, 007, 008, 010 (`BootstrapAndOwnershipUiTests`, services) | OWN-006 effective dates **Not built** (secured owner API has none); OWN-009 End uses a fixed reason, it does not ask for one |
| **MAP** | MAP-001, 002, 004, 005 (`Mappings_*` scenarios, SQLite and SQL Server; `MappingsAndChecklistUiTests`) | MAP-001: authorisation is in the new secured service; the trusted store remains for onboarding and set-up and now refuses privileged roles itself; MAP-003 keys appear under Details (**Partial**); MAP-006 mapped grants show "From an identity group" without naming the group; MAP-007 change-as-one-action **Not built**; MAP-008 existing tests |
| **ADM** | ADM-001, 004 (UX-1) | ADM-002 attention badges, ADM-003 search **Not built**; ADM-005 Advanced entries exist under People & Access |
| **SEC** | SEC-002..007, 009 | SEC-001 deviation 1 above; SEC-008 the existing upgrade rehearsal passes on SQL Server; a grant-preservation check specific to this upgrade is **Not built** |
| **ACC** | ACC-001, 003 basics on the new pages (`GuidedAdministrationAccessibilityTests`) | ACC-002 keyboard walkthrough, ACC-004 zoom/320px and ACC-005 screen reader **Not Verified** |
| **DOC** | DOC-001 guides updated; DOC-004 labels in this document | DOC-002 permission counts, DOC-003 configurable profile definitions (profiles are compiled into the product; the configuration kind is a later slice S8, not built) |
| **USE** | none | USE-001..006 are moderated or reading-level measurements: **Not Verified**. The automated journey proves the steps work; it does not prove a first-time administrator finds them in a minute |

## 5. Manual acceptance scenario (owner's final review; re-run after UX-9)

Two automated journeys drive the scenario over HTTP on the Test/Demo host, through the real sign-in page, with an antiforgery token on every post, and check every page for role codes, permission codes, viewer kinds and group keys (none is shown):

* `GuidedAdministrationJourneyTests` (seeded Demo): Set up my access, Give me full Test-mode access, Add a person (now with the review and its confirmation), Preview, Exit, sign in as the person. **Passed.**
* `ManualAcceptanceScenarioTests.A_fresh_Test_mode_organisation_an_initial_administrator_and_a_synthetic_Jeffrey_end_to_end`: a **fresh, uninstalled** Test-mode installation of the JBS reference pack (tenant "JBS TEST"), set up through the real 15-stage wizard. **Passed with no recorded gap.** The three gaps found in the first run (section 6 of the earlier revision) are closed by UX-9 (ADR-0040).

| Step | Result | Evidence |
|---|---|---|
| Create organisation | **Done** | set-up wizard |
| Configure initial administrator: Administer, Create and manage, **Own all current and future categories** | **Done** | administrator stage; nothing is applied yet |
| Review effective access | **Done**: the "Administrator access" step shows *This person will be able to* / *will not be able to* / summary, in words, with no role or permission code, and no Advanced details at set-up | page text asserted |
| Confirm | **Done**; without the tick the step is refused and nothing is given | asserted |
| Reach Home; **+ New contract** exists | **Done**, even though no category exists yet (the administrator will own the first one the moment it is created); My Actions, My Contracts, Reviews Due, Expiring Soon are there | Home assertions |
| Create a new category after set-up; the administrator owns it automatically | **Done**: no ownership step; the administrator then creates a contract in it | ownership read from the category list; contract created |
| Add Jeffrey through People & Access, Contract Manager, selected-category ownership (Packaging), Internal ceiling, demonstration sign-in | **Done**, with the review and the confirmation; an unconfirmed save is refused and creates nobody | wizard steps; review page text |
| Jeffrey on the Demonstration sign-in page | **Done** | `/SignIn` |
| Preview Jeffrey; operational navigation only; banner; writes refused; Administration hidden | **Done** (a Category Owner reads every contract in the organisation within the ceiling, a confirmed decision, so Jeffrey sees the other category's contract read-only and cannot edit it) | banner, no Administration, People and Approval authorities forbidden, edit refused, a write refused |
| Exit preview; sign out; sign in normally as Jeffrey | **Done** | banner button with its own token |
| Jeffrey creates a contract in an authorised category; edit refused outside it | **Done** | redirect to the new contract; create in the other category refused; edit page unavailable |
| Configure an Approval Authority using names only | **Done**: the authority the workflow names is listed as "Group Head of Procurement"; Pat Approver is chosen as a member and Jeffrey as a backup; reviewed in words; refused until confirmed; saved. Pat's approval right is then proved with the evaluator | page text; no key in the page text |
| Onboarding completion | **Done**: validating and publishing the set-up shows **JBS TEST is ready to use** with *Create first contract*, *Add another person*, *Review organisation setup*, *Go to Home* | page text |

Two defects were found and fixed by running this journey, both in code that had shipped: **the supplier form bound the whole posted form as identifier types** when the organisation had none set up (every submission was refused with "ABN: That identifier type is not set up"), and **the effective-access report of a person who had not signed in yet was empty** because the loader returned no subject for an invited person. A third item was a wording defect: the onboarding review's Plan table printed raw terminology tokens.

A person doing the same in a browser is **Not Verified**.

## 5A. Final acceptance review: the 32 items

"Test" names a class or method under `tests/`. Items are **Implemented** (built and covered by a named automated test) unless stated. Where coverage is narrower than the item, it says so.

| # | Item | Result | Evidence and note |
|---|---|---|---|
| 1 | Simplified operational navigation | Implemented | `NavigationAndHomeTests` (short menu for every role; no Administration for everyday roles; every old entry still has a place) |
| 2 | Administration landing page | Implemented | `NavigationAndHomeTests.The_Administration_landing_page_lists_only_what_the_person_may_use_and_can_be_searched` |
| 3 | Task-oriented Home | Implemented | `NavigationAndHomeTests` (work cards, set-up card for administrators only); manual scenario step E. On a new organisation the work cards appear only after the pack's reference notification policy can be installed (needs the pack's classification levels) |
| 4 | Prominent **+ New contract** for authorised users | Implemented | `Home_offers_New_contract_only_to_a_person_who_can_start_one_and_otherwise_says_why`; both journeys |
| 5 | Access Profile catalogue | Implemented | eight profiles, compiled in (configurable definitions are slice S8, not built); `AccessProfile*` scenarios, `AccessProfileScopeTests` |
| 6 | Effective Access report | Implemented | `Effective_access_agrees_with_the_evaluator_for_every_person_and_permission` (SQLite and SQL Server); abilities catalogue test |
| 7 | My Access page | Implemented | `MyAccessUiTests` |
| 8 | People & Access list | Implemented | `PeopleAccessUiTests`; search and paging **Not built** |
| 9 | Add Person wizard | Implemented | `PeopleAccessUiTests`, `GuidedAdministrationJourneyTests`, manual scenario (two people) |
| 10 | Edit Access wizard | Implemented | `BootstrapAndOwnershipUiTests` (edit-mode review), `GuidedAdministrationAccessibilityTests`, service tests for the atomic apply and recent sign-in |
| 11 | Organisation, category, site and business-unit scope selection | Implemented; **coverage partial** | organisation and category end to end (manual scenario, journey); division, business-unit and site choices become the right scope sets and viewer kinds in `AccessProfileScopeTests` (unit level, added in this review); an HTTP test that selects a site or business unit in the wizard page is **Not built** |
| 12 | Classification in plain language | Implemented | the wizard asks for "up to" the organisation's own level names; both journeys use Internal |
| 13 | Category ownership in the same journey | Implemented | wizard step 4 (All current and future, Selected, None); `Category_owners_are_chosen_in_a_form...`; `Owning_all_categories_means_current_and_future_ones...` (SQLite and SQL Server); `Owning_all_current_and_future_categories_is_reviewed_saved...`; manual scenario |
| 14 | Approver membership in the same journey | Implemented | wizard step 6 (authorities offered by name); `Approval_authorities_are_set_up_by_name...`; `ApprovalAuthoritiesUiTests`; manual scenario (an authority chosen by names only, no key) |
| 15 | Advanced grants separated | Implemented | "Users and roles (advanced)" and Advanced links; `Every_pre_redesign_navigation_entry_still_has_a_place` |
| 16 | Guided initial-administrator access setup | Implemented | `SetupScenarios` (review, confirmation, nothing given before it), `BootstrapAndOwnershipUiTests`, manual scenario |
| 17 | Demo/Test-only "Give me full Test-mode access" | Implemented | `Full_test_mode_access_*`, `Test_mode_access_does_not_exist_in_production` (refused at the service) |
| 18 | Demo/Test-only "Allow demonstration sign-in" | Implemented | `A_test_person_signs_in_through_the_demonstration_provider...`, `Demonstration_sign_in_and_synthetic_people_are_refused_in_production_at_the_service`, manual scenario (listed on the sign-in page, signs in) |
| 19 | Demo/Test-only "Preview as this user" | Implemented | `Preview_*` scenarios, `PeopleAccessUiTests`, `Preview_does_not_exist_in_production` |
| 20 | Preview is read-only | Implemented | `A_preview_cannot_write_anything_but_its_own_record_and_the_audit_trail`; request layer and database layer; manual scenario (a supplier create is refused) |
| 21 | Preview uses the target's real effective access | Implemented | `A_preview_runs_with_the_previewed_persons_real_access_not_the_administrators`; manual scenario (the previewed person sees the contract of a category they do not own read-only and cannot edit it, per the read-all rule) |
| 22 | Preview cannot inherit administrator authority | Implemented | same; manual scenario (no Administration menu, People forbidden) |
| 23 | Preview cannot cross tenant or organisation | Implemented | `A_preview_cannot_cross_tenants`; `You_may_preview_only_what_you_could_give...` (the previewer must be able to give everything the target holds, including the organisation scope) |
| 24 | Persistent TEST MODE banner | Implemented | `PeopleAccessUiTests`, journey, manual scenario; not on the one refusal page (recorded deviation 5) |
| 25 | Clear Exit Preview | Implemented | the banner form carries its own antiforgery token; used exactly so by both journeys |
| 26 | Onboarding checklist, Required/Recommended/Optional | Implemented | `ReadinessScenarios` (SQLite and SQL Server), `OnboardingHostTests`, `MappingsAndChecklistUiTests` |
| 27 | Never "Ready" while Required operational access is incomplete | Implemented | `ReadinessScenarios`; manual scenario (publish refused with "Not ready to publish") |
| 28 | Category ownership as form/table, not CSV | Implemented | `BootstrapAndOwnershipUiTests`; manual scenario |
| 29 | Identity Group Mappings under Advanced | Implemented | `MappingsAndChecklistUiTests`, `Mappings_*` scenarios |
| 30 | CSV and bulk retained only as Advanced/Bulk | **Partial** | the onboarding draft's CSV sections remain, labelled "Draft sections" with an explanation rather than "Bulk import (Advanced)" (ONB-009); migration/bulk tools are unchanged under Operations |
| 31 | The existing evaluator remains the sole authority | Implemented | `AccessEvaluator.cs` and `PermissionMatrix.cs` have **no diff** from the baseline; the parity test above; a person holding every Test-mode role still cannot approve their own submission |
| 32 | Stable role and permission identifiers unchanged | Implemented | no diff in the role/permission reference CSVs or `RoleCodes`; the only change in `Roles.cs` is a new `AssignmentSource` value, `TestBootstrap` |

## 5B. Guided Fields (UX-10): the finding, the scenario and the result

**Finding.** Adding a custom field was hard enough that the administrator could not tell whether it worked. Causes, each fixed in the pages (the engine was sound): a field key to invent; choices typed as `CODE | Label | inactive`; a "Start a draft" step; every setting of every type on one page; separate validate and publish pages; no view of the field as the form would draw it; no statement of what publishing would do for this person; no way to see which fields were waiting.

**Scenario (Create without code).** Insurance Expiry (Date), Critical Supplier (Yes/No) and Payment Terms (Single choice: 30, 45, 60, 90 days), added on the Fields pages, published, then: all three on Create contract; values save; values on Contract details; values edited by an authorised person; an effective record still locked (its page shows the fields, offers no edit, and a posted edit is refused); each field filtered through the register's filter form and the filter tells two contracts apart; an authorised export contains the three columns and values; a field for category owners only does not appear to a viewer (details, the register's filter list, search, filter) nor in the export of a person who may export (a viewer may not export at all), and a field held above the category owner's classification ceiling does not appear to that owner on details, edit or create, and a value cannot be written for it on Create; retiring Critical Supplier keeps its values on the records, in the database and in exports; an earlier version restored; another organisation's administrator cannot reach the fields; the typed columns checked directly in SQL Server; no migration applied. `GuidedFieldsUiTests` (SQLite) and `GuidedFieldsSqlServerTests` (real SQL Server) run the same scenario. A second test shows the second-person path (the author is offered "Submit for publication", a reviewer publishes), and one runs the structural accessibility checks over every new page.

| Item | Status | Evidence |
|---|---|---|
| Fields landing with Field, Type, Section, Used on, Required, Status, + Add field, Search, Edit, Preview, Retire, Version history | Implemented | `GuidedFieldsUiTests` |
| Guided Add/Edit: basic information, type, only that type's settings, choice editor, placement, standard and collapsed Advanced rules, preview, plain-English review | Implemented | same; the settings that do not belong to a type are absent from its page |
| Save draft, Submit for publication, Publish according to governance | Implemented | same, including the second-person test |
| No JSON, SQL, field key, migration, code or release knowledge needed | Implemented | page text scanned for those words; applied-migration count unchanged |
| Preview "exactly as it will appear" | Implemented | the preview renders the same partials as Create and Details |
| Create, Edit and Details as three independent switches | **Not built (by design)** | the engine has none; "Used on" shows what the rules do. ADR-0041 asks the owner whether to add them |
| New field as a column of a shared view from the Fields pages | **Not built** | views are their own configuration; the export form's **Include the additional fields** covers exports |
| Real-browser walkthrough, screen reader | **Not Verified** | structural checks only |

**Backend defect, reported separately from the usability failures.** One, in the contracts module (not the field engine): `ForNewAsync` and `ValidateForNewAsync` skipped the classification gate. A creator whose classification ceiling was below a field's level was offered the field and could write a value that they could never read back (Details, Edit, Save and the register already gated it). Fixed; covered by the scenario above on SQLite and SQL Server. No defect was found in the metadata engine, the value storage, the validator, publication, rollback or retirement.

## 6. Known gaps (first things to do next)

1. Moderated usability test of the journey with real administrators (USE-001..006), and keyboard and screen-reader walkthroughs (ACC-002, 004, 005).
2. Ownership effective dates (OWN-006), End asks for a reason (OWN-009), mapping change-as-one-action (MAP-007), naming the group on mapped grants (MAP-006).
3. People search and paging; "why am I asked this" disclosures; Preview on the list and old Users page; the sign-in page hint; **Add a test colleague**.
4. An inventory sweep of every GET page under Preview (PRV-010) and a per-permission differential for effective access (EFF-002).
5. Configurable access-profile definitions with draft, publish and rollback (slice S8); today they are compiled in.
6. Documents hub, "Recent contracts", reports (UXG-09 and later).
7. Fields (UX-10): per-surface Create/Edit/Details switches (an engine change); a new field is not a register column until a shared view lists it (the Studio's Views page), only an export can include it on request; shared choice lists are still edited on the older Lists page; the older Studio field pages remain as the advanced editor.
8. Observations from the fresh journey, not changed: the set-up classification stage lets an administrator remove a level the pack's notification policy names ("Legal Privileged"), which then shows as an empty Home instead of a message on that stage; the data-migration route of the ownership policy has no dedicated test (the Categories screen and the trusted upsert do); the Approval authorities page carries the machine key in its edit link and a hidden field (never in the visible text); approval-rule authoring remains the form editor for a workflow that names a matrix.

## 7A. Verification after UX-10 (guided Fields)

Local, 2026-10-30: locked restore and Release build with warnings as errors (0 warnings, 0 errors); unit **979**, architecture **113**, API **262** (including `GuidedFieldsSqlServerTests` on a real SQL Server 2022 container), integration **731** (every real-SQL-Server wrapper and the upgrade tests): **2,085 passed, 0 failed, 0 skipped**. `tools/check-repo.py`, `tools/check-docs.py`, `tools/check-packs.sh` and the tooling tests pass. No migration was added, so the 1.1.0 upgrade manifest is unchanged. The full run caught one defect in the new pages (a stage-name helper used a regex lookbehind, unsupported by the non-backtracking engine, which made the Edit page fail) and two architecture-rule breaches (a fixed date format and a workflow-state literal in a comment); all fixed and re-run. CI for the final commit is recorded in `deploy/STATUS.md`.

## 7. Verification of the UX-9 state

After UX-9 (commit 6daa265 and the documentation commit that follows it):

| Check | Where | Result |
|---|---|---|
| Locked restore; Release build with warnings as errors | local workspace and CI | 0 warnings, 0 errors; CI run 86 (commit 6daa265) |
| Unit tests | local | **979 passed** |
| Architecture tests | local | **113 passed** |
| API tests (both journeys, the fresh manual acceptance journey, accessibility checks, approval authorities, ownership editor) | local | **247 passed** |
| Integration suite including every real SQL Server 2022 wrapper and the upgrade tests | local, `ECP_TEST_SQL` set | **731 passed, 0 failed, 0 skipped** |
| **Local total** | | **2,070 passed, 0 failed, 0 skipped** |
| N-1 to N upgrade rehearsal on SQL Server; SQL Server tests; performance baseline | CI run 86 | recorded in `deploy/STATUS.md` |
| Container image build and smoke | CI run 86 | recorded in `deploy/STATUS.md` |
| Repository checks, documentation consistency, solution packs, pack tool, tooling tests, supply chain | local and CI | pass; two licence items remain pending owner review (unchanged) |

One local API run failed 57 tests with HTTP 500 and the cause was the workspace, not the product: the file-watcher instance limit (128) of a restarted container was exhausted. With the limit raised (as before) the same API suite passed 247 of 247. CI is unaffected.

The earlier verification of the UX-8 state (commit a3f65e5; 979 unit, 113 architecture, 242 API, 715 integration; CI run 84 all green) stands as history.

Not Verified: a person completing the scenario in a browser; moderated usability (USE-001..006); keyboard, zoom and screen-reader walkthroughs (ACC-002/004/005); IIS and Azure (unchanged from the baseline). Production approval is external governance and is not declared.
