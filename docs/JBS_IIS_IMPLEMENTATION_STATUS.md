# JBS IIS implementation status

Branch: jbs/iis-integration (created from the verified product state; pull request 1's pipeline work is merged into it). `main` and the known-good baseline branches are untouched. Nothing here is deployed, and **nothing here has been run on IIS**. Production approval is external governance and is not declared.

Labels used throughout: **Implemented** (built and tested here, evidence named), **Not Verified** (built, but needs an environment this workspace does not have), **Needs Infrastructure answer**, **Needs DBA answer**, **Future**.

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
- Local totals: unit 991, architecture 113, integration 375 passed (6 skipped), API 310 passed (9 skipped): 1,789 passed, 0 failed, 15 skipped. Results of the full CI run and the Windows job are recorded in `deploy/STATUS.md` after the run completes.
