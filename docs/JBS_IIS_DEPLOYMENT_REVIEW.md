# JBS IIS deployment review and gap-plan addendum

Date: 2026-11-11. Scope: **on-prem IIS only** (JBS QA server `AUQLDDC01QA2WI` and a Production
server), deployed by GitHub Actions. Docker and Azure are out of scope for JBS and are not reviewed.
Reviewed against the JBS .NET standards (`jbs-dev-standards` 1.3.0) and `docs/JBS_DEPLOYMENT_GAP_PLAN.md`.
Nothing here declares production approval.

## 1. Verdict

`JBS_DEPLOYMENT_GAP_PLAN.md` is accurate in what it lists, and the architecture can stay as it is.
For an IIS-only deployment through GitHub Actions it is **not complete**. Seven gaps are missing and
four existing items need re-ranking:

| # | Missing from the gap plan | Rank |
|---|---|---|
| G-1 | **Automated database upgrade in the deployment pipeline** (section 3) | P0 |
| G-2 | **`appsettings.json` defaults to Demo/SQLite/auto-migrate**: an IIS site missing one `Ecp__Mode` variable runs as a demo with a SQLite file in the site folder | P0 |
| G-3 | **Data-protection keys under IIS**: `ProtectKeysWithDpapi()` (`Program.cs:78`) is per-user DPAPI; with no loaded profile, cookies/antiforgery keys can become unreadable after a recycle or identity change | P0 |
| G-4 | **Document uploads capped at ~30 MB** by the in-process `MaxRequestBodySize` default (no `RequestSizeLimit` on document pages), and IIS `maxAllowedContentLength` (100 MB in the scaffold, ~28.6 MB in the SDK-generated `web.config`) blocks the 2 GB migration upload | P0 (was part of P0-2) |
| G-5 | **Unit tests fail on Windows** (2 of 979): `MigrationAuditAndGuardTests.Several_problems_are_all_reported_together` (`/tmp/z` is not a full path on Windows) and `PackFormatTests.A_symbolic_link_is_refused` (`ZipArchive` on Windows writes a non-Unix "made by", so the symlink bit is ignored). Test-only defects, but they block a Windows build agent | P0 |
| G-6 | **IIS Windows Authentication, as in JBS AI Initiatives** (decided 2026-11-11, section 1a). The deploy scripts already set Windows Authentication on and Anonymous off. The app does not yet accept that identity, so until P0-1 is built nobody can sign in | P0 (with P0-1) |
| G-7 | **Backup share reachable by SQL Server and the app.** The database servers are separate hosts, so this must be a UNC share: the SQL Server service account on the DB server needs write access, and the runner and app pool accounts on the app server need read access. Needed by G-1 and by the app's own backup feature | P0 for G-1 |

## 1a. Decisions recorded 2026-11-11

| Decision | Effect |
|---|---|
| **Sign-in is the same as JBS AI Initiatives**: IIS Windows Authentication (Kerberos/NTLM) on, Anonymous off site-wide; the app reads the Windows identity IIS forwards (`IISDefaults.AuthenticationScheme`) | Answers I-12 (Entra not used) and I-05 (Anonymous off; only the warm-up path `/health/live` is anonymous in IIS). P0-1 drops from **L to M**: no Negotiate package or Kerberos code is needed, because IIS does the handshake. The app must (a) authenticate `/SignIn` with the IIS scheme, (b) match the Windows user to an **invited** account by SID (with UPN or `DOMAIN\user` shown), (c) optionally map AD groups through the existing group-mapping engine, (d) issue the existing `ecp.session` cookie. The first administrator from the setup wizard must be linked to a Windows account. OIDC and Demo stay available for other installations (portability) |
| **Servers** | QA: app `AUQLDDC01QA2WI`, database `AUQLDDC01QA2XI`. Production: app `AUQLDDC01APP3WI`, database `AUQLDDC01DBP3WI`. These are shared with JBS AI Initiatives; this app gets its own IIS site, app pool, host name and database |

Re-ranking for the JBS IIS route:

- **P1-2 Azure DevOps pipeline → not required.** This app follows the JBS GitHub route (`dotnet-delivery`:
  citizen developers, GitHub Actions to self-hosted IIS runners). That pipeline is now in
  `.github/workflows/deploy.yml` (see `docs/DEPLOYMENT.md`).
- **P1-5 Scripted migrations for a DBA → replaced by G-1** unless the DBA team insists on scripts (DB-06).
  The app's `upgrade-script <module>` can still produce SQL for review.
- **P0-3 Background jobs → mostly done by the pipeline.** `scripts/ci/Initialize-IisSite.ps1` /
  `Set-IisAlwaysOn.ps1` set `startMode=AlwaysRunning`, no idle time-out and preload. Remaining: the
  "no job cycle recently" readiness check.
- **P0-2 web.config → done by the pipeline, except limits.** The deploy stamps `APPENV_*` settings into
  the published `web.config`; request limits still need G-4.

## 2. Standards check (independent of the compliance document)

Confirmed by reading the code and running the tools, 2026-11-11:

| Standard | Result | Evidence |
|---|---|---|
| CSRF on every state-changing endpoint, global | Compliant | Only Razor Page handlers change state; antiforgery on by default, `SameSite=Strict`; no `DisableAntiforgery`; the only opt-out is the Error page |
| Authenticate by default | Partly | Fallback policy at `Program.cs:115`. **11 of 13** anonymous opt-outs have no "who calls it and why" comment, including the state-changing Setup pages (gap plan P2-7) |
| Scope parameters validated server-side | Compliant (sampled) | Tenant only from the session claim; organisation IDs from forms are checked in module services. To confirm: `OnboardingReadiness.GetAsync` and `Admin/Simulation` rely on callers for the scope check |
| Parameterised SQL | Compliant | Values always parameterised. Identifiers interpolated only from constants/EF metadata; `DBCC CHECKDB (N'{name}')` (`Recovery/Validation.cs:603`) is the one validated non-constant value (already a P2-6 deviation) |
| Serilog, enrichers, sinks | **Non-compliant** | No Serilog. Under IIS in-process with `stdoutLogEnabled=false`, only Warning+ reaches the Event Log; Information is lost (P0-4) |
| Log the exception object | Partly | 3 places log `ex.Message`/type name instead: `Setup/SetupService.cs:205`, `Host/Security/Oidc.cs:138`, `Program.cs:203-206` |
| Config layering, safe defaults, discoverable keys | **Non-compliant** | Unsafe base file (G-2); no `appsettings.Production.json`; no user secrets (P1-3) |
| csproj: SDK-style, lock files, warnings as errors | Compliant | `Directory.Build.props`; 26 lock files; locked-mode restore passes on Windows |
| csproj metadata | Non-compliant | No `Company`/`Copyright`/`NeutralLanguage` (P1-4) |
| SCA | Compliant | `dotnet list package --vulnerable --include-transitive`: no vulnerable packages in any project |
| Windows Negotiate sign-in | **Non-compliant** | Not present; OIDC and Demo only (P0-1) |
| Windows build | **Fails tests** | Publish OK on .NET SDK 10.0.204; unit tests 977/979 (G-5) |

The deviations in gap plan section 4 (Razor Pages, no Hangfire, no Moq/FluentAssertions, `Ecp.*`
naming) are still reasonable; record them in one ADR for acknowledgement as planned.

## 3. G-1: automated database upgrade in the pipeline

### Why it is not just "run migrate"

In Production mode, `migrate` works only on an **empty** database (`UpgradeCli.GuardMigrateAsync`).
On an existing installation the app requires its own upgrade procedure: a verified backup, maintenance
mode, a pre-flight check, then `upgrade-apply` with a typed confirmation phrase and the manifest hash.
It applies migrations module by module, validates, and releases maintenance mode only if validation
passes. The pipeline should **drive that procedure**, not bypass it.

### Proposed flow (in `iis-deploy.yml`, before files are swapped)

New script `scripts/ci/Invoke-EcpDatabaseUpgrade.ps1`, run on the self-hosted runner with the
**new** package extracted to a staging folder and the site's settings plus the migration identity:

1. `upgrade-manifest-verify` (new build): fail if the manifest is invalid.
2. Detect state (`upgrade-preflight --json`):
   - **Empty database** → `migrate`, done (first install; `setup-token` stays a manual first-run step).
   - **No pending migrations** → skip to the file swap.
   - **Pending** → continue.
3. With the **currently deployed** build (the runbook requires the running version for the backup):
   `maintenance on`, then `backup --reason "Deploy <sha>" --hold-maintenance`, then `verify-backup <id>`.
4. Take the site offline (`app_offline.htm`), swap in the new files (existing `Deploy-IisSite.ps1`
   behaviour), keeping the previous release folder for rollback.
5. New build: `upgrade-plan --backup <id>` (read the confirmation phrase), then
   `upgrade-preflight --backup <id> --apply-phase --json`. **Blocked → fail.** Warnings are accepted only
   if their codes are listed in a per-environment variable `ECP_UPGRADE_ACCEPT` (empty by default, so a
   new warning stops the deploy for a person to decide).
6. `upgrade-apply <id> --reason "Deploy <sha>" --confirm "<phrase>" --expect-sha256 <manifest hash>`.
7. Success → remove `app_offline.htm`, run the smoke test (`/health/ready`), upload the evidence files
   (`<BackupDirectory>/_evidence`, preflight JSON) as workflow artifacts.
   **Failure → stop.** The site stays in maintenance mode, which is the runbook's rule. The run log
   names the backup ID and points to `docs/ROLLBACK_BY_RESTORE.md`. No automatic restore.

QA runs this on every merge to `main`. Production runs the same steps only after the
Production environment's required reviewers approve, which counts as the change sign-off.

### What it needs

| Item | Where |
|---|---|
| Migration identity connection string (schema owner: `db_ddladmin` plus rights to create functions and **security policies** for row-level security, or `db_owner` if the DBA prefers) | GitHub environment **secret** `ECP_MIGRATION_SQL`, passed to the CLI as `Database__ConnectionStringRef=env:ECP_MIGRATION_SQL` for the upgrade steps only |
| Admin connection for `BACKUP`/`RESTORE` (`db_backupoperator`, plus `VIEW SERVER STATE` for pre-flight storage checks) | Secret `ECP_RECOVERY_ADMIN_SQL` → `Ecp__Recovery__AdminConnectionStringRef=env:...` |
| Backup directory writable by the **SQL Server service account** and readable by the runner and app pool | Variable `APPENV_Ecp__Recovery__BackupDirectory` (UNC path if SQL Server is on another host) |
| Runtime app pool login stays least-privilege (`db_datareader`, `db_datawriter`, `EXECUTE`) | DBA (DB-04) |
| Reviews for migrations flagged "review required" | `upgrades/migration-reviews.json` in the repo, before merge |

DBA questions this settles or raises: DB-06 (who runs migrations; answer: the pipeline, under a
separate identity, with approval for Production), DB-07 (app-level backups alongside the DBA's
backups: confirm both are wanted, and their retention).

## 4. Revised P0 list for JBS IIS (QA then Production)

| Order | Item | Effort |
|---|---|---|
| 1 | G-5 fix the two Windows test failures | S |
| 2 | G-2 safe base `appsettings.json` (Production defaults; demo values moved to `appsettings.Demo.json` / Development) | S |
| 3 | G-3 machine-level key protection (`ProtectKeysWithDpapi(protectToLocalMachine: true)`, or a certificate) and/or `loadUserProfile=true` in `Initialize-IisSite.ps1` | S |
| 4 | G-4 request limits: `RequestSizeLimit` on document upload pages; `maxAllowedContentLength` stamped by the deploy at 2 GB, with app-level limits enforcing the real per-page sizes | S |
| 5 | G-6 warm-up path set to `/health/live` (done in `scripts/ci/Set-IisAlwaysOn.ps1`); SPN `HTTP/<host>` for each app pool account | S |
| 6 | P0-4 Serilog with a rolling file sink under the data root, redaction kept | M |
| 7 | G-1 + G-7 automated database upgrade | M |
| 8 | P0-5 first QA deploy as the verification run, recorded in `deploy/STATUS.md` | M |
| 9 | P0-1 Windows sign-in through IIS, as in JBS AI Initiatives (section 1a) | M |

QA can be deployed and its hosting checked after items 1 to 8. Users can't sign in until item 9 is done.

## 5. Questions still open

- **DB-04 / DB-06:** the DBA must create the database on `AUQLDDC01QA2XI` / `AUQLDDC01DBP3WI` and three
  logins (runtime, migration, backup). Is a pipeline-run migration with Production approval acceptable?
- **Backup share (G-7):** where it lives (a share on the DB server, readable from the app server, is
  simplest) and how much space it needs. Also, does the DBA want the app's own backups alongside theirs (DB-07)?
- **Data root:** which drive for `EcpData` on `AUQLDDC01QA2WI` and `AUQLDDC01APP3WI`.
- **Access model:** must every user be invited in the app (the current design), or should membership of an
  AD group admit them? JBS AI Initiatives uses in-app membership plus a config fail-safe admin list.
