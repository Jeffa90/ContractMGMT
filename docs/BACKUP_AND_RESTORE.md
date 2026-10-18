# Backup and restore (OPS-01, OPS-02 foundation)

> Upgrades (OPS-02) build on this document: a verified backup is required before a Production upgrade, and rollback is restoring one. See `docs/UPGRADES.md` and `docs/ROLLBACK_BY_RESTORE.md`.

Status: **Implemented** for SQL Server with the local file system document store, verified in CI against a real SQL Server (see section 15). Owner decisions: `requirements/01-authoritative/CONFIRMED_DECISIONS.md`, section D10. Architecture decision: `decisions/ADR-0034-operational-resilience-backup-restore-maintenance-and-leases.md`. Companions: `docs/OPERATIONS_RUNBOOK.md` (what to do, step by step), `docs/DISASTER_RECOVERY.md`, `docs/MAINTENANCE_MODE.md`, `docs/OPERATION_LEASES.md`, `docs/SUPPORT_BUNDLE.md`.

> **Having backup tooling is not having a recovery plan, and it is not production approval.** This platform gives a trained operator the means to create, verify and restore a complete installation backup, and it records evidence of doing so. It does **not** decide how often to back up, how long to keep backups (retention), what the recovery point and recovery time objectives are, how backup storage is encrypted and who may read it, when to rehearse a restore, or who signs off. Those are the organisation's decisions and responsibilities (section 13 and the sign-off template in `docs/OPERATIONS_RUNBOOK.md`). Tooling never replaces organisational backup governance, and nothing here declares that an installation is ready for production.

## 1. Labels used in this document

| Label | Meaning here |
|---|---|
| **Implemented** | Built, tested, and used by the operator interface or command line |
| **Verified** | Implemented and exercised by an executed test, with the result recorded (`deploy/STATUS.md`) |
| **Not Verified** | Exists as guidance or scaffolding; no executed evidence |
| **Future** | Not built |
| Mocked / Disabled | None in this slice: no backup behaviour is simulated |

## 2. Supported and unsupported models

| Deployment model | Status |
|---|---|
| Container + external SQL Server + persistent document storage (local file system provider on a persistent volume) | **Implemented and Verified** (CI: real `BACKUP DATABASE`, `RESTORE VERIFYONLY`, `RESTORE DATABASE`, post-restore validation, disaster-recovery path) |
| IIS on Windows Server | **Scaffolded / Not Verified** |
| Azure App Service with Azure SQL and Blob storage | **Scaffolded / Not Verified**: Azure SQL is not implemented as a backup provider (its backups are managed by the service); no Blob document provider exists |
| SharePoint or Azure Blob document storage | **Future**: no provider exists. A provider must either list its objects (`IDocumentStoreInventory`) or have its own independently verified backup |
| SQLite | **Demonstration and tests only**: backup and restore are refused with a plain message |
| Per-tenant backup and restore | **Future** (decision D10.6). Restore is installation-level |
| Point-in-time recovery | **Documented, not implemented** by application code (decision D10.7, `docs/DISASTER_RECOVERY.md`) |
| Differential and transaction-log backups | **Future**. The platform takes full, COPY_ONLY backups; your own SQL Server backup jobs are unaffected |

## 3. Configuration (`Ecp:Recovery`)

Set in host configuration or environment (`Ecp__Recovery__BackupDirectory`). Nothing here is a secret except the administrator account, which is a secret **reference** in Production.

| Setting | Meaning |
|---|---|
| `BackupDirectory` | Absolute path where backups are written, as the application sees it. Must be outside any served path; the platform refuses a path inside or around the web content. Empty means "not configured" |
| `SqlServerBackupDirectory` | The same directory as the **database server** sees it (a mount on Linux, a share or drive on Windows). Empty means the same path |
| `RestoreTargetDirectory` | Parent of the document and package directories created for a restore (one sub-directory per target database) |
| `AdminConnectionStringRef` | Secret reference (`file:/run/secrets/...` or `env:...`) to a connection string for an account that may `BACKUP` and `RESTORE`. Not an inline value in Production. Empty falls back to the application account, and readiness says so |
| `EncryptionCertificate` | Name of a SQL Server certificate. When set, `BACKUP ... WITH ENCRYPTION (AES_256, SERVER CERTIFICATE)` is used and the manifest records it. The certificate and its private key must be backed up **separately** or the backup cannot be restored |
| `StorageProtection` | Your statement of how the backup storage is protected (for example "volume encrypted at rest; read access limited to the backup operators group"). Recorded in each manifest and in the evidence report. **Never verified by the platform** |
| `CopyOnly` (default true), `TableChecksums` (default true), `ReauthenticationMinutes` (10), `LeaseMinutes` (10), `StaleBackupDays` (7) | Behaviour; `TableChecksums` reads every table in full (turn off only for very large installations, which loses the content comparison but keeps counts) |

Readiness check `R-BKP-01` reports Degraded when backup is not configured, the database has no supported backup, the application account is used instead of a dedicated one, neither encryption nor a protection statement exists, no verified backup exists, or the newest verified backup is older than `StaleBackupDays`. It never reports an installation "ready for production".

## 4. The shared backup directory

SQL Server writes the `.bak` file itself, on **its** file system, as **its** service account. The application then reads that file to hash it, record it and (later) verify it. So the directory must be shared: a bind mount in a container, an NFS or SMB mount, or the same disk. Two permission facts follow:

* The database server's account must be able to **write** the database sub-directory of each backup and **traverse** the directory above it. The application creates the backup folder with mode `0711` (others may enter, not list) and the database sub-directory `0777` while the file is being written, then tightens it to `0711`.
* The application's account must be able to **read** what the server wrote. On Linux, where the server runs as a different user, grant this with a default ACL on the shared directory (`setfacl -d -m u:<app user>:rwx <dir>`), a shared group, or run both under one user. CI uses the default-ACL pattern (`.github/workflows/ci.yml`, step "Start disposable SQL Server") and `tools/sqlserver-up.sh --backup-dir DIR [--run-as-root]` does the same locally.

If the application cannot see the file the server reports writing, the backup fails with a message saying so; nothing is recorded as a recovery point.

## 5. What a backup is

```
<BackupDirectory>/bk-20261017T031500Z-0a1b2c3d/
  status.json            progress while running; "Written" or "Failed" afterwards
  database/<name>.bak    the SQL Server backup (CHECKSUM, COPY_ONLY, optionally encrypted)
  documents/index.ndjson one line per stored object: key, bytes, SHA-256, whether it matched its own address
  documents/t/<tenant>/<aa>/<sha256>   every object, at its content address
  packs/tenants/<tenant>/packages/<sha256>.ecpkg   installed package bytes (not quarantined uploads)
  inventory.json         counts and fingerprints before and after the database backup
  manifest.json          the manifest (written last; deterministic)
  manifest.sha256        its SHA-256
  verification-*.json    results of integrity checks (appended, never part of the manifest)
<BackupDirectory>/_evidence/restore-*.json   evidence of each restore
```

A folder **without a manifest** is Incomplete or Failed and is listed as such; it is never a recovery point. Failed attempts are cleaned of their payload but keep their status file. A backup id is never reused and a backup is never overwritten (files are created with `CreateNew`).

**Manifest contents** (format version 1; unknown fields and versions are refused): backup id; scope (`installation`); created and completed UTC; platform version; environment mode; operator id and name; reason; correlation id; database section (engine, version, name, backup type, COPY_ONLY, file, bytes, SHA-256, checksummed, `RESTORE VERIFYONLY` passed, encrypted, certificate name); schema section (applied migrations per module, enabled row-level security policies); document section (provider, whether content was backed up, object count and bytes, index hash, database versions recorded, missing, orphan and hash-mismatch counts, skipped entries); pack section (installed packs by tenant with package hash, signature status and status; shipped packs by hash; public trust keys by id, fingerprint and status); inventory before and after (per-tenant audit-chain head and validity, organisations with a regional fingerprint and active pack, release fingerprints, document counts, tax-identifier projection gaps; per-table row counts and content checksums); whether the backup was **quiescent**; protection section; the file list with SHA-256 and size; outcome (`Completed` or `CompletedWithWarnings`); warnings.

**Never in a manifest, evidence file or log**: passwords, tokens, connection strings, private keys, secret values, document or record content, personal data other than operator ids. Every manifest, evidence report and support bundle is scanned for secrets after it is built and refused if one is found; free text typed by an operator (a reason) is masked first and line breaks are flattened.

## 6. Creating a backup

Operator interface: **Administration, Operations, Backups, Create a backup** (permission `operations.backup`, recent sign-in, a reason). Command line: `backup --reason "<why>" [--hold-maintenance]`.

1. Checks: SQL Server provider; directory configured, absolute, outside web paths, writable.
2. Takes the installation-wide lease `recovery.exclusive` (section 12). Another backup, restore or validation running means a refusal that names the holder.
3. Records the run and an audit event (`operations.backup.started`); allocates the immutable id.
4. Optional maintenance hold (`--hold-maintenance` / checkbox, needs `operations.maintenance`): makes the backup **exact** by pausing changes while it runs.
5. Inventory before; `BACKUP DATABASE` (COPY_ONLY, CHECKSUM); `RESTORE HEADERONLY`; `RESTORE VERIFYONLY ... WITH CHECKSUM`; SHA-256 of the `.bak`.
6. Document store copy through the provider abstraction, hashing every object; cross-check with the database's document versions (missing, orphan, hash mismatch).
7. Package copy and cross-check with installed-pack records; shipped pack hashes and public trust keys recorded.
8. Inventory after; schema facts; audit-chain verification of every tenant.
9. Manifest written, sealed, then the **integrity check** (section 7) reads the whole backup back. Only then is the run recorded as Completed.
10. Audit event `operations.backup.completed` with the manifest hash.

Outcomes: **Completed**; **Completed with warnings** (still a valid recovery point; warnings list what the backup saw: documents missing or damaged at the source, orphans, a broken audit chain, an installation that changed during the backup, no encryption and no protection statement); **Failed / Incomplete** (not a recovery point; the reason is shown). A lost lease stops the work.

An **online** backup (no maintenance hold) is transactionally consistent for the database, but counts taken around it can differ by activity during the window; the manifest then records ranges (before and after) instead of exact values and validation compares within them. A **quiescent** backup (maintenance mode on and two equal inventories) allows an exact comparison of every table's content checksum.

## 7. Integrity verification

`verify-backup <id>` / **Verify again**, and automatically at the end of every backup and at the start of every restore. It checks, and reports with stable codes:

| Code | Finding |
|---|---|
| BKV-001 to 005 | The backup id or folder is not valid, is a link, is missing, has no manifest, is too large, or has no hash file |
| BKV-006 | The manifest does not match its recorded hash |
| BKV-007 | The manifest is not valid (version, scope, unknown field, unsafe or duplicate path, malformed hash, missing database entry) |
| BKV-010 to 013 | A listed file is unsafe, missing, the wrong size, or has a different hash (changed since the backup) |
| BKV-020 to 025 | A document entry is unsafe (including a link), missing, changed, or the index does not add up |
| BKV-026 | Files in the folder that the manifest does not list (warning; restore ignores them) |
| BKV-030, 031 | Not encrypted and no protection statement (warning); warnings recorded at backup time (information) |
| BKV-040 to 044 | The database server's own check: backup damaged, no checksums, `RESTORE VERIFYONLY` failed, header unreadable; or not checkable on this provider |

Outcomes: **Passed**, **Passed with warnings**, **Failed**, **Not Verified**. A manifest that someone altered **and re-sealed** is self-consistent, so the files alone cannot tell. The manifest hash is therefore also recorded in the audit event and the run history, and a restore can be given the hash to expect (`--expect-sha256`, or the optional field on the restore page); a mismatch refuses the restore.

## 8. The document store

* **Through the abstraction.** The platform asks the store for its objects (`IDocumentStoreInventory.EnumerateAsync`) and reads each through `IDocumentStorageProvider`; it never walks the store's directories itself. The local file system provider enumerates `t/{tenant}/{aa}/{sha256}`, skips links and unexpected names (reported, never read) and preserves content-addressed paths, so a restored object lands at the same address (a restore writes through `PutAsync` and refuses any object that does not hash to the address it was backed up under).
* **Metadata and content are separate.** Metadata (document, version, classification, parent contract, hash, size) is in the database backup. Content is in the documents folder of the backup. The backup cross-checks them and records missing, orphan and mismatching objects; restore and validation repeat the cross-check and may only report **no more damage than the backup recorded**.
* **Tenant prefixes** are preserved; one tenant's objects are never addressed with another's key.
* **Other providers.** A provider that cannot list its objects makes the backup say "document content was not backed up by the platform" (a warning), and restore refuses unless an operator accepts that. SharePoint and Azure Blob providers do not exist; the platform does not claim they are backed up.
* Objects are copied one at a time with constant memory per object; the object index is held in memory (a limit for very large stores, `docs/BACKUP_AND_RESTORE.md` section 16).

## 9. Packs, trust store and configuration

* Installed package bytes are copied by hash name and re-hashed; shipped packs come from the application image and are recorded by hash and version; **only public trust keys** (id, fingerprint, status) are recorded, never private keys (the platform holds none).
* Configuration drafts and releases, rollback history, terminology, capabilities, regional profiles, approval matrices, workflows, notification policies, custom fields, shared views, branding, classification policies, pack assignments and onboarding state all live in the database and travel with the database backup. Restore preserves package immutability, release hashes and pinned-workflow resolution; validation proves it (VAL-011, 013, 014).

## 10. Restore

Operator interface: **Administration, Operations, Restore**: choose a backup and a **new** target database name, read the sanitised preview, give a reason, type the phrase `RESTORE <backup id>` and confirm your sign-in (permission `operations.restore`, held by the System Administrator only). Command line: `restore <id> <new database> --reason "..." --confirm "RESTORE <id>" [--expect-sha256 <hash>] [--allow-different-environment] [--accept-missing-documents]`.

How the seventeen stages of the owner's workflow are implemented:

| Stage | Implementation |
|---|---|
| 1 Enter maintenance mode | After the preview checks, the installation enters maintenance mode (cause `RestoreInProgress`) |
| 2 Confirm the target is not the active production database | The target's server and database name are read **from the servers** and compared with the application's; names such as `master`, `tempdb`, a name with unusual characters, or an existing database are refused. The disaster-recovery path (stage 2b) is separate |
| 3 Select manifest | By backup id from the catalogue (read from the directory, not from the database) |
| 4 Validate schema | Strict manifest parsing: format version, scope, unknown fields, path safety |
| 5 Verify every declared hash | Section 7 |
| 6 Verify the database backup | `RESTORE HEADERONLY` and `RESTORE VERIFYONLY ... WITH CHECKSUM` |
| 7 Verify document completeness | The document index against every object |
| 8 Sanitised preview | Counts, versions, warnings, blockers; no secret, no content |
| 9 Explicit confirmation | Reason, typed phrase naming the backup, recent sign-in, permission |
| 10 Restore the database | `RESTORE DATABASE ... WITH CHECKSUM, MOVE ...` into the new database |
| 11 Restore documents | Through `PutAsync` into a new, empty document directory |
| 12 Restore package bytes | Hash-checked copy into a new, empty package directory |
| 13 Apply no new migrations | None applied. A database from an older platform version reports pending migrations as a warning; running `migrate` is a separate, deliberate step |
| 14 Restricted validation mode | The restored copy is opened beside the running installation and held in maintenance mode (`RestoreValidationPending`) |
| 15 Post-restore checks | Section 11 |
| 16 Release maintenance only after successful validation | Passed or Passed with warnings releases the copy's maintenance flag and the installation's; otherwise both stay in maintenance (`RestoreFailed`) |
| 17 Record evidence | Run history, audit events in both installations (restore started, completed or failed, and a lineage event appended to the copy's own chain), and `_evidence/restore-*.json` |

Constraints honoured: a restore cannot target another tenant (it has no tenant parameter and only `installation`-scope backups are accepted); a failure leaves the installation in maintenance mode until an operator releases it knowingly (`docs/MAINTENANCE_MODE.md`); nothing continues silently with missing documents (the integrity check stops it); tenant, organisation and audit identifiers and hashes are not rewritten; audit history is not regenerated (the restore only **appends** its own events to the installation chain); configuration is not republished; pack assignments are not changed; the restored database is not upgraded.

**Wrong environment.** A backup made in one environment mode (Production, Demo, Test) is not restored into another without `--allow-different-environment`, because live confidential data would land somewhere less protected.

**Stage 2b: disaster recovery over the active database.** Only from the command line, with the web application stopped: `restore <id> --in-place --reason "..." --confirm "RESTORE <id> OVER THE ACTIVE DATABASE"`. The database is set to single-user, restored with `REPLACE`, set back to multi-user, validated like any restore, and released only if validation passes. Document and package files are written into the live directories idempotently (objects already present are left; nothing is deleted). See `docs/DISASTER_RECOVERY.md`.

**Cut-over is yours.** A validated copy is a database, a document directory and a package directory. Pointing the application at them (connection string, `Ecp:DataDirectory`, `Ecp:Packs:StorageDirectory`), re-mapping the application's database login to the restored database user if the server is different, supplying the same secrets, and re-opening traffic are deliberate steps in `docs/OPERATIONS_RUNBOOK.md`.

### 10.1 Restoring a backup from an earlier platform version

A backup made by platform N-1 restores into a build at N with its migrations still pending (restore applies none; `VAL-003` reports a warning, not a failure, for tables that the pending migrations will create). It is a *copy at the older schema*: run the upgrade procedure on it (`upgrade-rehearse` for a rehearsal) rather than `migrate`, which Production refuses on an installation. To return the live installation to an older state, restore the backup and run the **matching older application** (`docs/ROLLBACK_BY_RESTORE.md`).

## 11. Post-restore validation

Run automatically on a restored copy, and on demand on the running installation (**Operations, Validation**, `operations.validate`, or `validate`). Each result has a stable code, a plain-language description, technical detail (shown only to holders of `operations.validate`), a severity, a suggested remediation and a correlation id.

| Code | Checks |
|---|---|
| VAL-001 | Database connectivity |
| VAL-002 | Migration history: every applied migration is known to this version (a newer database is refused), matches the backup, and any pending migrations are listed (never applied) |
| VAL-003 | Every table this version expects exists |
| VAL-005 | Tenant, organisation, contract, counterparty, audit and every other table: counts (and, for a quiescent backup, content checksums) equal the backup's; for an online backup, within the recorded range |
| VAL-006 | Foreign keys enabled, trusted and satisfied (including the composite tenant keys) |
| VAL-007, 008 | Row-level security policies enabled and equal to the backup's; `SESSION_CONTEXT` behaviour (nothing visible with no tenant; only one tenant's rows with one) |
| VAL-009 | Audit chain of every tenant verifies and ends where the backup recorded; a chain already broken at backup time is preserved and reported, not repaired |
| VAL-010 | Document metadata matches content (presence, size and, in a restore, SHA-256), with no more damage than the backup recorded |
| VAL-011 | Installed package files present, hashed to their record, signatures acceptable under the restored trust store, trust keys equal to the backup's |
| VAL-013 | Configuration release hashes recompute and equal the backup's |
| VAL-014, 015 | Pinned workflow releases exist; documents that belong to a contract find it |
| VAL-016 | Queued deliveries have their notifications |
| VAL-017, 018 | Every organisation resolves the **same** regional settings as before (locale, currency, zone, identifier and address profile); counterparty tax-identifier projection gaps equal the backup's |
| VAL-019 | Every organisation's assigned solution pack resolves (Blank, JBS Procurement, Harborline Construction or any other) |
| VAL-021 | `DBCC CHECKDB ... PHYSICAL_ONLY` |

Overall outcome: **Failed** if any check failed; else **Not Verified** if any could not be run; else **Passed with warnings** if any warning; else **Passed**. Only Passed and Passed with warnings release a restored copy.

## 12. Concurrency and leases

Backup, restore and installation validation share the lease `recovery.exclusive`; two cannot run at once, from any instance. A lease expires if its holder stops renewing it (a crashed instance never blocks for ever), and an operator can take one over with a reason. See `docs/OPERATION_LEASES.md`.

## 13. Protection of backup files (operator responsibility)

A backup holds the whole installation, including confidential contract data. The platform cannot encrypt your storage, restrict who reads it, or keep copies off-site. You must: encrypt the backup storage (volume or object-store encryption) **or** use `EncryptionCertificate` and back the certificate up separately; restrict read and write access to named operators; keep copies in a second location; define retention and deletion; protect the secret provider and the data-protection key ring separately (backups contain neither secret values nor keys); and record all of this in `StorageProtection` so the manifest and the evidence report state it. Operator-written statements are recorded, not verified.

## 14. Audit and evidence

Audit events (system chain): the backup events (started, completed, failed, verified), the restore events (started, completed, failed, and a lineage event appended to the restored copy), the maintenance events (enabled, disabled), the lease events (expired takeover, taken over), validation run, evidence exported and support bundle exported; each action name begins `operations.`. They carry ids, outcomes, hashes and reasons; never backup contents. The evidence report (**Operations, Evidence**, `operations.evidence.export`; JSON and formula-safe CSV) lists backups, runs, leases, maintenance state and the status of every deployment model.

## 15. Verification evidence

| Test | What it proves |
|---|---|
| `BackupRestoreSqlServerTests` | Populated three-business installation backed up (exact), verified, restored beside the running installation, validated (16 checks Passed), and read through the real services: lifecycle state, wording, capabilities, regional settings, packs, releases and documents identical; source untouched; same target refused |
| `BackupFailureSqlServerTests` | Changed component, forged manifest and the anchor, invalid manifests, wrong environment, source documents missing or damaged, document lost from a backup, concurrent backups, held and stale leases, a failed restore left in maintenance, secrets masked, backup directory rules, the active database never a target (including aliases and system names), disaster-recovery path, encrypted backup, planted links, readiness (missing, fresh, stale), a broken audit chain |
| `OperationsFoundationScenarios` (SQLite and SQL Server) and `Many_contenders_on_many_instances_at_once_produce_exactly_one_owner` | Leases, takeover, expiry, audit, maintenance semantics, history, tenant isolation of operational tables, permissions, recent sign-in, support-bundle and evidence redaction; 16 contenders on 4 instances produce one owner |
| `OperationsUiTests`, `OperationsCommandLineTests` | Who can open each page, maintenance enforcement by the host (503, banner, jobs paused), downloads, command-line verbs |
| `UpgradeSqlServerTests` and the OPS-02 tests | Restore feeds the N-1 to N rehearsal and the rollback evidence (`docs/UPGRADES.md` section 14) |
| `RecoveryTests` (unit) | Path and name safety, links, manifest strictness, determinism, redaction, document and package copying, formula safety, outcome rules |

Tests that need a shared backup directory report **NOT VERIFIED** (skipped with that reason), never Passed, when `ECP_TEST_SQL_BACKUP_DIR` and `ECP_TEST_SQL_BACKUP_DIR_SERVER` are not both set.

## 16. Limitations (stated honestly)

* One lease serialises backup, restore and validation; a very long validation blocks backups.
* Content checksums read every table; the document index and object list are held in memory (millions of objects need memory in proportion).
* `DBCC CHECKCONSTRAINTS` and `CHECKDB` cost time proportional to the database; validation is meant for rehearsal and for restores, not for every hour.
* The maintenance flag changes are seen by another instance within three seconds.
* Per-tenant restore, differential and log backups, point-in-time recovery, Azure SQL, SharePoint and Blob providers, scheduled backups, retention enforcement and off-site copy are Future. Scheduling and retention are the operator's (a cron job calling the `backup` command is the supported pattern).
* An encrypted backup needs its certificate on the server that restores it.
* The restored database's application login may be orphaned on a different server; remap it (`docs/OPERATIONS_RUNBOOK.md`).
