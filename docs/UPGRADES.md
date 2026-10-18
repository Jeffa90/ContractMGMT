# Upgrades (OPS-02)

Status: **Implemented** for SQL Server, rehearsed in CI on a real SQL Server (section 14). Owner decisions: `requirements/01-authoritative/CONFIRMED_DECISIONS.md`, section D11. Architecture decision: `decisions/ADR-0035-upgrade-safety-preflight-and-rollback-by-restore.md`. Procedure for operators: `docs/UPGRADE_RUNBOOK.md`. The way back: `docs/ROLLBACK_BY_RESTORE.md`.

> **Upgrade tooling is not production approval.** A Ready pre-flight, a passed rehearsal and retained evidence tell an operator that the mechanics work and what they found. Whether to upgrade a particular installation, when, and who signs it off are the organisation's decisions (change control, release governance). The platform never declares production readiness.

## 1. Labels used in this document

| Label | Meaning here |
|---|---|
| **Implemented** | Built, tested, and used by the operator interface or command line |
| **Verified** | Implemented and exercised by an executed test, with the result recorded (`deploy/STATUS.md`) |
| **Not Verified** | Exists as guidance or scaffolding; no executed evidence |
| **Future** | Not built |
| Mocked / Disabled | None in this slice: nothing about upgrades is simulated. On SQLite the pre-flight reports **Blocked** (upgrades are SQL Server only) rather than pretend |

## 2. Supported upgrade policy

| Rule | Meaning |
|---|---|
| Installation-level | The unit of upgrade is the whole installation (every tenant and module). There is no per-tenant upgrade (D11.1) |
| One step at a time | An upgrade goes from a *supported source version* to the version of the build that is running, as listed in that build's manifest. Skipping versions is supported only where the manifest lists the older version |
| Forward-only | Migrations never run backwards. The platform has no downgrade command, page or method (D11.6) |
| Application downgrade | Running an older application against a database that a newer one has migrated is **not assumed safe** and the pre-flight reports it as a blocker (`UPF-011`) |
| Rollback | Restore the verified pre-upgrade backup, with the application version that made it (D11.8; `docs/ROLLBACK_BY_RESTORE.md`) |
| Backup | A verified backup, named explicitly, is required before a Production upgrade (D11.4) |
| Maintenance mode | Required for the upgrade itself (D11.5). The upgrade enters it with cause *Upgrade in progress* if it is not on |
| SQL Server | Authoritative. SQLite (Demo, Test) is for demonstration; it can neither prove nor perform a supported upgrade (D11.2) |
| Where to test | Only on a restored copy, never on the active production database (D11.3) |
| Unknown paths | A source version, database state or manifest that the build cannot account for is Blocked or Not Verified, never "probably fine" (D11.14) |

The first upgrade this build supports is **1.0.0 to 1.1.0** (`upgrades/manifests/ecp-1.0.0-to-1.1.0.manifest.json`). It adds one table (`ops.MigrationRecord`, with row-level security and the application's grants), changes no configuration schema, and rewrites no data.

## 3. What "N-1" means, and what the fixture is

* **N** is the platform version of the build you are running. **N-1** is the version before it. Platform versions are in `Directory.Build.props`; each module also declares the identifier of its last migration as its *schema version*.
* The **N-1 fixture** used by the tests is a real SQL Server database migrated to *exactly the frozen N-1 schema*: the committed baseline ledger (`upgrades/baselines/ecp-1.0.0.ledger.json`) names the migrations of each module at N-1, and the test applies only those. It is then **populated by this build's own services**, through their public interfaces, with the three shipped businesses (Blank, JBS Procurement, Harborline Construction), contracts in several states, documents, configuration releases, installed packs, queued notifications and audit history.
* **What that proves:** the migrations upgrade a populated N-1 schema; nothing the businesses depend on changes; the upgraded installation passes validation and the representative journeys; the pre-flight, backup, restore, rollback and evidence machinery works end to end.
* **What it does not prove:** that the N-1 *application binaries* wrote that data. It is a frozen-schema fixture, not a historical release, and the only difference between N-1 and N in the first supported path is the tables the new release adds. A release pipeline that keeps the previous container image and populates the fixture with it would be stronger and is **Future**; each release that changes data shape should do it.
* Fixture data is synthetic. No live data is used or committed.

## 4. The upgrade manifest

Built by `upgrade-manifest-build` from three committed inputs and embedded in the build (`upgrades/**` as resources of `Ecp.Recovery`):

| Input | File | Rule |
|---|---|---|
| Frozen baseline ledger of the source version | `upgrades/baselines/ecp-<version>.ledger.json` | Written once at release with `upgrade-baseline`, committed, **never regenerated**. Holds every module and migration identifier with its checksum and a hash of the whole ledger |
| Release notes | `upgrades/release-notes/ecp-<target>.notes.json` | Written by release engineering: supported sources, maximum backup age, configuration changes, operational actions, compatibility warnings, breaking changes, post-upgrade checks, packs |
| Migration review register | `upgrades/migration-reviews.json` | One entry per migration that drops, rewrites or alters existing structure or data (Destructive, DataRewrite, SchemaAlteration), saying what it does and the evidence. Entries marked `baseline-accepted` say plainly that they predate the ledger |

The manifest records: format version; release id; source and target platform versions; minimum supported source; supported sources; source and target ledger hashes; deterministic module order; target schema version per module; **new migrations** with identifier, checksum, categories, touched objects and review state; pack and platform ranges; configuration-schema changes; requirements; operational actions; compatibility warnings; breaking changes; post-upgrade checks; and the rollback method. It states that **backup and maintenance mode cannot be waived** and that **rollback is restore**. It is canonical JSON with its own SHA-256 (`manifestSha256`), carries no secret, and its critical and warning problems are distinguished (`UPM-nnn`). It is deterministic: building it twice gives the same bytes (test).

Validation (`upgrade-manifest-verify`, and the first pre-flight check) refuses a manifest whose hash does not match, whose format is unsupported, whose versions are invalid or non-increasing, whose source baseline is missing or altered, in which a historical migration was removed or modified, whose new-migration list is not the true difference, or which appears to hold a secret. If this build's compiled migrations are not the ones the manifest describes (`UPM-050`) the manifest is not trusted.

## 5. Migration safety

* **Per-module ordered migrations**: modules migrate in tier order, then name; within a module in identifier order. The order is deterministic and tested.
* **Checksum**: SHA-256 of the SQL the engine generates from the migration's `Up` operations (history row excluded). Applied migrations are recorded in `ops.MigrationRecord` with that checksum; an installation that predates the table is *adopted* (checksums recorded from the build, flagged as adopted not applied).
* **Failures that block the pre-flight**: a duplicate identifier; a missing predecessor; a historical migration whose checksum differs from the baseline or from what was recorded (`UPF-015`); a migration in the database that this build does not know (`UPF-011`, database is ahead); schema drift (`UPF-016`).
* **Review**: destructive, data-rewriting and altering migrations are highlighted in the Upgrade Preview and Migration Report with their review evidence; a release cannot be built without them.
* **Reviewable scripts**: `upgrade-script <module>` and the Migration Report download the SQL that the pending migrations of a module would run, generated from the compiled migrations (SQL Server only), for reading. The platform does not execute operator-supplied SQL.
* **Order of events in Production**: nothing migrates before the backup and maintenance-mode checks pass. A bare `migrate` on a database that already holds an installation is refused in Production and points here.
* **On failure**: the upgrade stops at the failing migration, maintenance mode stays on with cause *Upgrade failed*, the step and error are audited and written to evidence, and nothing is retried or repaired. Recovery is rollback by restore (or, if the operator and the supplier understand the failure, a repaired build and a fresh pre-flight).
* **Forbidden**: no migration may silently rewrite audit events, immutable configuration releases, signed pack bytes or pinned workflow identifiers (D11.9). The review register and the post-upgrade audit-chain verification are the controls.

## 6. Pre-flight (`UPF-nnn`)

`upgrade-preflight` (command) and Administration, Operations, **Upgrade Readiness** (page) run it. It reads and changes nothing. Outcomes: **Ready**, **Ready with warnings**, **Blocked**, **Not Verified**; blockers win, then Not Verified, then warnings. Each finding has a stable code, severity (Critical, High, Medium, Low, Info), outcome (Passed, Warning, Failed, Not Verified), a plain explanation, technical detail, remediation and the report's correlation id. Reports download as JSON and formula-safe CSV; the command exits 0 only for Ready or Ready with warnings.

| Code | Checks | Blocks when |
|---|---|---|
| UPF-001 | Upgrade manifest present, intact, describes this build | missing, altered or not for this build |
| UPF-003, 004 | SQL Server and version; account may connect and alter schema | SQLite or unsupported engine; cannot connect; cannot alter |
| UPF-010 to 014 | State (Empty, AtSource, AtTarget, Partial, Ahead, Inconsistent), supported path, newer database, migration order, partial earlier upgrade | not AtSource of a supported version |
| UPF-015, 016 | Historical migration checksums; schema drift against the last applied migration's model | any changed migration or drift (SQL Server only; Not Verified elsewhere) |
| UPF-020 to 024 | Verified backup named, exists, passes its integrity check now, is recent enough (warning), is from this environment and this installation, holds exactly the source schema, and data-loss window (warning) | no, unverified, foreign or wrong-version backup |
| UPF-030, 032 | Free storage on the database and backup/document volumes | almost full (warning when merely tight) |
| UPF-040 to 043 | Maintenance mode (required in the apply phase); other operations running; background work; previous failed upgrade | not in maintenance when applying; another operation running |
| UPF-050 to 057, 059 | Installation health: the validation engine's checks under the upgrade profile (connectivity, tables, referential integrity, row-level security policies, audit chains, documents) | any failed critical validation |
| UPF-060 to 067 | Module probes: pack and configuration compatibility, in-flight work, saved views, outbox, committing migration batches (section 9) | a pack or live release in use that would fail; a committing batch |

*Advisory* phase (looking) treats "not in maintenance mode" as a low warning; *apply* phase makes it a blocker. Passing the pre-flight is not an approval to upgrade.

## 7. The upgrade (`upgrade-apply`, Upgrade Preview page)

Preconditions the service enforces for every caller, not only the page: caller holds `operations.upgrade` in the installation's own tenant with a sign-in within the re-authentication window; the typed words are exactly `UPGRADE <from> TO <to>` (the real installed version, not one the operator guessed); a reason is given (secrets masked); the named backup is verified; the apply-phase pre-flight passes (warnings only with their codes accepted explicitly, recorded in the evidence); if the operator supplied `--expect-sha256`, the manifest's hash equals it.

Then, in order: take the recovery lease and the upgrade lease (a second upgrade, backup, restore or validation is refused while they are held; a lease lost mid-run aborts the run); enter maintenance mode (*Upgrade in progress*); wait for background-job leases to drain (quiescence, default 60 s; if work does not finish, **stop before any migration** and stay in maintenance); migrate module by module; record each migration and checksum; run post-upgrade validation; if it passes, release maintenance mode (acknowledged, audited) and write evidence; if anything fails, keep maintenance mode on (*Upgrade failed*) and write evidence. Leaving an upgrade-related maintenance state by hand needs an explicit acknowledgement that the operator understands the restore state.

Every step is audited (`operations.upgrade.*`) with the correlation id, operator, versions, backup id, manifest hash and outcome; no secret and no business data enter an audit event, report or log line.

## 8. Rehearsal on a restored copy (`upgrade-rehearse`)

Needs `operations.restore` and the words `REHEARSE UPGRADE FROM <backup id>`. It restores the verified backup into a **new database** (never the active one, refused if the name is the active database), runs the pre-flight against the copy, upgrades the copy with the same engine, validates it and reports; the running installation is not touched (a test compares its migrations and audit heads before and after). Evidence is written to `<BackupDirectory>/_evidence`. Run the rehearsal with the same backup you intend to roll back to.

## 9. Pack and configuration compatibility

Probes (section 6) read what is installed and published and judge it against *this* build's validators. They never change it.

| Finding | Meaning |
|---|---|
| `UPG-PCK-001` blocker | A solution pack **in use** by an organisation does not satisfy this platform version (range, format, kinds, schemas, signature) |
| `UPG-PCK-002` warning | An installed pack that nobody uses is not usable by this version |
| `UPG-CFG-002` blocker | A live configuration release would no longer pass validation |
| `UPG-CFG-001`, `UPG-CFG-003`, `UPG-CFG-004` warnings | Published configuration of an unknown kind; configuration that could not be checked; open drafts this version would not accept |
| `UPG-LIF-001..003`, `UPG-NTF-001..003`, `UPG-VW-001`, `UPG-MIG-001`, `UPG-MIG-002` | In-flight signatures and approvals, outbox and jobs, unreadable saved views, a committing migration batch (blocker) |

The upgrade **never** modifies signed pack bytes, republishes configuration, rewrites a release, or moves a workflow pin to make something fit; contracts stay pinned to the workflow release they started under. A finding is resolved by the owner, deliberately, before the upgrade.

## 10. Post-upgrade validation and journeys

The validation engine (`IInstallationValidator`) runs the profile **PostUpgrade**: connectivity; migration records and schema (`UPV-001` records carry the manifest's checksums, `UPV-002` schema equals the final model on SQL Server, `UPV-003` every module at the target schema version); table counts and content checksums against the backup (data identical); referential integrity; row-level security policies and session-context behaviour; audit-chain verification for every tenant; document hashes; installed packs, releases, capabilities, wording and regional settings. The result is shown on the **Post-Upgrade Validation** page and kept in evidence.

The CI rehearsal then runs **representative journeys** through the real services as the real people of each business: create and move a Blank contract; take a lifecycle action on the reference-pack contract that was in flight before the upgrade; sign, submit, approve (by the people the Harborline matrix names) and activate a Harborline subcontract; search and export the register; capture notifications; resolve an approval; download a document; and verify every audit chain.

## 11. Operator interface

| Page (Administration, Operations) | Permission | Notes |
|---|---|---|
| Upgrade Readiness | `operations.read` (run it), `operations.evidence.export` (downloads) | Pre-flight; JSON and CSV downloads |
| Upgrade Preview | `operations.read`; apply needs `operations.upgrade`, rehearse `operations.restore` | What will run, categories, reviews, the words to type; **no downgrade control exists** |
| Migration Report | `operations.read`; script download `operations.validate` | Every migration: applied or pending, category, review, checksum agreement |
| Upgrade History | `operations.read` | Rehearsals, upgrades, rollback evidence; evidence files by exact name only |
| Post-Upgrade Validation | `operations.read` | Latest post-upgrade validation |
| Rollback Evidence | `operations.read`; prove needs `operations.restore` | Restore-based rollback proof; data-loss statement |

`operations.upgrade` is new (94 permissions in the catalogue) and is held only by the System Administrator. Changing actions need a sign-in within `Ecp:Recovery:ReauthenticationMinutes` (default 10) and the installation tenant. Commands: `upgrade-preflight`, `upgrade-plan`, `migration-report`, `upgrade-script`, `upgrade-rehearse`, `upgrade-apply`, `upgrade-rollback-evidence`, `upgrade-history`, `upgrade-manifest-verify`, `upgrade-manifest-build`, and `upgrade-baseline` (release engineering). Exit codes: 0 success; 2 refused or failed (message on stderr); 64 usage.

## 12. Release engineering: what each release that has migrations must ship

1. Bump `Version` in `Directory.Build.props`.
2. Freeze the *previous* release's ledger **before** adding migrations: `dotnet Ecp.Host.dll upgrade-baseline <previous version> upgrades/baselines/ecp-<previous>.ledger.json --note "<what it is>"`. Commit it. Never regenerate it.
3. Add the migrations.
4. For each Destructive, DataRewrite or SchemaAlteration migration, add an entry to `upgrades/migration-reviews.json` (what it does; evidence of a rehearsal on populated data).
5. Write `upgrades/release-notes/ecp-<target>.notes.json`.
6. `dotnet Ecp.Host.dll upgrade-manifest-build upgrades/release-notes/ecp-<target>.notes.json upgrades/manifests/ecp-<source>-to-<target>.manifest.json`; commit.
7. Run the SQL Server rehearsal (CI does) and keep the evidence with the release.

`UpgradeTests` and `UpgradeFoundationScenarios` fail the build if a baseline is altered or regenerated, a migration changed after release, a review is missing or stale, or the manifest is not the one that would be built.

## 13. Evidence

Files under `<BackupDirectory>/_evidence`: `upgrade-<utc>-<backup id>.json` and `rehearsal-<utc>-<backup id>.json` (versions, manifest hash, pre-flight, quiescence, per-module steps, validation, journeys, accepted warnings, correlation id), `rollback-<utc>-<backup id>.json`, and pre-flight reports. They hold identifiers, counts, hashes and outcomes; they hold no secret, connection string, key, business record, document content or personal data. Every finished document is scanned for secrets and refused if one is found; CSV cells are formula-neutralised. In CI they are uploaded as the `upgrade-rehearsal-evidence` artefact. Retention is the operator's; keep upgrade evidence with the change record.

## 14. Verification evidence

| Test | What it proves |
|---|---|
| `UpgradeTests` (unit) | Manifest determinism, hash, tamper refusal; modified or removed historical migrations; reviews for destructive/rewriting migrations; manifest cannot waive backup or maintenance; secrets and unsupported sources refused; state classification (Empty, AtSource, AtTarget, Partial, Ahead, Inconsistent); backup judged by schema; outcome rules fail closed; confirmation phrases; report safety (formulas, secrets); maintenance causes |
| `UpgradeFoundationScenarios` (SQLite and SQL Server) | The shipped manifest, baselines and reviews are intact and complete; maintenance mode refuses configuration publish, rollback and migration commit from any caller; permissions, recent sign-in and installation tenant; no downgrade method exists; one upgrade lease at a time; SQLite reports Blocked |
| `UpgradeSqlServerTests` (real SQL Server) | N-1 populated; backup, restore, pre-flight, upgrade, validation, journeys, evidence on a restored copy while the installation is untouched; the real upgrade under maintenance with release, repeat refused; rollback by restore proved; refusals: no/wrong/foreign/stale backup, no maintenance, concurrent operation; drift, unknown migrations and gaps; failing migration keeps maintenance on; non-quiescent background work; changed or missing manifest |
| `UpgradeUiTests`, `UpgradeCommandLineTests` | Who can open each page and post each action; plain Blocked/Not Verified on SQLite; no downgrade control; downloads are formula-safe and secret-free; evidence names are exact; command-line refusals and usage |

Tests that need a shared backup directory report **NOT VERIFIED** (skipped with that reason), never Passed, when `ECP_TEST_SQL_BACKUP_DIR` and `ECP_TEST_SQL_BACKUP_DIR_SERVER` are not both set. Results by commit: `deploy/STATUS.md`.

## 15. Limitations (stated honestly)

* The N-1 fixture is a frozen-schema fixture populated by current services (section 3). No previous-release binary is run.
* Upgrades need downtime: a maintenance window long enough for the migrations and validation. Zero-downtime and mixed-version operation are **Future**.
* Schema drift, recorded-checksum and final-schema checks run on SQL Server only. Free-space checks need `VIEW SERVER STATE`; otherwise they are Not Verified.
* Rollback by restore discards everything done after the backup (the data-loss window). The pre-flight shows how many audited events postdate the backup; making a fresh backup in maintenance mode immediately before the upgrade makes the window nil.
* Rollback needs the application version that made the backup (`RBK-010`); keep the previous release's files or image.
* Large databases: validation reads every table; plan its duration from a rehearsal, not from a guess.
* IIS and Azure deployments are **Scaffolded / Not Verified** for upgrades, as for backup. The container + external SQL Server model is the one with executed evidence.
* The container image build is verified in CI only (it cannot be built in the authoring sandbox); `upgrades/` is copied into the build stage.
* RPO, RTO, change windows, approval and sign-off are the organisation's (`docs/DISASTER_RECOVERY.md`, `docs/PRODUCTION_READINESS_CHECKLIST.md`).
