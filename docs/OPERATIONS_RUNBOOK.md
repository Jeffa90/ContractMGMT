# Operations runbook: backup, verify, restore, maintenance

Status: **Implemented**; the primary verified model is a container with an external SQL Server and persistent document storage (`docs/BACKUP_AND_RESTORE.md` section 2). IIS and Azure steps are **Scaffolded / Not Verified**. This runbook is written so that a trained operator can follow it without source code or undocumented knowledge. It does not replace your organisation's backup policy.

> **Tooling is not governance.** This platform will take, verify and restore backups and record evidence that it did. Deciding frequency, retention, RPO and RTO, where copies live, how they are encrypted, who may read them, when you rehearse and who signs off is yours. Fill in section 9 before you rely on any of this.

## 1. Before you start (once)

1. **Roles.** Only the System Administrator holds `operations.*`. Give the people who run backups and restores that role in the installation tenant. A tenant or organisation administrator cannot back up, restore or switch maintenance mode on (they are refused in the service, not only hidden).
2. **Accounts.** Create a dedicated SQL Server login for backup and restore (`BACKUP DATABASE`, `RESTORE`, `CREATE DATABASE`, and the right to read the restored database) and put its connection string in your secret provider; set `Ecp__Recovery__AdminConnectionStringRef`. Do not put it inline.
3. **Shared directory.** Provide one protected directory that both the database server and the application can reach (`docs/BACKUP_AND_RESTORE.md` section 4). Set `BackupDirectory` (application's view) and `SqlServerBackupDirectory` (server's view). Make sure it is outside the web content, encrypted, access-controlled and copied elsewhere by your own process.
4. **Restore area.** Provide a second directory with space for a full copy of the document store and packages; set `RestoreTargetDirectory`.
5. **Encryption.** Either encrypt the storage and state it in `StorageProtection`, or create a SQL Server certificate, set `EncryptionCertificate`, and back the certificate and its private key up **separately** from the backups.
6. **Check.** Open **Administration, Operations**. It shows what is configured. Readiness check `R-BKP-01` should not say "not configured".

Docker example (see `deploy/docker/compose.production.yml` for the commented settings): mount `ecp-backups` at `/var/ecp-backups` in the application container and the same volume where the database server container sees it; set `Ecp__Recovery__BackupDirectory=/var/ecp-backups` and `Ecp__Recovery__SqlServerBackupDirectory` to the server's path.

## 2. Take a backup

Web: **Administration, Operations, Backups**, enter a reason, optionally tick "Hold maintenance mode during the backup", **Create backup**, confirm your sign-in if asked. Command line (on the server or inside the container):

```
dotnet Ecp.Host.dll backup --reason "Nightly" [--hold-maintenance]
```

* **Normal (online) backup**: no interruption; counts in the manifest are ranges.
* **Exact backup**: hold maintenance mode. Changes are paused for the length of the backup. Use this for a rehearsal or the backup you will compare against.
* Result: *Completed*, *Completed with warnings* (read them; the backup is still valid) or *Failed* (not a recovery point; the reason is shown and nothing partial is kept).
* A message that another operation holds the lease means a backup, restore or validation is running (or crashed less than `LeaseMinutes` ago). Wait, or see section 8.

Schedule it with your own scheduler (for example a nightly job that runs the command and alerts on a non-zero exit code). The platform does not schedule backups.

## 3. Verify a backup

Every backup is verified when it is made. Verify again after copying it, before you rely on it, and regularly (storage rots):

```
dotnet Ecp.Host.dll verify-backup bk-20261017T031500Z-0a1b2c3d
```

or **Operations, Backups, Verify again**. *Passed* means every file hash matched, every document object matched, and SQL Server's own check passed. Record the manifest SHA-256 shown on the backup's page in your records (it lets you detect a manifest that someone replaced).

## 4. Rehearse a restore (do this on a schedule, and before you need it)

A rehearsal is a real restore into a separate database. It never touches the live database.

1. **Operations, Restore**: choose the backup, type a **new** database name (for example `ecp_rehearsal_2026_10`), **Preview**. Read the blockers and warnings. Typical blockers: the backup fails its integrity check; the name is taken or is the active database; the environment differs; the restore directory is not empty; document content is not in the backup.
2. Give a reason, type the phrase shown (`RESTORE <backup id>`), paste the manifest hash from your records, and confirm. The installation enters maintenance mode for the length of the restore.
3. Read the result. **Succeeded** or **Succeeded with warnings** means the restored copy passed validation and was released. **Failed** means it was not released and the installation stays in maintenance mode (section 7).
4. Open **Operations, Validation** results and the evidence file `_evidence/restore-*.json` (in the backup directory). Keep both.
5. Optional: start a second, **non-production** instance pointed at the copy (connection string, data directory, pack directory) and look at it. Never point production traffic at a rehearsal copy by accident.
6. Drop the rehearsal database and delete its restore directory when finished (your process; the platform never deletes databases).

## 5. Restore for real (the live installation is lost or damaged, the server is fine)

Prefer a **side-by-side** restore followed by a deliberate cut-over: it keeps the damaged installation for investigation and proves the copy before anything depends on it.

1. Do section 4, steps 1 to 4, with a database name such as `ecp_restored`.
2. **Cut over** (deliberate; nothing switches by itself):
   1. Stop traffic to the damaged installation (or leave it in maintenance mode).
   2. Re-map the application's database login to the restored database if the login is orphaned: `ALTER USER [ecp_app] WITH LOGIN = [ecp_app];` in the restored database (and grant the `ecp_runtime` role it already holds).
   3. Change `Database__ConnectionStringRef` to a connection string for the restored database, `Ecp__DataDirectory` to `<RestoreTargetDirectory>/<database>`, and `Ecp__Packs__StorageDirectory` to `<RestoreTargetDirectory>/<database>/packs` (or move the restored folders to the usual locations).
   4. Supply the **same secrets and the same data-protection key ring** as before (backups contain neither; without the key ring, sessions and protected setup state cannot be read).
   5. Start the application. If the restored database predates this platform version, do **not** run `migrate` on it in Production (it is refused on an existing installation): upgrade it by the procedure in `docs/UPGRADE_RUNBOOK.md` (pre-flight, then `upgrade-apply` with the backup you restored from), or restore a backup made by the version that is running. Then validate again.
   6. Run **Operations, Validation** on the running installation. Release traffic only when it passes.
3. Write down the incident and attach the evidence (section 9).

## 6. Disaster recovery over the active database (command line only)

Use only when the database itself is lost or unusable and you have decided to restore into the same name (`docs/DISASTER_RECOVERY.md`).

1. Stop the web application (so nothing holds connections).
2. On the server: `dotnet Ecp.Host.dll restore bk-... --in-place --reason "..." --confirm "RESTORE bk-... OVER THE ACTIVE DATABASE" --expect-sha256 <hash>`.
3. The database is set to single-user, restored, set to multi-user, opened and validated like any restore. If validation fails the installation stays in maintenance mode.
4. Start the application and run validation again.

## 7. A restore failed

The installation is in maintenance mode with cause `RestoreFailed`. Nothing else changed: the live database was never the target.

1. Read the failure on the Restore page or in the command output, and the validation table (codes and remedies).
2. Fix the cause (space, permissions, a missing certificate, a damaged backup), then restore again into a **new** database name. Drop the failed target when you are done with it.
3. When you are satisfied that the installation is as it should be, **Operations, Maintenance, Leave maintenance mode**: give a reason and tick the acknowledgement. (This is deliberate: a failed restore must not be forgotten.)

## 8. Maintenance mode and leases

* **Maintenance mode** (`docs/MAINTENANCE_MODE.md`): Operations, Maintenance. Use it for planned work. It refuses changes with a clear message, pauses background jobs and shows a banner.
* **A lease that will not clear**: Operations, Leases shows the holder and when it expires. If the holder crashed it expires by itself. If you know it is gone and cannot wait, **Take over** with a reason (audited).

## 9. Organisation decisions, recorded (complete these; the platform does not)

| Item | Your decision | Owner | Date |
|---|---|---|---|
| Backup frequency | *to be decided* | | |
| Retention period for backups (and deletion method) | *to be decided* | | |
| Recovery point objective (RPO) | *to be decided* | | |
| Recovery time objective (RTO) | *to be decided* | | |
| Where copies are kept (second location) | *to be decided* | | |
| Storage encryption and who may read backups | *to be decided* | | |
| Certificate and key custody (if `EncryptionCertificate` is used) | *to be decided* | | |
| Secret provider and key-ring backup | *to be decided* | | |
| Restore rehearsal frequency and who attends | *to be decided* | | |
| Point-in-time recovery (SQL Server log backups run by your DBA) | *to be decided* | | |
| Incident owner for recovery | *to be decided* | | |

## 10. Evidence and sign-off template

Record, for each rehearsal or recovery: date; operator; reason; backup id and manifest SHA-256 (from your records, not only from the screen); target database and directories; the restore result and validation outcome (attach `_evidence/restore-*.json` and the Operations evidence report); time taken against the RTO; the age of the backup against the RPO; problems found and actions; who reviewed it.

```
Restore rehearsal / recovery record
Date (UTC):                     Operator:
Backup id:                      Manifest SHA-256 (from records):
Target database / directories:
Result (Succeeded / with warnings / Failed):       Validation outcome:
Elapsed time (RTO target:   ):  Backup age (RPO target:   ):
Findings and actions:
Reviewed by:                    Date:                 Signature:
```

This record is evidence of what ran. **It is not approval for production use**; that is external governance.

## 11. Command reference

| Command | Purpose |
|---|---|
| `backup --reason "<why>" [--hold-maintenance]` | Create and verify a backup |
| `list-backups` | Backups in the directory with their state |
| `verify-backup <id>` | Re-check a backup's integrity |
| `restore <id> <new database> --reason "<why>" --confirm "RESTORE <id>" [--expect-sha256 <hash>] [--allow-different-environment] [--accept-missing-documents]` | Restore beside the running installation, validate, release |
| `restore <id> --in-place --reason "<why>" --confirm "RESTORE <id> OVER THE ACTIVE DATABASE"` | Disaster recovery over the active database (application stopped) |
| `validate` | Post-restore validation checks on the running installation |
| `maintenance on\|off --reason "<why>" [--acknowledge-restore-state]` | Maintenance mode |
| `support-bundle <file>` | Write a sanitised support bundle (`docs/SUPPORT_BUNDLE.md`) |
| `evidence <file> [--csv]` | Write the sanitised evidence report |

Upgrade commands (`docs/UPGRADE_RUNBOOK.md`; policy `docs/UPGRADES.md`):

| Command | Purpose |
|---|---|
| `upgrade-preflight [--backup <id>] [--apply-phase] [--json <file>] [--csv <file>]` | Read-only readiness report; exit 0 only when Ready or Ready with warnings |
| `upgrade-plan [--backup <id>]` | What would run and the words to type |
| `migration-report` | Every migration: applied or pending, category, review, checksum agreement |
| `upgrade-script <module> [<file>]` | The SQL pending migrations of a module would run, for review |
| `upgrade-rehearse <id> <new database> --reason "<why>" --confirm "REHEARSE UPGRADE FROM <id>" [--accept <codes>]` | Upgrade a restored copy; the installation is untouched |
| `upgrade-apply <id> --reason "<why>" --confirm "UPGRADE <from> TO <to>" [--accept <codes>] [--expect-sha256 <hash>]` | Upgrade the installation (verified backup and maintenance mode required) |
| `upgrade-rollback-evidence <id> <new database> --reason "<why>" --confirm "PROVE ROLLBACK TO <id>"` | Prove rollback by restore into a new database |
| `upgrade-history` | Rehearsals, upgrades and rollback evidence |
| `upgrade-manifest-verify` | Check the manifest this build carries |

Run as `dotnet Ecp.Host.dll <command>` (inside the container: `docker exec ecp dotnet Ecp.Host.dll <command>`). Exit codes: 0 success, 2 refused or failed, 64 usage.

## 12. Troubleshooting

| Message or symptom | Cause and remedy |
|---|---|
| "Backup and restore need SQL Server" | The installation runs on SQLite (demonstration). Use SQL Server |
| "No backup directory is configured" / "inside a path the web server serves" | Set `Ecp:Recovery:BackupDirectory` to an absolute path outside the web content |
| "The database server could not write the backup ... Operating system error 5" | The server's account cannot write the backup directory. Fix the directory permissions (section 4 of `docs/BACKUP_AND_RESTORE.md`) |
| "reported success but the backup file is not visible to the application" | The directory is not shared between the server and the application, or `SqlServerBackupDirectory` names a different path. Fix the mapping |
| "The backup account could not connect" | Check `AdminConnectionStringRef` and that the account can connect to `master` |
| "Another operation holds the recovery lease" | A backup, restore or validation is running or crashed recently. Wait up to `LeaseMinutes`, or take the lease over with a reason |
| Restore blocked: "never tested against the active database" | Choose a new database name |
| Restore blocked: "A database with the target name already exists" | Choose a new name, or drop the old target yourself |
| Restore blocked: "different environment" | The backup came from another mode. Use the explicit override only if you accept live data landing here |
| `VAL-002` Failed "created by a newer version" | Run the version of the platform that made the backup |
| `VAL-001` Failed after a restore on another server | The application's login is orphaned. Remap it (section 5) |
| `VAL-010` Failed | Document content is missing or damaged beyond what the backup recorded. Restore again; do not release the copy |
| `VAL-009` Failed | An audit chain does not verify. Investigate who changed the records |
| Encrypted backup cannot be restored | The certificate (and its key) is not on the restoring server. Restore it there first |

## 13. Failure map (OPS-03)

| Dashboard shows | Meaning | Do |
|---|---|---|
| Failed: *A migration failed* / maintenance *Upgrade failed* | The schema may be part-way | Roll back by restore (`docs/ROLLBACK_BY_RESTORE.md`). Do not retry |
| Failed: *Restore failed* | A restore stopped; maintenance mode kept on | Section 7. Do not retry blindly; acknowledge the restore state only when you know it |
| Failed: *recorded as running with no live lease* | The process stopped part-way | Read the operation's evidence and the application log by correlation id. Treat the result as unknown |
| Warning: *Lease expired without release* | A holder stopped | Nothing is blocked; check the history for an interrupted run |
| Warning: *no verified backup / older than the limit* | Recovery would lose more than intended | Take and verify a backup |
| Warning: *dead-lettered messages* | E-mail could not be delivered after retries | The tenant's administrator retries them once the provider is fixed |
| Failed: *pack in use would be refused* | A pack no longer satisfies the platform or the trust policy | Resolve before any upgrade (`docs/UPGRADES.md` section 9) |
| Failed: *signing key expired* | Packs signed by that key are refused | Register the publisher's new key; revoke the old one |
| Not Verified: anything | Not looked at, or not readable here | Decide: do it, or accept the gap in writing. It is never a pass |
