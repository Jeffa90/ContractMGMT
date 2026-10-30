# UX acceptance criteria: guided administration

Status: **Approved by the owner (2026-10-29).** These are the criteria the slices in `docs/UX_REDESIGN.md` section 18 must meet before they are called done. How far each is met, with the evidence and the honest gaps, is in `docs/UX_IMPLEMENTATION_STATUS.md` (section 4 per criterion, section 5A for the final 32-item review, section 5 for the manual scenario). Production approval is external governance and nothing here declares it.

How to read: each criterion has an ID, a statement written as *Given / When / Then* where useful, and **Verify**, the kind of evidence that counts. "Over HTTP" means a test that signs in through the real sign-in page and drives the real pages (the style of `NoCodeRehearsalTests`), on SQLite and on real SQL Server. "Role matrix" means the check is repeated for every role in `requirements/02-reference/role-permission-matrix.csv`. A criterion is **Passed**, **Failed**, or **Not Verified**; a slice does not close with an unexplained Not Verified.

Test users: the target person is a Procurement Excellence Manager or Contract Administrator, comfortable with business systems, not a developer. "Without help" means without a guide open and without an administrator's intervention after setup.

---

## A. Traceability to the owner's twelve observations

| Owner observation | Criteria that close it |
|---|---|
| 1. Initial Setup was clear | SET-001 (no regression) |
| 2. First administrator not guided to operational use | BOOT-001 to BOOT-009 |
| 3. Cannot create contracts | BOOT-003, BOOT-004, HOME-003, E2E-001 |
| 4. Onboarding publishable while empty | ONB-001 to ONB-010 |
| 5. Category ownership needs CSV plus a role | OWN-001 to OWN-010 |
| 6. Group mapping needs provider/group keys, viewer kinds | MAP-001 to MAP-009 |
| 7. Users and Roles exposes primitives | PPL-001 to PPL-012, ADV-001 to ADV-004 |
| 8. Home does not show the primary job | HOME-001 to HOME-008 |
| 9. About twenty primary links | NAV-001 to NAV-010 |
| 10. "Access to this tenant" misleading | NAV-007 |
| 11. "How do I set myself up to create a contract?" | HOME-003, BOOT-001, EFF-006, E2E-001 |
| 12. Not task-oriented or self-explanatory | the usability criteria in section P, E2E-001 to E2E-004 |
| Additional: an invited Test person can't be tried out without an identity provider | PRV-001 to PRV-018, DSI-001 to DSI-009, E2E-007, E2E-008 |

---

## B. Navigation (NAV)

| ID | Criterion | Verify |
|---|---|---|
| NAV-001 | **Given** any signed-in person, **then** the primary navigation contains at most: Home, Contracts, My Actions, Counterparties, Documents, Reports, Administration, with the bell and sign-out. At most seven text links | Over HTTP: count links in `nav`, role matrix |
| NAV-002 | A person with no administration permission sees no Administration link, and a request to `/Admin` returns the existing refusal | Over HTTP, role matrix |
| NAV-003 | Every item shown corresponds to a page the person can open; no item leads to a refusal | Over HTTP, role matrix: follow each link |
| NAV-004 | Every one of the 31 pre-redesign navigation items is reachable from Administration, Home, My Actions, Reports or the bell, for each role that had it | A test holds the old list and the new map and fails on any item that is unreachable |
| NAV-005 | No existing URL is removed or renamed; each still resolves for the role that could open it | Over HTTP: old URL list |
| NAV-006 | The Administration menu has at most nine entries, is a disclosure button (`aria-expanded`, `aria-controls`), opens on click/Enter/Space, closes on Escape and returns focus | Accessibility test, keyboard walkthrough |
| NAV-007 | The former "Access to this tenant" is named **Support & emergency access log**, sits under Security & Audit, and its page states in one sentence that it lists installation administrators' time-limited access to this tenant | Over HTTP: page text |
| NAV-008 | Labels follow terminology: with the JBS pack, "Counterparties" and "Contracts" show the pack's words; no raw `[[...]]` token is visible | Rendered-page sweep over Blank, JBS Procurement and Construction Subcontracts |
| NAV-009 | Breadcrumbs appear on every Administration page, name the group and the page, and the current page is not a link | Accessibility test |
| NAV-010 | At 320 CSS pixels the navigation wraps onto lines (it is a short row of links, so no Menu button is needed); no horizontal page scroll | Responsive test at 320 and 40rem |

## C. Home page (HOME)

| ID | Criterion | Verify |
|---|---|---|
| HOME-001 | Home shows the person's work first: Awaiting my action, My contracts, Expiring soon, Reviews due, Recent contracts and notifications, each from the existing dashboard and inbox services, each omitted when its capability is off or the person has no right to it | Over HTTP, role matrix |
| HOME-002 | The role table no longer appears on Home; it is on **My access** | Over HTTP |
| HOME-003 | **Given** a person who can create a contract in at least one active category, **then** a prominent **+ New contract** button is the first action. **Given** a person who cannot, **then** no button is shown and a card says why and what to do (names who can grant access, or "Set up my access" for an administrator) | Over HTTP: Procurement Administrator, Category Owner with and without ownership, Tenant Administrator |
| HOME-004 | The Setup & administration card is shown only to people with an administration permission, lists at most three open Required items, and collapses to one line when all are complete | Over HTTP |
| HOME-005 | The card never hides or replaces the work cards, on any viewport | Visual/DOM-order test |
| HOME-006 | Every empty state names what is empty, why, and the next action | Over HTTP, empty installation |
| HOME-007 | Home loads with the person's own access only: no count, title or row of something outside their scope | Tenant and organisation isolation tests, role matrix |
| HOME-008 | The page title and headings follow the heading order; the card regions have accessible names | Accessibility test |

## D. Access profiles (PRF)

| ID | Criterion | Verify |
|---|---|---|
| PRF-001 | Each default profile (Contract Manager, Contract Administrator, Reviewer, Approval Authority, Read Only, Organisation Administrator, Business Configuration Administrator, Auditor) issues exactly the roles, scope kinds and ownership of `UX_REDESIGN.md` 5.2, and nothing else | Unit: recipe table versus the issued grants |
| PRF-002 | A profile can never issue System Administrator or Tenant Administrator, or a permission directly | Unit |
| PRF-003 | Every grant a profile issues passes the existing delegation policy; a grant the signed-in person may not give is not offered and, if forced, is refused with the existing error | Role matrix, over HTTP |
| PRF-004 | The effective permissions after applying a profile equal those of the same grants made by hand through the Advanced form, for every profile and scope kind | Differential test against the evaluator |
| PRF-005 | Profile labels come from terminology; a pack's names appear without code change | Over HTTP with the JBS pack |
| PRF-006 | A profile hidden by a capability (for example Reviewer when Legal Review is off) is not offered, but the three administrator roles are never hidden | Over HTTP |
| PRF-007 | Applying a profile that issues several operations either completes, or reports exactly what was applied and offers to finish or undo that batch only | Fault-injection test on SQLite and SQL Server |
| PRF-008 | The profile-application record is display provenance only: deleting it changes no permission | Test: delete the row, compare effective access |
| PRF-009 | Existing grants made before this feature display as "Other access" with their role, scope and source, and are unchanged | Over HTTP on a seeded installation |

## E. People & Access (PPL) and the wizard

| ID | Criterion | Verify |
|---|---|---|
| PPL-001 | A person with `users.manage` reaches **People** from Administration; the list shows name, email, status, profiles and a "needs attention" flag; it supports search and paging | Over HTTP |
| PPL-002 | The wizard has seven steps; each asks one question, has a default, one example and a "why am I being asked this?" disclosure; the step list is visible and the current step is announced | Accessibility test, over HTTP |
| PPL-003 | No role code, "viewer kind", "scope", "claim", "ceiling" or provider key appears in the wizard outside the Advanced disclosure | Rendered-page sweep with a banned-word list |
| PPL-004 | **Back** preserves every answer; a failed save preserves every answer and shows an error summary linked to the fields | Over HTTP |
| PPL-005 | The Review step shows what the person **can** and **cannot** do in sentences naming the person and the place, with the highest classification, and what the save will add or end | Over HTTP: compare with the evaluator output |
| PPL-006 | Save requires a recent sign-in; without it the person is taken to sign in again and returns with answers preserved | Over HTTP |
| PPL-007 | Save writes the grants and ownerships through the existing services, producing the existing audit events (`role.granted`, `category.ownership_assigned`, `user.invited`) and no new ones | Audit assertions; frozen-identifier test |
| PPL-008 | Granting access to yourself is refused with a plain explanation and a pointer to the right path; it never silently succeeds | Over HTTP |
| PPL-009 | Removing the last administrator is refused with the existing protection, explained in words | Over HTTP |
| PPL-010 | Disable and Reactivate keep their behaviour and confirmation | Over HTTP |
| PPL-011 | **Advanced** shows the existing grant form with every field and the exact assignments, and works as today (including effective dates and privileged access if allowed) | Over HTTP |
| PPL-012 | A person's access is evaluated server-side exactly as before: UI selections cannot widen what the delegation policy allows | Role matrix, over HTTP, attempts with tampered form posts |

## F. Effective access (EFF)

| ID | Criterion | Verify |
|---|---|---|
| EFF-001 | A person's page lists what they can do, where, what they own, the highest classification, approval memberships, and for each ability **why** (direct assignment, access profile, category ownership, identity group, administrator role) | Over HTTP |
| EFF-002 | The list agrees with the evaluator for every permission and scope: nothing is shown that the evaluator would deny, and nothing is hidden that it would allow | Differential test over all 94 permissions, all roles |
| EFF-003 | Every permission in the catalogue maps to exactly one ability or is marked technical; adding a permission without a mapping fails the build | Unit test over `permission-catalogue.csv` |
| EFF-004 | Ended and future-dated grants appear under "Past and future access", never in the "can do" list | Over HTTP with FakeClock |
| EFF-005 | An ownership without the `CategoryOwner` role, or the role with no ownership, is shown as **Needs attention** with the fix | Over HTTP |
| EFF-006 | The signed-in person's **My access** page answers "what can I do and why?" and, for a person who cannot create contracts, says what is missing | Over HTTP |
| EFF-007 | Only people with `users.manage` (within their scope) can see another person's effective access | Role matrix, isolation tests |
| EFF-008 | The Advanced grants disclosure shows the exact assignments with role code, scope, ceiling, dates, source and reason | Over HTTP |

## G. Initial administrator and Test-mode bootstrap (BOOT)

| ID | Criterion | Verify |
|---|---|---|
| BOOT-001 | The setup wizard asks "What will you do in this organisation?" after the initial administrator, with plain-language choices | Over HTTP: wizard in Test and Production configuration |
| BOOT-002 | In Production nothing is pre-ticked and the choice is recorded in the setup report; in Demo and Test "Create and manage contracts" is pre-ticked | Over HTTP |
| BOOT-003 | **Given** the choice "Create and manage contracts", **after setup** the administrator can open **+ New contract** and create a contract in an owned category without any further grant (once a category exists with the intent applied) | Over HTTP end to end |
| BOOT-004 | If categories do not yet exist, the intent is kept and a Required checklist item "Give <name> ownership of categories" completes when they do | Over HTTP |
| BOOT-005 | **Give me full Test-mode access** is present only in Demo and Test, with a "synthetic data and testing only" banner; in Production the button is absent and a direct request is refused | Over HTTP in all three modes |
| BOOT-006 | The action requires a recent sign-in, uses the trusted grant path, grants only the documented profiles at ceiling Confidential or below (Legal Privileged only on an explicit extra tick), and cannot grant System or Tenant Administrator | Over HTTP; unit |
| BOOT-007 | The action writes `access.test_bootstrap` plus the ordinary `role.granted` / `category.ownership_assigned` events with source `TestBootstrap`; the audit chain still verifies | Audit assertions; `verify-audit` |
| BOOT-008 | **Remove Test-mode access** ends exactly the grants the action created and nothing else | Over HTTP |
| BOOT-009 | The confirmation page says that one person still cannot approve their own submission and offers **Add a test colleague**; the colleague can approve and the original person cannot | Over HTTP: separation of duties holds |

## G2. Preview as this user (PRV): Demo and Test only

| ID | Criterion | Verify |
|---|---|---|
| PRV-001 | A person holding `users.manage` over the target's organisation sees **Preview as this user** on the People list, the person's page and the Effective Access page (and on the existing Users details page before the new People screens ship) | Over HTTP, role matrix |
| PRV-002 | In Production the control is absent, the service refuses a start request, and a forged preview claim in a session is ignored and ended on the next request | Over HTTP and unit, Production configuration |
| PRV-003 | **Given** a started preview, **then** every page, including error pages, shows a permanent banner "TEST MODE — Previewing as <display name>", the words "Read-only", the time remaining, and an **Exit user preview** button that is the first focusable control after the skip link | Over HTTP; accessibility test |
| PRV-004 | Exit returns the person to their own session and to the person's page; the preview record is closed; the banner is gone | Over HTTP |
| PRV-005 | In preview the navigation, Home cards, buttons and page access equal those of the target: the set of pages that return success for the preview session equals the set for a real session of the target, for every role in the matrix | Differential test: preview versus real demonstration sign-in |
| PRV-006 | Preview uses the target's roles, scopes, category ownership, classification ceiling, approver groups and capability restrictions: a record above the target's ceiling is not found; a contract in a category the target does not own offers no edit; a capability switched off for the target's organisation hides its pages | Over HTTP with prepared data |
| PRV-007 | Every preview start, end and refused write is audited (`preview.started`, `preview.ended`, `preview.write_refused`) with previewer, target, tenant, expiry and correlation id; denials during preview carry the previewer as actor with type Preview; the audit chain verifies | Audit assertions; `verify-audit` |
| PRV-008 | Preview never modifies the target: the user row, external-identity link, status (an Invited person stays Invited), credentials, sign-in history and tokens are byte-for-byte unchanged | Database snapshot before and after |
| PRV-009 | Preview is read-only: a POST, PUT, PATCH or DELETE (except Exit and sign-out) is refused with a friendly page, writes nothing, and is recorded; the unit of work rejects any non-audit write in a preview scope | Over HTTP; unit test of the write guard |
| PRV-010 | GET handlers that write (mark as read, last viewed, counters) make no change during preview | Over HTTP, inventory test of every GET page for the preview session, then a database diff |
| PRV-011 | **Isolation:** a previewer cannot start a preview of a person in another tenant (not found), of a person outside their organisations, or of a Disabled person; a preview shows nothing from another tenant or organisation | Isolation suite extended |
| PRV-012 | **Containment:** a previewer cannot preview a person who holds any grant the previewer could not give (a System Administrator, a higher ceiling, privileged access when the previewer is not a Tenant Administrator, a wider scope); the refusal is explained in words | Role matrix, over HTTP |
| PRV-013 | A person cannot preview themselves, cannot start a second preview inside one, and cannot preview during support or emergency elevation | Over HTTP |
| PRV-014 | Starting a preview requires a recent sign-in; without it the person signs in again and returns | Over HTTP |
| PRV-015 | A preview ends automatically at its limit, on sign-out, on a mode change, and when the previewer or the target is disabled or the previewer loses `users.manage`; the next request returns to the administrator's own session | Over HTTP with FakeClock |
| PRV-016 | A preview of an Invited person works and does not activate them | Over HTTP |
| PRV-017 | The banner and the refusal page remain usable at 200% zoom and 320 CSS pixels, and the banner is not hidden by the target's branding or capability settings | Responsive and accessibility tests |
| PRV-018 | Preview cannot be used to satisfy a separation-of-duties or second-person rule: a submission made by the target cannot be approved or published through a preview | Over HTTP |

## G3. Demonstration sign-in for synthetic people (DSI): Demo and Test only

| ID | Criterion | Verify |
|---|---|---|
| DSI-001 | **Allow demonstration sign-in** appears in the Add person wizard and invite form only when the mode is Demo or Test **and** demonstration sign-in is enabled on the installation; otherwise it is hidden with a one-line reason | Over HTTP in Demo, Test, Test with demonstration sign-in off, and Production |
| DSI-002 | **Given** the option ticked, **then** the person is created Active, linked to the demonstration provider, flagged Synthetic, and appears on the Demonstration sign-in page with their roles | Over HTTP |
| DSI-003 | A synthetic person can sign in through the demonstration page and sees the access the wizard granted (their navigation and Effective Access agree) | Over HTTP end to end |
| DSI-004 | Without the option the person is Invited, does not appear on the sign-in page, and the sign-in page's empty-state sentence explains how to try them (preview or the option) | Over HTTP |
| DSI-005 | Production: no checkbox, the service refuses, and invitation is unchanged (`InviteAsync`, invite-only, linked at the first verified provider sign-in) | Over HTTP and unit, Production configuration; existing invitation tests pass unchanged |
| DSI-006 | The audit trail records the creation (`user.created` with the persona source) and each grant (`role.granted`); no new secret or credential is created | Audit assertions |
| DSI-007 | A Synthetic badge is visible in People and on the person's page; it never appears in Production | Over HTTP |
| DSI-008 | Several synthetic people with different profiles can be created in sequence and each signs in without any provider configuration | Over HTTP |
| DSI-009 | Switching an already-Invited person to demonstration sign-in (if offered) is confirmed first, and a later real first sign-in through a configured provider is tested for the outcome; until tested the control is not offered | Over HTTP; **Not Verified** until performed |

## H. Organisation onboarding (ONB)

| ID | Criterion | Verify |
|---|---|---|
| ONB-001 | The checklist shows the thirteen items of `UX_REDESIGN.md` 8.2, each with exactly one state: Required, Recommended, Optional, Complete or Needs attention | Over HTTP |
| ONB-002 | States are computed from facts (role holders, owners, structure counts, approver groups, releases), not from a ticked box or the existence of text | Unit and over HTTP |
| ONB-003 | **Given** an organisation with categories and no owners, **then** the owners item is Needs attention and "Ready to publish" is not shown | Over HTTP |
| ONB-004 | **Given** an owner who lacks the `CategoryOwner` role, **then** the item is Needs attention | Over HTTP |
| ONB-005 | "Ready to publish" appears only when every Required item is Complete **and** the draft validates; otherwise Publish is disabled with a list of the open Required items, each a link to the fix | Over HTTP |
| ONB-006 | The solution pack determines which items are Required; two packs with different requirements give different checklists | Over HTTP with Blank and JBS Procurement |
| ONB-007 | At least one **Contract Manager** is a Required item for any pack that uses contracts | Over HTTP |
| ONB-008 | The existing validate, publish, second-person submit, rollback and `onboarding.applied` behaviour is unchanged | Existing onboarding and rehearsal tests continue to pass |
| ONB-009 | CSV steps remain, labelled **Bulk import (Advanced)**, with the format and an example | Over HTTP |
| ONB-010 | Plurals read correctly ("1 category", "3 categories") | Over HTTP |

## I. Category ownership (OWN)

| ID | Criterion | Verify |
|---|---|---|
| OWN-001 | The editor lists every category with a primary-owner and backup-owner drop-down; no CSV is needed to assign an owner | Over HTTP |
| OWN-002 | The people list includes everyone the administrator may manage, not only existing Category Owners; choosing someone without the role states that the role will be added and does so on save | Over HTTP; audit shows both grants |
| OWN-003 | Primary and backup are distinguishable and stored (`IsPrimary`); a backup cannot equal the primary | Over HTTP |
| OWN-004 | **Own all current and future categories** shows a preview and applies to every active category now and to each category created later, in the same transaction as the category, without touching ownership others were given | Over HTTP |
| OWN-005 | Bulk selection sets primary or backup for the selected rows and announces the count | Over HTTP; accessibility test |
| OWN-006 | Optional effective dates are accepted and enforced; an ownership not yet effective does not confer rights | Over HTTP with FakeClock |
| OWN-007 | Filter "Needs an owner" shows exactly the categories without a current primary owner; sub-categories show "inherits" | Over HTTP |
| OWN-008 | Existing duplicate primaries are shown as Needs attention and are not changed silently | Over HTTP |
| OWN-009 | Ending an ownership asks for a reason and removes edit rights immediately | Over HTTP |
| OWN-010 | CSV import is available under Bulk import / Advanced and behaves as today | Existing onboarding tests |

## J. Identity group mappings (MAP)

| ID | Criterion | Verify |
|---|---|---|
| MAP-001 | `IGroupMappingService` requires `rolemappings.manage`, applies the delegation policy and scope containment, and is organisation-filtered; an administrator without the permission is refused | Unit and over HTTP, role matrix (**before** the screen ships) |
| MAP-002 | The mapping screen is under Administration > People & Access > Advanced > Identity group mappings and is not required to set up an individual person | Over HTTP |
| MAP-003 | The form asks for provider (by display name), group, access profile, scope and classification; provider key, group key, role code and viewer kind appear only under Details | Rendered-page sweep |
| MAP-004 | A mapping to System or Tenant Administrator is refused with a plain explanation | Over HTTP |
| MAP-005 | A preview states in a sentence who will receive what | Over HTTP |
| MAP-006 | A mapped grant appears on the person's Effective Access page as **From identity group** naming the group; leaving the group ends it at next sign-in as today | Over HTTP with the demonstration identity |
| MAP-007 | End mapping works; "Change" is end-and-add presented as one action | Over HTTP |
| MAP-008 | Bulk CSV import under Advanced still works | Existing tests |
| MAP-009 | Mapped grants count toward Needs attention only where the checklist says so | Over HTTP |

## K. Administration landing page (ADM)

| ID | Criterion | Verify |
|---|---|---|
| ADM-001 | `/Admin` shows only the cards the person has permission for; each has a title, a sentence and two common tasks | Over HTTP, role matrix |
| ADM-002 | A card shows an attention badge (for example "2 reviews waiting") from existing counts | Over HTTP |
| ADM-003 | Search filters pages and people the person may see; it never reveals a page or person outside their permission | Over HTTP, role matrix |
| ADM-004 | Capabilities, Terminology, Configuration Studio, Configuration Reviews and Regional Settings are reachable from their group cards and are unchanged | Over HTTP |
| ADM-005 | The Advanced card exposes role grants, group mappings, the access simulator, history and rollback and bulk imports | Over HTTP |

## L. Security and compatibility (SEC)

| ID | Criterion | Verify |
|---|---|---|
| SEC-001 | No role code, permission code, evaluator rule or audit action name changes, except the documented additions (`access.test_bootstrap`, source `TestBootstrap`) | Frozen-identifier tests, architecture tests |
| SEC-002 | Every action re-checks permission server-side through the existing services; each new page is requested by every role and refused for those without the permission | Role matrix, over HTTP |
| SEC-003 | Tenant and organisation isolation: a person never sees another tenant's or organisation's people, grants, categories or counts | Isolation suite extended to the new pages |
| SEC-004 | The Test-mode bootstrap is refused in Production at the service, not only in the UI | Unit; API test in Production configuration |
| SEC-005 | Secrets, tokens and group claims are never logged or shown; error pages show a correlation id and no stack trace | Existing guards, rendered-page sweep |
| SEC-006 | No evaluation uses the profile table; tests that delete it show unchanged permissions | PRF-008 |
| SEC-007 | CSV import and export remain formula-safe | Existing tests |
| SEC-008 | Existing installations upgrade without regrant: after the upgrade every existing grant, ownership and mapping is unchanged | N-1 to N upgrade rehearsal on SQL Server, extended with grant checks |
| SEC-009 | A regenerated upgrade manifest accompanies the single new table; the pre-flight reports Ready | Upgrade tests |

## M. Accessibility (ACC)

| ID | Criterion | Verify |
|---|---|---|
| ACC-001 | Every new page passes the project's accessibility checks (landmarks, one `h1`, labels, table captions, contrast) | `Accessibility.AssertBasics` and axe |
| ACC-002 | Every task in the end-to-end set can be done with the keyboard alone, with a visible focus indicator | Recorded keyboard walkthrough |
| ACC-003 | Errors are announced and linked to their fields; status is not conveyed by colour alone | Accessibility test |
| ACC-004 | The pages work at 200% zoom and 320 CSS pixels | Responsive test |
| ACC-005 | A screen-reader walkthrough of the Add person wizard and the ownership editor is recorded | **Not Verified** until performed by a person; recorded as such |

## N. Documentation and configuration (DOC)

| ID | Criterion | Verify |
|---|---|---|
| DOC-001 | Each slice updates the affected guides (`SETUP_WIZARD_GUIDE`, `ORGANISATION_ONBOARDING_GUIDE`, `BUSINESS_ADMIN_GUIDE`, `IMPLEMENTATION_PLAYBOOK`) and `tools/check-docs.py` passes | CI |
| DOC-002 | Permission counts in documents are regenerated from the matrix | Script or check |
| DOC-003 | Configuration for profiles and pack requirements has draft, validation, preview, publish, version history, rollback and audit, closed vocabulary, no code | No-code review |
| DOC-004 | Status labels (Implemented, Mocked, Not Verified, Future) are honest for every capability the slice adds | Documentation review |

## P. Usability criteria (the target person)

| ID | Criterion | Verify |
|---|---|---|
| USE-001 | A first-time administrator can answer "How do I set myself up to create a contract?" from Home within one minute, unaided | Moderated test, 5 participants, recorded |
| USE-002 | Plain language: no unexplained jargon outside Advanced; measured reading level at most grade 8 to 9 for step text | Review plus a readability check on the strings |
| USE-003 | Every page states its next action; no dead ends | Over HTTP: each terminal page contains a link or button |
| USE-004 | Every long form has progress ("Step 3 of 7") and can be left and resumed without loss where the platform stores drafts | Over HTTP |
| USE-005 | Confirmation pages state what changed, who was notified (if anyone), and the next step | Over HTTP |
| USE-006 | A person who is refused is told what permission or profile would allow it | Over HTTP |

## E2E. End-to-end scenarios (the acceptance test of the whole branch)

| ID | Scenario | Pass condition |
|---|---|---|
| **E2E-001** | Fresh Test installation, Setup finished with the default choice; the first administrator signs in | Home shows **+ New contract** or a one-click **Set up my access**; within five clicks the administrator has created a contract in an owned category, with no CSV, no role code and no security term used |
| **E2E-002** | Production-configuration installation; administrator chose "administer only" | Home explains that another person must give the Contract Manager profile; a second administrator does so through the wizard; the first can then create a contract without any further step |
| **E2E-003** | Onboarding a new organisation from the Blank pack and from JBS Procurement | Checklist shows different Required items; Publish stays disabled until owners and a Contract Manager exist, then is enabled; the review page lists the facts |
| **E2E-004** | Add a Reviewer, an Approval Authority and a Read Only person; each signs in | Each sees exactly the navigation and pages their profile allows, and the Effective Access page agrees with what they can open |
| **E2E-005** | An existing installation upgraded from the previous release | All prior grants, owners, mappings and releases unchanged; old URLs resolve; People & Access shows prior grants as "Other access" |
| **E2E-006** | Identity group mapping in a Production-like configuration with the demonstration provider standing in for SSO | A mapped group grants the described access at sign-in and removes it when membership ends; no individual setup was needed |
| **E2E-007** | Fresh Test installation; the administrator invites a colleague, gives them the Reviewer profile and chooses **Preview as this user** | The banner "TEST MODE — Previewing as <name>" shows on every page; the colleague's navigation appears; a write attempt is refused with an explanation; **Exit user preview** returns to the administrator; the audit shows start, refusal and end; the colleague is still Invited |
| **E2E-008** | Same installation; the administrator creates a synthetic Approval Authority with **Allow demonstration sign-in**, then a Contract Manager the same way | Both appear on the Demonstration sign-in page; the Contract Manager submits a contract; the Approval Authority signs in and approves it; the Contract Manager cannot approve their own submission; no identity provider was configured |

---

## Definition of done for a slice

1. Every criterion for the slice is **Passed** (or **Not Verified** with a stated reason and an owner for the follow-up).
2. Release build with warnings as errors; unit, architecture, API and integration suites green on SQLite and on real SQL Server; CI green.
3. New pages in the rendered-page sweep, the accessibility checks and the role-matrix request test.
4. Guides and status documents updated; `tools/check-docs.py` and `tools/check-repo.py` pass.
5. The rehearsal ledger updated where the journey changed.
6. The owner has seen the slice working and recorded the decision. Production approval remains external governance and is not declared by any slice.
