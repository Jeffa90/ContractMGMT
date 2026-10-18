# Rollback by restore (OPS-02)

Status: **Implemented** (procedure, evidence run and checks), **Verified** on SQL Server in CI. Decisions D11.6 to D11.8: migrations are forward-only; an application downgrade after a schema upgrade is not assumed safe; rollback is **restoring the verified pre-upgrade backup together with the matching application version**. The platform has no downgrade operation (no command, page or method), and a test asserts it.

> Rollback by restore **discards every change made after the backup**: contracts, approvals, documents, configuration releases, audit events, notifications. This is the data-loss window. A backup taken in maintenance mode immediately before the upgrade makes it nil; a backup taken yesterday makes it a day. The pre-flight shows how many audited events postdate the backup (`UPF-023`) and warns when the backup is older than the release's limit (`UPF-021`).

## 1. When to roll back

* A migration failed (maintenance mode stays on, cause *Upgrade failed*).
* Post-upgrade validation failed and nobody can explain the difference.
* The upgrade completed but a business-critical problem is found within the window the owner decides is acceptable to lose.

Do **not** roll back by editing the schema, deleting migration records, or running an older application against the migrated database (`UPF-011` blocks that; it can corrupt data).

## 2. Prerequisites

* A **verified** backup made by the version you are returning to, of *this* installation and environment (`UPF-020`, `UPF-022`, `UPF-024`).
* The **previous application version** (files or image), with its configuration. `RBK-010` warns when the running build differs from the one that made the backup.
* The backup's documents and packs folders (they are part of the backup directory).
* The same maintenance-window discipline as the upgrade.

## 3. Procedure

1. **Keep maintenance mode on.** If the upgrade failed it already is. Stop all application instances (new and old).
2. Record the data-loss window: the backup's time and the time of the failure; the number of audit events since (`list-backups`, Upgrade History).
3. Restore. Either into the active database name (disaster-recovery restore; replaces the database; the application must be stopped):
   ```
   dotnet Ecp.Host.dll restore <backup id> --in-place --reason "Rollback of <change id>" --confirm "RESTORE <backup id> OVER THE ACTIVE DATABASE" --expect-sha256 <manifest hash>
   ```
   or into a new database and repoint the application's connection (the safer choice when the failed database should be kept for investigation; `docs/OPERATIONS_RUNBOOK.md`).
4. Start the **previous** application version against the restored database. It will not run `migrate` (and in Production a bare `migrate` is refused on an installation anyway).
5. Validate: `dotnet Ecp.Host.dll validate`. It must pass (`docs/BACKUP_AND_RESTORE.md` section 11). The restored schema must equal the schema the backup recorded.
6. Release maintenance mode with the explicit acknowledgement of the restore state, admit users, and tell the business what was discarded.
7. File the evidence with the change record. Keep the failed database (renamed) until the investigation is finished.

## 4. Rollback evidence (before you need it)

`upgrade-rollback-evidence <backup id> <new target database> --reason "..." --confirm "PROVE ROLLBACK TO <backup id>"` (needs `operations.restore`; Rollback Evidence page). It restores the verified pre-upgrade backup into a **new database** (never the active one), validates it, and writes `rollback-<utc>-<backup id>.json`. Checks:

| Code | Check |
|---|---|
| RBK-001 | The backup restores into the new database and passes post-restore validation |
| RBK-002 | The restored copy opens and answers queries |
| RBK-003 | The restored schema is exactly the schema the backup recorded (the pre-upgrade version's baseline) |
| RBK-004 | The restored copy holds no trace of the upgrade (none of the target's migrations or migration records) |
| RBK-005 | Audit chains verify |
| RBK-006 | Documents match their hashes |
| RBK-007 | Packs and configuration releases are those the backup recorded |
| RBK-008 | Organisations (Blank, JBS Procurement, Harborline Construction in the shipped installation) are present with their packs and regional settings |
| RBK-009 | Data-loss window: what would be discarded (Warning when anything postdates the backup) |
| RBK-010 | Application version needed: the version that made the backup (Warning when the running build differs) |

The evidence states the data-loss window in words and numbers, the manifest hash, the backup id and the application version required. It is evidence that the way back *works*, at that time, for that backup. It is not a promise about any later backup, and it is not production approval.

## 5. Limits

* Restore recreates the database from a full COPY_ONLY backup. Point-in-time recovery to just before the failure uses the operator's own log backups, outside the platform (`docs/DISASTER_RECOVERY.md`), and still needs the matching application version.
* An encrypted backup needs its certificate on the restoring server. A restored database's application login may be orphaned on another server.
* Per-tenant rollback does not exist; restore is installation-level (D10.5, D11.1).
* Secrets and private signing keys are not in backups (D10.3); re-supply them from the secret provider as for any restore.

## 6. Dashboard (OPS-03)

A failed upgrade is shown as Failed on the operations dashboard, in the failure register (no new attempt offered) and in the health panel, and maintenance mode stays on, until the operator acknowledges the restore state after rolling back (`docs/OPERATIONS_DASHBOARD.md` section 6).
