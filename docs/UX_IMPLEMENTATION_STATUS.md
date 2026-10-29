# Guided administration: implementation status

Status: **Implemented in slices UX-1 to UX-7 and verified (final acceptance review 2026-10-29, section 5A); three UX gaps found by the owner's manual scenario and a moderated usability test remain open (sections 5 and 6).** Branch ux/guided-administration (from claude/stoic-volta-dvszm5 at commit 5cdf9f7; that branch is unchanged). Design: `docs/UX_REDESIGN.md`. Criteria: `docs/UX_ACCEPTANCE_CRITERIA.md`. Decisions: `decisions/ADR-0039-guided-administration-access-profiles.md`. Production approval is external governance and nothing here declares it.

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
2. **Two new tables**, not one (SEC-009's "single new table").
3. **Pack requirements** live in the existing solution-pack document (`onboardingRequirements`), not a new pack kind; this avoids changing the pack format. A pack can raise or relax anything except the platform's three universal items.
4. **A tenant administrator alone cannot assign category ownership** (existing rule); the Test-mode shortcut and the ownership editor are used by an organisation administrator, and the screens say so in words instead of failing silently.
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
| **BOOT** | BOOT-001..003, 005..008 (set-up scenarios, `BootstrapAndOwnershipUiTests`, SQLite and SQL Server) | BOOT-004: the intent is kept as a note and the checklist's owners item reads "Waiting for categories" (**Partial**); BOOT-009: the page explains the separation-of-duties limit and the evaluator test proves it; an **Add a test colleague** button is **Not built** (use Add a person with demonstration sign-in and the Approval Authority profile) |
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

## 5. Manual acceptance scenario (owner's final review)

Two automated journeys drive the scenario over HTTP on the Test/Demo host, through the real sign-in page, with an antiforgery token on every post, and check every page for role codes, permission names, viewer kinds and group keys (none appears):

* `GuidedAdministrationJourneyTests` (seeded Demo): Set up my access, **Give me full Test-mode access**, Add a person, Preview, Exit, sign in as the person. **Passed.**
* `ManualAcceptanceScenarioTests.A_fresh_Test_mode_organisation_an_initial_administrator_and_a_synthetic_Jeffrey_end_to_end` (a **fresh, uninstalled** Test-mode installation set up through the real 14-stage wizard; the administrator ticks "Administer the organisation", "Create and manage contracts" and "Own all categories"). **Passed**, with the three named gaps below recorded by the test itself (the test fails if the gap list changes, so this page cannot drift from the product).

| Step | Result | Evidence |
|---|---|---|
| A. Fresh Test-mode organisation | **Done** | the set-up wizard in the test |
| B. Initial administrator chooses Administer, Create/manage contracts, Own all categories | **Done** | set-up admin stage |
| C. Review page explains in plain English what access results | **Partial (gap 1)**: the set-up stage applies the choice and **My access** is the plain-English review after the fact; the set-up stage has no separate review page before saving | `/Access` contains "Create contracts" |
| D. Save access | **Done** (saved by set-up) | |
| E. Home shows + New contract, My Contracts, My Actions, Reviews Due, Expiring Soon | **Done once a category exists and is owned** (gap 2); My Actions is in the navigation, the other four are on Home | Home assertions |
| F. Create a second synthetic person, "Jeffrey" | **Done** | Add a person wizard |
| G. Contract Manager, selected organisation, selected categories, Internal ceiling, Allow demonstration sign-in | **Done** | wizard steps 2 to 5 |
| H. Save | **Done** | |
| I. Jeffrey appears on the Demonstration sign-in page | **Done** | `/SignIn` |
| J. Preview as Jeffrey | **Done** | person page |
| K. Operational navigation only; only authorised records; Administration hidden; banner; writes refused | **Done**, with a note: a Category Owner **reads every contract in the organisation** within the ceiling (confirmed decision), so Jeffrey sees a contract in a category he does not own, read-only, and cannot edit it | banner, no Administration menu, People forbidden, edit refused, supplier create refused |
| L. Exit preview | **Done** (the banner's own button) | |
| M. Sign out and sign in normally as Jeffrey | **Done** | |
| N. Jeffrey creates a contract in an authorised category | **Done** | redirect to the new contract |
| O. Jeffrey cannot create or edit outside the scope | **Done** | create in the other category refused; edit page not available |

**UX gaps found by running the scenario (reported, not worked around through an Advanced page):**

1. **No review page at set-up.** The first administrator's choices are saved by the set-up stage; the plain-English description of the resulting access is on **My access**, afterwards.
2. **"Own all categories" at set-up does not carry to categories added later.** A new organisation has no categories; the choice is kept, but until the administrator adds categories and makes themself the owner in the guided ownership editor, Home offers no **+ New contract** (it says why).
3. **Approver groups.** Approval authorities need an approver group, and the page that creates the first one asks for a **Group key**; no guided path creates it. Separately, publishing the set-up is refused (correctly, by the readiness gate) until the pack's required "Workflow and approvals" item is done, and the reference pack ships **no approval rules**; the only way to author them is the approval-matrix form editor. So a fresh JBS organisation cannot reach "Ready" without a specialist step. The scenario, Home work cards, supplier and contract creation do not depend on publishing the set-up.

Two things the run exposed and that were fixed on this branch: the **Plan table on the onboarding review page printed raw terminology tokens** (now expanded); and the scenario's own set-up had to include the reference pack's **Legal Privileged** classification level, otherwise the pack's notification policy cannot be installed and Home shows no work cards (the set-up classification stage, as shipped, lets an administrator remove a level the pack's policy names; the error surfaces on Home as no cards rather than on the stage; recorded here as an observation, not changed).

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
| 13 | Category ownership in the same journey | Implemented | wizard step 4; `Category_owners_are_chosen_in_a_form_from_everyone_and_the_owner_role_is_given_in_the_same_save`; manual scenario |
| 14 | Approver membership in the same journey | Implemented | wizard step 6; `An_approval_authority_needs_an_approver_group...`; manual scenario (Approval Authority). The approver group must exist first (gap 3) |
| 15 | Advanced grants separated | Implemented | "Users and roles (advanced)" and Advanced links; `Every_pre_redesign_navigation_entry_still_has_a_place` |
| 16 | Guided initial-administrator access setup | Implemented | `SetupScenarios`, `BootstrapAndOwnershipUiTests`, manual scenario; gaps 1 and 2 |
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

## 6. Known gaps (first things to do next)

0. The three scenario gaps in section 5: a review page at set-up, ownership of categories added later, a guided first approver group and a guided path to the pack's approval rules.
1. Moderated usability test of the journey with real administrators (USE-001..006), and keyboard and screen-reader walkthroughs (ACC-002, 004, 005).
2. Ownership effective dates (OWN-006), End asks for a reason (OWN-009), mapping change-as-one-action (MAP-007), naming the group on mapped grants (MAP-006).
3. People search and paging; "why am I asked this" disclosures; Preview on the list and old Users page; the sign-in page hint; **Add a test colleague**.
4. An inventory sweep of every GET page under Preview (PRV-010) and a per-permission differential for effective access (EFF-002).
5. Configurable access-profile definitions with draft, publish and rollback (slice S8); today they are compiled in.
6. Documents hub, "Recent contracts", reports (UXG-09 and later).

## 7. Verification of this state

| Check | Where | Result |
|---|---|---|
| Locked restore; Release build with warnings as errors | local workspace and CI | 0 warnings, 0 errors; CI run 82 (commit 7bf540c) succeeded |
| Unit tests | local | **979 passed** (the 967 of the full run plus the 12 scope tests added in this review, run separately) |
| Architecture tests | local | **113 passed** |
| API tests (including both journeys, the accessibility checks and the manual acceptance scenario) | local | **242 passed** |
| Integration suite including every real SQL Server 2022 wrapper and the upgrade tests | local, `ECP_TEST_SQL` set | **715 passed, 0 failed, 0 skipped**. Local total **2,049 passed, 0 failed, 0 skipped** |
| N-1 to N upgrade rehearsal on SQL Server; SQL Server tests; performance baseline | CI run 82 | succeeded |
| Container image build and smoke | CI run 82 | succeeded |
| Repository checks, documentation consistency, solution packs, pack tool, tooling tests, supply chain | local and CI | pass; two licence items remain pending owner review (unchanged) |

Not Verified: a person completing the scenario in a browser; moderated usability (USE-001..006); keyboard, zoom and screen-reader walkthroughs (ACC-002/004/005); IIS and Azure (unchanged from the baseline). Production approval is external governance and is not declared.
