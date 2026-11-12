# JBS IIS deployment review and gap-plan addendum

Date: 2026-11-11. Scope: **on-prem IIS only** (JBS QA server `AUQLDDC01QA2WI` and a Production
server), deployed by GitHub Actions. Docker and Azure are out of scope for JBS and are not reviewed.
Reviewed against the JBS .NET standards (`jbs-dev-standards` 1.3.0) and `docs/JBS_DEPLOYMENT_GAP_PLAN.md`.
Nothing here declares production approval.

## Update: Section 4 implemented on branch jbs/iis-integration

The application changes in Section 4 were built on branch jbs/iis-integration. The mapping from this review to the work is in `docs/JBS_IIS_IMPLEMENTATION_STATUS.md` (items A to J). Everything is **Implemented** and tested here and **Not Verified on IIS**; the first QA deployment is the proof. The pipeline calls for external recovery points (section 3) are built as `Ecp:Recovery:Mode=External`; the upgrade is still not run by the pipeline.

CI: commit e0d2ac7 passed every job (Linux, Windows, SQL Server 2022, container); see `deploy/STATUS.md`. Nothing has run on IIS.

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
| G-7 | **Backups belong to Infra and the DBA team, but the app's upgrade insists on its own backup.** `upgrade-apply` and its apply-phase pre-flight require a verified backup taken by the app (`UpgradeRequest.BackupId`), and there is no setting to use an external one. JBS needs an *external recovery point* mode (section 3). The app's documents and packs live in the data folder on the app server, so Infra's server backups must include `EcpData` | P0 for G-1 |

## 1a. Decisions recorded 2026-11-11

| Decision | Effect |
|---|---|
| **Sign-in is the same as JBS AI Initiatives**: IIS Windows Authentication (Kerberos/NTLM) on, Anonymous off site-wide; the app reads the Windows identity IIS forwards (`IISDefaults.AuthenticationScheme`) | Answers I-12 (Entra not used) and I-05 (Anonymous off; only the warm-up path `/health/live` is anonymous in IIS). P0-1 drops from **L to M**: no Negotiate package or Kerberos code is needed, because IIS does the handshake. The app must (a) authenticate `/SignIn` with the IIS scheme, (b) match the Windows user to an **invited** account by SID (with UPN or `DOMAIN\user` shown), (c) optionally map AD groups through the existing group-mapping engine, (d) issue the existing `ecp.session` cookie. The first administrator from the setup wizard must be linked to a Windows account. OIDC and Demo stay available for other installations (portability) |
| **Backups are done by Infra and the DBA team**, not by the app (2026-11-11) | The app's backup, restore and rollback-evidence commands are not used at JBS. Rollback after a failed upgrade is a DBA point-in-time restore plus Infra's restore of `EcpData`. No backup share is needed. Answers DB-07 |
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
| Scope parameters validated server-side | Compliant (sampled) | Tenant only from the session claim; organisation IDs from forms are checked in module services. To confirm: `OnboardingReadiness.GetAsync` and the access simulator page (src/Ecp.Host/Pages/Admin/Simulation.cshtml) rely on callers for the scope check |
| Parameterised SQL | Compliant | Values always parameterised. Identifiers interpolated only from constants/EF metadata; `DBCC CHECKDB (N'{name}')` (`Recovery/Validation.cs:603`) is the one validated non-constant value (already a P2-6 deviation) |
| Serilog, enrichers, sinks | **Non-compliant** | No Serilog. Under IIS in-process with `stdoutLogEnabled=false`, only Warning+ reaches the Event Log; Information is lost (P0-4) |
| Log the exception object | Partly | 3 places log `ex.Message`/type name instead: `Setup/SetupService.cs:205`, `Host/Security/Oidc.cs:138`, `Program.cs:203-206` |
| Config layering, safe defaults, discoverable keys | **Non-compliant** | Unsafe base file (G-2); no an appsettings.Production file; no user secrets (P1-3) |
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

### Code change needed: external recovery point

The app's own upgrade procedure is kept (maintenance mode, pre-flight, manifest hash, migrations
module by module, post-upgrade validation, release only on success). The one change is that, when
configured with `Ecp:Recovery:Mode=External`:

- `upgrade-apply` takes `--external-recovery-point "<UTC time and reference>"` instead of a backup ID,
  and records it in the evidence and upgrade history;
- the pre-flight backup checks are skipped. **This matches JBS AI Initiatives** (decided 2026-11-11):
  the pipeline trusts that the DBA's normal backups exist and does not check them. A later option is
  a read-only check of `msdb.dbo.backupset` that blocks the upgrade if the last backup is too old;
- `backup`, `restore` and `upgrade-rollback-evidence` refuse to run and point to the DBA process;
- failure messages say "ask the DBA to restore to <recovery point>" instead of naming an app backup.

Effort: S to M, mostly in `Ecp.Recovery` (preflight, upgrade service, CLI) plus tests.

### Proposed flow (in `iis-deploy.yml`, before files are swapped)

New script a new Invoke-EcpDatabaseUpgrade script (to be written), run on the self-hosted runner with the **new**
package in a staging folder and the site's settings plus the migration identity:

1. `upgrade-manifest-verify`: fail if the manifest is invalid.
2. Detect state (`upgrade-preflight --json`):
   - **Empty database** → `migrate`, done (first install; `setup-token` stays a manual first-run step).
   - **No pending migrations** → skip to the file swap.
   - **Pending** → continue.
3. Take the site offline (`app_offline.htm`), and record the recovery point (UTC now). Keep the
   previous release folder for rollback.
4. New build: `upgrade-plan` (read the confirmation phrase), then `upgrade-preflight --apply-phase --json`,
   **Blocked → fail.** Warnings are accepted only if their
   codes are in the per-environment variable `ECP_UPGRADE_ACCEPT` (empty by default, so a new warning
   stops the deploy for a person to decide).
5. `upgrade-apply --external-recovery-point "<time> deploy <sha>" --reason "Deploy <sha>" --confirm "<phrase>" --expect-sha256 <manifest hash>`.
6. Success → swap files, remove `app_offline.htm`, smoke test `/health/ready`, upload the pre-flight and
   upgrade evidence as workflow artifacts.
   **Failure → stop.** The site stays offline in maintenance mode. The run log gives the recovery point
   for the DBA's point-in-time restore and the previous release folder to redeploy. No automatic restore.

QA runs this on every merge to `main`. Production runs the same steps only after the
Production environment's required reviewers approve, which counts as the change sign-off.

### What it needs

| Item | Where |
|---|---|
| Migration identity: **QA uses the app pool account `AMH\svc_jbsai_qa`** (decided 2026-11-11). It needs schema-owner rights (`db_ddladmin` plus rights to create functions and **security policies** for row-level security, or `db_owner`) | The pipeline starts the CLI as that account with `Start-Process -Credential` using `IIS_APP_POOL_PASSWORD`, so Integrated Security connects as it. The runner's own account needs no SQL rights. Needs the Secondary Logon service on the app server |
| `APPENV_Ecp__Recovery__Mode=External` | GitHub environment variable |
| One account for running and migrating means the running app has schema-change rights. Accepted for QA; decide separately for Production (DB-04) | DBA |
| Infra backup of `EcpData` on the app server, in step with the DBA's database backups | Infra |
| Reviews for migrations flagged "review required" | `upgrades/migration-reviews.json` in the repo, before merge |

DBA questions this settles or raises: DB-06 (who runs migrations; proposed: the pipeline, under a
separate identity, with approval for Production).

## 4. Revised P0 list for JBS IIS (QA then Production)

| Order | Item | Effort |
|---|---|---|
| 1 | G-5 fix the two Windows test failures | S |
| 2 | G-2 safe base `appsettings.json` (Production defaults; demo values moved to a Demo settings file / Development) | S |
| 3 | G-3 machine-level key protection (`ProtectKeysWithDpapi(protectToLocalMachine: true)`, or a certificate) and/or `loadUserProfile=true` in `Initialize-IisSite.ps1` | S |
| 4 | G-4 request limits: `RequestSizeLimit` on document upload pages; `maxAllowedContentLength` stamped by the deploy at 2 GB, with app-level limits enforcing the real per-page sizes | S |
| 5 | G-6 warm-up path set to `/health/live` (done in `scripts/ci/Set-IisAlwaysOn.ps1`); SPN `HTTP/<host>` for each app pool account | S |
| 6 | P0-4 Serilog with a rolling file sink under the data root, redaction kept | M |
| 7 | G-7 external recovery point mode, then G-1 automated database upgrade | S to M + M |
| 8 | P0-5 first QA deploy as the verification run, recorded in `deploy/STATUS.md` | M |
| 9 | P0-1 Windows sign-in through IIS, as in JBS AI Initiatives (section 1a) | M |

QA can be deployed and its hosting checked after items 1 to 8. Users can't sign in until item 9 is done.

## 5. Questions still open

- **DB-04 / DB-06:** the DBA must create the database on `AUQLDDC01QA2XI` / `AUQLDDC01DBP3WI` and grant `AMH\svc_jbsai_qa` (QA) the rights above; Production account to be decided. Is a pipeline-run migration with Production approval acceptable?
- **Backups:** confirm the new databases are in the DBA's normal backup schedule (point-in-time restore
  wanted), and that Infra's server backups include `EcpData` on the app servers.
- **Data root:** which drive for `EcpData` on `AUQLDDC01QA2WI` and `AUQLDDC01APP3WI`.
- **Access model:** for Jeff to decide. See section 6.

## 6. Decision for Jeff: who can sign in with Windows sign-in?

With IIS Windows Authentication, every JBS domain user who reaches the site arrives already
identified. The question is what the app does with someone it has not seen before.

**What the app does today (with OIDC):** sign-in succeeds only for an account an administrator has
already **invited** (`IdentityApi.ResolveLoginAsync` refuses everyone else with "not_invited").
Identity **group mappings** already exist (src/Modules/Ecp.Access/Mappings.cs, Admin > People > Mappings). They
give roles to people *who are already signed in* according to the groups the provider reports.
They do not create accounts.

| | A. Invite only (current design) | B. AD group admits people (new) | C. Both |
|---|---|---|---|
| Who can sign in | Only people an admin has invited | Anyone in a named AD group, such as `AMH\ContractPlatform-Users`; the account is created at first sign-in | Invited people, plus members of the named group(s) |
| Who grants access | App administrators, in the app | IT/AD group owners, through AD requests | Both |
| Roles | Assigned in the app | From group mappings (e.g. `...-Legal` gives Legal reviewer); otherwise none until an admin adds them | Either |
| Leavers | Admin disables them, or AD disables the login (they can't authenticate anyway) | Removed from the group, so no access at next sign-in | Both |
| Like JBS AI Initiatives? | Close: it uses in-app membership plus a fail-safe admin list in config | No | Partly |
| Build effort on top of Windows sign-in (P0-1) | None extra | S to M: create the account at first sign-in, but only for configured groups; pass Windows group SIDs into the existing mapping engine; audit it; tests | S to M (same as B) |
| Main risk | Admin effort for a large user base | A wrongly broad group (e.g. `Domain Users`) admits everyone, so the setting must refuse very broad groups and be audited | Two places to check when someone asks "why can they get in?" |

**Things to know whichever option is chosen:**
- Windows identities are matched by **SID**, not by name, so a renamed account keeps its access.
- Windows sign-in gives no verified email address, so invitations need to name the Windows account
  (`AMH\user`) as well as, or instead of, the email. That is a small change either way.
- Group membership comes from the user's Kerberos token, so a group change takes effect at the
  user's next Windows logon, not instantly.
- Under option B or C, the app must stay safe if the group setting is missing or empty: it should
  then admit only invited people, which is the safe default under the JBS configuration standard.

**Recommendation (Jeff's call):** start with **A**. It matches the current design and JBS AI Initiatives
and needs no extra build. Add **C** later if inviting people becomes a burden, limited to named groups
that IT owns.

**Decision:** ______ (Jeff), date ______
