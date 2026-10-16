# Contract lifecycle, Legal review, register authorisation, Variations, renewals and notifications (as implemented)

Status of this document: describes the code at the end of the lifecycle slice (2026-10-16). Decisions: ADR-0019 (classification and elevation), ADR-0020 (engine and workflow as configuration), ADR-0021 (notifications). Test evidence: `docs/TESTING.md` section 3. Every capability carries one of the repository labels: **Implemented**, **Mocked**, **Disabled**, **Scaffolded (Not Verified)** or **Future**.

## 1. What exists

| Capability | Label | Where |
|---|---|---|
| Configurable lifecycle state machine from the JBS pack (17 states, 35 transitions), pinned per contract | Implemented | `src/Modules/Ecp.Lifecycle/Engine`, `templates/packs/jbs-procurement/workflows/contract-lifecycle.json` |
| Variation workflow (11 states, 18 transitions) as its own definition | Implemented | `templates/packs/jbs-procurement/workflows/variation.json` |
| Structured Legal review: rounds, threaded comments, attachments, assignment, queue, timeline, owner response and resubmission | Implemented | `LegalReviews.cs`, pages `/Legal`, `/Legal/Review/{id}` |
| Execution boundary: manual executed-copy upload; provider interface; envelope id and status stored | Manual **Implemented**; Mock **Mocked** (refused in Production); external envelope adapter **Scaffolded (Not Verified)**, no live credentials | `Execution.cs` |
| Register authorisation: Group Head of Procurement or backup, first response wins, immutable snapshot, no self-approval, Registered Date in UTC, rejection returns to Initial Creation | Implemented | `RegisterAuthorisation.cs`, pages `/Approvals`, `/Approvals/Details/{id}` |
| Effective lock and audited emergency unlock (permission, recent sign-in, reason, time limit, automatic re-lock) | Implemented; second-person confirmation **Future** (refused by validation) | `RecordUnlocks.cs`, page `/Contracts/Unlock/{id}` |
| Variations (restricted fields, before/after, own workflow, Legal optional or required) | Implemented | `Variations.cs`, pages `/Variations`, `/Variations/Create`, `/Variations/Details/{id}` |
| Renewals and successor contracts | Implemented | `Renewals.cs`, page `/Renewals` |
| Notification policy as configuration, outbox dispatch, in-app inbox, acknowledgement | Implemented | `Ecp.Notifications`, page `/Notifications` |
| Email providers: demo log and dev capture | **Mocked** (Demo/Test only; refused in Production) | `Delivery.cs`, page `/Admin/EmailCapture` |
| Email provider: SMTP | Implemented, **Not Verified** against a live mail server | `Delivery.cs` |
| Email provider: Microsoft Graph | **Scaffolded (Not Verified)**; fails closed (`provider_not_configured`) | `Delivery.cs` |
| Reminders (30/60/90/120 per contract, Evergreen, Critical Notice, Review Date), once per window | Implemented | `Reminders.cs`, `ReminderSettings.cs` |
| Escalation of unacknowledged reminders | Data only (`EscalationDueUtc`) for Qlik; no escalation messages | **Future** |
| Failure and retry visibility, job status | Implemented | page `/Admin/Notifications` |
| Ten dashboards (lists and counts) | Implemented; no embedded analytics | `Dashboards.cs`, page `/Dashboards` |
| Retention periods, legal holds | **Scaffolded**: `IDispositionChecks` defaults to "no block"; the disposition guards exist and are enforced, the checks that would block are not built | `LifecycleApi.cs` |
| Migrating existing contracts to a newly published workflow release | **Future** | |
| Visual No-Code Studio, AI assistant, marketplace, generic workflow designer | Not built by design | |

## 2. The state machine

The reference lifecycle (all names are data; the engine knows only state *roles*):

`Draft → AwaitingLegal → InLegalReview → (LegalReworkRequired ↔ AwaitingLegal) → LegalCleared → InitialCreation → OutForSignature → AwaitingApproval → Effective` (locked).
From Effective: `VariationInProgress`, `RenewalInProgress`, `Suspended`, `Ended`, `Terminated`; then `FlaggedForDisposition` and `DisposedArchived`. `Withdrawn` is reachable from Draft, Legal rework and Initial Creation (not once an executed copy exists). Rejection at register authorisation returns the contract to `InitialCreation`.

Each state declares: a role (used by the engine), whether it **locks** the record, whether it **freezes content**, and a **document policy** (`Open`, `LegalOnly`, `Closed`). Each transition declares the permission needed, a transition role (`plain`, `submitLegal`, `recallLegal`, `beginLegal`, `returnLegal`, `clearLegal`, `resubmitLegal`, `returnToLegal`, `sendSignature`, `returnToDrafting`, `submitRegister`, `approve`, `reject`, `openVariation`, `applyVariation`, `rejectVariation`, `cancelVariation`, `startRenewal`, `completeRenewal`, `cancelRenewal`, `withdraw`, `flag`, `archive`, `cancel`) and named guards. **Managed** roles cannot be run through the generic transition call: they have their own service, which enforces extra rules (Legal pool, approver group, snapshot).

Closed vocabulary of guards: `LegalRequired`, `LegalNotRequired`, `CommentRequired`, `ReasonRequired`, `FinalDocumentExists`, `ExecutedCopyExists`, `NoExecutedCopy`, `AuthorisedApprover`, `SubmitterNotApprover`, `ParentRemainsLocked`, `NoOtherOpenVariation`, `NoOpenRenewal`, `RenewalSuccessorEffective`, `Authorised`, `RetentionChecksPass`, `NoLegalHold`, `ControlledFieldChanged`, `VariationOutcomeApplied`, `VariationOutcomeNotApplied`, `SignatureVoidedOrDeclined`, `RequiredFieldsPresent`, `RequiredDocumentsPresent`, `ClassificationClearance`. A definition naming anything else cannot be saved.

### Order of checks for every transition

1. The contract must be visible to the caller (otherwise 404, indistinguishable from "does not exist").
2. The caller must hold the transition's permission for this record (403).
3. Idempotency: a repeated `ClientRequestId` returns the earlier result.
4. The contract must be in a *from* state of the transition and, when the screen supplied one, in the state the screen showed (409 on a stale screen).
5. Managed-role check, then guards.
6. One transaction writes: the contract state (through the contracts port), a `StatusHistory` row (sequence, from, to, actor, reason, comment, release number), a `LifecycleEvent` outbox row and the audit event. If anything fails nothing is written.

### Pinning and publication

A contract records `WorkflowRef` and `ConfigReleaseId` when it is created and uses that release for its whole life. Publishing a new release (configuration administration, `/Admin/Configuration`) affects contracts created afterwards. Publication runs the validator; an unknown guard, an unlocking transition or a dead end is refused with a message that names the path (`$.transitions[7].guards`).

## 3. Legal review

* One review per subject (contract or Variation). Each submission opens a **round**; returning for rework closes the round with a mandatory reason, and the owner's resubmission opens the next. Nothing is overwritten: rounds, comments, attachments and events (`LegalEvent` with sequence) are append-only and shown as a timeline.
* Comments are threaded (a reply names its parent). Attachments are contract documents of type `LegalMarkup`, attached during review.
* **Assignment**: a person holding `legal.review` can assign any holder of the Legal role for the organisation (others are refused with `not_a_reviewer`); beginning the review assigns the person who starts it. The queue lists reviews with filters (status, assigned to, outstanding for me, subject kind, text) and shows only contracts the reader can open.
* **Clearance** by Legal (`legal.clear`) is not execution and not register authorisation. After clearance the owner continues to Initial Creation.
* The owner can recall a submission before review starts, respond to rework comments (`legal.respond`) and withdraw (closing the review).

## 4. Execution and register authorisation

* **Send for signature** requires a `FinalAgreement` document. The Manual provider records that the owner took the document out for signature; the owner then **uploads the executed copy** (type `ExecutedContract`). A Mock provider simulates sent, signed, declined and voided events (Demo/Test only). The external adapter stores an envelope id and status but has no credentials and is not callable (Scaffolded).
* **Submit for register authorisation** requires the executed copy. An immutable snapshot is stored with a SHA-256 checksum of its content: reference, title, supplier, category, classification, dates, value, the final and executed documents with their checksums, and the submitter.
* **Decision**: the Group Head of Procurement (approver group key from the definition) or a backup may approve or reject; the first response wins and a second gets `already_decided` (enforced by a unique pending-request index and verified with concurrent calls on SQL Server). The submitter cannot respond. Approval stamps the **Registered Date** in UTC, locks the contract in `Effective` and writes the approval evidence; rejection requires a comment and returns the contract to Initial Creation.
* While a request is pending, the approvers can open that contract (and only that); when it is decided the access ends.

## 5. Effective contracts, locking and emergency unlock

An Effective contract is locked: no edit, no document upload, no ownership-dependent bypass. It changes only by an approved Variation or an emergency unlock.

**Emergency unlock** (`/Contracts/Unlock/{id}`): permission `contracts.unlock_emergency` (Procurement Administrator, scoped), a recent sign-in (default 10 minutes; if the sign-in is older the person is taken to the re-authentication page first), a reason of at least 20 characters, a duration within the definition's limit (default 60, maximum 240 minutes), classification is never among the fields offered, and only the person who opened it may change fields. Each change is audited with before and after and the unlock id. The unlock ends when the person closes it or at expiry (the background job `unlock-expiry` closes it and re-locks). Every open, change, close and expiry is an audit event and is visible on the contract page and in the audit browser.

## 6. Variations and renewals

* **Variation**: opened from an Effective contract by an owner (`variations.create`); the contract moves to `VariationInProgress` and stays locked. The Variation names proposed values for the fields its definition allows (value, currency, extension type, notice period, critical notice, review date, end date, business unit, site); invalid values are refused when drafted. Legal review is Optional/Required/None per definition. After Legal (if any), a final `VariationDocument`, signature and executed copy, it goes to the same register authorisation. Approval applies the changes through the controlled path, stores the before and after values, returns the parent to Effective and does **not** re-stamp the Registered Date. Rejection or cancellation leaves the parent exactly as it was. Only one Variation can be open per contract.
* **Renewal**: `Renewals` starts a draft successor in the first state of the pipeline. Only the fields in `copyFields` and documents of types in `copyDocumentTypes` are copied; dates, status, audit and provenance never are. The start date follows `startDateRule` (`DayAfterPredecessorEnd` or `None`). Predecessor and successor link to each other. When the successor becomes Effective the renewal can be completed; cancelling withdraws the draft successor and restores the predecessor. The predecessor is never altered by a renewal.

## 7. Notifications and reminders

**Events** (28, in the policy): Legal submitted, resubmitted, assigned, started, returned, cleared, withdrawn, commented; approval submitted, approved, rejected; contract registered; Variation raised, applied, rejected, cancelled; renewal started, completed, cancelled; disposition requested; unlock opened, closed; ownership assigned, ended; reminders for Review Date, End Date and Critical Notice; reminder acknowledged.

**Recipient rules**: `CategoryOwners`, `Submitter`, `LegalPool`, `AssignedLegal`, `ApproverGroup`, `Counterpart`, `AffectedUser`, `Actor`. Whoever the rule names is dropped if they cannot read the record.

**Classification safety**: message content follows `content.withholdTitleFromLevel` (default Restricted: the reference remains, the title is replaced) and `withholdEverythingFromLevel` (default Legal Privileged: only "A restricted record needs your attention" and the link, with no reference or title). The inbox re-checks access at read time.

**Reminders**: per-contract lead time 30, 60, 90 or 120 days (`reminders.configure`; organisation default 90), calculated in the organisation's time zone. Windows are the allowed lead times up to the contract's lead; the most urgent window reached is sent once and the larger ones are marked covered. Evergreen contracts get no End Date reminder but keep the Review Date. Critical Notice contracts get a longer lead (`earlierLeadDays`, default +30) and a notice-deadline series (default 30, 14, 7 and 1 days before End Date less the notice period). A date already in the past produces nothing. If the category has no current owner the organisation's Procurement Administrators are told. Acknowledging a reminder (recipient or any current owner) records who and when and tells the other owners.

**Delivery**: in-app is immediate; email is attempted by the delivery job with the policy's back-off (default 1, 5, 30, 120, 720 minutes; five attempts) and dead-lettered afterwards. Administrators with `notifications.administer` (Tenant Administrator, Procurement Administrator; limited to their organisations) see failures, the attempts with correlation ids, and can retry; they also see the background jobs. The development mailbox page exists only outside Production.

**Background jobs**: `BackgroundJobService` runs `lifecycle-events`, `email-delivery`, `reminder-scan` and `unlock-expiry` for each active tenant every `Notifications:JobIntervalSeconds` (default 60; 0 turns the timer off) and records every run. They can also be run once from `/Admin/Notifications`.

### Configuration keys

| Key | Meaning | Default |
|---|---|---|
| `Notifications:EmailProvider` | `demo-log`, `dev-capture`, `smtp`, `graph` | `dev-capture` (refused in Production, so Production must choose `smtp` or `graph`) |
| `Notifications:BaseUrl` | Prefix for links in email | empty (relative links) |
| `Notifications:Smtp:*` | Host, port, TLS, from-address; user and password are **secret references** | none |
| `Notifications:BatchSize`, `Notifications:JobIntervalSeconds` | Work per run, timer | 100, 60 |
| `Ecp:DefaultPackId` (host configuration; read by the Lifecycle and Notifications modules) | The installation default pack, used only for an organisation that has no pack of its own; unset by default (ADR-0024) | unset |
| `Lifecycle` options (code) | Re-authentication window | 10 minutes |

## 8. Dashboards

`/Dashboards` shows ten cards, five rows each, with a link to the full list: **My Contracts**, **Awaiting My Action** (Legal items, approvals you can decide, contracts that need the owner), **Awaiting Legal**, **Pending Approval**, **Variations in Progress**, **Renewals in Progress**, **Expiring Soon** (within the lead time), **Reviews Due**, **Critical Notice**, **Reminded and Unacknowledged**. Each is built from the same secured reads as the register or the queue, so tenant, organisation, role, ownership and classification limits apply identically and the count on a card equals the length of the list behind it. They are operational lists; analytics belong in Qlik (`EscalationDueUtc`, history and audit are the data it reads).

## 9. Assumptions, contradictions and reversible decisions

| ID | Record |
|---|---|
| LIF-ASM-01 | `FinalAgreement` and `LegalMarkup` were added to the DocumentType lookups, and `reminders.configure`, `notifications.administer`, `classification.lower`, `records.read_privileged`, `contracts.suspend` and `legal.respond` to the permission catalogue (**DR-2026-10-16 lifecycle slice**, recorded in `requirements/02-reference`). Reversible by removing the rows. |
| LIF-ASM-02 | `RenewalInProgress`, `Suspended`, `Terminated`, `FlaggedForDisposition` and `Withdrawn` are additional states beyond `status-transitions-supplemental.csv`; the reference pack carries them as data. |
| LIF-ASM-03 | Second-person confirmation of an emergency unlock is Future: the definition may not turn it on (validation error) rather than being ignored. |
| LIF-ASM-04 | `Registered Date` is stamped once, at the first register authorisation; a Variation never re-stamps it. |
| LIF-ASM-05 | Rejection at register authorisation returns to Initial Creation (as instructed), not to Draft; resubmission needs a fresh executed copy only if the document set changed. |
| LIF-ASM-06 | The Legal pool is every holder of the Legal role in scope; a smaller pool is a role-assignment decision, not a setting. |
| LIF-ASM-07 | Escalation after N days is stored as data only; no message is sent. |
| LIF-ASM-08 | A dashboard spanning organisations uses the first organisation's time zone and lead time for "today". |
| LIF-ASM-09 | Existing contracts stay on the workflow release they were created under; there is no bulk migration to a newer release. |
| LIF-ASM-10 | No real mail server was available: the SMTP provider is unit-tested against a fake sender (configuration checks, send, fail-closed) and delegates the wire protocol to the platform sender from the Operations module; nothing was sent to a real server, so it is labelled Not Verified. Escalation data defaults to 3 days after an unacknowledged reminder. |
