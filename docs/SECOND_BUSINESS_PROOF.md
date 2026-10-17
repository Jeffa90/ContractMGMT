# Second business proof (slice S9): design and results

Status: **Sections 1 to 15 are the design (written 2026-10-17 after slices S1 to S7). Section 16 records what was built and what the tests showed.** The design text below was not rewritten after the build; where the build differs, section 16 says so. It is the plan for portability slice S9 of `docs/PORTABILITY_REMEDIATION_PLAN.md` (task PKM-04; acceptance criteria PRT-002, PRT-004, PRT-013, PRT-015). It builds, changes and approves nothing, and it does not declare production approval.

**Amendments from the owner decisions of 2026-10-17 (`CONFIRMED_DECISIONS.md` D8), which take precedence over the text below where they differ:**

* **D1: no Defects Liability state.** Practical completion and similar completion facts are recorded by an **Administrative Change Order** (a Variation that changes the recorded fact), not by a role-less state and not by a "certify practical completion" step. The lifecycle therefore has **ten** states, not eleven; the `Defects Liability Period` state, the "certify practical completion" step and the finding about missing reminders in that state are **withdrawn**. Where sections 5 and 6.4 and the risk table still mention them, read them as withdrawn.
* **D2, D3: G11 and G1b are done before S9.** The pack is no longer designed around pack-time validation (G11); its organisation-dependent documents upload as they are. PRT-004 is to be satisfied in full. Both are implemented (`docs/S9_READINESS.md` section 13).
* **D4: G2 is deferred to S10.** The proof leaves the tax identifier empty or uses an eleven-digit value and records the limit.
* **D8.9 and D8.10:** Harborline Construction, `construction-subcontracts`, en-US, USD, America/Chicago; the pack ships in `templates/packs` as a first-class pack. **D8.7:** the neutral label of the `Approver` role is "Approval Authority", so the pack names its tiers explicitly. **D8.11:** sell-side Professional Services is the preferred next business.

The pack, tests and pages described below are **Implemented** (section 16); the CI result is recorded in `deploy/STATUS.md`. The pack itself is documented in `docs/CONSTRUCTION_SUBCONTRACTS_PACK.md`.

## 1. What S9 has to prove

The rule in `CLAUDE.md` is that JBS is a shipped reference configuration, not universal logic, and that a business administrator can configure fields, forms, rules, workflows, approvals, lookups, branding, terminology, notifications and templates without code. PRT-002 turns that into a gate:

> A non-JBS pack with different states, terms, fields and approval tiers installs and runs the whole lifecycle with **no source change**.

S1 to S7 built the mechanism. S9 is the first time the mechanism is asked to carry a **whole other business**. Success is a business that a reader would not mistake for JBS, running end to end on a checked-in pack and nothing else.

The invariant is narrow and strict: **installing and running the pack needs no source change.** If the proof finds a defect in the platform, the defect is recorded and fixed in a named slice as a generic change. It is never fixed by special-casing the second business (section 9, rule 3).

## 2. What the review found

Sources read: `docs/PACK_ARCHITECTURE.md`, `docs/TERMINOLOGY.md`, `docs/CAPABILITIES.md`, `docs/PORTABILITY_AUDIT.md`, `docs/PORTABILITY_REMEDIATION_PLAN.md`, `docs/PROJECT_STATUS.md`, plus the shipped Blank and reference packs and the code paths named in the evidence column. "Verified in code" means the line was read in this pass, not inferred from a document.

### 2.1 What is already proved

| Claim | Evidence |
|---|---|
| An organisation works from its own pack; the platform names no business | S1 (ADR-0024); `tests/Ecp.Integration.Tests/PackResolutionScenarios.cs` |
| A pack the repository has never seen installs from a file and runs a contract with no source change | S7 (ADR-0026); `A_third_pack_the_repository_has_never_seen_runs_a_contract_with_no_source_change`. It proves the **mechanism**, with a one-state-different lifecycle; `docs/PACK_ARCHITECTURE.md` says plainly that it "does not cover the pack content needed for a real second business (S9)" |
| Labels, notifications, dashboards, lists and exports speak the organisation's words | S4 (ADR-0028); 42 terminology keys, 32 required; guard stage B |
| Role codes, permission codes and group keys are permanent; their names are data | S5 (ADR-0029) |
| Legal Review, Register Authorisation, Variations, Renewals and Signature Requests can each be off, with no code change | S6 (ADR-0030); the all-five-off proof |
| A pack can carry lifecycle, Variation workflow, notification policy, terminology and capabilities | Shipped Blank 1.4.0 and reference 2.4.0 |

### 2.2 What is not yet proved

| Gap | Evidence | Consequence for S9 |
|---|---|---|
| **No shipped pack carries the other packable kinds.** Approval matrix, custom fields and lookups, classification policy, list views and branding are accepted by the format and installed as drafts, but Blank and the reference pack carry none of them | `src/Ecp.Packs/PackKinds.cs` (ten packable kinds); `templates/packs/blank` and `templates/packs/jbs-procurement` hold five documents each; `docs/PORTABILITY_AUDIT.md` section 0.2 | These kinds are untested **as pack content**. Each is a place the proof can fail |
| **JBS does not use an approval matrix** (a single approver group, first response wins) | `docs/APPROVAL_MATRIX.md` "JBS default" | The most powerful approval feature has never run as a pack's default |
| **PRT-002, PRT-004 and PRT-013 are NOT VERIFIED as wholes** | `docs/PROJECT_STATUS.md` section 9.2 | They are the exit gate |
| **R-01 to R-04 are open** | `docs/PORTABILITY_AUDIT.md` section 3 (findings table) | See section 7; they are visible to any business that is not JBS |
| **The proof business so far (Blank) is neutral, not different** | `templates/packs/blank` | A neutral pack cannot show that a business with its own opinions fits |

### 2.3 Constraints the review found in the closed vocabularies

These shape the design. Each was read in `src/Modules/Ecp.Lifecycle/Definitions/WorkflowDefinition.cs` unless noted.

| # | Constraint | Effect on a second business |
|---|---|---|
| C1 | A state role is given to **at most one state** (`role_duplicate`) | A lifecycle cannot have two live "effective" phases. Practical Completion and Defects Liability cannot both be effective states |
| C2 | A transition that moves a locked record to an unlocked state is an error (`unlocks`) | "Return for revision" must land in a **locked** state with the `drafting` role, not in the initial state |
| C3 | Submitting for, or giving, register authorisation **requires** the `ExecutedCopyExists` guard (`executed_copy_guard`); the approving transition must land in a locked state | Approval always happens **after** signing. A business that approves **before** signing (award approval) is not expressible today |
| C4 | Reminders are about End Date, Review Date and Critical Notice, on records in the live roles (`effective`, `variation`, `renewal`, `suspended`) (`src/Modules/Ecp.Notifications/Reminders.cs`, the same list in `src/Modules/Ecp.Notifications/Dashboards.cs`) | A state with no role gets no reminders. A custom date field gets none |
| C5 | An effective contract is locked; change needs a Variation or an audited unlock | A progress fact (a completion date) cannot simply be typed into an active contract. Section 6.4 gives the options |
| C6 | Core field labels are partly literal: "Vendor Number", "ABN", "Risk Level", "Function", "Owner", "Start Date", "Effective Date", "Review Date", "End Date", "Evergreen", "Currency", "Extension Type", "SAP Outline [[agreement]] Number" are not terminology keys (`src/Modules/Ecp.Metadata/FieldModel.cs`, `CoreFields.All`) | A second business's register columns and forms show these words (R-01, PRT-004) |
| C7 | The notification event list, recipient list and placeholders are closed (`src/Modules/Ecp.Notifications/Policy/NotificationPolicy.cs`, `PolicyVocabulary`) | A pack chooses among 28 events and 9 recipient rules; it cannot invent an event |
| C8 | The platform runs only the **lifecycle, Variation workflow and notification policy** from a pack's starting documents on first use; every other kind arrives as a **draft** that an administrator must publish, and a workflow naming a matrix cannot be published before the matrix exists (`docs/APPROVAL_MATRIX.md`) | Publication has an order (section 6.9). The test must drive it |
| C9 | The classification scale is a pack default, but `Legal Privileged` is a compiled constant and a default in places (R-02) | A scale with no privileged level is untested |

## 3. The strongest candidate: construction subcontracts

### 3.1 Choice

**A US general contractor managing trade subcontracts, change orders and project sites.** Synthetic, like JBS: the pack name makes that plain, and no real company, person, licence number or document is used. Working name for the business: **Harborline Construction** (invented). Pack key: construction-subcontracts. Display name: "Construction Subcontracts (synthetic example)".

This is also the business named in the requirement itself (`requirements/08-portability/PORTABILITY_ACCEPTANCE_CRITERIA.md`, PRT-002: "for example a Construction demo pack with different states, terms, fields and approval tiers") and in the plan's open decision (section 9: "a construction or facilities example already named in the plan; synthetic, like JBS"). The review confirms the choice on the merits rather than by precedent.

### 3.2 Candidates compared

| Candidate | Differs from JBS on lifecycle, terms, capabilities, approvals, notifications, onboarding | Exercises platform features JBS never runs | Fits the closed vocabularies without platform change | Verdict |
|---|---|---|---|---|
| **Construction subcontracts** | **High on all six.** Project sites, trade packages, change orders, bonds, retainage, stop-work, defects liability | **High.** Value-tier matrix with sequential, parallel and all-required steps; exception rows; foreign-currency rates; delegation; change-order approvals; custom fields and lookups; required fields at a transition; per-site time zones; a US locale | **Mostly.** C1 to C5 force honest modelling choices (section 6.4); none blocks it | **Recommended** |
| Facilities and property services | Medium. Renewals and service levels dominate, which JBS also has | Low. Close to JBS in shape; the Blank demo organisation is already "facilities" | Yes | Weaker proof; kept as the **Blank** organisation's role |
| Professional services (sell-side client engagements) | High on terms (client, engagement, partner), medium elsewhere | Medium. Inverts buy-side to sell-side, which stresses "supplier" wording | Yes | **Best third business**; do after S9 to prove inversion |
| Public-sector or council procurement | High on approvals (delegations of authority) | Medium | Mostly, but the gate `ExecutedCopyExists` (C3) clashes with award-before-signature, so the strongest feature would be unprovable | Deferred; C3 would dominate |
| Healthcare supplier agreements | Medium | Medium; heavy classification | Mostly | Deferred; sensitive-data realism adds risk with little extra proof |
| SaaS customer agreements | Medium on terms | Low | Yes | Weak: renewals and signature only |

Why construction wins: it is the only candidate that changes **every** axis at once and runs the three features JBS leaves unexercised (the approval matrix, pack-carried custom fields and lookups, and change orders as the central event). It also stresses the platform's walls (C1 to C5, C9) in ways that are **informative** rather than fatal.

### 3.3 What S9 deliberately does not model

A construction product would have progress claims, payment applications, retainage calculations, lien waivers, bonding workflow and schedule of values. **None of that is in scope.** The proof is of platform portability, not of construction software. Retainage and bond facts are carried as fields the contract manager records, and nothing computes payment. Section 9 rule 5 stops scope creep.

## 4. The business model

**Harborline Construction** (synthetic) is a general contractor with two operating companies, two regions and a dozen live projects. It buys labour and materials as **subcontracts** let by trade package, each project a **site** with its own time zone. Its commercial team issues a subcontract from a template, the subcontractor signs, a **Project Director** authorises it by value tier, and work starts. Scope changes arrive as **change orders** with their own approval tiers. Work can be stopped and restarted. At **practical completion** the subcontract moves into a defects liability period, and at **final completion** it closes. Legal review is not a step: the commercial team negotiates standard terms, and legal counsel is consulted outside the platform.

Roles in the business (the twelve platform role codes are fixed; the business names them):

| Business role | Platform role code (stable) | Notes |
|---|---|---|
| Commercial Administrator | ProcurementAdministrator | Manages packages, subcontractors and assignments |
| Package Manager | CategoryOwner | Owns a trade package and its subcontracts |
| Project Director | Approver (group ProjectDirectors) | First authority |
| Deputy Project Director | BackupApprover (group ProjectDirectors) | Backup |
| Commercial Manager | Approver (group CommercialManagers) | Second tier |
| Regional Director | Approver (group RegionalDirectors) | Third tier |
| Safety and Risk Manager | Approver (group SafetyRisk) | Exception tier for high-risk work |
| Project Viewer | Viewer | Read access by project |
| Internal Auditor | Auditor | |
| Business Configuration Administrator and the three administrative roles | as platform | |
| Legal counsel | Legal | **Hidden** from role pickers by the pack (ADR-0029 D8); Legal Review is off |

Locale: **en-US, USD, America/Chicago** for the head office, with project sites in America/Chicago and America/Denver. This is chosen to be as unlike the reference configuration as the locale settings allow (date order, number format, currency, time zones, spelling), which is the cheapest test of R-03 (section 7).

## 5. The seven differences from JBS

JBS is the reference: seventeen lifecycle states, Legal Review with rounds, a single approver group, no matrix, all five capabilities on, procurement wording, an Australian locale.

### 5.1 Lifecycle

| Aspect | JBS reference | Construction subcontracts |
|---|---|---|
| Entry | Draft, then Legal or "proceed without Legal" | Preparing (initial, editable) |
| Review | Legal queue, review, rework, cleared (four states) | **None.** No Legal states |
| Signing | Out for Signature | Out for Signature (kept) |
| Authorisation | Awaiting Approval, one group | Awaiting Authorisation, tiered by matrix |
| Live | Effective | Work in Progress |
| Change | Variation in Progress | Change Order Open |
| Pause | Suspended | Stop-Work |
| Phases | none | none (practical completion is recorded by an Administrative Change Order; D1) |
| End | Ended, flagged, archived | Final Completion; Terminated; Withdrawn |
| Renewal | Renewal in Progress | **None.** Subcontracts do not renew |
| State count | 17 | About 10 |

### 5.2 Terminology

Nearly every required key changes. The business reads "Subcontract", "Subcontractor", "Trade Package", "Project", "Change Order", "Package Manager", "Project Director". Section 6.5 lists the mapping. The proof asserts that no JBS word and no neutral default word remains for any key the pack overrides.

### 5.3 Capabilities

| Capability | JBS | Blank | Construction | Why |
|---|---|---|---|---|
| Legal Review | on | off | **off** | Commercial team negotiates; counsel outside the platform |
| Register Authorisation | on | on | **on** | Project Director authorises the executed subcontract |
| Variations | on | on | **on** | Change orders are the central event; require Register Authorisation |
| Renewals | on | off | **off** | Project subcontracts end; they do not renew |
| Signature Requests | on | off | **on** | Subcontracts are signed through the signature step |

Honest note: against Blank only Signature Requests differ. The capability axis is therefore a **weak** differentiator for this pack; the proof's weight on this axis comes from showing the same switches **coherent with a different lifecycle** and from the override scenarios in section 8 (turning Legal Review on is coherent but turning Variations or Signature Requests off is refused, because this lifecycle uses them).

### 5.4 Approvals

| Aspect | JBS | Construction |
|---|---|---|
| Model | One approver group, first response wins, no matrix | **Two approval matrices** |
| Tiers | none | By value: under 250,000 USD one step; 250,000 to under 2,000,000 two steps in sequence; 2,000,000 and over three steps in parallel, all required |
| Exception | none | High risk level adds the Safety and Risk step |
| Currency | one | USD, with a dated rate for CAD so a Canadian subcontractor can be judged; a missing rate blocks (existing rule) |
| Change orders | Variation workflow, optional Legal | Own Variation workflow with its own matrix; no Legal |
| Delegation | available | Used (acting Project Director) |

The numbers are illustrative synthetic tiers, not advice. Half-open boundaries (minimum included, maximum excluded) are tested at the boundary values.

### 5.5 Notifications

| Aspect | JBS | Construction |
|---|---|---|
| Events used | Legal, approval, registered, variation, renewal, reminders | Approval, registered ("subcontract authorised"), change order raised/applied/rejected/cancelled, ownership, unlock, reminders. **No legal.\* and no renewal.\* events** (C7) |
| Reminder windows | lead 30/60/90/120 days | Lead 14/30/60/90 days |
| Critical notice | 30, 14, 7, 1 days | 14, 7, 3, 1 days |
| Escalate unacknowledged | 3 days | 2 days |
| Unowned fall-back | organisation administrators | organisation administrators |
| Wording | procurement | construction, written with terminology tokens and the closed placeholders |

### 5.6 Onboarding

| Aspect | JBS | Construction |
|---|---|---|
| Structure | legal entities, divisions, business units, sites | Two legal entities; **regions** as divisions; Buildings and Civil as business units; **projects** as sites, each with its own time zone |
| Categories | packaging and procurement categories | **Trade packages**: Earthworks, Concrete, Structural Steel, Mechanical, Electrical, Finishes, with sub-packages; a package manager owns each |
| Contract types | JBS types | Lump sum, Unit rate, Cost plus, Supply only, Consultancy |
| Groups to roles | procurement groups | Project directors, package managers, commercial, safety, auditors; a mapping to the Legal role gets a warning because Legal Review is off |
| Reference prefix | JBSAUS-style | A short prefix chosen by the organisation (the exact pattern capability is confirmed in S9-0) |
| Classification scale | five levels, one privileged | Four levels, **no privileged level** (section 7, G7) |
| Locale | en-AU, AUD, Brisbane | en-US, USD, America/Chicago and America/Denver |

### 5.7 Beyond the six asked for

Fields (a pack-carried set with lookups), list views and dashboard emphasis, branding, and the classification scale all differ too. They are covered in section 6.

## 6. Pack design

### 6.1 Identity

| Item | Value |
|---|---|
| Pack key | construction-subcontracts |
| Display name | Construction Subcontracts (synthetic example) |
| Version | 1.0.0 |
| Publisher | Platform (built-in sample), synthetic: true |
| Signature | AllowUnsignedSynthetic for Demo and Test; signed with an ephemeral key in the CI pack check; Production refuses unsigned, as for every pack |
| Location | templates/packs/construction-subcontracts, built by the existing pack tool; no embedded resource; no project-file change |
| Names to avoid | No trademarked standard's names or numbering (for example no MasterFormat division codes). Trade package names are plain words |

### 6.2 Contents (one document per kind)

| Kind | Document | Purpose |
|---|---|---|
| WorkflowContractLifecycle | the subcontract lifecycle | States, steps, permissions, settings (6.3) |
| WorkflowVariation | the change-order workflow | Open, submit, approve (applied), reject, cancel (6.3) |
| NotificationPolicy | policy | Events, recipients, windows, templates (6.6) |
| Terminology | terminology | en and en-US wording; approver group names; hidden roles (6.5) |
| Capabilities | capabilities | Section 5.3 |
| ApprovalMatrix | two matrices | Subcontract authorisation and change-order approval (6.7) |
| CustomField | sections, lookups, fields | Project, commercial and compliance facts (6.8) |
| ContractListView | shared views | "Awaiting authorisation", "Change orders open", "Insurance expiring", "Stop-work", "By project" |
| ClassificationPolicy | scale | Four levels, no privileged level (6.8) |
| Branding | colours and name | Proves branding as pack content |
| SolutionPack (catalogue) | pack.json | Name, description, classification defaults, **starting trade packages and contract types** |

### 6.3 Lifecycle design

Design intent, to be validated by the existing lifecycle validator in S9-1. Roles are from the closed vocabulary; a role is used once (C1).

| State (display) | Role | Locked | Notes |
|---|---|---|---|
| Preparing | initial | no | Editable; the only unlocked state |
| Revising | drafting | yes | Landing state for "return for revision" (C2) |
| Out for Signature | signature | yes | Signature Requests on |
| Awaiting Authorisation | approval | yes | Matrix-judged |
| Work in Progress | effective | yes | Live; reminders on End Date and Review Date |
| Change Order Open | variation | yes | Variations on |
| Stop-Work | suspended | yes | Live role, so reminders continue |
| Final Completion | ended | yes | Terminal |
| Terminated | terminated | yes | Terminal |
| Withdrawn | withdrawn | yes | Terminal; before award |

Steps (permission and role vocabulary as in the reference lifecycle): issue for signature (sendSignature, needs a final document), submit for authorisation (submitRegister, needs the executed copy), approve and reject (with the mandatory authorised-approver and submitter-is-not-approver guards, C3), return for revision (returnToDrafting), open, apply, reject and cancel change order (the four Variation roles), stop work and reinstate, certify practical completion, certify final completion, terminate, withdraw before award. No legal, renewal or disposition steps.

Settings: approver group ProjectDirectors, matrix subcontract-authorisation, executed copy required, document types for the final and executed copy, change-order workflow named, unlock default 60 minutes.

Variation workflow: states Open, Submitted (approval), Applied, Rejected, Cancelled, with no Legal states; changeable fields include value, end date, risk level and the configured fields marked changeable; Legal review setting "None"; matrix change-order-approval.

### 6.4 Modelling choices forced by the constraints

| Need | Constraint | Choice | Alternative kept as a stress variant |
|---|---|---|---|
| Practical Completion and Defects Liability as phases | C1 (one effective state) | **One** extra live state, Defects Liability Period, with **no role**, entered by "certify practical completion" | Model the phase as a field. Rejected: C5 locks the record |
| Reminders during defects liability | C4 (no role, no reminders) | Accept for the proof and record a finding: the business sets a Review Date through the final change order if it wants a reminder. If the owner wants this fixed, it is a generic change (an additional live role or a flag on a state), decided in S9-0 | Give the state a spare live role. Rejected: it would misuse a role's meaning |
| Recording the practical completion date on a locked contract | C5 | Capture it **with the step**: the step requires the field to hold a value, and the value is entered by an **administrative change order** or an audited unlock before certification. Q1 in section 12 asks whether a better generic mechanism is wanted | Add an "editable while locked" flag on a field. A generic platform change, not a pack change |
| Approval before signing (award approval) | C3 | **Not modelled.** Authorisation follows signing. This is recorded as a limit of the register-authorisation capability, shared with JBS (where DocuSign handles delegation of authority) | A pre-signature approval capability: Future, needs an owner decision and an ADR |

### 6.5 Terminology design

Locale chain en-US to en. Every one of the 32 required keys is set; the others in the 42 are set where the business has a word. Examples (singular):

| Key | Neutral default | Construction |
|---|---|---|
| contract | Contract | Subcontract |
| contract.register | Contract Register | Subcontract Register |
| contract.reference | Contract Reference | Subcontract Number |
| contract.type | Contract Type | Subcontract Type |
| agreement | Agreement | Subcontract Agreement |
| supplier, counterparty | Counterparty | Subcontractor |
| category, subcategory | Category, Subcategory | Trade Package, Work Section |
| contract.owner | Contract Owner | Package Manager |
| contract.administrator | Contract Administrator | Commercial Administrator |
| primary.approver, backup.approver | (neutral) | Project Director, Deputy Project Director |
| registered.date | Registered Date | Authorised Date |
| registration.approval | Registration Approval | Subcontract Authorisation |
| variation | Variation | Change Order |
| legal.review (off) | Review | Commercial Review |
| site | Site | Project |
| business.unit, division | Business Unit, Division | Division, Region |
| organisation | Organisation | Organization (en-US) |

The terminology document also names the approver groups (ProjectDirectors, CommercialManagers, RegionalDirectors, SafetyRisk) and hides the roles the business does not use (Legal). The group keys are free text and permanent (ADR-0029 D3).

Gap to close before PRT-004 can pass: the core field labels in C6 are not keys. See G1.

### 6.6 Notification policy design

Events enabled: approval.submitted, approval.approved, approval.rejected, contract.registered, variation.raised, variation.applied, variation.rejected, variation.cancelled, ownership.assigned, ownership.ended, unlock.opened, unlock.closed, reminder.endDate, reminder.reviewDate, reminder.criticalNotice, reminder.acknowledged. Recipients are chosen from the nine closed rules (approver group, submitter, category owners, organisation administrators, actor, affected user). Templates use the closed placeholders and terminology tokens only. The policy validator must accept a policy with no legal or renewal events and a content rule that withholds nothing at a privileged level, because there is none (G7).

### 6.7 Approval matrices design

Two matrices, each a document with effective dates, rows, steps and (for USD) a rate table.

| Matrix | Used by | Rows (illustrative) |
|---|---|---|
| subcontract-authorisation | the lifecycle | **Small** under 250,000: Project Director, first response, backup allowed. **Medium** 250,000 to under 2,000,000: Project Director then Commercial Manager, sequential. **Large** 2,000,000 and over: Project Director, Commercial Manager and Regional Director in parallel, all required. **Exception** high risk level: adds the Safety and Risk step after the base steps |
| change-order-approval | the Variation workflow | Tiers on the value the contract would have once the change is applied (the platform judges the resulting value, not the delta; this is a documented behaviour and a risk, section 10) |

Conditions use value, risk level (a core field with a pack lookup), contract types, and a custom field marked usable in approvals. Categories are **not** used in conditions in the pack, because category codes belong to the organisation and the pack-level validator runs without an organisation (risk 9 in section 12).

### 6.8 Fields, lookups, classification, views

Custom field sections: Project, Commercial, Compliance. Illustrative fields: project number, scope summary, retention percentage, retention release date, bond required, bond amount, insurance expiry, contractor licence number, prequalification status, safety rating, practical completion date, final completion date. Lookups named after core fields so the core fields become controlled lists: riskLevel (Low, Medium, High, Critical), contractType, supplierType (Trade, Supply only, Consultant, Plant hire), extensionType. Fields marked usable in approvals: risk level, bond required, safety rating. Required-at-step: practical completion date before certifying practical completion; final completion date before final completion.

Classification scale: Public, Internal, Commercial in Confidence, Restricted; default Internal; **no privileged level**.

List views: awaiting authorisation, change orders open, stop-work, insurance expiring in 60 days, by project.

### 6.9 Publication order

The pack installs as drafts (C8). The proof publishes in dependency order, as an administrator would: classification policy, custom fields and lookups, terminology, capabilities (nothing to publish: applied as defaults), approval matrices, then the change-order workflow and the lifecycle (each names a matrix), then the notification policy, list views and branding. The proof also asserts that publishing a workflow **before** its matrix is refused with the existing error, which is a regression guard on C8.

## 7. Platform findings the proof will hit

The review of the code, not of the documents, found these. They are findings, not decisions. Each has a recommended disposition and **none is fixed by special-casing the second business.**

| ID | Finding | Evidence | Recommended disposition |
|---|---|---|---|
| G1 | Core field labels are literal (C6), so the construction register shows "Vendor Number", "ABN", "SAP Outline Subcontract Agreement Number", "Risk Level", "Function" and the date names | `CoreFields.All` | Make every core label a terminology key with the current English as its neutral default. Small, generic, PRT-004. **Prerequisite for claiming PRT-004** |
| G2 | The ABN rule rejects a value that is not 11 digits, in the domain | `docs/PORTABILITY_AUDIT.md` R-01 | S10: make it a rule. For S9, the field is optional and left empty; the proof asserts the label is neutral and records that a US tax identifier could not be entered (no UI path stores it) |
| G3 | One state per role (C1) and no role means no reminders (C4) | validator, `src/Modules/Ecp.Notifications/Reminders.cs` | Accept for S9 with the choice in 6.4; owner decides in S9-0 whether a generic fix is wanted |
| G4 | Approval cannot precede signing (C3) | validator | Document as a limit of the capability. Future: a pre-signature approval capability |
| G5 | A locked contract has no generic way to record a progress fact (C5) | contract lock rules | Q1; either use an administrative change order or add a generic "editable while locked" field flag |
| G6 | Locale fall-backs (R-03) and the Evergreen literal (R-04) | audit | The proof runs an en-US organisation; a rendered-page scan finds any AU fall-back. Fix in S10 if found |
| G7 | "Legal Privileged" is compiled (R-02); a scale with no privileged level is untested | audit R-02 | The pack uses no privileged level on purpose. If the platform needs one, the finding decides between a generic fix and a documented minimum |
| G8 | Pack drafts for matrices, fields, views and branding have never been published in sequence from a pack | C8 | The proof drives it; a pack-assignment checklist page that lists the order is a small usability improvement, not a prerequisite |
| G9 | Approval matrix judges the resulting value of a change order, not the amount of change | `docs/APPROVAL_MATRIX.md` facts | Document in the pack guide; tier design in 6.7 uses resulting value |
| G10 | The reference-prefix numbering capability (prefix, pattern) is stated in the portability checklist; the code shows a prefix | `Organisation.ReferencePrefix` | Confirm in S9-0; the pack uses whatever exists |

### 7.1 Readiness review

`docs/S9_READINESS.md` re-checked G1 to G10 against the code and classifies them. In short: **G11 (new), G8 and the country- and ERP-specific part of G1 must be fixed before S9**; G6 and the rest of G1 should be; G2, G3, G4, G5, G7 and G9 can wait; **G10 is closed**. Its corrections to this design (a defects-liability state is replaced by an administrative change order; the notification policy states its content levels explicitly; matrix conditions and views over custom fields only upload once G11 is fixed) take precedence over the text above until they are applied here.

## 8. Acceptance criteria

S9 passes only when **every** criterion below passes on SQLite locally and on SQL Server in CI. Anything that cannot run is reported NOT VERIFIED and keeps the criterion open. Numbering SBP-AC-nn.

| ID | Criterion | Maps to |
|---|---|---|
| SBP-AC-01 | **No source change.** The pack is added as files under templates/packs only. A CI step builds the repository, then builds the pack tool output, and a repository test asserts the three project files contain no pack reference and the source tree contains none of the pack's distinctive words (SBP-AC-17). Removing the pack directory makes assignment fail with the existing unknown-pack error | PRT-002, PRT-001 |
| SBP-AC-02 | **Pack integrity.** All documents validate through their own kind validators with no organisation; the build is byte-identical on two builds; signing with an ephemeral key is accepted; unsigned, wrong-key and tampered packs are refused; the existing pack check covers three packs | PRT-007, PRT-008 |
| SBP-AC-03 | **Coherence.** The capabilities document is coherent; the pack's workflows use no capability the pack turns off; the readiness check `capabilities` reports Healthy with three packs | PRT-015 |
| SBP-AC-04 | **Separation.** In one tenant a JBS organisation and a construction organisation each see only their own states, steps, terms, matrices, notification wording and capabilities. The JBS organisation's release hashes and its test results are identical before and after the pack is installed | PRT-002, PRT-014 |
| SBP-AC-05 | **Whole lifecycle.** Scenarios visit **every** state and take **every** step of both workflows at least once, computed from the loaded definitions so a new state without a scenario fails the test. Includes the happy path, revision loop, rejection, stop-work and reinstate, practical and final completion, termination, withdrawal and a change order applied, rejected and cancelled | PRT-002 |
| SBP-AC-06 | **Approvals.** Table-driven cases for each tier and each boundary value (exclusive maximum); the high-risk exception; the parallel all-required tier with one step outstanding; a CAD contract with the dated rate and without it; delegation; backup; submitter excluded; the change-order matrix; and agreement between the simulator and the live plan for the same facts | PRT-002 |
| SBP-AC-07 | **Capabilities.** Legal Review and Renewals are off by the pack's defaults and absent from navigation, pages, lists, dashboards, register columns and the available steps; the gate refuses a legal or renewal start with the stable code; an organisation cannot turn Variations or Signature Requests off (this lifecycle uses them); turning Legal Review on is accepted as coherent and changes nothing the lifecycle offers | PRT-015 |
| SBP-AC-08 | **Terminology (PRT-004).** A render sweep of a fixed page set (home, register, details, create, lifecycle, approvals, variations, suppliers, categories, dashboards, notifications, onboarding, administration pages) as three roles; plus every notification template, every export header row (CSV and XLSX), help text, and validation messages. For every key the pack sets, neither the neutral default nor any JBS word appears (word-boundary match, so "Subcontract" does not match "Contract"). A short, written allow-list covers the words that are legitimately the same | PRT-004 |
| SBP-AC-09 | **Names are data (PRT-013).** Role names, approver group names and state names shown to people come from terminology and the definition; the hidden Legal role is not offered in the role picker, yet existing grants of it still show; the frozen-identifier tests pass unchanged | PRT-013 |
| SBP-AC-10 | **Notifications.** Each enabled event reaches exactly the intended recipients with the pack's wording; no legal or renewal event is produced; a lead time outside the pack's allowed windows is refused; critical-notice offsets and the unacknowledged escalation use the pack's values on a fixed clock; the unowned fall-back works; content is withheld as the policy says | PRT-002 |
| SBP-AC-11 | **Onboarding.** The wizard runs from pack selection to publish for a construction organisation: the pack step shows the optional steps and the Legal Review note; starting trade packages and contract types load; two sites with different time zones; category ownership; group mappings, including the Legal-role warning; preview, publish and rollback | PRT-002 |
| SBP-AC-12 | **Fields.** Pack-carried lookups make the core lists controlled; custom fields appear on forms, in the register, in exports and in the reporting projection; the step requirements are enforced; approval conditions can use the marked fields; classification-gated fields are hidden from people below the level | PRT-003 |
| SBP-AC-13 | **Locale (PRT-012).** The construction organisation shows US date order, number and currency formats, its own time zones, US spelling; the rendered pages for it contain no AU culture, currency or time-zone fall-back; date-only values do not shift between sites in different zones | PRT-012 |
| SBP-AC-14 | **Classification.** The four-level scale with no privileged level works in access decisions, field visibility, notification content rules and the policy validator, or the finding G7 is recorded and the proof is held open | PRT-002 |
| SBP-AC-15 | **Isolation and caches.** A tenant administrator who can see both organisations gets each organisation's terminology, capabilities and approvals without mixing; a second tenant installs the same pack and shares nothing; no cache serves one organisation's answer to another | PRT-014 |
| SBP-AC-16 | **Publication order.** Publishing a workflow before the matrix it names is refused; publishing in the documented order succeeds; every draft the pack creates is either published by the scenario or reported as left open | PRT-002 |
| SBP-AC-17 | **Guard.** A new architecture test fails if any distinctive construction word appears in src or in a shipped platform resource (a short, maintained list that avoids common words such as "project"); the existing guard stage B stays green with no new allowance | PRT-001 |
| SBP-AC-18 | **Findings register.** Every platform gap found (G1 to G10 and any new one) is recorded in docs/PORTABILITY_AUDIT.md with a disposition; none is closed by a branch that names a pack, a role code, a state code or a business | PRT-013 |
| SBP-AC-19 | **Honest status.** PRT-002, PRT-004 and PRT-013 are re-labelled in docs/PORTABILITY_AUDIT.md and docs/PROJECT_STATUS.md only to the extent the evidence supports; PKM-04 is ticked only when SBP-AC-01 to SBP-AC-18 pass; what stays NOT VERIFIED (section 11) is listed | all |

## 9. Rules for the work

1. The pack is **data**. It is built by the existing tool from files under templates/packs. No document in it is executed.
2. The proof is a **test of the platform**, so tests drive the product through the real services and pages, publish through the real lifecycle and never write to tables.
3. A defect found is fixed as a **generic** change with its own test and an audit entry. A branch on a pack key, a state code, a role code or a business word is forbidden and is checked by SBP-AC-17.
4. The reference pack and the Blank pack are not edited to make the proof pass. If a platform fix changes them, their version is raised and their tests stay.
5. No construction **product** features are built (section 3.3).
6. Synthetic data only; no real names, licence numbers, tax identifiers or documents.

## 10. Tests

New files (named here, not created):

| Layer | File | Content |
|---|---|---|
| Unit | tests/Ecp.UnitTests/ConstructionPackTests.cs | Every document validates; lifecycle and change-order validate; the states and steps use each role at most once; no JBS state, role code or business word in the pack beyond stable identifiers; the 32 required terms set for en and en-US; policy and matrices validate; matrices produce the expected plans for a table of facts (a pure-function test over the resolver, no database); the capabilities document is coherent with the workflows via the same rule the compatibility check uses |
| Unit | extend tests/Ecp.UnitTests/PackFormatTests.cs | The pack builds identically twice and its digest is stable |
| Integration | tests/Ecp.Integration.Tests/SecondBusinessScenarios.cs, SQLite and SQL Server wrappers | The walk in SBP-AC-05 with a coverage assertion; approvals table (SBP-AC-06); notifications on a fixed clock (SBP-AC-10); capabilities and overrides (SBP-AC-07); onboarding service path (SBP-AC-11); fields and exports (SBP-AC-12); classification (SBP-AC-14); isolation and cache (SBP-AC-04, 15); publication order (SBP-AC-16). Uses the Pack Manager upload and assign path, not a test shortcut |
| API | tests/Ecp.Api.Tests/SecondBusinessUiTests.cs | The render sweep and vocabulary scan (SBP-AC-08, 09); onboarding pages (SBP-AC-11); capability pages with the construction pack; locale scan (SBP-AC-13); role picker (SBP-AC-09) |
| Architecture | extend tests/Ecp.Architecture.Tests/CapabilityGuardTests.cs or add SecondBusinessGuardTests.cs | Second-business vocabulary guard (SBP-AC-17); no pack reference in project files |
| Tooling | extend tools/check-packs.sh | Three packs: reproducible, signed with an ephemeral key, no embedded resource, no private key |
| Demo (optional) | the demonstration seeder | A third demonstration organisation on the pack, so the owner can see it. Not part of the acceptance gate |

Two design points for the vocabulary scan, because it is the likeliest source of false results:

* It compares **rendered output with the pack's terminology and the neutral defaults**, by word boundary and case-sensitively where the platform capitalises, so "Subcontract" never matches "Contract".
* The allow-list is a short written list per page with a reason, rejects entries that no longer match anything, and is capped, in the style of guard stage B.

The proof's runtime should be controlled: one pack install per test class, a fixed clock, and the full walk in one scenario with the table-driven cases in a second, so the SQL Server job grows by a bounded amount.

## 11. What stays NOT VERIFIED after S9

| Item | Why |
|---|---|
| PRT-005 deployment parity (IIS, Azure) | Not runnable here; unchanged by S9 |
| PRT-006 and PRT-011 export and import round trips | Pack export and tenant export are not built (S7e, EXP-01) |
| PRT-009 pack upgrade preserving local changes | Upgrade workflow is not built (S7d) |
| A business whose approval precedes signing | Not expressible (C3) |
| Sell-side businesses and public-sector delegations of authority | Not modelled; the recommended third business covers sell-side |
| Real-world suitability of the construction pack | It is synthetic; no domain expert has reviewed it. It proves portability, not fitness for a real contractor |

## 12. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | The proof surfaces platform defects (G1 to G10) and the slice grows | **High** | Medium | Findings are recorded first and sized; generic fixes are separate small commits; the gate is explicit that platform defects may block the claim without invalidating the design |
| 2 | Core label leaks (G1) make PRT-004 fail | **High** | Medium | Do G1 first (small); until then report PRT-004 as NOT VERIFIED and show the allow-list |
| 3 | C1 and C4 force a lifecycle that feels contrived (an unroled Defects Liability state, no reminders in it) | Medium | Medium | State the choice in the pack guide; owner decides in S9-0 whether to extend the vocabulary generically |
| 4 | A scale with no privileged level breaks compiled assumptions (G7) | Medium | Medium | The proof is the detection; the fix is generic and small if needed |
| 5 | C3 makes the approval story look unrealistic to a construction expert | Medium | Low | Say so in the pack guide; do not widen S9 to build pre-signature approval |
| 6 | The vocabulary scan produces false positives or misses (common words, plurals, capitalisation) | Medium | Medium | Word-boundary, case-aware matching; a per-page allow-list with reasons; positive controls (a seeded JBS word must be caught) |
| 7 | Publication order and second-person or step-up rules make the scenario brittle | Medium | Low | One helper that publishes in the documented order as a named administrator; the order itself is tested (SBP-AC-16) |
| 8 | SQL Server job time grows and the run is cancelled by a later push | Medium | Low | Bounded scenario count; push once; record the run |
| 9 | Pack-level validation cannot check organisation-specific codes (categories in matrix conditions) | Medium | Medium | The pack uses no category conditions; the organisation can add them after assignment and the matrix validator runs then |
| 10 | The change-order matrix judges the resulting value, which surprises users | Medium | Low | Document in the pack guide; tier text says "resulting value" |
| 11 | Scope creep into construction features | Medium | High | Section 3.3 and rule 5; reviewers reject payment, retainage or schedule features |
| 12 | One extra business gives false confidence that the platform is "portable" | Medium | High | Section 11; the plan keeps sell-side as the next business; PRT-002 is worded "a second business passes", never "any business" |
| 13 | A US locale reveals an ICU or time-zone data gap in a container image | Low | Medium | The container smoke already checks ICU and zone data; add the two US zones to the globalisation probe |
| 14 | Trademarked standards or real company names leak into the pack | Low | Medium | Section 6.1 rule; review at the pack's content scan |
| 15 | The JBS organisation is disturbed by the install or by a generic fix | Low | **High** | SBP-AC-04 compares release hashes; the reference tests stay green and unedited (rule 4) |

## 13. Sequence and effort

Effort is relative, not days: S small, M medium, L large.

| Step | Contents | Size | Exit |
|---|---|---|---|
| S9-0 | Decisions Q1 to Q6; G1 (core labels as terminology keys); confirm G7 and G10 by reading code; update the audit with the findings register | S to M | Owner decisions recorded; G1 merged with tests; the proof's allow-list is empty or justified |
| S9-1 | Author the pack documents; unit tests; pack tool and pack check for three packs | M | SBP-AC-02, 03 and the unit part of 17 |
| S9-2 | Integration scenarios on SQLite and SQL Server: install, publish in order, the walk, approvals, notifications, capabilities, isolation | M to L | SBP-AC-04 to 07, 10, 14 to 16 |
| S9-3 | UI sweep, onboarding pages, locale scan, role picker, fields and exports | M | SBP-AC-08, 09, 11 to 13 |
| S9-4 | Guard, documents (pack guide, audit, plan, status, testing, tasks), CI run, honest status | S | SBP-AC-17 to 19; PKM-04 ticked only if all pass |

Documents to update when S9 is built: docs/PORTABILITY_AUDIT.md (PRT-002, 004, 013, 015 rows and a section 0.7), docs/PORTABILITY_REMEDIATION_PLAN.md (section 8.15, the S9 row, open decisions), docs/PROJECT_STATUS.md, docs/TESTING.md, TASKS.md (PKM-04), deploy/STATUS.md, docs/PACK_AUTHORING.md (a worked second-business section), docs/DOCUMENT_INDEX.md, PACKAGE_MANIFEST.md, and a new pack guide in the style of DOCS-JBS.

## 14. Questions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | How should a progress fact (practical completion date) be recorded on a locked contract? | Use an administrative change order in S9 (no platform change). Consider a generic "editable while locked" field flag later, with its own ADR |
| Q2 | Is a state with no role (Defects Liability Period) acceptable, or should the platform give a live role or a "live" flag to a state? | Acceptable for S9. Record the missing reminders as a finding; decide the generic fix after the proof |
| Q3 | Should G1 (all core field labels become terminology keys) be done before S9 starts? | **Yes.** It is small, generic, and PRT-004 cannot pass without it |
| Q4 | Locale for the second business: en-US, USD, America/Chicago? | **Decided (D8.9): yes.** It is the most different from the reference locale and tests R-03 cheaply |
| Q5 | Should the construction pack ship in templates/packs and the demonstration seed, or stay a test pack? | Ship it in templates/packs as a synthetic sample (like the reference pack); add a demonstration organisation as an optional extra |
| Q6 | Is the next business after S9 sell-side professional services? | Yes. It inverts buy and sell and is the best remaining stress on the "supplier" model |

## 15. What this design does not claim

* It does not claim PRT-002, PRT-004 or PRT-013 pass. They stay NOT VERIFIED until S9 is built and its evidence is recorded.
* It does not claim the tier values, field list or state list are final; they are design intent that the validators and the owner will refine.
* It does not claim the construction pack is fit for a real contractor.
* It builds, changes and approves nothing, and it does not declare production approval.

## 16. Results (S9 built, 2026-10-17)

**What was built.** The pack `construction-subcontracts` 1.0.0 (Harborline Construction, synthetic) under `templates/packs/construction-subcontracts` (`docs/CONSTRUCTION_SUBCONTRACTS_PACK.md`); 15 unit test cases (`tests/Ecp.UnitTests/ConstructionPackTests.cs`, including four renderer cases); 11 integration scenarios on SQLite and the same 11 on SQL Server (`tests/Ecp.Integration.Tests/SecondBusinessScenarios.cs`, `HarborlineWorld.cs`); 4 API render tests (`tests/Ecp.Api.Tests/SecondBusinessUiTests.cs`); 6 guard tests (`tests/Ecp.Architecture.Tests/SecondBusinessGuardTests.cs`); one new stage-B rule. The design's 11-state lifecycle became 10 (owner decision D1).

**Platform files changed** (the guard lists them and fails on any other): `src/Ecp.Host/Pages/Shared/_ContractFields.cshtml` (S9-discovered generic fix F1, below), `tools/check-packs.sh` (signs and verifies every shipped pack, not only the first), `.github/workflows/ci.yml` (full-history checkout so the guard can read the baseline). Nothing else under `src`, `tools`, `deploy` or `.github` changed. No schema, migration, permission, route, role or option was added.

### 16.1 Acceptance criteria

Evidence is on SQLite locally; the SQL Server column is the CI result recorded in `deploy/STATUS.md`.

| ID | Result | Evidence and limits |
|---|---|---|
| SBP-AC-01 No source change | **Passed** | Journey scenario: upload, install, assign, publish and onboard through the services the pages call, with no rebuild; an absent version is refused with `pack_not_available`. Guard: no shipped platform file, project file or compiled `Ecp.*` assembly contains the pack's vocabulary; the S9 commits touch only the pack, tests, documents and the three listed platform files |
| SBP-AC-02 Pack integrity | **Passed** | Unit tests: valid, every packable kind, identical bytes twice, signs, trusted only with the right key; `tools/check-packs.sh`: three packs built twice, identical, valid, each signed and verified, tampered, wrong-key and unsigned refused |
| SBP-AC-03 Coherence | **Passed** | Unit test (capabilities coherent with both workflows); `/health/ready` reports `capabilities` and `solution-packs` Healthy with three packs |
| SBP-AC-04 Separation | **Passed** | Isolation scenario: Blank, reference and Harborline organisations in one tenant see only their own states, terms, capabilities, matrices, fields, views and notification wording; the reference organisation's release hashes are unchanged by the install; a second tenant installing the same pack shares nothing |
| SBP-AC-05 Whole lifecycle | **Passed** | Walk scenario: coverage is computed from the pack's definitions; every state and every step of both workflows is taken, including revision loop, rejection, stop work and resume, completion, termination, withdrawal and a change order applied, rejected and cancelled |
| SBP-AC-06 Approvals | **Passed** | Unit boundary table plus the Approvals scenario (section in `docs/APPROVAL_MATRIX.md`); simulator and live plan agree |
| SBP-AC-07 Capabilities | **Passed** (see limit) | Capabilities scenario: steps offered, built-in lists, gate refusals, refusal to switch off a capability the lifecycle uses, Legal Review on is coherent and changes nothing. Navigation absence is shown by the render sweep. Dashboard cards and register columns for the two capabilities are covered by the generic S6 suites, not re-checked for Harborline |
| SBP-AC-08 Terminology (PRT-004) | **Passed** | Render sweep for four roles over the register, contract, lifecycle, approval, change-order, supplier, category, dashboard, notification, administration and onboarding pages; notification inbox; CSV header and XLSX sheet name; banned-vocabulary scan of every pack document. **Not covered:** platform validation-message wording as a class, and help text (the product has no pack-specific help pages) |
| SBP-AC-09 Names are data (PRT-013) | **Passed** | Role names, approver-group names and state labels come from terminology and the definition; the hidden Legal role is not in the picker of an organisation-only administrator while existing grants still show; the frozen-identifier tests pass unchanged |
| SBP-AC-10 Notifications | **Passed** | Notifications scenario: recipients, pack wording, no formal-review or extension events, a lead time outside the allowed list refused, the critical-notice window drawn from the pack's offsets, the escalation date two days after the reminder (the platform keeps the date; nothing is escalated automatically), acknowledgement by the owner only, the unowned fall-back. Unit test: the real renderer under the pack's policy withholds the title from Confidential and everything from Restricted |
| SBP-AC-11 Onboarding | **Passed** (see limit) | Journey scenario (two legal entities, two regions, two divisions, two sites in different time zones, trade packages, contract types, ownership), the Onboarding scenario (preview, publish, rollback, the note that a Legal group mapping has nothing to do) and the onboarding pages in the render sweep. **Not exercised:** a group mapping used by a real sign-in (identity-provider mapping is generic and covered by the setup and identity suites) |
| SBP-AC-12 Fields | **Passed** | Fields scenario: controlled lists, pack fields in views, exports and the reporting projection, step requirements (internal review needs a project number), a Confidential field (Retention cap) hidden from a viewer whose ceiling is Internal; Approvals scenario: a condition on a field marked usable in approvals (bond required) |
| SBP-AC-13 Locale (PRT-012) | **Passed** (see G12) | Locale scenario and sweep: US date order and number formats, USD, America/Chicago, date-only values do not move; no AU fall-back on any page the four roles reach. Dates in notifications and some pages are ISO (G12), neutral rather than US-styled |
| SBP-AC-14 Classification | **Passed** | Four levels and no privileged level in the resolved scale (Fields scenario), in field visibility (Retention cap), in notification content rules (renderer unit test) and in the policy validator (unit tests). Access ceilings of the Harborline users (Internal, Confidential) decide what each can open throughout the scenarios |
| SBP-AC-15 Isolation and caches | **Passed** | Isolation scenario (a tenant administrator who reaches all organisations; a second tenant) |
| SBP-AC-16 Publication order | **Passed** | Order scenario: a workflow before its matrix is refused until it is published; the documented order succeeds; every draft is published or reported |
| SBP-AC-17 Guard | **Passed** | `SecondBusinessGuardTests` (six tests) and the stage-B rule `fixed-field-label-literal`; the stage-B allow-list has not grown (13 entries, unchanged) |
| SBP-AC-18 Findings register | **Passed** | `docs/PORTABILITY_AUDIT.md` section 0.8 |
| SBP-AC-19 Honest status | **Passed** | `docs/PORTABILITY_AUDIT.md` section 0.8, `docs/PROJECT_STATUS.md` section 9, `TASKS.md` |

### 16.2 What the build found

* **S9-discovered generic fix F1.** The shared contract-field partial typed four labels in a tuple literal; the rendered-page sweep found "Contract type", "Agreement type", "Risk level" and "Function" on a Harborline form. They now resolve through terminology, with a stage-B rule and tests that benefit every pack. This was the only generic platform defect found; no pack-specific change was made to fix it.
* **G12 ISO dates in notifications.** Reminder and due dates in messages are ISO (2026-11-14), the same for every culture. Neutral and unambiguous; not US-styled. Left, recorded (S10 candidate).
* **G13 `OrganisationAdministrators` recipient rule.** It resolves to the holders of the stable administrator role (`ProcurementAdministrator`); a business that calls that role Commercial Administrator gets the right people, but only by terminology. Left, recorded.
* **Closed notification event list.** No event exists for entering internal review, a signature request, stop work, or for insurance, bond and defects-liability dates; custom date fields get no reminders. The pack uses the 16 events that exist and records the limit.
* **Tenant-wide documents.** Classification policy and branding are tenant-level, so businesses sharing a tenant share one scale and one brand. The proof uses one tenant for three businesses and records the limit.
* **Step `requiredFields` check the contract the step runs on**, not a change order being prepared (a Variation `Prepare` step cannot require a change-order field). The pack requires them at submission by field flags instead.
* **Pack upload judges organisation-dependent documents only by warning** (by design, ADR-0031); publication is where they are judged.

### 16.3 What stays NOT VERIFIED

PRT-005 (deployment-model parity), PRT-006 and PRT-011 (configuration and tenant round trips), PRT-009 (pack upgrade preserving local changes) are untouched by S9 and stay NOT VERIFIED. G2 (non-Australian tax identifier formats) is deferred to S10 (D4). The proof says nothing about whether a real construction company could use the platform: no payment applications, lien waivers, schedule of values, retainage accounting or similar product features exist, and none was added.

### 16.4 CI

See `deploy/STATUS.md` for the run, the SQL Server result and the container smoke result.
