# JBS IIS implementation status

Branch: jbs/iis-integration (created from the verified product state; pull request 1's pipeline work is merged into it). `main` and the known-good baseline branches are untouched. Nothing here is deployed, and **nothing here has been run on IIS**. Production approval is external governance and is not declared.

Labels used throughout: **Implemented** (built and tested here, evidence named), **Not Verified** (built, but needs an environment this workspace does not have), **Needs Infrastructure answer**, **Needs DBA answer**, **Future**.

## QA deployment preparation (2026-11-16)

The owner supplied the Infrastructure and DBA answers (recorded in `docs/DEPLOYMENT.md`, "JBS QA deployment configuration"). This round prepared QA and changed no product behaviour beyond what the answers required:

| Change | Status | Evidence |
|---|---|---|
| Anonymous `/warmup` for IIS preload: 200, empty body, nothing else; works before setup and over plain HTTP from the local warm-up | Implemented | `WarmupEndpointTests`; the anonymous-endpoint allowlist test still passes (the warm-up is a terminal middleware, not an endpoint) |
| IIS warm-up page switched from `/health/live` to `/warmup`; anonymous access removed from `/health/live` | Implemented in `scripts/ci/Set-IisAlwaysOn.ps1`; **not run** | `WarmupEndpointTests` checks the script text |
| Existing IIS sites get the idempotent settings on every deploy (load user profile, folder grants, always-on, warm-up); previously the script stopped early for an existing site | Implemented; **not run** | - |
| Folders: data, keys, packs and logs as confirmed; `Data\Keys` restricted to the pool account and Administrators | Implemented (configuration and deploy script); **not run** | setup-script offline dry run |
| Log file `log-.txt`, 31 files, console on | Implemented (`Ecp:Logging:FileName`, existing retention setting) | `LoggingTests` |
| No Application Insights for JBS | Nothing to remove: no sink exists; the JBS values set none | dry run output; R-LOG-02 |
| Setup script: `--environment QA`, `--dry-run --offline`, folder and log values | Implemented; dry run executed **offline only** | `tools/tests/test_setup_github_environments.py` (16 tests) |
| SQL Server permission probe for the DBA | Implemented and run in CI ([run 111](https://github.com/Jeffa90/ContractMGMT/actions/runs/43799781505)): proposed runtime rights work; proposed migration rights are refused (see `docs/DEPLOYMENT.md`) | `SqlPermissionProbeTests` |
| Hangfire wording removed from the IIS scripts (the platform does not use Hangfire) | Done | - |

CI for this preparation: [run 111](https://github.com/Jeffa90/ContractMGMT/actions/runs/43799781505) (commit e630bda), all four jobs green. Linux: every repository, documentation, pack, secrets and supply-chain step and the IIS publish check. Windows: unit 991, architecture 113, integration 375 passed and 6 skipped (SQL variants), API 317, publish check passed. SQL Server 2022: 401 passed, 0 failed, plus the upgrade rehearsal, the permission probe and the performance baseline. Container: passed.

### REQUIRED BEFORE QA

- **Infrastructure:** confirm the site folder and names (proposed: site and pool `JbsContracts`, site folder `C:\inetpub\JbsContracts\app`); the `IIS_APP_POOL_PASSWORD` secret for `AMH\svc_jbsai_qa`, only if the deploy is to create the pool; the self-hosted runner on the QA server with label `iis-qa`, local administrator for the first site set-up, and its service account recorded as the migration identity; how the first-run setup wizard is reached (it answers only on loopback and the binding is host-name specific: either a temporary `localhost` binding on the server or, for the first set-up only, `Ecp__Setup__AllowRemote=true`, which is still protected by the one-time setup token; remove it afterwards).
- **DBA:** database creation and the name JBSContracts; SQL Server version; compatibility level; collation; row-level security approval; application-lock approval (`sp_getapplock`); backup and recovery policy; copy-only pre-deploy backup decision; RPO and RTO; and **the migration identity's rights**: the probe proved db_ddladmin + db_datareader + db_datawriter cannot run the first install (SQL 15247: creating the `ecp_runtime` role and granting on it), and adding db_securityadmin still fails (SQL 262: ALTER ANY SECURITY POLICY). The runtime rights (db_datareader + db_datawriter) worked; adding the runtime account to `ecp_runtime` is recommended.
- **SMTP:** host email.jbssa.com.au is known; port, authentication method, TLS and sender address are **not confirmed**. Until then QA uses the mock mail provider and the setup readiness step asks for that to be acknowledged; real mail is not tested.
- **GitHub:** a repository administrator (not the Production reviewer) runs the setup script dry run **online** (`gh` logged in), reads it, then applies it for QA only.

## What changed, by owner decision

| Item | Decision / requirement | Status | Evidence (tests are in the repository) |
|---|---|---|---|
| A | Windows-only test failures fixed | Implemented | Reparse-point and case-insensitive temp-path fixes and tests in `PackFormatTests`, `MigrationAuditAndGuardTests`; the Windows CI job (`windows-build-and-test`) runs every non-SQL test on `windows-latest` |
| B | Fail-safe Production defaults | Implemented | The published `appsettings.json` is Production, SQL Server, no demo, no auto-migrate, no data directory. Demo/SQLite values moved to `appsettings.Development.json` (never published). New guards R-ST-01 (blank data directory), R-ST-02 (data or key directory inside the application folder), R-MIG-01 (auto-migrate in Production). Tests: `HostConfigurationTests`, `ProductionRefusalTests` |
| C | IIS publish packaging | Implemented (files); **IIS run Not Verified** | `src/Ecp.Host/web.config` (AspNetCoreModuleV2, in-process), published by `dotnet publish` and checked by `tools/check-iis-publish.py` (CI step); per-endpoint upload limits (`UploadLimits.cs`); explicit request-timeout policy. Tests: `IisPackagingTests`, `tools/tests/test_check_iis_publish.py` |
| D | Windows Authentication provider, invite-only | Implemented (simulated server authentication); **IIS Not Verified** | `WindowsSignIn` service, capture middleware, sign-in and refusal pages. Tests: `WindowsSignInTests` (11 on SQLite, 6 more on SQL Server in CI), `WindowsAuthenticationHostTests` (7) |
| E | First administrator linked to a Windows identity | Implemented | Setup identity step offers "Windows sign-in"; the administrator step records the Windows account; "Test sign-in" proves it. Test: `The_first_administrator_set_up_with_a_windows_account_signs_in_and_reaches_the_application` |
| F | Data Protection | Implemented (Windows DPAPI Not Verified) | `DataProtectionSettings`, readiness R-SEC-04, guards R-KEY-01 and R-ST-02. Tests: `DataProtectionTests` (key ring survives a new process; Windows protection only runs on the Windows CI job) |
| G | External DBA recovery point | Implemented | `Ecp:Recovery:Mode=External`, `declare-recovery-point` / `list-recovery-points`, preflight check UPF-020 reports **NOT VERIFIED** for a declared point and the operator must accept it by name. Tests: `ExternalRecoveryPointTests_Sqlite` (6) and `_SqlServer` (3) |
| H | Integrated SQL authentication without a secret file | Implemented; **integrated authentication itself Not Verified** (no Windows or domain here) | Production accepts a credential-free connection string in ordinary configuration; any string with a user name or password must still be a secret reference. Readiness R-DB-02 reads the login and roles from the server. Tests: `ConnectionInspectionTests`, `HostConfigurationTests`, `RuntimePrivilegeSqlServerTests` |
| I | Serilog foundation | Implemented; **Application Insights and fatal-error email Not Verified** | Structured JSON lines, shared context, redaction, durable rolling file. Tests: `LoggingTests` (16) |
| J | Pipeline safety | Implemented in the workflow and script files; **not run** | `deploy.yml` waits for green CI on the exact commit, one build, QA then the same artifact to Production only from `main` after approval; build manifest verified at deploy; rollback of a half-copied site; no secrets stamped into `web.config`. `scripts/setup/setup-github-environments.sh` rewritten and **not executed** |

## How invite-only Windows sign-in works

1. IIS authenticates the person (Windows Authentication, Anonymous off except `/health/live`) and puts a Windows principal on the request. That proves identity and nothing else.
2. A middleware **before** `UseAuthentication` lifts the principal out of `HttpContext.User` and keeps only the account name and SID. Without this, the authorisation fallback policy would treat the server's principal as a signed-in user. Test: `A_windows_identity_supplied_by_the_server_is_never_an_application_session`.
3. `/Account/WindowsSignIn` asks the Identity module whether the person may sign in. All of these must hold: an invitation names this Windows account (or this SID is already linked), the account is not disabled, and the person holds at least one active role. Email is never used. Nothing is created and no role is granted or changed by signing in.
4. The invitation names `DOMAIN\name`. At the first sign-in it is linked to the stable SID. A later account with the same name but a different SID is refused (`not_invited`), so a deleted-and-recreated directory account does not inherit access.
5. Refusals redirect to a plain page that names the Windows account (what to tell an administrator) and write an audit event `auth.signin_refused` with the reason code, account, SID and correlation id.
6. Sign-out goes to a page that does not sign straight back in. Step-up re-authentication re-checks that the same Windows identity is still behind the browser; Windows cannot prompt for a password again (see "Needs Infrastructure answer").

Not built, on purpose: Active Directory group-driven onboarding (deferred by the owner).

## Things that need an answer before QA (do not guess)

**NEEDS INFRASTRUCTURE ANSWER**

- Certificate source and thumbprint for each binding, and the DNS names.
- The Production application-pool account. QA is recorded as `AMH\svc_jbsai_qa`, shared with another application; that sharing is recorded as a justified deviation and needs Infrastructure's confirmation.
- SPN registration for Kerberos; whether NTLM fallback is acceptable.
- Whether the Windows domain list should be restricted (the provider accepts a list of NetBIOS domain names; empty accepts any authenticated domain, still invite-only).
- Whether interactive step-up (password prompt) is required for sensitive actions. Windows Authentication cannot provide it.
- The data root location and permissions (documents, key ring, logs), its backup scope, and the data-protection key protection (machine scope is the default; a certificate is supported and Not Verified).
- Reverse proxy or load balancer addresses, if any.
- Log retention, any central log collection, the Application Insights decision, and the SMTP relay for fatal-error email.
- Runner installation, labels and the runner account.

**NEEDS DBA ANSWER**

- Server and database names (recorded IT decisions exist; the DBA must confirm).
- The runtime and migration accounts. The runtime account should hold the `ecp_runtime` role only; the migration account needs rights to change the schema and create row-level security policies. The QA account is shared and is currently expected to be both, which readiness R-DB-02 will report as over-privileged.
- Whether the SQL Server certificate is trusted by the web server. The script does not write `TrustServerCertificate`; the DBA must say if it is needed. Readiness R-DB-02 reports it when present.
- Backup and restore ownership: recovery point cadence, retention, encryption, who restores, rehearsal evidence, recovery point objective and time objective.
- SQL Server version, compatibility level and collation (tested on SQL Server 2022 only), and whether row-level security and application locks are permitted.
- The change window and account for `dotnet Ecp.Host.dll migrate`.

## Verification that did not happen

- No test ran on IIS. The ASP.NET Core Module, Windows Authentication, Kerberos, the application-pool identity, DPAPI machine scope and integrated SQL authentication are **Not Verified**.
- `scripts/setup/setup-github-environments.sh` and the PowerShell deploy scripts were **not executed**. The setup script is checked statically (`tools/tests/test_setup_github_environments.py`), and the workflows parse as YAML.
- Real SQL Server evidence comes from the CI job `sqlserver-integration` (SQL Server 2022 on Linux); it is not available in this workspace.
- CI result (commit e0d2ac7, [run 109](https://github.com/Jeffa90/ContractMGMT/actions/runs/43129533634)): **all four jobs green** (Linux 1 job with every repository, documentation, pack, secrets and supply-chain step; Windows; SQL Server 2022; container). Windows: 1,789 passed, 0 failed, 6 skipped (SQL variants). SQL Server: 400 passed, 0 failed, 0 skipped. Local totals matched (1,789 passed). CI history on this branch: run 107 (commit 6db826a) failed on three Windows-only defects, all fixed in 446e6f0 (the Windows build names embedded upgrade files with a backslash so no upgrade manifest was found: a **product** defect that would have broken upgrades on the IIS server; and a test that left a document file open, which Windows cannot delete: a **test** defect). Run 108 (446e6f0) passed on Linux, Windows and the container job, and had one SQL Server failure in the cleanup of the new runtime-privilege test (pooled connections kept the test login open; a **test** defect), fixed in e0d2ac7. Run 109 (e0d2ac7) is fully green.
