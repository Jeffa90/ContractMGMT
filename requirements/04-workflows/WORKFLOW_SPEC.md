# Contract Lifecycle Specification

Added by repository review. It gives meaning to `status-transitions.csv` (original, unchanged) and fills gaps GAP-12, CON-03, CON-04 and CON-13. Tags: `[CONFIRMED]` traceable to an authoritative source; `[PROPOSED]` assumption (see `docs/REPOSITORY_REVIEW.md`).

The lifecycle is **data**, not code. The states and transitions below are the JBS Procurement reference workflow definition; the platform ships a generic workflow engine and the JBS definition is loaded from the JBS pack (ASM-19). State *codes* are stable; display names are terminology aliases.

## 1. State semantics

| State code | Display | Lock | Who edits | Notes |
|---|---|---|---|---|
| `Draft` | Draft | Unlocked | Category owner (assigned, active category) | Entry point. `[CONFIRMED]` |
| `AwaitingLegal` | Awaiting Legal | Unlocked, content frozen | Nobody; owner may upload supporting documents | `[PROPOSED]` content freeze |
| `InLegalReview` | In Legal Review | Unlocked, content frozen | Legal comments and uploads Legal documents | `[PROPOSED]` |
| `LegalReworkRequired` | Legal Rework Required | Unlocked | Category owner | Must have an exit (CON-04) |
| `LegalCleared` | Legal Cleared | Unlocked | Category owner on non-controlled fields | Changes to a controlled field return the contract to Legal (supplemental transition) `[PROPOSED]` |
| `InitialCreation` | Initial Creation | Unlocked | Category owner | Final document assembled here. Reached after Legal, without Legal, and after approval rejection `[CONFIRMED]` |
| `OutForSignature` | Out for Signature | Unlocked, content frozen | Nobody; executed copy is attached by the e-signature integration or manually | DocuSign executes `[CONFIRMED]` |
| `AwaitingApproval` | Awaiting Approval | Unlocked, content frozen | Nobody; approver acts | `[PROPOSED]` freeze |
| `Effective` | Effective | **Locked** | Nobody; only an applied Variation or audited emergency unlock | Lock set by `Approve`. `[CONFIRMED]` |
| `VariationInProgress` | Variation in Progress | **Locked** (parent) | Nobody; the Variation record is edited | Parent remains locked `[CONFIRMED]` |
| `Ended` | Ended | **Locked** | Nobody | |
| `DisposedArchived` | Disposed-Archived | **Locked**, archived, restricted | Nobody | Means *archived under retention*; deletion is a separate later act (archive-before-delete) `[CONFIRMED]` |
| `Withdrawn` | Withdrawn | Locked | Nobody | Terminal state for abandoned drafts `[PROPOSED]` |

Rules:

1. `status` is never directly editable by any user or API payload; only the workflow engine changes it (CON-03). `[PROPOSED]`
2. The lock (`recordLocked`) is set by the engine on entry to `Effective` and is not cleared by any later transition except via an applied Variation (which changes fields but does not unlock the record) or an emergency unlock (section 5). `[CONFIRMED]`
3. Locked records reject every write to fields whose dictionary entry is `No`, `Variation only` or `Admin unlock only`, regardless of role, at the domain layer. `[CONFIRMED]`
4. Users cannot hard-delete legal records. Removal is `Withdrawn` (before Effective) or archive (after). `[CONFIRMED]`

## 2. Transitions

`status-transitions.csv` defines 13 transitions `[CONFIRMED]`. `status-transitions-supplemental.csv` adds proposed transitions needed to remove dead ends and to complete Variations `[PROPOSED]`. Guard codes (the "Condition" column) are defined in `guard-vocabulary.csv`.

Every transition, when executed:

1. Evaluates permission, scope rule, lifecycle state, lock and all guards, server-side, in one transaction.
2. Writes the new state, any stamps and side effects, an audit event (old and new state, actor, outcome) and outbox messages, atomically.
3. Is idempotent for the same `(subject, transition, clientRequestId)`.
4. On guard failure, returns a plain-language explanation and writes a `Denied` audit event. Business rules may also *block* a transition with an explanation. `[CONFIRMED]` (Master: rules action "block transition with an explanation")

## 3. Final authorisation (`Approve`)

On `AwaitingApproval -> Effective` the engine, in one transaction: `[CONFIRMED]` unless noted

1. Confirms guards `ExecutedCopyExists`, `AuthorisedApprover`, `SubmitterNotApprover` `[PROPOSED]` (ASM-17).
2. Writes an `ApprovalRecord` (actor, on-behalf-of for a backup approver, matrix version, evidence document).
3. Stamps `registeredDate` (now, UTC) and `registeredBy`, separately from `effectiveDate`.
4. Sets `recordLocked = true` and `lockedUtc`.
5. Generates `ReminderSchedule` rows (section 6).
6. Emits outbox events for reporting and ERP adapters.

The approver identity is the authenticated platform user. It is never taken from a callback body of an external system (approval-spoofing control). `[CONFIRMED]` (Master: approval spoofing)

## 4. Legal review rounds `[PROPOSED]`

- `Begin review` opens `LegalReviewRound(n)`. Only one round is open at a time.
- `Return for rework` closes the round with outcome `ReturnedForRework`; a comment/summary is required. The contract goes to `LegalReworkRequired`.
- The owner resubmits (supplemental transition `Resubmit to Legal`), which returns the contract to `AwaitingLegal`; the next `Begin review` opens round n+1.
- `Clear` closes the round with outcome `Cleared`.
- Closed rounds and comments are immutable (append-only); documents reviewed are linked by version so what Legal saw is provable.
- "Structured multi-round review" `[CONFIRMED]` is satisfied by rounds, comments and per-round outcomes. Finer structure (checklists, issue lists) is configuration (forms) and out of scope for this workflow definition.

## 5. Variations `[PROPOSED]` detail, `[CONFIRMED]` principle

- Principle: an Effective record changes only through an authorised Variation or an audited emergency unlock.
- `Create variation` (permission `variations.create`) moves the parent to `VariationInProgress` (still locked) and creates a `Variation` record. At most one open Variation per contract (ASM-15).
- The Variation runs its **own workflow definition** (`variation`): Draft, optional Legal review, Out for Signature, Awaiting Approval, then an outcome of Applied, Rejected or Cancelled. It reuses the same engine, guards and approval matrix mechanics.
- Only fields marked `Variation only` in the dictionary, plus custom fields flagged "editable by Variation", may appear in `proposedChanges`.
- On `Applied`, the engine writes the changes to the parent in one audited transaction, links the Variation document, and returns the parent to `Effective` (supplemental transition `Apply variation`). `registeredDate` is **not** re-stamped; the Variation's own approval record carries its authorisation time.
- On `Rejected` or `Cancelled`, the parent returns to `Effective` unchanged.

## 6. Reminders: Evergreen and Critical Notice `[PROPOSED]`

Master requires "Evergreen and Critical Notice reminder behaviour" without detail (GAP-12). The behaviour below is a configurable placeholder pending Q-04:

| Kind | Trigger | Default offsets (placeholder) | Recipients |
|---|---|---|---|
| `EndDate` | Fixed-term contract, `endDate` present | 180, 90, 60, 30 days before `endDate` | Category owner |
| `Review` | `reviewDate` present | 90, 60, 30 days before `reviewDate` | Category owner |
| `Evergreen` | `extensionType = Evergreen` (no `endDate`) | Annual on the anniversary of `effectiveDate`, plus 30 days before `reviewDate` | Category owner |
| `CriticalNotice` | Notice deadline = `endDate` minus `noticePeriodDays` (or for Evergreen, `reviewDate` minus `noticePeriodDays`) | 30, 14, 7, 1 days before the deadline | Category owner, with escalation to the approver group if unacknowledged for 3 days |

Rules: due times are computed from date-only values in the organisation (or site) time zone and stored as UTC instants; reminders carry a `dedupeKey` so a retry or restart never sends twice; offsets, recipients and escalations are configuration (notification templates and escalation rules), not code; changing `endDate` through a Variation recomputes unsent reminders.

## 7. Emergency unlock `[CONFIRMED]` principle, `[PROPOSED]` mechanics

- Permission `contracts.unlock_emergency` (Procurement Administrator). Mandatory reason; mandatory expiry (maximum configurable, default 4 hours); optional or required second-person confirmation (default required in Production).
- Creates a `RecordUnlock`, shows a visible banner on the record, and allows edits to the fields the unlock names.
- The record auto-relocks on expiry or early close by a scheduled job. Every edit during the unlock carries the unlock id in its audit event.
- Unlock does not change `status` and does not clear `registeredDate`.

## 8. Legacy loads `[PROPOSED]` (ASM-06)

Migration may create contracts directly in `Effective`, `Ended` or `DisposedArchived` under a legacy-load policy (`MigrationProject.legacyLoadPolicy`, permission `migration.legacy_load`). Such contracts are locked on creation, flagged `isLegacyLoad`, may carry a source `registeredDate`, receive no workflow side effects (no notifications except reminders if enabled) and are fully audited with the batch ID. They enter the normal Variation process afterwards.
