# ADR-0043: Recovery points owned by the DBA are declared, never claimed

Status: Implemented on branch jbs/iis-integration. Production approval remains external governance and is not declared.
Builds on the backup and upgrade work (`docs/BACKUP_AND_RESTORE.md`, `docs/UPGRADE_RUNBOOK.md`).

## Context

At JBS the database administrators and infrastructure team own backup and restore. The platform's own backup engine would take a second, unmanaged copy, and an upgrade's rollback depends on a recovery point the platform cannot create or verify.

## Decision

1. `Ecp:Recovery:Mode` is `Application` (default: the platform takes and verifies backups) or `External`. Any other value is refused at start-up.
2. In External mode an operator records a recovery point the DBA took: identifier, source, environment, verifier, time taken, the platform version the database held, reason, who declared it, correlation id and a hash of the record. The record is written once, never edited, and audited. Every display says **"Declared by DBA; not verified by this application"**; the record has `verifiedByApplication = false`.
3. The upgrade preflight (UPF-020) treats a declared point as **NOT VERIFIED** (never Passed). The operator must accept it by name and the acceptance is in the upgrade evidence. Age, environment and the declared schema version are still checked (UPF-021, UPF-022, UPF-024). The point is "genuinely verified" only if it names one of the application's own verified backups, which then takes the normal path.
4. The application neither rehearses nor proves a restore of a declared point (the rehearsal and rollback-evidence commands refuse it with a clear message); the DBA's evidence stands.
5. Declarations refuse another environment, a future time, a point older than a year, text with a secret in it (redacted), and a duplicate (never replaced).

## Consequences

* The platform never pretends it created or verified a backup in External mode; readiness says recovery is owned externally and shows the latest declaration.
* Rollback in External mode is "the DBA restores the declared point with their tools, and the matching application version is started"; the evidence says so.
* The record is a file in the data root plus an audit event carrying its hash; a changed file is detectable against the audit chain.
