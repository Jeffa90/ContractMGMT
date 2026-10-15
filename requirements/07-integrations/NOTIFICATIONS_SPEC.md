# Notifications Specification

Master: notifications, reminders and escalations are configurable without code; notification-idempotency tests are required. This file specifies behaviour (GAP-14, GAP-16). `[CONFIRMED]` detail.

## 1. Channels

| Channel | Behaviour |
|---|---|
| In-app | A notification centre with unread count; always available; respects the same access rules as the linked record (a notification never reveals a record the user cannot read) |
| Email | Sent through `IEmailProvider` via the outbox; Demo uses a local capture mailbox; Production requires a configured provider and a successful test send in the wizard |
| Webhook | Event stream per `API_AND_WEBHOOKS_SPEC.md` |

## 2. Triggers

Workflow events (state entered, task assigned, approval requested, decision recorded, rework requested), Variation events, reminders (`WORKFLOW_SPEC.md` section 6), escalations (timeout without action), document events (executed copy attached, scan failed), migration events (batch completed, unmatched files waiting), and configuration events (release published). Triggers, recipients and timing are configuration attached to workflow definitions and notification rules.

## 3. Recipient resolution

By role within scope (category owners of the contract's category, approver group for the step, Legal pool), by named person, by field value (contract owner), or by group. Resolution happens at send time against current assignments. A user without read access to the record receives no notification that names it. Backup approvers are notified together with the primary (first-response model).

## 4. Templates

- Versioned configuration definitions with draft, validate, preview and publish like any other.
- Syntax: plain text/limited safe markup with `{{placeholder}}` substitution from an allowlisted variable set per event (for example `contract.title`, `contract.reference`, `step.name`, `link`). No expressions, loops, conditions or includes. All substituted values are encoded for the target format. Unknown placeholders fail validation at publish time, not at send time. `[CONFIRMED]` (no unsafe templates)
- Localised per organisation with explicit fallback.
- Default content avoids commercial detail: reference, title, action required and a link that requires sign-in. Including more fields is an explicit, classification-aware template choice.
- Header injection is prevented by constructing messages through the provider API, never by concatenating raw headers; subject and recipient values reject control characters.

## 5. Delivery guarantees

- At-least-once delivery with **idempotent send**: every notification has a `dedupeKey` (event or reminder id + recipient + channel + offset). A retry, restart, job re-run or duplicate event never produces a second message (test NTF-001 to NTF-005).
- Failures retry with back-off, then dead-letter visibly; bounces are recorded against the recipient.
- User preferences can reduce non-mandatory notifications (digest, channel), never mandatory ones (approval requests, escalations).
- Rate limiting and bundling prevent floods (for example a migration commit does not send one email per imported record: migration notifications are batch-level).

## 6. Escalations

An escalation rule = (target state or task, timeout, who to notify, repeat interval, maximum, optional reassignment). Timeouts use business calendars per organisation when configured, otherwise calendar time. Escalation is a notification and optional reassignment only; it never auto-approves.
