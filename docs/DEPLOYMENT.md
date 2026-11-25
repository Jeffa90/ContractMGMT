# Deployment to IIS with GitHub Actions (JBS reference)

Status: **Implemented as files; CI green (commit e0d2ac7) including a Windows build-and-test job; Not Verified on IIS and not yet run.** The first QA deployment is the verification run. Docker and Azure models are unchanged (`deploy/docker`, `deploy/azure`). Nothing here is production approval. Open questions are in `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.

## JBS QA deployment configuration (owner-confirmed 2026-11-16)

The values below were confirmed by the owner from Infrastructure and DBA answers. Non-secret values for the GitHub QA environment are in `docs/jbs/jbs-github-environments.env`. Nothing has been applied to GitHub and nothing has been deployed.

| Item | QA | Production |
|---|---|---|
| URL | https://jbs-contracts-qa.jbssa.com.au | https://jbs-contracts.jbssa.com.au |
| Application-pool and runtime SQL identity | `AMH\svc_jbsai_qa` | `AMH\svc_jbs_contracts` |
| Kerberos SPN | HTTP/jbs-contracts-qa.jbssa.com.au on `AMH\svc_jbsai_qa` | HTTP/jbs-contracts.jbssa.com.au on `AMH\svc_jbs_contracts` |
| Certificate thumbprint | 409A784432142E96CD8D5FDCBEEC7AC0870411A1 | REQUIRED BEFORE PRODUCTION |
| Runner label | `iis-qa` | `iis-prod` (not set up) |
| SQL Server | AUQLDDC01QA2Xi (SQL Server 17.0.4065.4, confirmed 2026-11-17), database JBSContracts (**DBA to confirm**), Windows integrated, `Encrypt=True;TrustServerCertificate=True` | REQUIRED BEFORE PRODUCTION |
| Migration identity | the self-hosted runner's service account (today the same account as the app pool) | a **separate** runner service account, not the app pool account (owner decision 2026-11-25): it holds `db_ddladmin` + `db_datareader` + `db_datawriter`; the app pool account holds `db_datareader` + `db_datawriter` only. Account name REQUIRED BEFORE PRODUCTION |

**Folders (data root `C:\inetpub\JbsContracts\`).** `Ecp__DataDirectory` = `C:\inetpub\JbsContracts\Data` (documents are written to `Data\Documents`; Windows folder names are not case-sensitive), `Ecp__DataProtection__KeyDirectory` = `Data\Keys`, `Ecp__Packs__StorageDirectory` = `Data\SolutionPacks`, `Ecp__Logging__Directory` = `C:\inetpub\JbsContracts\logs`. The setup state, declared recovery points and the mock mail outbox are sub-folders of `Data` the application creates itself. `Data\Uploads` is not used by this application: uploaded documents and migration archives are stored in `Data\Documents`, uploaded packs in `Data\SolutionPacks`, so the folder may be created but stays empty.

**Site folder: CONFIRMED by IT (2026-11-17).** Site and pool `JbsContracts`; application (site) folder `C:\inetpub\JbsContracts\app`; persistent data `C:\inetpub\JbsContracts\Data`; logs `C:\inetpub\JbsContracts\logs`. Data and logs sit beside the application folder, not inside it, so every deploy can mirror the new build over `app` and roll back without touching data. The application would refuse a site folder at `C:\inetpub\JbsContracts` (R-ST-02, tested by `The_confirmed_JBS_QA_layout_with_the_site_in_app_beside_Data_is_accepted_and_a_site_folder_at_the_data_root_is_refused`), and the setup script refuses it before writing anything.

**IIS.** Windows Authentication on and Anonymous off for the site; Anonymous on for `/warmup` only, the Application Initialization page. `/warmup` answers 200 with an empty body and nothing else; every ordinary path, `/health/*` included, needs Windows authentication at IIS. Always Running, preload, no idle time-out, load user profile. No proxy or load balancer, so no forwarded-header settings.

**Logging.** Serilog rolling daily file `logs\log-.txt` (`Ecp__Logging__FileName=log-.txt`; the date is inserted, for example `log-20261116.txt`), 31 files kept, console also on. No Application Insights and no external telemetry: this build has no Application Insights sink, and the JBS values set no `ApplicationInsightsConnectionRef`, so readiness R-LOG-02 reports that no optional destination is configured.

**Authentication.** IIS Windows Authentication, invite-only, no Windows-domain restriction in the application (the provider's domain list stays empty), no AD-group onboarding. Break-glass administrators stay configuration-controlled (the existing emergency-access mechanism; nothing new).

**SQL Server model: simplified for JBS (ADR-0044, owner decision 2026-11-18).** SQL Server Row-Level Security, its predicate functions and `security` schema, the `SESSION_CONTEXT` plumbing and the `ecp_runtime` role are retired. Kept: `sp_getapplock` for the audit chain, EF Core's migration lock, the audit append-only trigger, TenantId with composite foreign keys, EF tenant filters, all application-layer organisation, business-unit, site, category, ownership and classification security, and SQL-backed operation leases. Multi-tenant hosting is unsupported for this deployment.

| Account | Database roles (exactly) | Not required |
|---|---|---|
| Migration: the `iis-qa` runner's service account; the pipeline runs `deploy-migrate` (first install only; upgrades follow the runbook, ADR-0045) | `db_ddladmin`, `db_datareader`, `db_datawriter` (the grant the DBA has already made) | db_owner, db_securityadmin, `CREATE ROLE`, `ALTER ANY SECURITY POLICY` |
| Runtime: `AMH\svc_jbsai_qa` | `db_datareader`, `db_datawriter` | db_owner, db_ddladmin, any application role |

```sql
USE [JBSContracts];
ALTER ROLE db_ddladmin   ADD MEMBER [<runner service account>];
ALTER ROLE db_datareader ADD MEMBER [<runner service account>];
ALTER ROLE db_datawriter ADD MEMBER [<runner service account>];
ALTER ROLE db_datareader ADD MEMBER [AMH\svc_jbsai_qa];
ALTER ROLE db_datawriter ADD MEMBER [AMH\svc_jbsai_qa];
```

Proven on SQL Server 2022 and 2025 by `SqlServerSimplifiedModelTests`: a fresh install with exactly the three migration roles (no other permission) succeeds and leaves no security policy, predicate function, `security`-schema object or `ecp_runtime` role; a re-run with nothing pending succeeds; the runtime account with db_datareader + db_datawriter provisions a tenant, adds reference data and creates and reads a contract; it cannot create, alter or drop tables, schemas or indexes, cannot disable, alter or drop the audit trigger, and its audit UPDATE and DELETE are refused (SQL 51000) with the events unchanged. R-DB-02 readiness flags a runtime login that is sysadmin, db_owner or able to change the schema. `SqlPermissionProbeTests` prints the same facts for the DBA in the CI step "SQL Server permission probe". The schemas the migration account creates are owned by it; if that account is ever replaced, move ownership of all application schemas together first.

The earlier probe (CI runs 115 and 116, before ADR-0044) found the extra `CREATE ROLE` + `ALTER ANY SECURITY POLICY` grant necessary; that grant is **no longer needed** and should not be made. A database created before ADR-0044 must have the retired objects removed first: see `docs/UPGRADE_RUNBOOK.md`, "Databases created before ADR-0044". No JBS database exists yet, so JBS starts with the simplified model.

**First-run setup from the pipeline (ADR-0045, recommended).** Set these on the GitHub QA environment: `SETUP_ANSWERS_FILE=docs/jbs/setup-answers.qa.json` and, for each administrator, `APPENV_Ecp__Security__FailsafeAdmins__0__Account` (`AMH\name`) `..._0__DisplayName` and `..._0__Email`. Each deploy then runs `deploy-migrate` before copying files (fresh database: create; current: nothing; an installed database this release would change: the deploy stops, follow `docs/UPGRADE_RUNBOOK.md`) and `setup-apply` after it (the wizard's own stages from the answers file; failsafe administrators invited with the administrator roles). The first run ends with a warning until each administrator has signed in once at `https://jbs-contracts-qa.jbssa.com.au/Account/WindowsSignIn?testProvider=windows` from their own PC; re-run the workflow and setup completes. The runner account needs Modify on `C:\inetpub\JbsContracts\Data`. Production mode needs a working SMTP provider: QA uses `email.jbssa.com.au` port 25 with STARTTLS as an anonymous relay (tested from AUQLDDC01QA2WI, owner-confirmed 2026-11-25), sender `no-reply@jbssa.com.au`, no credentials. No pack signing key is needed: the packs shipped inside the application folder are trusted in Production (ADR-0047). Signing by the build remains optional (ADR-0046: the `PACK_SIGNING_KEY` secret with the `APPENV_Ecp__Packs__TrustedKeys__0__KeyId` / `__PublicKeyPem` variables).

**First-run setup on IIS by hand (fallback).** The setup wizard stays as designed: it answers only on the server's loopback address and needs the one-time token printed on the server by `dotnet Ecp.Host.dll setup-token`; nothing is weakened. On IIS the site binding is host-name specific, so from the server itself the safest way in is a temporary hosts-file entry on that server, `127.0.0.1 jbs-contracts-qa.jbssa.com.au`, then `https://jbs-contracts-qa.jbssa.com.au/Setup` in a browser on the server: the request arrives on loopback, the existing binding and certificate match the host name, and no binding or setting changes. Remove the hosts entry when setup is complete. Not verified on IIS: Windows Authentication for a loopback request to the site's host name normally uses the confirmed SPN (Kerberos); if the server falls back to NTLM, Windows' loopback check can refuse it, and IT would then add the host name to the server's `BackConnectionHostNames` for the duration. Fallback only if neither works: `Ecp__Setup__AllowRemote=true` for the set-up window (the token is still required), removed immediately afterwards. A localhost binding is not required.

**REQUIRED BEFORE QA** (not guessed; see `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`): the site folder and names above; the `IIS_APP_POOL_PASSWORD` secret for `AMH\svc_jbsai_qa` (needed only if the deploy creates the pool; it is set by hand with `gh secret set`); the DBA items (database, version, compatibility level, collation, row-level security and application-lock approval, the database roles above (no `CREATE ROLE` or `ALTER ANY SECURITY POLICY` since ADR-0044), backup and recovery policy, copy-only pre-deploy backup, RPO and RTO); how the first-run setup wizard is reached (it answers only on the server's loopback address, and the site binding is host-name specific).

## Workflows

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci.yml` | Push to any branch, pull requests | Full build, unit, architecture, API, SQL Server integration, Docker image checks, repository and documentation checks, secrets scan, supply-chain checks, IIS publish output check; plus the **Windows** job (`windows-build-and-test`) that builds and runs every non-SQL test on Windows |
| `deploy.yml` | Push to `main` (code changes) or manual | **Waits for CI to be green on the exact commit** (`verify-ci`; it does not repeat CI), then builds and deploys to **QA**, A push deploys QA only. **Production** runs only from a manual run on `main` with `promote-to-production` ticked (owner decision 2026-11-25), builds the **same commit** and deploys it after the Production environment's required reviewer approves. No workflow artifacts are stored |
| `iis-deploy.yml` | Reusable | Runs on the self-hosted runner on the IIS server; checks out the commit, builds it with `.github/actions/build-app` (locked-mode restore, publish, **build manifest**: commit, run, SHA-256 of every file) straight into the deploy package, verifies the manifest, stamps `APPENV_*` **variables** into `web.config`, and runs `scripts/ci/Deploy-IisSite.ps1`, all in one job |

The runners need no .NET SDK installed in advance: the GitHub setup-dotnet action (called by `.github/actions/build-app/action.yml`) installs the version pinned in `global.json` into the runner's tool cache on first use. Tests are not repeated on the web server, because the deploy only starts once `ci.yml` is green on the same commit (that includes the Windows test job).

`scripts/ci/*.ps1` run on Windows PowerShell 5.1: keep them ASCII-only.

**Safety properties of the pipeline** (each is in the files; none has been run on a server):

- Same commit everywhere: QA and Production each build exactly `github.sha` on their own runner, with every package version pinned by the lock files (`--locked-mode`), and no build is kept as a workflow artifact (account artifact storage is shared). `scripts/ci/Test-BuildManifest.ps1` checks the package against its manifest before anything is copied and fails on any missing, changed or extra file (web.config is excluded because it is stamped per environment).
- Fail before touching the server: the deploy step refuses to run without `Ecp__Mode`, `Ecp__DataDirectory` and `Database__ConnectionString`, refuses Demo mode, and refuses a connection string that contains a password.
- No secret is ever written to `web.config`: only `APPENV_` **variables** are stamped, and the pipeline does not read secrets for that purpose. It uses two secrets, both set on the environment by hand: `IIS_APP_POOL_PASSWORD`, only to set the application-pool account, and `PACK_SIGNING_KEY`, only to sign the shipped packs during the build (ADR-0046).
- A deploy that fails while copying restores the snapshot taken just before; if the restore also fails, the site is left **offline**, never half-copied and online.
- The smoke test does not roll back files automatically, because an older application must never run against a database that has already been upgraded. Its message says so.
- The pipeline creates the schema of a **fresh** database (`deploy-migrate`, as the runner account) and stops before copying anything if the release would change an installed Production database; that upgrade remains a deliberate runbook step (`docs/UPGRADE_RUNBOOK.md`). ADR-0045.

## GitHub environments and variables

`scripts/setup/setup-github-environments.sh` creates the QA and Production environments. It has **not been executed**; read its header first. In short: run it with `--dry-run`; it never writes a placeholder; it stops before changing anything if a value is missing; it never overwrites an existing value unless you name it; Production needs an explicit reviewer who is not the person running it, with self-review prevented; each environment accepts deployments only from the branch you name, and that branch must exist; secrets are never read or written (it prints the `gh secret set` commands). `scripts/setup/environments.example.env` lists every value to supply, and `--list` prints the full contract.

Variables the pipeline reads: `IIS_SITE_NAME`, `IIS_APP_POOL`, `IIS_SITE_PATH`, `IIS_BINDING`, `IIS_CERT_THUMBPRINT` (or `IIS_CERT_AUTO` with `IIS_CERT_CA`, `IIS_CERT_TEMPLATE`, `IIS_CERT_DNS_NAMES`), `IIS_APP_POOL_USER`, `SMOKE_TEST_URL`, and the `APPENV_` settings below. Secret: `IIS_APP_POOL_PASSWORD`, only when the pool runs as a domain account whose password the pipeline must set.

Settings written with fixed safe values: `Ecp__Mode=Production`, `Ecp__DemoTenantResolution=false`, `Ecp__DemoSeed=false`, `Database__Provider=SqlServer`, `Database__AutoMigrate=false`, `Ecp__Recovery__Mode=External`. Supplied values: `Ecp__DataDirectory` and `Database__ConnectionString` (built from the server and database names with `Integrated Security=True;Encrypt=True`; `TrustServerCertificate=True` is written only if the DBA says it is needed).

## Application settings that matter on IIS

| Setting | Meaning |
|---|---|
| `Ecp:Mode` | `Production` (the published default). A host with no settings never falls back to Demo. |
| `Ecp:DataDirectory` | Absolute path **outside** the site folder. Documents, key ring (`keyring`) and logs (`logs`) live here. Blank, temporary or inside the application folder is refused (R-ST-01, R-ST-02). |
| `Database:ConnectionString` | Allowed in configuration **only** when it holds no credential (integrated authentication). Anything with a user name or password must be `Database:ConnectionStringRef` (`file:` or `env:`) (R-SEC-02). |
| `Database:AutoMigrate` | Refused in Production (R-MIG-01). |
| `Ecp:DataProtection:KeyDirectory`, `:Protection`, `:CertificateThumbprint` | Where the key ring is and how it is protected (below). |
| `Ecp:Recovery:Mode` | `Application` (default) or `External`. Anything else is refused (R-REC-01). |
| `Ecp:Logging:*` | Log folder, level, retention, console, and the not-yet-verified destinations (below). |
| `Ecp:Http:RequestTimeoutSeconds`, `UploadTimeoutSeconds`, `MigrationTimeoutSeconds` | Request-time policy (below). |

## Windows Authentication (invite-only)

IIS authenticates the person; the application decides whether to let them in. Windows Authentication **establishes identity only**.

- A person signs in only if they were **invited** (the invitation names their `DOMAIN\name`), are **active**, and hold **effective access** (at least one active role). Otherwise they see a plain page that names their Windows account so an administrator can invite them, and the refusal is audited.
- Nothing is created by signing in, and no role, scope, category ownership or classification comes from Windows. Existing role assignments, scopes, ownership and classification rules apply exactly as for any other sign-in method.
- Invitation to first sign-in: the invitation is matched by account name; at the first sign-in it is linked to the account's SID. The email address is never used to link a Windows account.
- Setup: choose "Windows sign-in" on the identity step; the administrator step records the first administrator's Windows account; "Test sign-in" proves it. People & Access shows a Windows account field when the provider is on.
- IIS must have Windows Authentication enabled and Anonymous off. `/health/live` stays anonymous for the warm-up (`scripts/ci/Set-IisAlwaysOn.ps1`). If the web server supplies no identity, the sign-in page says so instead of looping.
- Test and Demo identities keep working outside Production; Production refuses them.
- Not built: Active Directory group-driven onboarding (deferred by the owner). Interactive step-up is not possible with Windows Authentication; re-authentication re-checks the same Windows identity.

## Data protection

The key ring (cookies, antiforgery, setup state) is in `<data root>\keyring`, outside the site, so it survives an application-pool recycle, a redeploy and a restart. Protection: `DpapiMachine` is the default on Windows (works without a loaded profile); `DpapiUser` ties the keys to the pool account and needs its profile loaded (the deploy script sets `loadUserProfile`); `Certificate` uses a certificate from `LocalMachine\My` whose private key the pool account can read and lets keys move between servers; `None` is the default elsewhere (container) and means only file permissions protect the keys. The pool account needs Modify on the data root and nobody else should (that tightening is Infrastructure's decision; the deploy script only grants). Losing the keys signs everyone out and fails forms in progress; no stored data is lost. Readiness `R-SEC-04` reports the folder, the protection and the key count and never prints key material. **Windows DPAPI and the certificate mode are Not Verified** until run on Windows with the real account.

## Upload limits and timeouts

Three limits apply and the smallest wins: IIS request filtering (`web.config`), the application's request-body limit, and the service's own limit. The default for every URL is **10 MiB**; only upload URLs are raised, each to what its purpose needs:

| URL | Limit | Why |
|---|---|---|
| everything else | 10 MiB | forms and API calls |
| `/Contracts/Documents`, `/Contracts/Lifecycle`, `/Variations/Details`, `/Legal/Review` | 101 MiB | 100 MiB document limit plus multipart framing |
| `/Admin/Packs/Upload` | 9 MiB | packs are capped at 8 MiB |
| `/Migration/Profiles` | 6 MiB | mapping CSV files (5 MiB) |
| `/Migration/Batches` | 2 GiB | migration archives; the only URL opened this far |

`tests/Ecp.Api.Tests/IisPackagingTests.cs` keeps `web.config` and the page attributes consistent. ANCM in-process hosting has no request timeout, so the application applies its own policy: 5 minutes by default, 15 for uploads, 60 for migration, each overridable with `Ecp:Http:*TimeoutSeconds`; the handler must honour cancellation.

## Logging

Serilog writes one JSON line per event to a **durable rolling file** in `<data root>\logs` (daily, 31 files kept, 100 MB per file; the two workers of an overlapped recycle may write the same file) and to the console unless `Ecp:Logging:Console` is false. Every line carries application, version, environment, process, thread, the database as its server and database name (never a credential), the correlation id, and for a signed-in request the account's id and provider (not name or email). A redactor removes passwords, tokens, bearer values, JWTs and connection-string credentials from the finished line. Request events are logged without the query string; health probes at a quiet level. Readiness `R-LOG-01` checks the folder is writable and outside the application folder. **Application Insights** (`Ecp:Logging:ApplicationInsightsConnectionRef`) is accepted and validated but this build has no telemetry sink for it: **Not Verified, nothing is sent**. **Fatal-error email** (`Ecp:Logging:FatalEmail:*`) is implemented and unit-tested against a fake sender, rate-limited and redacted, and **Not Verified** against a real mail server. `R-LOG-02` says so.

## Recovery points owned by the DBA

With `Ecp:Recovery:Mode=External` the database administrators and infrastructure team own backup and restore. The application does not take or verify backups and says so (readiness `R-BKP-01`). Before an upgrade the DBA takes a recovery point with their tools and an operator records it:

`dotnet Ecp.Host.dll declare-recovery-point --id "<identifier>" --source "<how it was taken>" --verifier "<who confirmed it restores>" --taken "<UTC time>" --schema-version <version the database holds> --environment <Production> --reason "<why>"`

The record (identifier, source, environment, verifier, time taken, schema version, reason, who declared it, correlation id and a hash of the record) is written once, never edited, and audited. It is always labelled **"Declared by DBA; not verified by this application"**. The upgrade preflight (check UPF-020) reports it as NOT VERIFIED: the operator has to accept it by name, and the acceptance is in the upgrade evidence. Its age, environment and declared schema version are still checked. The application cannot rehearse or prove a restore of a declared point; the DBA's evidence stands for that. To use one of the application's own backups instead, name it in the upgrade and it is verified in the normal way.

## Server checklist

1. Runner: install a self-hosted runner for this repository on each server with the label `iis-qa` or `iis-prod` (the existing runner on the QA server belongs to another repository; use its own folder and service).
2. Service account for the application pool; Kerberos SPN for the site's host name (`setspn`).
3. DNS and a certificate for the site (set `IIS_CERT_THUMBPRINT`).
4. Data root outside the site: Modify for the pool account.
5. Database: created by the DBA; runtime account with db_datareader + db_datawriter only; a separate migration identity with db_ddladmin + db_datareader + db_datawriter (ADR-0044).
6. IIS: Hosting Bundle, Windows Authentication feature, Application Initialization (warm-up). The application pool is set to Always Running with no idle time-out and **load user profile**.
7. First install: set `SETUP_ANSWERS_FILE` and the `FailsafeAdmins` variables and deploy (ADR-0045); each administrator signs in once, then re-run the deploy. The manual route (`migrate`, `setup-token`, `/Setup` from the server) still works.

Deploying to QA proves the hosting; it is not production approval.
