# UX redesign: guided administration and task-oriented navigation

Status: **Approved by the owner (2026-10-29) and implemented in slices UX-1 to UX-7 on branch ux/guided-administration, opened from claude/stoic-volta-dvszm5 at commit `5cdf9f7`.** Final acceptance review of 2026-10-29: the 32 items, the automated manual scenario and the three UX gaps it found are in `docs/UX_IMPLEMENTATION_STATUS.md` sections 5 and 5A. What was built, what differs from this design and what is still open is recorded there; decisions are in `decisions/ADR-0039-guided-administration-access-profiles.md`. No permission code or role code changed. Production approval is external governance and nothing here declares it.

Evidence base: the first manual localhost usability test (owner finding, 2026-10-29, twelve observations, plus one additional finding on testing as another user, section 7A) and a read of the code on this branch. Where this document says "the code does X", the file is named so it can be checked. Where something could not be confirmed it is labelled **Not Verified** or listed as a gap in section 16. Acceptance criteria are in `docs/UX_ACCEPTANCE_CRITERIA.md`.

Labels used: **Reused** (exists, used as is), **Wrapped** (exists, new UI calls it), **New (UI)** (new pages or components over existing services), **New (backend)** (a genuine service gap, additive only), **Moved** (same page, new place), **Future**.

---

## 0. Decision summary

1. **Keep the security engine exactly as it is.** Role + scope + category ownership + classification ceiling stays the one authorisation model. No role code, permission code or evaluator rule changes. Profiles are a presentation and composition layer that issue the same grants a person could issue by hand.
2. **Put tasks in front of it.** Everyday navigation shrinks to Home, Contracts, My Actions, Counterparties, Documents and Reports, plus one **Administration** entry. The 31 navigation items that exist today are all kept, regrouped under Administration.
3. **People & Access replaces four screens as the normal path** (Users and roles, Category owners, Viewer scopes, Identity group mappings). Those screens remain, under **Advanced**.
4. **A first administrator ends setup able to do the job.** Setup asks what the administrator will do and grants the matching existing roles; in Demo and Test a single audited action gives a one-person trial full access; Production requires explicit selection.
5. **Onboarding stops saying "Ready to publish" while required access is missing.** Readiness is computed from facts (who holds which role, which categories have owners), and the solution pack decides which items are required.
6. **An administrator can test what a person will experience without an identity provider (Demo and Test only):** a read-only, audited **Preview as this user**, and an **Allow demonstration sign-in** option for synthetic test people (sections 7A and 7B). Production invitation is unchanged.
7. **Most of this is a UX layer.** Eleven narrow, additive backend items are genuinely needed (section 16). One is a security defect to fix before any new screen exposes it (identity group mappings are not authorised in the service).

---

## 1. Current-state problems

### 1.1 Owner observations traced to causes

| # | Owner observation | What the code does | Evidence |
|---|---|---|---|
| 1 | Initial Setup was reasonably clear | Fourteen stages, one purpose each, plain-language results | `docs/SETUP_WIZARD_GUIDE.md` |
| 2 | First administrator was not guided into being an operational user | Setup grants `TenantAdministrator` (tenant-wide, top ceiling) and `OrganisationAdministrator` (first organisation); nothing else, ever. There is no "what will you do" step or hook | `SetupStages.cs` `AdminStage` (about 312-376) and `OrganisationStage` (about 453-467) |
| 3 | The administrator cannot create a contract | `contracts.create` is held outright only by `ProcurementAdministrator`; `CategoryOwner` holds it only with an active ownership of the category; the two administrator roles hold none. Both administrator roles are "administrative" and carry no implicit contract access. The platform also refuses `self_grant`, so the administrator cannot fix this alone | `role-permission-matrix.csv`; `AccessEvaluator.cs` layer 4; `IdentityApi.cs:355` |
| 4 | Onboarding looks publishable while structure, owners, viewers and mappings are empty | "Ready to publish" is shown when the draft is `Validated` and unchanged (`IsPublishable`, `ConfigurationRecords.cs:71`). The validator treats an empty structure as fine, "no owners at all" as a warning, and has no facts about who holds which role | `OnboardingRules.cs:36-155`, `Index.cshtml.cs:29` |
| 5 | Category ownership needs CSV plus a separate Category Owner role | In onboarding, owners are a CSV step (`categoryCode,email,primary`). On `/Admin/Categories` an owner can be chosen from a drop-down, but the drop-down offers only people who **already hold an active `CategoryOwner` role**, and the page always assigns non-primary ownership. Assigning ownership does not check or grant the role | `OnboardingCsv.cs:116`, `Categories.cshtml.cs:34, 85`, `ContractAccess.cs:604` |
| 6 | Identity group mapping needs provider keys, group keys, roles, viewer kinds, scopes and ceilings | The only editing path is the onboarding CSV `providerKey,groupKey,role,viewerKind,categories,businessUnits,sites,ceiling`; no admin page exists. Onboarding may map six roles only | `OnboardingCsv.cs:128`, `OnboardingRules.cs:22-26` |
| 7 | Users and Roles exposes low-level primitives | The grant form shows each role with its stable code, then "Viewer kind (Viewer role only)", "Applies to", five multi-selects of narrowing keys, a classification ceiling, and an approver group | `Admin/Users/Details.cshtml:46-72` |
| 8 | Home does not show the primary job | Home shows the person's role table and status counts. The cards that already exist (My Contracts, Awaiting My Action, Expiring Soon, Reviews Due) are on a different page, Dashboards | `Index.cshtml`; `src/Modules/Ecp.Notifications/Dashboards.cs` `DashboardKeys` |
| 9 | About twenty links at the same level as everyday work | `_Layout.cshtml` builds up to **31** items from permissions; a tenant plus organisation administrator saw 20 | `src/Ecp.Host/Pages/Shared/_Layout.cshtml` |
| 10 | "Access to this tenant" opens Support and Emergency Access | The label is the nav text for `/Admin/Elevations`, a **log** of installation administrators' time-limited access to this tenant (title "Support and emergency access to this tenant"). It is gated on `audit.read_admin` | `_Layout.cshtml`; `src/Ecp.Host/Pages/Admin/Elevations.cshtml` |
| 11 | A first-time administrator cannot answer "how do I set myself up to create a contract?" | Nothing on Home, Contracts or Users says what is missing. The Contracts list shows "Create" to anyone allowed to reach it; `contracts.create` as `S` for `CategoryOwner` passes the menu test but fails at create time | `Contracts/Index.cshtml:71`, `Create.cshtml.cs:113` |
| 12 | Not task-oriented or self-explanatory | The sum of 2 to 11: the model is sound, the surface is a thin skin over its primitives | - |

### 1.2 Additional findings from the review

| ID | Finding | Evidence |
|---|---|---|
| F-1 | **Identity group mappings are not authorised in the service.** `IGroupMappingService` performs no `IAccessService` check; `rolemappings.manage` exists in the matrix but is enforced nowhere except in the emergency-access permission set. Today only the onboarding path (gated by `onboarding.run`) reaches it, so the defect is latent; a new screen would expose it | `IdentityApi.cs:118, 485` |
| F-2 | Role grant and revoke do not require a recent sign-in. Step-up is applied to publish, rollback, elevation and unlock only | `Admin/Users/Details.cshtml.cs:33-52` |
| F-3 | The grant form cannot set effective dates, privileged access or an explicit high ceiling, although the request type supports them | `GrantRequest`, `IdentityApi.cs:33-35` |
| F-4 | There is no organisation-structure admin page. Divisions, business units, sites and legal entities are written only by the onboarding CSV, the organisation-creation CSV and the demo seeder | `OrgStructureApi.cs` |
| F-5 | There is no "effective access" or "why do I have this permission" service. The simulator evaluates eight fixed permissions against a synthetic record | `AccessSimulation.cs:20-36` |
| F-6 | The set-up checklist always has 12 fixed items; the pack changes the state of the "Pack" item only. It does not read role holders, owners or viewers | `OrganisationCreation.cs:302-370` |
| F-7 | Some counts in the documents differ from the matrix (82 or 87 permissions quoted, 94 in the file) | `requirements/02-reference/permission-catalogue.csv` |
| F-8 | `_OnboardingBlocked.cshtml` says Business Configuration Administrators also hold onboarding rights; the matrix lists Tenant and Organisation Administrator only. **Not Verified** which is right | `src/Ecp.Host/Pages/Onboarding/_OnboardingBlocked.cshtml`; matrix line 51 |
| F-10 | **An invited person cannot be tried out.** The demonstration sign-in page lists only users whose status is Active (`DemoIdentityService.ListAsync`), and a person stays Invited until a first verified sign-in. A Test administrator who invites a colleague and grants a role therefore cannot sign in as them, or see what they would see, without configuring a real identity provider. Observed in the second manual test | `src/Ecp.Host/Security/DemoIdentity.cs:ListAsync` |
| F-9 | Cosmetic: a naive plural on the onboarding checklist ("3 categorys") | `Onboarding/Index.cshtml.cs:27-28` |

### 1.3 Top ten usability problems (ranked by harm to the target user)

1. The first administrator cannot create or manage a contract and is not told why or how to fix it.
2. Nothing connects the three things needed to create a contract: a role, a scope and a category ownership.
3. Onboarding reports "Ready to publish" with no people, owners or structure.
4. The grant form speaks the security model's language (role codes, viewer kind, narrowing, ceiling).
5. Category ownership is a CSV, or a drop-down that is empty until someone has been given a role elsewhere.
6. Identity group mappings are reachable only through a CSV that needs provider and group keys.
7. About twenty administration links sit beside everyday work, with technical names.
8. Home shows the role table, not the work; the useful cards are on another page.
9. "Access to this tenant" is a misleading name for a support-access log.
10. No screen answers "what can this person actually do, and why?".

Additional (second test): an administrator has no way to experience a new person's access in Test without configuring an identity provider (F-10); this is addressed in sections 7A and 7B.

---

## 2. Principles and what is deliberately not changed

| Principle (owner A to J) | How this design honours it |
|---|---|
| A. Preserve the security engine | Profiles issue ordinary `GrantAsync` / `AssignOwnerAsync` calls. The evaluator, permission matrix, role codes and the delegation policy are untouched. No second engine |
| B. Task-oriented interface in front | Section 3 (navigation), 4 (Home), 5 to 7 (People & Access) |
| C. Normal users see six things | Home, Contracts, My Actions, Counterparties, Documents, Reports |
| D. One Administration entry | Section 3.3 |
| E. People & Access for normal user management | Section 5 |
| F. Plain-English profiles translate automatically | Section 5.2 |
| G. Advanced stays available | Section 10 and 12 |
| H. CSV is bulk import, not the primary editor | Sections 8, 9, 10 |
| I. Setup ends with a usable first administrator | Section 7 |
| J. No "Ready to publish" while required access is missing | Section 8.3 |

**Not changed:** role codes (frozen by ADR "A+", `docs/ROLE_CATALOGUE_ADR.md`), permission codes, the evaluator's eight layers, scope intersection, classification ceilings, the delegation policy and last-administrator protection, audit action names (frozen by `FrozenIdentifierTests`), second-person publication, existing routes (all old URLs keep working), and the existing grants in any installation. No data migration of grants is required.

---

## 3. Target information architecture

### 3.1 Primary navigation

Labels follow the active terminology (`[[contract|plural]]`, `[[supplier|plural]]`), so a pack that calls contracts "Agreements" shows that word.

| Item | Visible when (existing permission test) | Opens | Source |
|---|---|---|---|
| **Home** | signed in | Home (section 4) | Wrapped: `Index` |
| **Contracts** | `contracts.read` anywhere | `/Contracts` | Reused |
| **My Actions** | holds any of `legal.review`, `legal.respond`, `contracts.submit_legal`, `approvals.action`, `contracts.submit_approval`, `variations.create`, `renewals.create`, or any configuration-review right | `/Actions` (one list: awaiting my action, reviews, approvals, variations, renewals, configuration reviews) | New (UI) over `IDashboardService` card `awaiting-my-action` and the existing queue services; the existing queue pages stay as detail views |
| **Counterparties** | `suppliers.read` anywhere | `/Suppliers` | Reused |
| **Documents** | `contracts.read` anywhere | A documents hub | **Gap UXG-09:** no cross-contract document list exists. Until it does, the item is not shown; documents stay on each contract |
| **Reports** | `contracts.read`, `audit.read` or export rights | A hub listing the existing dashboards, saved list views and exports | New (UI) over existing; **no reporting module exists** and none is promised (section 4.4) |
| **Administration** | holds any administration permission (section 3.2) | The Administration landing page (section 12) | New (UI) |

Plus, at the right: **Notifications** (bell with unread count, existing `INotificationInbox`), the signed-in name, and **Sign out**. The notification link is removed from the main list and shown as a bell.

### 3.2 Which people see "Administration"

The test is the union of the existing permission tests already used by the layout (`users.manage`, `organisations.manage`, `onboarding.run`, `config.*`, `terminology.*`, `capabilities.*`, `packs.read`, `audit.read*`, `notifications.administer`, `ops.manage`, `operations.read`, `setup.run`, `support.elevate`, `categories.manage`, `migration.stage`). A Contract Manager with none of them sees no Administration item at all.

### 3.3 Administration groups (nine cards, eight menu groups)

The brief names eight menu groups in section 1 and nine landing cards in section 10, which differ slightly (**recorded contradiction C-1**). Reconciliation used here: nine cards on the landing page; the menu groups Operations and Security & Audit are shown together as "Operations & Security"; "Contract Setup" is "Contracts & Metadata". The names can be changed in terminology without code.

| Group | Contains (existing capability) | Existing page |
|---|---|---|
| **People & Access** | People (new), Access profiles, Effective access, Approval authorities | `/Admin/People` (new) over Users; links to approver groups |
| **Organisation** | Organisation details, structure (new editor), Regional settings, Branding, Organisation onboarding | `/Admin/Organisations`, `/Admin/Regional`, `/Onboarding` |
| **Contracts & Metadata** | Categories & owners, Contract types, Terminology, Capabilities, Configuration Studio (fields, forms, views), Data migration | `/Admin/Categories`, `/Admin/Terminology`, `/Admin/Capabilities`, `/Admin/Studio`, `/Migration` |
| **Workflows & Approvals** | Workflow configuration, Approval matrices, Approver groups, Configuration reviews (inbox) | `/Admin/Configuration`, `/Admin/Approvals`, `/Admin/ApproverGroups`, `/Admin/Reviews` |
| **Notifications** | Notification delivery, notification policy, development mailbox | `/Admin/Notifications`, form editor, `/Admin/EmailCapture` |
| **Solution Packs** | Packs, assignment, trusted publishers | `/Admin/Packs` |
| **Security & Audit** | Audit events, **Support & emergency access log** (today "Access to this tenant"), Document scanning | `/Admin/Audit`, `/Admin/Elevations`, `/Admin/Documents/Scanning` |
| **Operations** | Health & readiness, Operations dashboard, backups and upgrades, Support access (installation administrators), Installation setup | `/Admin/Health`, `/Admin/Operations/*`, `/Admin/Support`, `/Admin/Setup` |
| **Advanced** | Role grants (per person), Identity group mappings, Access simulator, Configuration history and rollback, raw configuration diagnostics, onboarding CSV bulk import | Existing pages, new entry point |

### 3.4 Where every one of today's 31 navigation items goes

| Today | New place |
|---|---|
| Home | Home |
| Dashboards | Reports (and the cards on Home) |
| Contracts | Contracts |
| Legal review, Approvals, Variations, Renewals | My Actions (each list remains a detail view) |
| Notifications | Bell icon |
| Suppliers | Counterparties |
| Categories | Administration > Contracts & Metadata > Categories & owners |
| Users and roles | Administration > People & Access > People (this page becomes a Wrapped list; its grant form moves to Advanced) |
| Organisations | Administration > Organisation |
| Approver groups | Administration > Workflows & Approvals |
| Audit events | Administration > Security & Audit |
| Organisation onboarding | Administration > Organisation > Set-up checklist (section 8) |
| Data migration | Administration > Contracts & Metadata |
| Support access | Administration > Operations (installation administrators only) |
| **Access to this tenant** | Administration > Security & Audit > **Support & emergency access log** (renamed; the explanation sentence is on the card) |
| Notification delivery | Administration > Notifications |
| Approval matrices | Administration > Workflows & Approvals |
| Configuration reviews | Administration > Workflows & Approvals; also a count on Home and My Actions |
| Workflow configuration | Administration > Workflows & Approvals |
| Configuration Studio | Administration > Contracts & Metadata |
| Health and readiness, Operations | Administration > Operations |
| Document scanning | Administration > Security & Audit |
| Capabilities, Terminology | Administration > Contracts & Metadata |
| Regional settings | Administration > Organisation |
| Solution packs | Administration > Solution Packs |
| Installation setup | Administration > Operations |

### 3.5 Navigation behaviour

* **Administration is a disclosure menu** (button with `aria-expanded`, not a hover menu) that opens the landing page; it never lists more than nine entries. On narrow screens the whole navigation collapses behind one "Menu" button.
* **Breadcrumbs** on every Administration page: `Administration > People & Access > Jeff Judd`. The current page is not a link.
* **Search**: a single "Find a page or person" box on the Administration landing page, searching page titles, descriptions and (for people) names. It is a filter over a static list and `IPeopleDirectory`, not a new index. Contract search stays on `/Contracts`.
* Deep links and bookmarks continue to work: no existing route is removed (section 13).

---

## 4. Home page

### 4.1 Layout

```
Home
[ + New contract ]                                    (only when the person can create one)

Awaiting my action (3)        Expiring soon (2)         Reviews due (1)
  rows ...                      rows ...                   rows ...
My contracts (12)             Recent contracts          Notifications (4 new)
  rows ...                      rows ...                   rows ...

Setup & administration                          (administrators only; a card, not the page)
  Your set-up: 3 of 7 done    [ Set up my access ]  [ Continue organisation set-up ]
```

Cards use the existing `IDashboardService.OverviewAsync` (keys `my-contracts`, `awaiting-my-action`, `expiring-soon`, `reviews-due`, `critical-notice`, and the capability-gated queues). "Recent contracts" is a list of the person's last-updated contracts through `ContractFilter` (new sort, no new service). The role table currently on Home moves to the person's own "My access" page (section 11).

### 4.2 + New contract

Shown when the person **can actually create** in at least one active category. Today `HoldsAnywhere("contracts.create")` is true for a `CategoryOwner` who owns nothing, because the permission is a relationship rule. The Home page therefore needs a small query, **UXG-11**, "categories in which I may create" (the owned categories, or all of them for a Procurement Administrator). Until it exists the button is shown on `HoldsAnywhere` and the create page lists only categories the person may create in (a UI filter over data the page already loads).

If the person cannot create: no button; the card says "You can't create contracts yet. [ How do I get access? ]". For administrators the link goes to **Set up my access** (section 7); for others it names the people who can grant it ("Ask <Organisation Administrator names>", read through the existing role-holder lookup, or the generic sentence when the lookup is not permitted).

### 4.3 Setup & administration card

* Visible only to people with an administration permission. It is a **card**, collapsed to one line once everything required is complete; it never dominates.
* Content: the first up to three incomplete **Required** onboarding items for the person's organisation (section 8), the count of configuration reviews waiting for them, and **Set up my access** when they cannot create a contract.
* Source: the new readiness service (**UXG-06**); until it exists, the existing `IOrganisationCreation.ChecklistAsync` items.

### 4.4 Empty states and honest limits

* No contracts yet: "No contracts yet. [ + New contract ]" or the access sentence above.
* **Reports** is a hub over what exists (the Dashboards cards, saved list views and CSV/XLSX export). The platform has **no reporting module and none is proposed here**; the page says "More reports are not available in this version" rather than showing empty promises.
* **Documents** appears only when a cross-contract listing exists (UXG-09).


---

## 5. People & Access

### 5.1 The model the screen presents

One page per **person** answers four questions: *What will they do? Where? What do they own? How sensitive?* Behind it the same four things already exist:

| Question on screen | Existing engine concept | Stored as |
|---|---|---|
| What will they do? | Role (stable code) | `RoleAssignment.RoleCode` |
| Where? | Scope (organisation, division, business unit, site, category) | `RoleAssignment` scope columns |
| What do they own? | Category ownership (a relationship, not a role) | `CategoryAssignment` (primary or not, effective dates) |
| How sensitive? | Classification ceiling (and privileged access) | `RoleAssignment.ClassificationCeiling`, `PrivilegedAccess` |
| Who decides? | Approver group membership | `RoleAssignment.ApproverGroupKey` |

No new authorisation concept is introduced. The **access profile** is only a named recipe for issuing the right combination of these.

### 5.2 Access profiles

A profile is a plain-English name, a one-sentence description, and a recipe made only of existing role codes, scope choices and ownership. The recipe is the entire definition; a profile **cannot** carry a permission, a new role, a System or Tenant Administrator grant, or a ceiling the granting person is not allowed to give (the existing delegation policy still checks every grant).

| Profile (default label) | Recipe (existing roles) | Scope the wizard asks for | Ownership step | Ceiling default | Notes |
|---|---|---|---|---|---|
| **Contract Manager** | `CategoryOwner` at organisation level **plus** category ownership of the chosen categories | organisation, optionally narrowed | Required: selected categories or all | Internal | `CategoryOwner` alone holds almost nothing; the ownership is what unlocks create, edit, submit, variations, renewals and close (`ActiveCategoryOwnership` relationship). The wizard therefore always issues both and shows both |
| **Contract Administrator** (broad) | `ProcurementAdministrator` **plus** `CategoryOwner` with ownership | organisation | all categories (default) | Confidential | Adds create anywhere, close, migration, export. Offered only to people who may grant it |
| **Reviewer** | `Legal` | organisation | none | Confidential; "Legal Privileged" only by an explicit extra tick (sets `PrivilegedAccess`) | Hidden when the Legal Review capability is off for the organisation (existing `TermSet.IsRoleOffered` / capability test) |
| **Approval Authority** | `Approver` (or `BackupApprover`) | approver group | none | Confidential | Needs an approver group; the step offers existing groups or a link to create one. Sees a contract only while a request for the group is pending |
| **Read Only** | `Viewer` with the kind chosen from the scope step (AllContracts, Category, BusinessUnit, Site, Restricted) | the scope step maps directly to the viewer kind | none | Internal (Restricted viewers capped at Confidential) | The wizard hides the words "viewer kind"; it shows "Everything", "Selected categories", "Selected business units", "Selected sites" |
| **Organisation Administrator** | `OrganisationAdministrator` | organisation | none | the organisation default | Holds no contract rights; the wizard says so and offers to add Contract Manager |
| **Business Configuration Administrator** | `BusinessConfigurationAdministrator` | organisation | none | the organisation default | Cannot grant roles or manage users |
| **Auditor** (optional) | `Auditor` | organisation or tenant | none | Confidential | Read and export; shown to those who may grant it |

Rules:

* A person may hold **several profiles**; each becomes its own set of ordinary grants.
* Profile **labels** come from terminology, so the JBS Procurement pack can show its own names ("Category Manager", "Procurement Lead") without code. A pack may also ship an **AccessProfiles** document (section 5.5) to adjust labels, descriptions, defaults and which profiles are offered.
* The three administrator-grade roles can never be hidden from the people entitled to grant them (existing rule); a profile cannot hide them either.

### 5.3 What "Save access" does

A profile application is an ordered series of existing, individually audited operations, performed by a new application service (**UXG-01**):

1. Check that the person making the change may grant each role in the requested scope (`DelegationPolicy.GrantableBy`, scope containment, ceiling and privileged-access rules). Anything refused is shown **before** saving, on the review step, in plain words.
2. Require a **recent sign-in** (step-up) for the save. Today role grants have none (finding F-2); the new screen adds it at the UI layer with the existing `ReauthenticationContext`. This is a UX-layer policy and needs the owner's confirmation (section 17, Q-4).
3. For each role: `IRoleAssignmentService.GrantAsync` (one `role.granted` audit event each, as today).
4. For each owned category: `ISecuredCategoryService.AssignOwnerAsync` (one `category.ownership_assigned` event each).
5. Record a small **profile application** row so the person's page can later say "from the Contract Manager profile" (section 5.6). If recording fails the grants stand; the person's page then shows them as direct assignments.

There is no distributed transaction across these services (**UXG-01**). The application service therefore (a) validates everything first, (b) applies in a fixed order, and (c) on a failure part-way, reports exactly what was applied and offers to finish or to undo that batch by revoking only what this save created. It never leaves a silent partial state, and it never revokes anything it did not create.

### 5.4 Editing and removing

* **Edit** shows the current grants grouped under profiles where a profile application is recorded, and as "Other access" otherwise. Changing a profile's scope or ownership is implemented as revoke-and-regrant of that profile's grants only (the engine has no in-place edit, **UXG-13**); the review step lists "ends X, adds Y".
* **Remove access** ends the profile's grants and, after a separate confirmation, its ownerships. Ending ownership removes edit rights immediately (existing behaviour, stated on the screen).
* **Last administrator** protection stays: removing the last System or Tenant Administrator is refused with the existing message.
* **Disable person** and **Reactivate** keep their current service calls and confirmation.

### 5.5 Profile definitions are configuration

Profile definitions follow the platform's no-code rules: a configuration kind (`AccessProfiles`) with draft, validation, preview, publish, version history, rollback and audit. Validation is closed-vocabulary: only existing role codes, only scope kinds a role allows (`AssignmentRules.AllowedLevels`), no System or Tenant Administrator, labels as plain text, no scripts or templates. The platform ships a **default catalogue compiled into the product** (the table in 5.2), which is what runs until a pack or tenant publishes an override. This keeps the first slices free of the configuration kind; the kind is a later slice (section 18, S8).

### 5.6 Remembering which profile produced a grant

The engine records `Source` (Manual, GroupMapping, Onboarding, Setup, Demo) and `Reason` per grant, and nothing groups grants. To display "Access profile: Contract Manager" without guessing, one small **additive** table records each profile application (person, profile key, version, the grant and ownership ids it created, who, when). It is display provenance only: **access is always computed from the real grants**, never from this table, so deleting it changes no one's permissions. If the owner prefers no new table, the fallback is to infer the profile by matching a person's grants against the recipe; this is more fragile and is marked as the alternative in Q-3.

---

## 6. Add / edit person wizard

Route: `Administration > People & Access > People > Add person` (and **Change access** on an existing person). Seven steps, one question each, a visible step list ("Step 3 of 7: What can this person access?"), **Back** that never loses answers, and a plain-language **Review** at the end. Every step has a default, a one-sentence explanation and, where there is a choice, an example.

### Step 1: Person

| Field | Behaviour |
|---|---|
| Name | Required, 200 characters (existing limit) |
| Email | Required, 254 characters. If the person already exists the wizard says "Jeff Judd already has an account. Change their access instead?" and continues in edit mode |
| Identity status | Read-only text: "Invited. They become active at their first sign-in" or "Active" |
| **Allow demonstration sign-in** (Demo and Test only; hidden in Production) | Unticked by default. Ticked: the person is created as a **synthetic test person**, active at once, and appears on the Demonstration sign-in page (section 7B). Hidden, with a one-line reason, when demonstration sign-in is switched off on this installation |

Calls `IUserAdministration.InviteAsync` only on Save, not at this step. With **Allow demonstration sign-in** ticked the Demo/Test application service in section 7B is used instead; Production always uses `InviteAsync`.

### Step 2: What will this person do?

Plain-language choices, each a profile (5.2), with the one-sentence description under it:

* Create and manage contracts
* Review contracts (legal review)
* Approve contracts
* Read contracts
* Administer the organisation
* Configure business rules

Multiple choices allowed. Choices the signed-in person may not grant are not shown (existing delegation policy); a line at the bottom says "You can't see a choice you aren't allowed to give."

### Step 3: What can this person access?

Radio: **The whole organisation** (default) / **Selected divisions** / **Selected business units** / **Selected sites** / **Selected categories**. Selection uses searchable checklists with the structure shown as a tree. For Read Only this choice sets the viewer kind. The step shows a live sentence: "Jeff will see contracts in JBS TEST, in business units Energy and Packaging."

Where the organisation has no structure yet the step says so and links to the structure editor (F-4/UXG-08) instead of showing an empty list.

### Step 4: Contract ownership

Shown only when a chosen profile needs it (Contract Manager, Contract Administrator): **All categories** / **Selected categories** / **None**. For "None" the wizard explains that the person can read but not create or edit. A sentence under "All categories": "Owning a category also covers its sub-categories. New top-level categories are not owned automatically; they will appear as 'needs an owner' on the set-up checklist." Primary or backup owner is chosen here for each selected category (default: primary when the category has no owner, otherwise backup).

### Step 5: Information classification

A single plain-language choice, the maximum level the person may see, using the organisation's own level names in order (default from the profile): "Up to Internal", "Up to Confidential". Levels at or above the high threshold show a warning and are only offered to those entitled (Tenant Administrator, existing rule). "Legal Privileged" additionally asks whether the person needs privileged records.

### Step 6: Approvals

Shown only for Approval Authority: **Approver group** (list of existing groups, with their members), **Backup approver** (people list), **Can delegate** (yes or no; delegation eligibility follows the `approvals.delegate` permission the role already holds). If there are no groups the step links to create one and returns.

### Step 7: Review

The **effective permissions in plain English**, computed from what would exist after saving (section 11), for example:

```
Jeff Judd can:
  ✓ Create contracts                         in Energy and Packaging
  ✓ View all contracts                       in JBS TEST
  ✓ Edit contracts                           in Energy and Packaging (owner)
  ✓ Upload documents                         in Energy and Packaging
  ✓ Create variations                        in Energy and Packaging
  ✓ Configure the organisation               in JBS TEST
Jeff Judd cannot:
  ✗ Perform legal review
  ✗ Approve contracts
Highest classification: Confidential
This will: add 2 access grants, assign 2 category ownerships
[ Save access ]   [ Back ]   [ Advanced: show exact grants ]
```

Save requires the step-up, then shows a confirmation page: what was done, a link to the person's Effective Access page, and the next action ("Ask Jeff to sign in", "Add another person").

### Errors, empty states and advanced

* Refusals use the existing messages translated into words (e.g. `self_grant`: "You can't give yourself access. Ask another administrator, or see 'Set up my access'"; `last_administrator`: "This is the only administrator; add another before removing this one").
* **Advanced** (step 7 and the person page) opens the existing grant form, unchanged, with every field (role code, viewer kind, exact scope lists, ceiling, approver group, reason) and the explanation of each. It is the escape hatch for exact scopes, division/site administrators, effective dates and delegated administration.

---

## 7. Initial administrator bootstrap

### 7.1 The facts that shape it

* Setup grants the first administrator `TenantAdministrator` and `OrganisationAdministrator` through the trusted path (`GrantTrustedAsync`, source `Setup`). Nothing operational is granted.
* A person cannot grant to **themselves** (`self_grant`), so after setup the administrator cannot repair this through the screens, whatever they are allowed to grant others. This is the root cause of observations 2, 3 and 11.
* The trusted grant path is not reachable from request input; only setup, onboarding and the demo seeder use it.

### 7.2 Setup: "What will you do in this organisation?"

A new step directly after the Initial administrator stage (and in the organisation-creation form when an initial administrator is named). It uses the profile list from 5.2 with checkboxes, and applies the choice with the same trusted path setup already uses (source `Setup`, one `role.granted` event per grant, plus ownership through the trusted owner call):

| Choice | Grants |
|---|---|
| Administer the platform and organisation | already granted |
| Create and manage contracts | Contract Manager (ownership of all categories once they exist) |
| Review | Reviewer |
| Approve | Approval Authority (offers to create a first approver group) |
| Read only | Read Only |

* **Production:** nothing is pre-ticked. The step is an explicit, itemised choice and records who chose what in the setup report. Because the setup session has no signed-in person, the grants carry the same source and evidence as the administrator grant itself. A Production installation may legitimately choose "administer only" (a pure administrator); the step then says so, and the Home page tells that person that someone else must be given the Contract Manager profile.
* **Demo and Test:** "Create and manage contracts" is pre-ticked.

If categories do not exist yet when the step runs (the usual case), ownership is recorded as an **intent** that the organisation set-up checklist applies as soon as categories exist (section 8), shown as a Required item "Give <name> ownership of categories". This avoids a role with nothing to own.

### 7.3 After setup: "Give me full Test-mode access" (Demo and Test only)

A button on the person's own page and on the Home "Set up my access" card.

| Rule | Detail |
|---|---|
| Availability | Only when `HostModeAccessor.Mode` is Demo or Test. In Production the button is absent and the service refuses (throws a domain error, the pattern used by `EnablementAdministration`) |
| Labelling | A prominent "Synthetic data and testing only" banner on the confirmation, the same wording as the environment banner |
| What it grants | Contract Manager (ownership of all current categories), Contract Administrator, Reviewer, Approval Authority (in a new or chosen "Test approvers" group), Read Only is implied. Ceiling **Confidential** unless the person ticks "Legal Privileged" separately |
| How | A new trusted application service (**UXG-03**) calling `GrantTrustedAsync` and `AssignOwnerTrustedAsync`, because the delegated path refuses self-grant. It is the only place the self-grant rule is bypassed, and only in Demo and Test |
| Recent sign-in | Required |
| Audit | One `access.test_bootstrap` event (new, added to the frozen audit list) naming the person, the profiles and the mode, plus the ordinary `role.granted` and `category.ownership_assigned` events. The assignment source is a new value `TestBootstrap` (stored as a string, so additive; no migration) |
| Reversible | "Remove Test-mode access" ends exactly the grants this action created |

**Honest limit for a one-person trial.** Full access does not let one person approve their own submission: separation of duties still applies (`approvals.action` is denied to the submitter; configuration publication needs a second person when governance is enforced). In Demo and Test the screen offers **Add a test colleague** (a synthetic person with Approval Authority) so one laptop user can rehearse an approval. This is stated on the confirmation page so nobody mistakes the refusal for a defect.

### 7.4 Production: guided explicit selection

In Production the administrator uses the People wizard (section 6) for others and, for themselves, asks another administrator, or uses the setup-time step above. The Home card explains: "Someone else must give you the Contract Manager profile. Administrators can't give themselves access." and names the people who can.

---

## 7A. Test and Demo user preview ("Preview as this user")

### 7A.1 Why

In Test a person who is merely **Invited** cannot sign in with the demonstration identity (it lists Active people only, F-10), so an administrator cannot see the result of the access they just gave. Real identity-provider configuration is the wrong price for a trial. Preview lets an authorised administrator **see the application as that person would**, using that person's real effective access, without touching the person's identity.

### 7A.2 What the administrator does

1. From **People**, the person's page, or their Effective Access page, choose **Preview as this user**. (Before the new People screens exist, the same action is on the existing `/Admin/Users/Details` page.)
2. A short confirmation explains: "You will see the application as Jeff Judd sees it. Preview is read-only. Nothing you do changes Jeff's account." Sign in again if the recent-sign-in window has passed.
3. The application opens at Home **as that person**: their navigation, their cards, their buttons. A **permanent banner** runs across the top of every page, including error pages:

```
TEST MODE — Previewing as Jeff Judd      Read-only · ends in 28 min        [ Exit user preview ]
```

4. **Exit user preview** (a button, keyboard reachable, always the first control after the skip link) returns to the administrator's own session and to the person's page.

### 7A.3 Who may preview whom (all rules enforced in the service, not the UI)

| # | Rule | Reason |
|---|---|---|
| 1 | The host mode is **Demo or Test** (`HostModeAccessor`). In Production the service refuses, the middleware ignores the claim, and no control is rendered | Requirement 7; defence in depth |
| 2 | The previewer holds `users.manage` over an organisation of the target, as `IPeopleDirectory.GetManageableUserAsync` already tests | No new permission code; same population that manages the person |
| 3 | The previewer has signed in recently | Step-up, as for other sensitive actions |
| 4 | The target is in the **same tenant** and is Active or Invited (not Disabled), is not the previewer, and holds no installation-scope role (System Administrator is never previewable) | Requirement 8; a preview cannot cross a tenant |
| 5 | **Containment: every active grant of the target could have been granted by the previewer** under the existing delegation policy (role grantable, scope contained in the previewer's scope, ceiling below the high threshold unless the previewer is a Tenant Administrator, privileged access only for a Tenant Administrator) | "You can preview only what you could give." A preview must not let someone see more than they could grant, so it cannot be used to read records above the previewer's own authority |
| 6 | No preview during support or emergency elevation, and no preview inside a preview | Keeps the two mechanisms separate and the actor unambiguous |

A refusal is explained in words, for example: "Priya has access you aren't allowed to give (Legal Privileged records). Ask a Tenant Administrator to preview her."

### 7A.4 How it works (a pattern the platform already has)

Support and emergency access already put a claim in the signed session and **re-validate it against the database on every request** (`EcpClaims.ElevationId`, `SessionMiddleware`). Preview follows that pattern:

* A small record, **PreviewSession** (an additive table: id, tenant, previewer, target, started, expires, ended, ended-reason, count of refused writes, correlation id), is created at start.
* The session carries `ecp:preview = <id>`. On every request the middleware checks that the mode is not Production, the record is open and not expired, the previewer is still active and still holds `users.manage`, and the target is not Disabled. Any failure ends the preview and returns the person to their own session.
* While valid: `CurrentUser` is the **target**, so the access evaluator, capability restrictions, notifications, dashboards and navigation are exactly what the target would get, with no special-casing and no new evaluator rule. `ActorContext` records `Type = Preview` and `ActorId = <previewer>` (a new string value of `ActorType`, additive: the audit column is a string, and the hash chain is unaffected).
* The tenant is the previewer's tenant, unchanged, so all existing tenant filters and row-level security still apply.
* The target's **identity and credentials are never read or written**: no sign-in is recorded, no external-identity link, no status change, no token. An Invited person stays Invited.

### 7A.5 Read-only

Preview shows the experience; it does not act. Three layers enforce it:

1. **Request layer:** any request other than GET/HEAD, except Exit preview and sign-out, is refused with a friendly page ("Preview is read-only. To act as Jeff, use demonstration sign-in") and recorded.
2. **Unit-of-work layer (defence in depth):** in a preview scope the unit of work may write **audit rows only**; any other change throws. This also covers handlers that write on a read (for example "mark as read" or "last viewed").
3. **Review of reads with side effects:** the implementation must inventory GET handlers that write (gap UXG-20) and make them side-effect-free in preview.

Because it is read-only, preview cannot be used to satisfy a second-person rule or to approve or publish one's own work; separation of duties is unaffected. Acting as another person is **not** offered (Q-10); for that, section 7B gives a synthetic person who signs in normally.

### 7A.6 Audit (every session)

| Event | When | Recorded |
|---|---|---|
| `preview.started` | Start | previewer, target, tenant, organisations, expiry, correlation id |
| `preview.write_refused` | Each refused write | method and route pattern only (no body, no values), preview id |
| `preview.ended` | Exit, expiry, sign-out, mode change, or either person becoming disabled | duration, end reason, count of refused writes |

All three are new audit actions, added to the frozen list. Authorisation denials during preview carry `ActorType = Preview` with the previewer's id, so the audit browser can show "Admin Name (previewing Jeff Judd)". The Security & Audit page gains a "Previews" filter. The target is not notified; their own sign-in history is untouched.

### 7A.7 Limits to state on the screen

* The preview shows the target's own notifications and saved views, read-only and without marking anything read. This is acceptable only because Demo and Test hold synthetic data.
* Preview is not an authorisation test of the platform's secrets or identity provider; real sign-in through a provider stays **Not Verified** until performed.
* Maximum 30 minutes by default (configurable up to 60); "Extend" requires a fresh sign-in.

---

## 7B. Demonstration sign-in for synthetic test people

### 7B.1 The option

In Demo and Test, the Add person wizard (step 1) and the existing invite form offer **Allow demonstration sign-in**. When ticked:

* The person is created as a **synthetic test person**: active immediately, linked to the demonstration provider, listed on the Demonstration sign-in page with their roles, and carrying a visible **Synthetic** badge in People and in the banner of environment pages.
* The wizard then grants access exactly as for any person (profiles, scopes, ownership, ceiling).
* The confirmation says: "Jeff Test can now be chosen on the sign-in page under Demonstration sign-in. Use **Preview as this user** to look without signing out."

This lets a test administrator create several personas (a Reviewer, an Approval Authority, a Read Only user) and switch between them without any Entra ID or other provider.

### 7B.2 Rules

| Rule | Detail |
|---|---|
| Availability | Only when the host mode is Demo or Test **and** demonstration sign-in is enabled on this installation (`DemoIdentityService.IsEnabled`). Otherwise the checkbox is hidden and the page says why, and offers Preview |
| Service | A Demo/Test application service calling the existing trusted creation (`IUserService.CreateActiveTrustedAsync(displayName, email, "demo", DemoIdentityService.Issuer, ...)`) with a source naming the test persona. It refuses in Production at the service |
| Permission | `users.manage` over the organisation, as for any invitation |
| Existing invited person | A person already **Invited** can be switched on with a confirmation ("Jeff will no longer wait for a first sign-in through your identity provider"). Its interaction with a later real first sign-in is **Not Verified** and must be tested before this ships (UXG-18). Until then the control is offered at creation only |
| Audit | The existing `user.created` event with the persona source; the grants the existing `role.granted` events |
| Sign-in list hint | The Demonstration sign-in page gains an empty-state sentence: "Missing someone? People who are only invited don't appear here. Ask an administrator to allow demonstration sign-in for them, or to preview as them" |

### 7B.3 Production is unchanged

Production invitation remains `IUserAdministration.InviteAsync`: invite-only, linked at the first verified sign-in through the configured identity provider. The checkbox, the service path and the Preview controls do not exist in Production, and requests to them are refused.

---

---

## 8. Organisation onboarding

### 8.1 The aim

Replace "all optional text boxes, then Validate, then Publish" with a checklist built on **business outcomes**, where the state of each item is computed from facts, and where "Ready to publish" is impossible while a Required item is open.

### 8.2 Sequence

| # | Item | What the person does | Wraps (existing) | State rule |
|---|---|---|---|---|
| 1 | Organisation details | Name, country, locale, time zone, currency, reference prefix | organisation creation / setup | Complete when created |
| 2 | Solution pack | Choose the starting pack | onboarding `pack` step | Required; Complete when chosen |
| 3 | Regional settings | Review the proposed regional profile | `/Admin/Regional` | Complete when a release is published |
| 4 | Organisation structure | Divisions, business units, sites | the CSV step, plus the new form editor (**UXG-08**) | Pack-defined: Required, Recommended or Optional; Needs attention if a role needs a scope that does not exist |
| 5 | Contract portfolios / categories | Add categories (from the pack or by hand) | `/Admin/Categories`, `categories` step | Required when the pack uses categories |
| 6 | Contract types | Add contract types | `types` step | Recommended |
| 7 | People & Access | Add the people who will use the system | People wizard | Required: at least one **Contract Manager** exists |
| 8 | Contract owners | Every active category has an owner | Category ownership editor (section 9) | Required: each category has a primary owner who holds the `CategoryOwner` role; **Needs attention** otherwise |
| 9 | Approval authorities | Approver groups with members | `/Admin/ApproverGroups` | Required when the Approvals capability is on |
| 10 | Workflow & approvals | Review the workflow and approval matrix | form editors | Required when the pack ships them (published release exists) |
| 11 | Notifications | Review the notification policy | notification form | Recommended |
| 12 | Review set-up | Read the plain-language summary and the simulation | onboarding `review` step | Required; Not available until 1 to 10 are Complete |
| 13 | Publish | Publish, or submit for a second person | existing publish / submit | Disabled with a reason while any Required item is open |

### 8.3 States

| State | Meaning | Blocks Publish? |
|---|---|---|
| **Required** | Needed for the selected pack to be usable | Yes |
| **Recommended** | Strongly advised; the person may continue after acknowledging | No, but listed on the review page |
| **Optional** | Nothing to do unless wanted | No |
| **Complete** | The fact is true (computed, not ticked) | - |
| **Needs attention** | Started but contradicted by a fact: a category without an owner, an owner without the role, a role needing a division that does not exist, a validation error | Yes |

Wording rules: no "Ready to publish" appears unless every Required item is Complete **and** the draft validates. The review page lists each open Required item with a link to fix it. Counts say "1 category / 3 categories".

### 8.4 Who decides what is Required

The solution pack. A pack ships an **onboarding requirements** document naming, for each of the items above, Required, Recommended or Optional (**UXG-06**; pack documents are data, so no code). Without one, the platform default is: 1, 2, 3, 5, 7, 8, 12, 13 Required when the pack has categories; 9 and 10 Required when their capability is on. This is a design decision for the owner (Q-5).

### 8.5 Computing readiness

A new read-only service (**UXG-06**) produces the per-item state from existing sources:

| Fact | Existing source |
|---|---|
| Structure counts | `IOrganisationStructureService.GetAsync` |
| Categories and their owners | `ICategoryAdministration.ListAllAsync` (`CategoryItem.Owners`) |
| Who holds which role in the organisation | `IRoleAssignmentService.ListAsync(activeOnly)` |
| Approver groups with members | `IApproverGroupService.ListAsync` |
| Published releases and open drafts | `IConfigurationReleases.LatestAsync`, `OpenDraftsAsync` |
| Owners lacking the `CategoryOwner` role | cross-check of the two lists above (a new rule, no new data) |

The existing onboarding validator keeps its job (is the draft well-formed). It is extended with these role-holder facts so that `no_owners` and `no_access` become errors for Required items instead of warnings.

### 8.6 Reused

The onboarding configuration kind, its draft/validate/publish/rollback/second-person flow, the applier (idempotent upserts with a ledger), the review and simulation page, and the set-up checklist page all remain. The new checklist is a different **front page** over them; the CSV steps stay as **Bulk import** under Advanced.

---

## 9. Category ownership editor

Route: `Administration > Contracts & Metadata > Categories & owners`.

```
Categories and owners                         [ Assign all to one owner ▾ ]  [ Bulk import (CSV) ]
Search [____]   Show: ( ) All  ( ) Needs an owner

Category       Primary owner       Backup owner        Effective        Status
Energy         [Jeff Judd ▾]       [Select ▾]          from today       ✓ Complete
 └ Solar       (inherits Energy)                                         ✓
Packaging      [Select ▾]          [Select ▾]                           ⚠ Needs an owner
```

| Requirement | Design |
|---|---|
| Form, not CSV | One row per category from `ISecuredCategoryService.ListAsync`; two drop-downs per row |
| People list | **All** people the administrator may manage, not only those who already hold `CategoryOwner`. Choosing someone who lacks the role shows an inline note, "Jeff doesn't have the Contract Manager profile yet; saving will add it", and the save issues both (UXG-01/UXG-04). Today the list offers only existing `CategoryOwner` holders, which is why it appears empty |
| Primary vs backup | Primary uses `isPrimary=true`; backup is a non-primary owner. The page today always passes `false`, so this needs the owner API to accept it from the UI (it already takes the argument) |
| Assign all to one owner | One action, with a preview "Jeff will own 14 categories" |
| Bulk select | Check rows, then "Set primary owner" or "Set backup owner" |
| Effective dates | An optional "from / until" per assignment (for example a holiday cover). The secured API has no dates; the trusted one has (**UXG-04**); the secured API gets optional dates |
| Search and filter | Text filter and "Needs an owner" filter (a client-side filter over the loaded list) |
| Validation | Primary owner is required for each active category when the item is Required; a backup cannot equal the primary; ownership covers sub-categories and the row says "(inherits ...)"; ending an owner asks for a reason |
| One primary | The model has no uniqueness rule. The editor enforces one primary per category in the UI and surfaces existing duplicates as Needs attention rather than silently changing them |
| CSV | `Bulk import (CSV)` opens the existing onboarding "owners" step. It is labelled Advanced and shows the format and an example |
| Audit | Existing `category.ownership_assigned` and `category.ownership_ended` |

---

## 10. Identity group mappings (Advanced)

Route: `Administration > People & Access > Advanced > Identity group mappings`.

### 10.1 Purpose, said on the page

"Group mappings give people access automatically when they sign in through your company's single sign-on, based on the groups they belong to there. You do **not** need them to set up an individual person. Use them when you run Production with a corporate identity provider and want group membership to drive access."

### 10.2 Guided form

| Field on screen | Plain wording | Behind it |
|---|---|---|
| Identity provider | List of configured providers by display name ("Company sign-in") | provider key (shown only under "Details") |
| Group | "The group name your provider sends" with a note that it is case-sensitive and an example | group key |
| Access profile | The profile list of 5.2, limited to roles mappings may carry | role code (+ viewer kind from the scope choice) |
| Scope | Same scope control as the person wizard | organisation, narrowing lists |
| Classification | Same plain control | ceiling |
| Approver group | Only for Approval Authority | approver group key |

Rules from the engine, restated in plain words: a mapping can never give System or Tenant Administrator ("mapping_not_allowed"); onboarding's list of mappable roles (Viewer, CategoryOwner, Legal, Auditor, ProcurementAdministrator, DivisionSiteAdministrator) is a rule of the **bulk import**, not of the service, which accepts any role except those two; the guided form offers the same six by default and the owner decides whether to widen it (Q-6).

### 10.3 Other behaviour

* A list of existing mappings with provider, group, profile in words, scope in words, state (active or ended), and **End mapping** (existing `DeactivateAsync`). There is no edit in the engine, so "Change" is end-and-add, shown as one action (**UXG-13**).
* A **preview**: "People in group *Finance-Viewers* will be able to read contracts in business unit FIN, up to Internal." Mapped grants appear on the person's Effective Access page as **From identity group** with the group named.
* Raw keys (provider key, group key, role code, viewer kind) appear only in a collapsed "Details" block.
* **Bulk import (CSV)** opens the existing onboarding "mappings" step.
* **Authorisation first (UXG-05, finding F-1).** Before this screen exists, `IGroupMappingService` must enforce `rolemappings.manage` (Tenant Administrator today) and apply the delegation policy and scope containment, with tests. Without that, a page for any administrator would let them create a mapping that grants a role they could not grant by hand.
* Mappings are not organisation-filtered by the service today; the screen filters by the organisations the administrator may manage, and the service gains the same check.

---

## 11. Effective Access summary

Every person has an **Effective access** page (and the signed-in person has **My access**, replacing the role table now on Home).

### 11.1 Layout

```
Jeff Judd   jeff@example.test   Active      [ Change access ]  [ Disable ]
Access profiles: Contract Manager · Organisation Administrator
Highest classification: Confidential        Approval groups: none

What Jeff can do                          Where / what                         Why
 ✓ Create contracts                       Energy, Packaging                    Owns the category (Contract Manager)
 ✓ View all contracts                     JBS TEST                             Contract Manager profile
 ✓ Edit contracts                         Energy, Packaging                    Owns the category
 ✓ Upload documents                       Energy, Packaging                    Owns the category
 ✓ Configure the organisation             JBS TEST                             Organisation Administrator
What Jeff can't do (and what would allow it)
 ✗ Perform legal review                   Needs the Reviewer profile
 ✗ Approve contracts                      Needs the Approval Authority profile
What Jeff owns         Energy (primary), Packaging (backup)
Where the access came from      ▸ Direct assignment  ▸ Access profile  ▸ Category ownership  ▸ Identity group  ▸ Administrator role
▸ Advanced grants (exact roles, scopes, ceilings, dates)
```

### 11.2 Data

| Part | Source | Status |
|---|---|---|
| Grants with scope, ceiling, dates, `Source`, `Reason`, granted-by | `IRoleAssignmentService.ListForUserAsync` | Reused |
| Ownerships | `ICategoryAdministration` / `ISecuredCategoryService` (current owners per category) | Reused (a "categories owned by person X" query is small, UXG-04) |
| What a role can do | `PermissionMatrix.PermissionsOf(role)`, `Info(permission).Description` | Reused |
| Whether it applies where | the evaluator's own `GrantsFor` per permission for the person's subject | **New (backend) UXG-02**: a read-only report service that enumerates permissions per person by calling the existing evaluator; no new rules |
| Why (provenance) | `RoleAssignment.Source` (Manual, GroupMapping, Onboarding, Setup, Demo, and the new TestBootstrap); mapping id; the profile application row (5.6); ownership as a relationship | Reused plus the optional profile row |
| Plain wording of 94 permissions | a new **ability catalogue** (about 15 to 20 abilities such as "Create contracts") mapping each permission to an ability; every permission must map to one ability or be marked technical, enforced by a test (**UXG-14**) | New (content) |
| Can't-do suggestions | the ability's profile list ("allowed by Reviewer") | New (content) |

### 11.3 Rules

* Computed on read from the real grants; **never** from the profile table.
* An ownership without the `CategoryOwner` role, or the role with no ownership, is flagged **Needs attention**: "Jeff owns Energy but doesn't have the role that lets owners edit." (the silent-failure case, finding 5).
* A grant whose effective dates have ended or not started is shown under "Past and future access", not in the green list.
* Denials are explained in words ("A record above Confidential is hidden, not refused"), reusing the evaluator's layer names; the existing technical reason is available under Advanced.
* Sensitive reasons (what a classification ceiling hides) are shown to those who may see the person's grants (`users.manage`), not to others.

---

## 12. Administration landing page

Route: `/Admin` (today there is none). Nine cards in a responsive grid; each card has a title, one sentence, the **two most common tasks** as links, and a badge when something needs attention (for example "2 reviews waiting"). The cards shown depend on the person's permissions, so an Organisation Administrator sees about six.

| Card | One-sentence description | Common tasks |
|---|---|---|
| People & Access | Who can do what | Add a person · See who has access |
| Organisation | Your organisation, structure and regional settings | Set-up checklist · Structure |
| Contracts & Metadata | Categories, owners, fields, wording | Categories & owners · Terminology |
| Workflows & Approvals | How contracts move and who approves | Workflow · Approval matrix |
| Notifications | What people are told and how | Notification policy · Delivery |
| Solution Packs | Starting configurations and trusted publishers | Installed packs · Assign a pack |
| Security & Audit | Evidence of what happened | Audit events · Support & emergency access log |
| Operations | Health, backups, upgrades, setup | Health & readiness · Operations |
| Advanced Configuration | Exact grants, mappings and raw configuration | Role grants · Identity group mappings |

Above the cards: the search box, the **Setup progress** (section 8) and, for installation administrators, a banner when Support access is active (the banner already exists).

---

## 13. Migration strategy: a UX layer over existing services

### 13.1 What is added and what is not

| Kind | Items |
|---|---|
| **No schema, role, permission or grant change** | Navigation, Home, Administration landing, People & Access screens, wizards, category editor, group-mapping screen, onboarding checklist |
| **Additive backend** (section 16) | Access-profile application service (UXG-01); effective-access report (UXG-02); Test/Setup bootstrap service (UXG-03); owner API options (UXG-04); mapping authorisation (UXG-05); onboarding readiness (UXG-06); structure form editor (UXG-08); "categories I may create in" (UXG-11) |
| **Additive data** | one table for profile applications (5.6); one new `AssignmentSource` value (string column, no migration); new audit action `access.test_bootstrap` (added to the frozen list); an `AccessProfiles` configuration kind and a pack requirements document (later slices) |
| **Resource files** | the ability catalogue (UXG-14) |

The upgrade manifest must be regenerated for the one new table (`tools/add-migration.sh`), as for any migration. Existing grants, ownerships, mappings and releases are untouched and need no conversion.

### 13.2 Page-by-page treatment

| Existing page | Treatment |
|---|---|
| `/` (Home) | **Replaced** by the task Home; its role table becomes "My access" |
| `/Dashboards` | **Reused** as the Reports hub and the detail view for Home cards |
| `/Contracts`, `/Contracts/*` | **Reused**; the "Create" button follows the 4.2 rule |
| `/Legal`, `/Approvals`, `/Variations`, `/Renewals` | **Reused** as detail views behind **My Actions** |
| `/Suppliers` | **Reused**, relabelled Counterparties through terminology |
| `/Notifications` | **Reused**, reached from the bell |
| `/Admin/Users` (list, invite) | **Wrapped** by `/Admin/People` (same service calls, new list, search, profile summary) |
| `/Admin/Users/Details` grant form | **Moved under Advanced** ("Role grants"), unchanged; the new person page links to it |
| `/Admin/Categories` | **Wrapped** by the ownership editor; the old page stays reachable |
| `/Onboarding/*` | **Wrapped** by the new checklist; the CSV steps move under **Bulk import** |
| `/Admin/Organisations/*` | **Reused**; the existing 12-item checklist is merged into the new one |
| Identity group mappings (no page today) | **New** page under Advanced, after UXG-05 |
| `/Admin/Simulation` | **Moved under Advanced** ("Access simulator"); also linked from the review step |
| `/Admin/Elevations` | **Moved and renamed** "Support & emergency access log" (title unchanged) |
| `/Admin/Support` | **Moved** under Operations (installation administrators) |
| `/Admin/Reviews`, `/Admin/Configuration`, `/Admin/Studio`, `/Admin/Capabilities`, `/Admin/Terminology`, `/Admin/Regional`, `/Admin/Packs`, `/Admin/Approvals`, `/Admin/ApproverGroups`, `/Admin/Notifications`, `/Admin/Audit`, `/Admin/Documents/Scanning`, `/Admin/Health`, `/Admin/Operations/*`, `/Admin/Setup`, `/Migration/*` | **Moved** in navigation only; pages and URLs unchanged |

### 13.3 Compatibility rules

1. **No route is removed or renamed.** Bookmarks, guides and tests keep working. A page that has a wrapper gets a banner "Easier way: People & Access" until the owner decides otherwise.
2. **Authorisation is checked where it is today.** New screens call the same services, which re-check permissions server-side. The UI hides what a person cannot do, but hiding is never the control.
3. **Guides, tests and screenshots** are updated slice by slice; `tools/check-docs.py` already fails a guide that names a missing page.
4. **Rollout** is behind one configuration switch per slice in Test first (the platform's existing mode and configuration facilities), so the old layout remains available until each slice is accepted.

---

## 14. Accessibility, localisation and usability requirements

Targets: WCAG 2.2 AA, plus the project's accessibility and localisation requirements (`.claude/skills/accessibility-review`, `docs/TESTING.md` section 11).

| Area | Requirement |
|---|---|
| Wizards | A step list with `aria-current="step"`; one `h1` per step; focus moves to the step heading after Next/Back; **Back** preserves answers; errors in an error summary linked to the fields (`aria-describedby`), never colour alone; no time limits except the existing sign-in window, which is announced |
| Disclosure navigation | The Administration menu is a button with `aria-expanded` and `aria-controls`; opens on click/Enter/Space; Escape closes and returns focus; no hover-only behaviour; usable at 320 CSS pixels and at 200% zoom |
| Tables and pickers | Real tables with captions and headers; every drop-down has a visible label; searchable pickers are native `select` with a filter box, or the ARIA combobox pattern, tested with a keyboard only; bulk selection announces counts |
| Status | Badges carry text ("Complete", "Needs attention"), not colour alone; contrast 4.5:1 (the accent-colour validator already refuses weaker accents) |
| Plain language | Reading age about 12 to 14 years; no role codes, "viewer kind", "scope intersection" or "claim" outside Advanced; each wizard step has one example; sentences name the person and the place ("Jeff will see contracts in JBS TEST") |
| Help | An inline "Why am I being asked this?" disclosure on every step; no help that exists only in a PDF |
| Empty and error states | Every empty list says what it is, why it is empty and the next action; every refusal says what happened, why, and what to do |
| Responsive | Cards and tables reflow; tables scroll in a labelled region; the navigation collapses behind a Menu button at 40rem and below (the stylesheet has one breakpoint today) |
| Localisation | All labels through terminology and the localisation resources; dates and numbers in the organisation's locale and time zone; Australian English in the JBS reference pack; UTC storage unchanged |
| Reduced motion | No animation beyond the existing reduced-motion rule |
| Testing | Each new page is added to the rendered-page sweep (no raw `[[...]]` tokens, no role codes outside Advanced), the axe-based checks, and a keyboard walkthrough recorded as evidence |

---

## 15. What is reused

| Existing component | Used for |
|---|---|
| `IAccessService`, the evaluator, `PermissionMatrix`, `DelegationPolicy` | Every authorisation decision; the visibility test for each menu item and button |
| `IRoleAssignmentService.GrantAsync` / `RevokeAsync` / `ListForUserAsync` | All profile grants and the person page |
| `IUserAdministration`, `IPeopleDirectory` | Invite, disable, reactivate, people lists, display names, owner candidates |
| `ISecuredCategoryService`, `ICategoryAdministration` | Category list, owners, assign, end |
| `IGroupMappingService` (after UXG-05) | Group mappings |
| `IApproverGroupService` | Approval authorities |
| `IAccessSimulator` | Review-step preview (limited to its eight permissions until UXG-02) |
| `IDashboardService` (ten cards) | Home and My Actions |
| `IOrganisationStructureService`, `IOrganisationCreation.ChecklistAsync` | Structure and the existing checklist items |
| `IOnboardingService`, applier, validator, review page | Onboarding drafts, validation, publish and rollback |
| `IConfigurationReleases` and the configuration lifecycle | Draft, validate, preview, publish, history, rollback, audit for any new configuration kind |
| `HostModeAccessor`, `EcpMode` | Demo/Test/Production gating of the bootstrap |
| `ReauthenticationContext` | Step-up for access changes and for starting a preview |
| `SessionMiddleware`, `EcpClaims.ElevationId` pattern, `ActorContext` | The preview claim, per-request re-validation and actor recording |
| `IUserService.CreateActiveTrustedAsync`, `DemoIdentityService` | Synthetic test people who can sign in with the demonstration identity |
| Terminology and `TermSet.IsRoleOffered` | Labels; hiding roles a capability does not use |
| `tools/check-docs.py`, rendered-page sweep, `Accessibility` test helper | Guide and page guards |

---

## 16. Backend gaps (genuine, narrow, additive)

None changes the security model. "Blocks" says which part of the UX cannot ship without it.

| ID | Gap | Why it matters | Blocks |
|---|---|---|---|
| **UXG-01** | No profile concept; `GrantAsync` makes one assignment per call; no shared transaction between grants and ownership. **Resolved:** `IUnitOfWork` gives one transaction across module contexts and buffers audit events, so a profile application is atomic (all or nothing); no partial state needs reporting | "Save access" must apply several operations safely | People wizard, bootstrap |
| **UXG-02** | No effective-access service: nothing lists a person's permissions with sources; the simulator covers eight permissions on a synthetic record; `GrantsAsync` works only for the current user | Section 11 | Effective Access, review step |
| **UXG-03** | No path lets a person grant themselves access; the delegated path refuses (`self_grant`) and the trusted path is not exposed. A Demo/Test-only application service is needed, with `access.test_bootstrap`, source `TestBootstrap`, recent sign-in and mode gating | Section 7 | Test bootstrap |
| **UXG-04** | Secured owner API cannot set effective dates or reuse "primary vs backup" from the UI; assigning ownership does not check the `CategoryOwner` role; no "categories owned by person X" query; no one-primary rule | Sections 9 and 11 | Ownership editor |
| **UXG-05** | **Security defect.** `IGroupMappingService` has no authorisation check; `rolemappings.manage` is unenforced; mappings are not organisation-filtered; no edit | Section 10 | Mapping screen |
| **UXG-06** | No per-item readiness service; the validator has no role-holder facts; no pack-defined Required/Recommended/Optional | Section 8 | Onboarding checklist |
| **UXG-07** | Role grant and revoke have no recent-sign-in rule | Section 5.3 | Policy decision only (UI-layer step-up can ship without it) |
| **UXG-08** | No organisation-structure admin page (CSV only) | Step 3 of the wizard, onboarding item 4 | Structure editor (can follow) |
| **UXG-09** | No cross-contract documents list | Documents navigation item | Documents hub |
| **UXG-10** | No reporting module | Reports hub is a list of existing views | Honest labelling only |
| **UXG-11** | No "categories I may create in" query; `HoldsAnywhere("contracts.create")` is true for an owner with no categories | Section 4.2 | + New contract button |
| **UXG-12** | No secured "holders of role X"; `ManageableUsersAsync` loads all users and all assignments into memory | Naming who can grant access; people list at scale | Home hint, large installations |
| **UXG-13** | No in-place edit of a role assignment or a mapping (revoke and re-grant) | Edit flows produce two audit events | Cosmetic; explained on screen |
| **UXG-14** | No plain-English ability catalogue for the 94 permissions | Sections 6 and 11 | Review step, Effective Access |
| **UXG-15** | Documents quote 82 or 87 permissions; the matrix has 94 | Truthfulness | Documentation fix |
| **UXG-17** | No preview mechanism: no preview session record, claim, per-request validation, read-only enforcement or unit-of-work write guard; `ActorType` has no `Preview`; three new audit actions | Section 7A | Preview |
| **UXG-18** | No Demo/Test application service that creates a synthetic person who can use the demonstration sign-in (the trusted creation exists but is used only by setup and the seeder); behaviour of switching an already-Invited person to demonstration sign-in, and its interaction with a later real first sign-in, is **Not Verified** | Section 7B | Demonstration sign-in option |
| **UXG-19** | The containment rule for preview ("every grant could have been granted by the previewer") has no reusable service; the delegation checks exist inside the grant path only | Section 7A.3 rule 5 | Preview eligibility |
| **UXG-20** | GET handlers that write (mark as read, last viewed, usage counters) are not inventoried; the unit of work has no read-only scope | Section 7A.5 | Preview read-only guarantee |
| **UXG-16** | **Not Verified:** who holds onboarding rights (`_OnboardingBlocked.cshtml` vs matrix) | Section 8 | Documentation or text fix |

---

## 17. Open decisions for the owner

| ID | Question | Recommendation |
|---|---|---|
| Q-1 | Reconcile the menu groups (eight) and landing cards (nine) as in C-1? | Yes, as section 3.3 |
| Q-2 | Should a profile be allowed to change the **default** recipes (for example, make "Contract Manager" also include `Legal`)? | Defaults compiled; changes only through a published `AccessProfiles` document that validates against the closed vocabulary |
| Q-3 | One additive table recording profile applications (5.6), or infer the profile from grants? | Add the table; it is display-only |
| Q-4 | Add a recent-sign-in requirement to People & Access saves? | Yes, in the UI layer (UXG-07 optional later) |
| Q-5 | Who defines Required onboarding items: platform default, pack, or tenant? | Pack, with the platform default as the fallback |
| Q-6 | Which roles may identity group mappings carry in the guided form? | The six onboarding allows; widen deliberately |
| Q-7 | Should "own all categories" mean all **current** categories, or a standing rule that covers future ones? | Current categories plus a Needs-attention reminder; a standing rule would be a new ownership concept and is Future |
| Q-8 | Is the one-person Test bootstrap allowed in **Demo** as well as Test? | Yes (both are non-production); Production never |
| Q-9 | Navigation label for `/Admin/Elevations` | "Support & emergency access log" |
| Q-10 | Should preview ever allow **acting** as the person (writes attributed to the administrator "as" the person)? | No. It would let one human satisfy both sides of a separation-of-duties rule, which defeats the governance being rehearsed. Use synthetic people with demonstration sign-in to act |
| Q-11 | Should **Allow demonstration sign-in** be ticked by default in Test? | Unticked in Test, ticked in Demo; remember the last choice per administrator |
| Q-12 | Use `users.manage` for preview, or add a dedicated permission? | `users.manage` plus the containment rule; a new permission code would touch the frozen catalogue for no gain |
| Q-13 | Preview time limit | 30 minutes, configurable up to 60 |

---

## 18. Recommended implementation slices

Each slice is a vertical, tested increment on this branch, behind a configuration switch, with guides and the acceptance criteria updated. The order puts the owner's most painful finding first: *the first administrator can't do the job*.

| Slice | Content | Needs | Fixes owner findings |
|---|---|---|---|
| **S1 Navigation and Home** | Primary navigation, Administration disclosure menu and landing page, bell, renamed support-access log, Home cards from `IDashboardService`, Setup & administration card (using the existing checklist), "Set up my access" explanation (text only) | UXG-11 (or the interim rule), small layout and CSS work | 8, 9, 10 |
| **S2 Access foundation** | Compiled default profile catalogue; ability catalogue (UXG-14); effective-access report (UXG-02); application service (UXG-01); profile-application table (5.6) | The additive table and manifest regeneration | 7, 11 (backend) |
| **S3 Bootstrap and test personas** | Setup step "What will you do?"; Test/Demo "Give me full access" (UXG-03); Add a test colleague; **Allow demonstration sign-in** for new synthetic people (UXG-18); **Preview as this user** (UXG-17, 19, 20) on the existing Users page first; audit; Production guards | S2 | 2, 3, 11, and the additional finding |
| **S4 People & Access** | People list, Add/Edit wizard, Effective Access page, My access; Advanced grants moved; Preview and demonstration sign-in re-homed into People | S2, step-up | 7, 11, 12 |
| **S5 Category ownership** | Ownership editor, primary/backup, dates, bulk, "assign all"; prerequisite-role prompt | UXG-04 | 5 |
| **S6 Onboarding readiness** | Readiness service, states, pack-defined requirements, Publish blocked while Required items open, structure editor (UXG-08) | UXG-06, UXG-08 | 4 |
| **S7 Identity group mappings** | Authorise the service (UXG-05) with tests first; guided Advanced screen | UXG-05 | 6 |
| **S8 Configurable profiles** | `AccessProfiles` configuration kind; pack overrides; JBS names | S2, S4 | (G, F) |
| **S9 Hubs** | My Actions page, Documents hub (UXG-09), Reports hub | UXG-09 | 9, 12 |

Every slice finishes with: Release build with warnings as errors, unit/architecture/API/integration suites on SQLite and SQL Server, the rendered-page sweep and accessibility checks for new pages, an updated rehearsal ledger, and a guide update checked by `tools/check-docs.py`. A slice is not "done" on a page that merely renders: its acceptance criteria (UX_ACCEPTANCE_CRITERIA.md) must pass.

---

## 19. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| A profile layer is read as a second security engine, or drifts from the real grants | Wrong access shown or granted | Access is always computed from real grants; profiles only issue ordinary grants; tests compare the Effective Access output with the evaluator for every role and scope combination |
| Partial failure while applying several grants | Half-configured person | Validate first, fixed order, report exactly what was applied, undo only what the batch created |
| The self-grant exception is abused | Privilege escalation | Demo/Test only, recent sign-in, audited, scoped to a fixed profile set, cannot grant System or Tenant Administrator, refused in Production (tests), removable by one action |
| Group-mapping screen exposes an unauthorised service | A mapping grants more than the administrator could | Fix and test UXG-05 before the screen |
| Hiding navigation items looks like authorisation | A hidden page is still reachable | Server-side checks unchanged; tests request each page as each role and expect refusal |
| Plain-English wording misleads (a "can't" that is really "can with an approval") | Admin makes a wrong decision | Ability catalogue reviewed by the owner; each ability states conditions; "Advanced: exact grants" always one click away |
| Required-item rules differ by pack and cause confusion | Admin cannot publish and does not know why | Publish disabled with a list of exactly which items; pack requirements shown in the checklist |
| Scope creep into reporting, document search or a new role system | Delay | Section 4.4 and 16 label them; out of scope for this branch |
| New table requires a manifest regeneration and upgrade rehearsal | Release process | One migration only, covered by the existing N-1 to N rehearsal |
| Preview is used to read more than the previewer may see | Privilege leak | Containment rule (7A.3 rule 5), tenant and organisation scope unchanged, Demo/Test only; tests for each role and for a target with higher authority |
| Preview leaks into Production by configuration | Impersonation in Production | Mode checked in the service **and** in the middleware on every request; tests in Production configuration |
| A GET handler writes during preview | The target's data changes | Request-layer refusal, unit-of-work write guard, inventory of GET side effects (UXG-20) |
| Preview mistaken for signing in as the person | Wrong expectation about what the person can do | Permanent banner, read-only message, link to demonstration sign-in |
| One-person trials still cannot approve their own submissions | "It still doesn't work" feedback | State it on the confirmation page; offer the test colleague |
| Navigation change surprises existing users and guides | Support load | Routes unchanged; banners; switch per slice; guides updated with each slice |
| Rework of 31 nav items touches every page's chrome | Regressions | Navigation built from one declarative list in one place; rendered-page tests assert every old URL still resolves for the role that had it |

---

## 20. Screen map

```
Home                                   Contracts ─ Create / Details / Edit / Lifecycle
 ├ Setup & administration card         My Actions ─ Awaiting me / Legal / Approvals / Variations / Renewals / Config reviews
 └ My access                           Counterparties ─ list / details / create
                                       Documents (when UXG-09)      Reports ─ Dashboards / Saved views / Exports

Administration (/Admin)
 ├ People & Access ─ People ─ Add person (7 steps) ─ Person ─ Effective access ─ Change access
 │                   Access profiles (read-only list; editor in S8)       Approval authorities ─ Approver groups
 │                   Person ─ Preview as this user (Demo/Test, read-only) · Allow demonstration sign-in (Demo/Test)
                   Advanced ─ Role grants · Identity group mappings · Access simulator
 ├ Organisation ─ Organisations · Set-up checklist (13 items) · Structure · Regional settings · Branding
 ├ Contracts & Metadata ─ Categories & owners · Contract types · Terminology · Capabilities · Studio · Data migration
 ├ Workflows & Approvals ─ Workflow · Approval matrix · Approver groups · Configuration reviews
 ├ Notifications ─ Policy · Delivery · Development mailbox
 ├ Solution Packs ─ Installed · Assign · Trusted publishers
 ├ Security & Audit ─ Audit events · Support & emergency access log · Document scanning
 ├ Operations ─ Health & readiness · Operations · Support access · Installation setup
 └ Advanced Configuration ─ Role grants · Group mappings · Simulator · History & rollback · Bulk imports (CSV) · Diagnostics
Setup wizard (first run)  ─ … Initial administrator ─ What will you do? (new) ─ …
```

