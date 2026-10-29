# Guided administration: implementation status

Status: **Implemented in slices UX-1 to UX-7; verification and usability testing described below.** Branch ux/guided-administration (from claude/stoic-volta-dvszm5 at commit 5cdf9f7; that branch is unchanged). Design: `docs/UX_REDESIGN.md`. Criteria: `docs/UX_ACCEPTANCE_CRITERIA.md`. Decisions: `decisions/ADR-0039-guided-administration-access-profiles.md`. Production approval is external governance and nothing here declares it.

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

## 5. Manual acceptance journey

`GuidedAdministrationJourneyTests.A_first_administrator_gets_to_work_adds_a_colleague_and_checks_what_the_colleague_sees_without_any_code` drives the scenario the owner described over HTTP on the Demo/Test host, signing in through the real sign-in page: Home says "Set up my access" with no **+ New contract** → **Give me full Test-mode access** (what it grants and does not, first) → My access shows "Create contracts" and Home shows **+ New contract** → Add a person (name, email, Allow demonstration sign-in) → Contract Manager → ownership of one category chosen by name from a list → review in plain English → save → **Preview as this user** (banner, the person's navigation and records, administrator's authority absent, a write refused) → **Exit user preview** → the person signs in through demonstration sign-in and sees the same. Every page of the journey is checked for role codes, viewer kinds and permission names. It **passed**. A person doing the same in a browser is **Not Verified**.

## 6. Known gaps (first things to do next)

1. Moderated usability test of the journey with real administrators (USE-001..006), and keyboard and screen-reader walkthroughs (ACC-002, 004, 005).
2. Ownership effective dates (OWN-006), End asks for a reason (OWN-009), mapping change-as-one-action (MAP-007), naming the group on mapped grants (MAP-006).
3. People search and paging; "why am I asked this" disclosures; Preview on the list and old Users page; the sign-in page hint; **Add a test colleague**.
4. An inventory sweep of every GET page under Preview (PRV-010) and a per-permission differential for effective access (EFF-002).
5. Configurable access-profile definitions with draft, publish and rollback (slice S8); today they are compiled in.
6. Documents hub, "Recent contracts", reports (UXG-09 and later).
