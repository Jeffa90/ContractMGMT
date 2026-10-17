# Harborline Construction: the `construction-subcontracts` solution pack

Status: **Implemented** as a shipped synthetic example (slice S9, 2026-10-17). This document describes what the pack contains, how it is installed and what the proof shows. It is **not legal, contractual, tax, safety or industry advice**, the business is fictional, and nothing here claims that the pack fits a real construction company. Production approval is external governance and is not declared.

## 1. Purpose

The pack is the second business of the portability proof (`docs/SECOND_BUSINESS_PROOF.md`, PRT-002). Harborline Construction is a **synthetic United States general contractor** that manages trade subcontracts and the change orders that follow them. It differs from the JBS reference configuration in every way the platform lets a business differ: states, steps, words, roles shown, capabilities, approval tiers, currency, locale, time zone, fields, views and notification wording. It is added to the platform **only as files**: no project, source file, database column, permission, route or platform behaviour mentions it.

| | |
|---|---|
| Pack key | `construction-subcontracts` |
| Package id | `platform.pack.construction-subcontracts` |
| Version | 1.0.0 |
| Display name | Construction Subcontracts (synthetic example) |
| Source | `templates/packs/construction-subcontracts/` (built to `construction-subcontracts-1.0.0.ecpkg` by the host build; never embedded in an assembly) |
| Signature | none (the shipped sample is unsigned and declared synthetic; the pack tool signs it like any pack, see `docs/PACK_SIGNING.md`) |
| Defaults | culture `en-US`, currency `USD`, time zone America/Chicago, reference prefix `HBL` |

## 2. Contents (eleven documents)

| Kind | Scope | What it carries |
|---|---|---|
| Solution pack | catalogue | Metadata, defaults, five contract types, eight trade packages (two with sub-packages), the four-level classification scale |
| Contract lifecycle | organisation | Ten states and seventeen steps (section 3) |
| Variation workflow | organisation | The change order process: seven states, seven steps |
| Notification policy | organisation | Sixteen events, lead times 14/30/60/90 days, critical-notice offsets 14/7/3/1, two-day escalation, unowned fall-back, content-withholding levels |
| Terminology | organisation | 52 terms (all 32 required keys and the field-label keys), approver-group names and one hidden role |
| Capabilities | organisation | Legal Review off, Renewals off, Register Authorisation on, Variations on, Signature Requests on |
| Custom fields | organisation | Five lookups and 24 fields in five sections (project, commercial, compliance, programme, change order); Retention cap is classified Confidential |
| Approval matrices | organisation | `subcontract-activation` and `change-order-approval` (section 5) |
| Shared views | organisation | Eleven contract-list views over core and pack fields |
| Classification policy | tenant | Four levels with no privileged level; notification content levels stated explicitly |
| Branding | tenant | Product name "Harborline Subcontract Hub", one accent colour |

## 3. Lifecycle

Draft, Internal Review, Ready for Signature, Sent for Signature, Awaiting Activation Approval, **Active**, Change Order Open, Stop Work, **Completed**, **Terminated**, **Withdrawn** (the labels are the pack's; the stable roles are `initial`, none, `drafting`, `signature`, `approval`, `effective`, `variation`, `suspended`, `ended`, `terminated`, `withdrawn`).

* **Internal Review has no role.** Commercial staff review a draft before it goes for signature; the state needs no lifecycle behaviour of its own, so it holds none. The platform allows any number of role-less states.
* **No Legal Review states, no Renewal states.** Both capabilities are off. Counsel is consulted outside the platform, and the pack says so in the capability note.
* **No Defects Liability state (owner decision D1).** A state with no role would have no reminders (finding C4) and a role is unique per workflow. **Practical completion is recorded by an Administrative Change Order**: a change order of type `ADMINISTRATIVE` that sets the Practical Completion Date and changes no value. The contract stays Active, the fact is audited and versioned, and the approval matrix gives it a single Commercial Manager step.
* The activation approval needs an executed copy on file (the generic rule for the `approve` role), exactly as for any pack.

## 4. Change orders

Change orders use the platform's Variations capability, presented as **Change Orders** through terminology. A change order has a type (Scope, Time, Value, Administrative), a reason, proposed and approved value change, time-extension days, a revised completion date, an affected trade package and a supporting instruction reference. It is prepared, optionally sent for signature, submitted for approval and applied or rejected, or cancelled while open. Applying it changes the fields the variation workflow lists as changeable and leaves the original version in the history. The approval matrix judges the **resulting** contract value (finding G9), so the Change Order tiers are expressed in that value.

## 5. Approvals

| Matrix | Row | Condition | Steps |
|---|---|---|---|
| Subcontract activation | sub-small | below 250,000 | Project Director |
| | sub-medium | 250,000 to below 2,000,000 | Project Director, then Commercial Manager (sequential) |
| | sub-large | 2,000,000 and above | Project Director, Commercial Manager, Regional Director (parallel, **all required**) |
| | sub-high-risk (exception) | risk High or Critical | adds Safety and Risk |
| | sub-bonded (exception) | 250,000 or more and the Bond Required field is true | adds a bond check by the Commercial Manager |
| Change order approval | co-small / co-medium / co-large | below 1,000,000 / below 5,000,000 / above | the same pattern |
| | co-high-risk (exception) | High or Critical | adds Safety and Risk |
| | co-administrative | change order type is Administrative | one Commercial Manager step |

Amounts are in US dollars. A contract in Canadian dollars converts at a **synthetic, labelled** rate (0.74, effective-dated); a currency with no rate is refused with a stated reason rather than guessed. Backups, delegation (including on-behalf-of recording), submitter exclusion, effective dates, the trace and the plain-language explanation are the platform's, unchanged.

## 6. Words

Terminology maps the neutral catalogue to Subcontract, Subcontractor, Trade Package, Package Manager (the contract owner), Commercial Administrator (the contract administrator), Project Director, Change Order, Formal Review, Activation Approval, Division, Project, Executed Subcontract, Subcontract Number and the rest. Approver groups are named Project Directors, Commercial Managers, Regional Directors and Safety and Risk Managers (and a deputy group for the first two tiers). The Legal role is hidden from pickers of organisations that use the pack. Role **codes**, permission codes and approver-group keys are never renamed (ADR-0029); only their displayed names change.

## 7. Installing and using it

1. A platform administrator opens **Pack Manager** and uploads the `.ecpkg` and installs it (the host build also catalogues the shipped file).
2. The administrator **assigns** it to an organisation. Every document becomes a draft in that organisation (tenant-level documents become tenant drafts).
3. The administrator publishes through the normal controls, in dependency order: lookups and fields, matrices, workflows, terminology, capabilities, notification policy, views. Publishing a workflow before a matrix it names is refused.
4. The onboarding wizard sets sites and time zones, trade packages, contract types, ownership and group mappings.

No rebuild and no restart are needed; the shipped file is simply another entry in the catalogue. The architecture guard fails the build if any project file names the pack or any compiled assembly contains its vocabulary.

## 8. What the proof shows and does not show

See `docs/SECOND_BUSINESS_PROOF.md` section 16 for the acceptance results. In short: the pack uploads, assigns and publishes through normal controls; both workflows are walked state by state; approvals, capabilities, notifications, fields, locale and isolation are exercised on SQLite and SQL Server; the rendered pages contain no JBS or neutral wording where the pack sets a term.

It does **not** show that real construction companies could run on the platform. Open limits: non-US-neutral tax identifier formats (S10), no construction product features (payment applications, lien waivers, schedule of values, retainage accounting), closed notification event vocabulary (no event for entering Internal Review, signature requested, stop work or insurance, bond and defects dates; custom date fields get no reminders), `OrganisationAdministrators` resolves to the holders of the stable administrator role, and tenant-level classification and branding apply to a whole tenant (a tenant shared by several businesses must agree on one scale). Each is recorded in `docs/PORTABILITY_AUDIT.md`.
