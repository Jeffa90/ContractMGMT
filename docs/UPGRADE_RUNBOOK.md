# Upgrade runbook (OPS-02)

For a trained operator. Policy and background: `docs/UPGRADES.md`. Way back: `docs/ROLLBACK_BY_RESTORE.md`. Backups: `docs/BACKUP_AND_RESTORE.md`. Run commands as `dotnet Ecp.Host.dll <command>` (inside the container: `docker exec ecp dotnet Ecp.Host.dll <command>`). Exit codes: 0 success, 2 refused or failed, 64 usage. Everything below can also be done on **Administration, Operations** (Upgrade Readiness, Upgrade Preview, Migration Report, Upgrade History, Post-Upgrade Validation, Rollback Evidence) by a System Administrator with a recent sign-in.

> Passing every step below is evidence, not approval. The decision to upgrade, the change window and the sign-off belong to the organisation (section 10). This procedure never skips the backup or maintenance mode, and has no downgrade step.

## 0. Before you start (days before)

1. Read the release notes and the manifest summary: `upgrade-manifest-verify` (valid for this build? source versions? breaking changes? operational actions?).
2. Confirm which application version is running now and that you can redeploy it: **keep the previous release's files or image until the upgrade is accepted**. Rollback needs it.
3. Confirm the backup directory is configured and shared with the database server (`docs/BACKUP_AND_RESTORE.md` section 4) and has room for a backup and for a restored copy.
4. Book the maintenance window. Estimate its length from the rehearsal (step 3), not from a guess.
5. Decide who signs off (section 10).

## 1. Deploy the new files without admitting users

Deploy the new build next to the old one (new container image, same configuration and secrets, **do not route users to it yet**). The new build must not run `migrate` in Production: on a database that already holds an installation it refuses.

## 2. Pre-flight against the real installation (read-only)

```
dotnet Ecp.Host.dll upgrade-preflight --json preflight.json --csv preflight.csv
```

Read every finding. Outcomes:

| Outcome | Meaning | Do |
|---|---|---|
| Ready | Nothing found | Continue |
| Ready with warnings | Each warning needs the owner's explicit acceptance, by code, at the apply step | Decide, record, continue |
| Blocked | A critical issue exists | Fix it (the remediation is in the finding); never work around; run again |
| Not Verified | A check could not be made (for example SQLite, or storage figures without `VIEW SERVER STATE`) | Resolve it, or accept in writing that it is unchecked; never treat as passed |

At this stage "not in maintenance mode" and "no backup named" are expected. Findings about **drift**, **unknown or modified migrations**, **a database newer than this build**, **an unsupported source version**, **packs or published configuration that would not pass**, or **a committing migration batch** are real problems: stop and take them to the supplier or the owner.

## 3. Rehearse on a restored copy (never on the real database)

1. Enter maintenance mode, take and verify a backup with the *running* version, release maintenance if the window is later:
   ```
   dotnet Ecp.Host.dll maintenance on --reason "Upgrade rehearsal backup"
   dotnet Ecp.Host.dll backup --reason "Pre-upgrade rehearsal" --hold-maintenance
   dotnet Ecp.Host.dll verify-backup <backup id>
   ```
2. Rehearse (restores into a NEW database, upgrades it, validates it, leaves it for inspection; the real installation is not touched):
   ```
   dotnet Ecp.Host.dll upgrade-rehearse <backup id> <new rehearsal database> --reason "Rehearsal for <change id>" --confirm "REHEARSE UPGRADE FROM <backup id>"
   ```
3. Read the evidence: pre-flight on the copy, each migration and its duration, post-upgrade validation (`UPV-001..003` and the integrity checks), and the journeys result. Note the **duration** for the window.
4. Look at the copy: point a *separate* application instance at it if you want business users to try it. Drop the copy when finished (the platform never drops databases itself).

If the rehearsal fails, the real upgrade must not proceed. Fix the cause, build a new rehearsal.

## 4. The window: backup, maintenance, plan

1. Enter maintenance mode (all changes refused with 503, background work takes nothing new).
2. Take and verify a **fresh** backup with the running version. This is the rollback point; taking it inside the window makes the data-loss window nil.
   ```
   dotnet Ecp.Host.dll backup --reason "Pre-upgrade <change id>" --hold-maintenance
   dotnet Ecp.Host.dll verify-backup <backup id>
   ```
3. Stop the old application instances, start the new build (still without users; the health endpoints work, changes are refused).
4. Plan and pre-flight in the apply phase:
   ```
   dotnet Ecp.Host.dll upgrade-plan --backup <backup id>
   dotnet Ecp.Host.dll upgrade-preflight --backup <backup id> --apply-phase --json preflight-apply.json
   ```
   Must be Ready or Ready with warnings. Note the manifest SHA-256 and the exact words to type.
5. Review pending migrations marked for review (`migration-report`; `upgrade-script <module>` shows the SQL).

## 5. Apply

```
dotnet Ecp.Host.dll upgrade-apply <backup id> --reason "<change id and why>" --confirm "UPGRADE <from> TO <to>" --expect-sha256 <manifest hash> [--accept UPF-021,UPF-023]
```

* The configured database account must be allowed to change the schema; the pre-flight checks this (`UPF-004`). If your deployment separates the application account from the schema owner, configure the new build with the schema owner's connection for the duration of the window only, then switch back.
* The command takes the upgrade and recovery leases, enters maintenance mode (*Upgrade in progress*), waits up to 60 s for background jobs to finish (it stops before any migration if they do not), migrates module by module, records each migration, validates, and **releases maintenance mode only if validation passes**.
* While it runs nothing else may back up, restore, validate or upgrade. A second `upgrade-apply` is refused.
* **If it fails or validation does not pass, maintenance mode stays on** (*Upgrade failed*). Do not release it by hand and do not run it again to "see if it works". Go to section 7.

## 6. After a successful upgrade

1. Open **Post-Upgrade Validation** (or `upgrade-history`) and read it: migration records, schema, row-level security, audit chains, documents.
2. Admit users to the new build. Watch health, notification dispatch and the first approvals.
3. Run your own acceptance checks (a contract in each organisation: search, export, approve, document download). The CI journeys are the platform's check, not yours.
4. Take a new backup with the new version and verify it. **Keep the pre-upgrade backup and the previous release's files** until the owner accepts the upgrade; then follow your retention policy.
5. Prove the way back, off to the side (does not touch the live database):
   ```
   dotnet Ecp.Host.dll upgrade-rollback-evidence <pre-upgrade backup id> <new target database> --reason "Rollback proof <change id>" --confirm "PROVE ROLLBACK TO <backup id>"
   ```
6. Sign off (section 10) and file the evidence (`<BackupDirectory>/_evidence/*` or the Upgrade History downloads) with the change record.

## 7. If it goes wrong: roll back by restore

The only supported way back. Full steps: `docs/ROLLBACK_BY_RESTORE.md`. In short: leave maintenance mode on; stop the new application; restore the verified pre-upgrade backup (`restore <backup id> --in-place ...` or into a new database and repoint); start the **previous** application version; validate; admit users. Everything done after the backup is discarded: tell the business what that window was.

## 8. Failure map

| Symptom | Meaning | Do |
|---|---|---|
| Pre-flight Blocked: *database newer than this build* | You are running an older application against a migrated database | Run the newer application, or restore a backup that matches the application |
| Blocked: *schema drift* | Someone changed the database outside the platform | Find out what and why; do not upgrade over it. Restore a clean copy or have the supplier assess |
| Blocked: *historical migration changed* | The build was altered or built from different source | Use the supplier's build; do not proceed |
| Blocked: *no/unverified/foreign backup* | Rollback would not be possible | Take and verify a backup of this installation, in this environment, with the running version |
| Warning: *backup older than the limit* / *changes since backup* | Rollback would discard that work | Take a fresh backup, or accept the loss explicitly (`--accept`) |
| Refused: *operation in progress* | A backup, restore, validation or upgrade holds the lease | Wait, or inspect **Leases**; takeover only after you know the holder is gone |
| Stopped: *background work did not finish* | A job still held its lease after the quiescence wait | Maintenance stays on; let it finish or investigate; run the procedure again from section 4 |
| Failed during a migration | Schema may be partly changed | Roll back by restore. Do not retry |
| Validation failed after a clean upgrade | Something differs from the backup | Keep maintenance on; read the failing checks; roll back by restore unless the supplier can explain the difference |

## 9. Compatibility findings

Packs and configuration findings (`UPG-PCK-*`, `UPG-CFG-*`) are resolved by the owner before the window: install a compatible pack version, fix or archive an unused pack, correct a draft. The upgrade never republishes, rewrites a release or moves a workflow pin. A contract stays on the workflow release it started under.

## 10. Operator sign-off (placeholders for the organisation)

| Item | Recorded by | Value |
|---|---|---|
| Change / ticket id | | |
| Source and target platform versions | | |
| Manifest SHA-256 | | |
| Pre-upgrade backup id and verification time | | |
| Rehearsal evidence file and duration | | |
| Warnings accepted (codes) and by whom | | |
| Maintenance window start / end | | |
| Post-upgrade validation outcome | | |
| Rollback evidence file | | |
| RPO / RTO for this installation (organisation to define; placeholders in `docs/DISASTER_RECOVERY.md`) | | |
| Upgrade accepted by | | |

The platform does not fill these in and does not declare production readiness.

## 11. Dashboard cross-check (OPS-03)

Before the window and after the upgrade, open the operations dashboard (`docs/OPERATIONS_DASHBOARD.md`) and file its JSON form with the change record: before, no Failed line you did not expect; after, schema versions at the target, no pending migrations, maintenance released, jobs and outbox draining. A failed upgrade is never retried; see `docs/ROLLBACK_BY_RESTORE.md`.
