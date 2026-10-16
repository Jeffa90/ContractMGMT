# ADR-0019: Five-level classification, privileged access and support/emergency elevation

Status: Accepted (lifecycle slice, DR-2026-10-16)

## Context

The owner confirmed five classification levels (Public, Internal, Confidential, Restricted, Legal Privileged) and a privileged level that no ordinary role can open. The earlier slice had four levels, a single ceiling check and no way for the platform operator to act inside a tenant without borrowing a tenant account (IDN-03). The lifecycle slice adds Legal review, notifications, dashboards and documents that must never reveal a record to someone who could not open it.

## Decision

1. **The scale is data, the privileged flag is part of it.** `ClassificationScale` holds ordered levels, a default and the set of privileged levels. Legal Privileged is privileged. A person may open a record at a privileged level only if their **ceiling reaches it and** they hold `records.read_privileged` (Legal by default) or an explicit `PrivilegedAccess` flag on the assignment, granted by a Tenant Administrator. A role alone never opens it. The evaluator takes this as a separate layer, so it cannot be skipped by a role, a Viewer scope, delegation or an owner relationship.
2. **Lowering needs its own permission.** `classification.lower` (Procurement Administrator, Legal) with a mandatory reason, only within the actor's own ceiling, audited. Raising is an ordinary controlled change. A change event is classified at the higher of the old and new level so a move away from a restricted level is never visible to people who could not see the record before.
3. **Classification travels.** Documents, audit events, history rows, notifications, reminder state and dashboard rows carry the classification of the record they describe. Notification content is withheld above policy thresholds (title from Restricted, everything from Legal Privileged) and is re-checked against the reader's access when the inbox is read, so a message written before a reclassification stops revealing the record.
4. **Elevation is a separate, tenant-visible grant.** An installation administrator may open a Support (read-only) or Emergency (account and role recovery only) elevation into one tenant with a reason and a time limit; recent re-authentication is required; Emergency may need a second installation administrator. The grant is stored in the target tenant, shows a banner while used, carries its id on every audit event written under it, and **never** includes a write to a contract, Variation, approval, unlock or configuration publication. It does not impersonate a person. Support carries read permissions only (contracts, documents, suppliers, audit, configuration history); Emergency adds user, role and role-mapping recovery. `records.read_privileged`, `classification.lower` and every lifecycle, approval and unlock permission are on a never-granted list checked by `Elevation.Open` and by tests, so an elevation cannot open a Legal Privileged record.
5. **Migration preserves classification.** A mapped classification is kept; a blank one takes the tenant default; one not on the scale is rejected (`MIG-E004`). Documents inherit the contract's level.

## Consequences

* The privileged layer is a hard stop in the evaluator, covered by the matrix-driven tests and the classification scenarios on SQLite and SQL Server.
* Reclassification changes who sees history, inbox items and dashboard rows immediately; there is no cache to expire.
* Operators can recover a tenant without a tenant credential, and the tenant can see that they did.
* Residual: classification of free-text comments is the author's responsibility; there is no content scanning. Tenants that configure their own scale must keep the privileged flag consistent with their policy (validated at setup).
