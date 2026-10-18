# Disaster recovery

Status: guidance plus an **Implemented and Verified** in-place restore path (`BackupFailureSqlServerTests.The_disaster_recovery_path_restores_over_the_active_database_only_by_name...`). Point-in-time recovery is **documented, not implemented**. IIS and Azure recovery are **Scaffolded / Not Verified**. Companions: `docs/BACKUP_AND_RESTORE.md`, `docs/OPERATIONS_RUNBOOK.md`.

> This document describes what the platform provides and what the organisation must decide. Disaster recovery is an organisational capability (people, storage, networks, secrets, decisions), not a feature of one application. Nothing here declares readiness.

## 1. Scenarios

| Scenario | What to use |
|---|---|
| Accidental deletion or corruption inside the platform's data, server intact | Restore the last good backup **beside** the running installation, validate, then cut over (runbook section 5). Keep the damaged one for investigation |
| Database lost or unusable, same server | Disaster-recovery path: `restore <id> --in-place ...` with the application stopped (runbook section 6), or restore to a new name and cut over |
| Server or host lost | Build a new host from your deployment definition; restore the **database** to the new SQL Server, the **documents and packages** from the backup directory, supply the **secrets and the data-protection key ring** from their own backups, then validate (runbook section 5) |
| Region or site lost | As above in the second location; requires copies of backups, secrets, certificates and the key ring kept there (organisation's process) |
| Ransomware or malicious change | Restore into a clean environment, verify the manifest hash against your records, compare with the audit chain anchors, investigate before reconnecting. A backup in the same place as the attack may be affected: keep an offline or immutable copy |
| A bad release or migration | Rollback by restore: restore the verified pre-upgrade backup (the upgrade procedure requires one), validate, and run the **previous** platform version (`docs/ROLLBACK_BY_RESTORE.md`). Everything after the backup is discarded; migrations never run backwards |

## 2. What a backup contains, and what it does not

Contains: the whole database (every module schema, migration history, tenants, organisations, contracts and counterparties, document metadata, configuration drafts and releases, audit chains, migration projects and provenance, installed pack records and public trust keys, notification queues, regional settings and identifiers, operational history); document content; installed package bytes.

Does **not** contain, and you must recover separately: **secrets** (connection strings, provider credentials, signing keys; they stay in the secret provider); the **data-protection key ring** (`Ecp:DataDirectory/keyring`: cookies, antiforgery and protected setup state); **TLS certificates**; the **SQL Server certificate** if backups are encrypted; the container image or build; shipped packs (they are in the image); host and network configuration; database logins (server-level), which may need remapping; SQL Agent jobs, linked servers and anything else outside the database.

## 3. Point-in-time recovery (documented, not implemented)

The platform takes full, COPY_ONLY backups and does not manage SQL Server log backups. If your RPO is shorter than your backup interval, your DBA runs SQL Server's own full, differential and log backups for the database (the platform's COPY_ONLY backups do not disturb that chain) and restores to a point in time with SQL Server tooling (`RESTORE ... WITH STOPAT`). The document store is content-addressed and append-only in normal operation, so restoring documents from the backup nearest after the database point is safe: extra objects are orphans, not damage; **missing** objects are damage. After a point-in-time restore, run `validate` and compare with the nearest backup's manifest; the audit chain will end where the restored database ends. This path is **not verified** by the platform's tests and the application code does not drive it.

## 4. Recovering around the platform

1. Establish **what** you are restoring from: list backups, verify the chosen one, compare its manifest hash with your records.
2. Establish **where** it goes: a new database (preferred) or in place.
3. Bring back **secrets and key ring** before starting the application.
4. Restore, validate, review warnings, accept or reject.
5. Cut over deliberately; re-run validation on the running installation.
6. Re-enable integrations (email, identity provider) knowingly: a restored copy will send the notifications that were queued at backup time once maintenance mode ends and jobs resume. If that is not wanted, clear or hold the queue first (**Operations**, notifications administration).
7. Record the incident and the evidence (runbook section 10).

## 5. Rehearsal

Rehearse at the frequency your organisation chooses, with the people who would do it in earnest, from the backup storage you would really use, into a database and directories that are not production's, and record the time against your RTO. The platform's own proof (the CI backup and restore of a populated three-business installation) shows the tooling works; only your rehearsal shows your **process, storage, permissions and people** work.

## 5a. Upgrades and rollback (OPS-02)

An upgrade is rehearsed on a restored copy, applied in maintenance mode after a verified backup, and rolled back only by restoring that backup (`docs/UPGRADES.md`, `docs/UPGRADE_RUNBOOK.md`, `docs/ROLLBACK_BY_RESTORE.md`). The **data-loss window** of a rollback is the time between the backup and the failure. RPO and RTO for upgrades are the organisation's to set; the rehearsal and rollback evidence record the measured durations against them:

| Item | Organisation's value (placeholder) |
|---|---|
| Maximum acceptable data-loss window for an upgrade rollback | |
| Maximum acceptable time to complete an upgrade window | |
| Maximum acceptable time to roll back by restore | |
| Rollback evidence last produced (date, file) | |

## 5b. Seeing recovery readiness at a glance (OPS-03)

The operations dashboard (`docs/OPERATIONS_DASHBOARD.md`) shows, in one place, whether a verified backup exists and how old it is, whether a restore has ever been rehearsed, the last validation, the state of maintenance mode and leases, and the last failures. Each is Verified, Warning, Failed or **Not Verified**; a recovery capability that has never been exercised is Not Verified, not Verified. It does not state the organisation's RPO and RTO; those stay with the organisation (section 5a above).

## 6. Not Verified

IIS and Azure recovery; Azure SQL as a source or target; SharePoint or Blob document stores; restoring onto a different SQL Server version or edition than the backup's (SQL Server restores to the same or a newer version; downgrade is not possible); restoring a backup older than the previous major platform version; scale (multi-gigabyte databases and millions of documents have not been rehearsed); point-in-time recovery.
