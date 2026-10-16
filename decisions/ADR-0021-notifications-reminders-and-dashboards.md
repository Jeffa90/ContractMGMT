# ADR-0021: Notifications, reminders and operational dashboards

Status: Accepted (lifecycle slice, DR-2026-10-16)

## Context

Owners, Legal, approvers and administrators need to be told about lifecycle events and approaching dates without any message revealing a record to a person who could not open it, without duplicates, and with a way for administrators to see and retry failures.

## Decision

1. **`Ecp.Notifications` (tier 9)** consumes the lifecycle outbox and the audit stream (ownership changes, through a per-tenant `DispatchCursor`) and writes notifications. It references Lifecycle and Operations but not their persistence.
2. **Policy is configuration.** `NotificationPolicy` is a Configuration kind: reminder lead times (30/60/90/120 allowed, per-contract choice, organisation default), Critical Notice offsets and extra lead, Evergreen rules (End Date suppressed, Review Date kept), escalation days (data only), content-withholding levels, delivery attempts and back-off, event rules and plain-text templates. Templates may contain only allow-listed placeholders (`{{contract.reference}}`, `{{contract.title}}`, `{{actor.name}}`, `{{link}}`, `{{due.date}}`, `{{days.remaining}}`, `{{window.days}}`, `{{step.name}}`); expressions, conditions, loops, includes and markup are rejected at publication.
3. **Recipients follow current ownership** at the moment of sending (category assignment, most specific first), plus named rules (submitter, Legal pool, assigned reviewer, approver group, affected user, actor). Every recipient is filtered through `access.SimulateAsync(…, "contracts.read")` for the record, so a person who cannot open it is never named, and content is further withheld by classification threshold.
4. **Idempotent by construction.** A dedupe key per event, recipient and subject; a `ReminderWindow` row per contract, kind, window and due date; larger windows covered by the one acted on are marked so they never fire later; a changed date opens new windows. Concurrent scans lose on a unique index and do nothing.
5. **Providers.** In-app inbox (always). Email via a provider factory: demo-log and dev-capture (Demo/Test only; refused in Production), SMTP (Implemented; **Not Verified** against a live server) and Microsoft Graph (Scaffolded; fails closed with `provider_not_configured`). Secrets are references, never values in configuration, logs or the database. A failed delivery is retried with the policy's back-off and then dead-lettered; administrators see attempts with correlation ids and can retry. An email failure never affects the in-app notification.
6. **Background work** runs from a Host hosted service per active tenant (event dispatch, delivery, reminder scan, unlock expiry), each recorded as a `JobRun`; it can also be run from the administration page. Events that cannot be processed after ten attempts are parked and visible.
7. **Acknowledgement.** A reminder that requires it is acknowledged by a recipient or any current owner; everyone sees who and when; other owners are told. Unacknowledged reminders appear on a dashboard. Escalation after N days is recorded as data (`EscalationDueUtc`) for the reporting layer (Qlik); this slice sends no escalation messages.
8. **Dashboards are lists and counts built from the secured reads** (`ISecuredContractService`, Legal queue, approvals, Variations, renewals), so every limit that applies to the register applies to them. Ten cards: My Contracts, Awaiting My Action, Awaiting Legal, Pending Approval, Variations in Progress, Renewals in Progress, Expiring Soon, Reviews Due, Critical Notice, Reminded and Unacknowledged. No embedded analytics.

## Consequences

* A reclassification or loss of access immediately turns old inbox items into "a record you can no longer open".
* Because recipients are resolved at send time, a handover is honoured without any rewrite of past data, and a reminder window that fires after a handover goes to the new owner only.
* Real email delivery needs an operator to configure and verify SMTP or Graph; until then the honest status is Not Verified.
* Residual: no SMS, Teams or push channels; no digest emails; escalation messaging is Future; time zone for "today" is the first organisation's zone when a dashboard spans organisations.
