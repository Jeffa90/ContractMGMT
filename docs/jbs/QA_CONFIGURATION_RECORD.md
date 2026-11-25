# JBS QA: the configuration that works (record as at 2026-11-25)

The QA site `https://jbs-contracts-qa.jbssa.com.au` deployed from `main`, setup completed ("Setup is complete.", Deploy run 13, commit 46c5e1f) and the owner signed in. This file records **everything** that was set up by hand or learned the hard way, so it is never worked out again, whether for this app, Production or a rebuilt app on the same server. No secret values are recorded here.

## 1. Server and IIS (QA web server AUQLDDC01QA2WI)

| Item | Value | Who set it |
|---|---|---|
| IIS site / app pool | `JbsContracts` / `JbsContracts` (No Managed Code, integrated pipeline, AlwaysRunning, no idle time-out, load user profile on) | Pool identity set by IT (Clayton); the rest re-applied by every deploy |
| App pool identity | `AMH\svc_jbsai_qa` | IT, in IIS Manager (so no `IIS_APP_POOL_PASSWORD` secret is needed) |
| Folders | app `C:\inetpub\JbsContracts\app`; data `C:\inetpub\JbsContracts\Data` (documents, `Keys`, `SolutionPacks`, setup state); logs `C:\inetpub\JbsContracts\logs` (daily `log-YYYYMMDD.txt`) | IT created; deploy grants the pool Modify and resets the `Keys` files' permissions every run |
| Binding / certificate | `https:*:443:jbs-contracts-qa.jbssa.com.au`, thumbprint `409A784432142E96CD8D5FDCBEEC7AC0870411A1` | IT issued the certificate; deploy binds it |
| Authentication | Windows Authentication on, Anonymous off; Anonymous only on `/warmup` (Application Initialization) | Deploy |
| Prerequisites | .NET 10 Hosting Bundle (AspNetCoreModuleV2), IIS Windows Authentication feature | IT |
| GitHub runner | Self-hosted, label `iis-qa`, runs as `AMH\svc_jbsai_qa` (the same account as the pool in QA), work folder `C:\deploy\JBSContracts`, local administrator for IIS changes | IT |

## 2. Database (SQL Server AUQLDDC01QA2Xi, SQL Server 17.0.4065.4)

- Database `JBSContracts`, created by the DBA; Windows integrated authentication; connection string `Server=AUQLDDC01QA2Xi;Database=JBSContracts;Integrated Security=True;Encrypt=True;TrustServerCertificate=True` (no password, ever).
- Roles: `AMH\svc_jbsai_qa` has `db_ddladmin`, `db_datareader`, `db_datawriter` (it is both the deploy and the runtime account in QA). No db_owner, no `CREATE ROLE`, no security policies (Row-Level Security removed, ADR-0044).
- The deploy creates the tables itself on an empty database (`deploy-migrate`) and stops, without touching the site, if a release would change an existing database (follow `docs/UPGRADE_RUNBOOK.md`).
- Outstanding: `TrustServerCertificate=True` stays until IT issues a trusted certificate for the SQL Server.

## 3. Email

Anonymous relay `email.jbssa.com.au`, port 25, STARTTLS, sender `no-reply@jbssa.com.au`, no username or password (port 25 offers no login). Tested end to end: the setup test message arrived (ADR-0048).

## 4. GitHub (repository Jeffa90/ContractMGMT, environment `QA`)

Deployment branch: `main` only. Production runs only from a manual run of the Deploy workflow on `main` with **promote-to-production** ticked, and then waits for the Production reviewer; a push never starts it.

**Variables on the QA environment** (values as set; names are stored uppercase by GitHub, which is fine):

| Variable | Value |
|---|---|
| `IIS_SITE_NAME` / `IIS_APP_POOL` | `JbsContracts` |
| `IIS_SITE_PATH` | `C:\inetpub\JbsContracts\app` |
| `IIS_BINDING` | `https:*:443:jbs-contracts-qa.jbssa.com.au` |
| `IIS_CERT_THUMBPRINT` | `409A784432142E96CD8D5FDCBEEC7AC0870411A1` |
| `IIS_APP_POOL_USER` | `AMH\svc_jbsai_qa` |
| `SMOKE_TEST_URL` | `https://jbs-contracts-qa.jbssa.com.au/health/live` |
| `SETUP_ANSWERS_FILE` | `docs/jbs/setup-answers.qa.json` |
| `APPENV_Ecp__Mode` | `Production` |
| `APPENV_Ecp__DataDirectory` | `C:\inetpub\JbsContracts\Data` |
| `APPENV_Ecp__DataProtection__KeyDirectory` | `C:\inetpub\JbsContracts\Data\Keys` |
| `APPENV_Ecp__Packs__StorageDirectory` | `C:\inetpub\JbsContracts\Data\SolutionPacks` |
| `APPENV_Ecp__Logging__Directory` | `C:\inetpub\JbsContracts\logs` |
| `APPENV_Ecp__Logging__FileName` / `__RetainedFiles` | `log-.txt` / `31` |
| `APPENV_Database__Provider` | `SqlServer` |
| `APPENV_Database__ConnectionString` | as in section 2 |
| `APPENV_Database__AutoMigrate` | `false` |
| `APPENV_Ecp__DemoSeed` / `APPENV_Ecp__DemoTenantResolution` | `false` / `false` |
| `APPENV_Ecp__Recovery__Mode` | `External` |
| `APPENV_Ecp__Email__AllowAnonymousRelay` | `true` (ADR-0048) |
| `APPENV_Documents__Scanner__Provider` | `external` (JBS scans before upload, ADR-0049) |
| `APPENV_Ecp__Security__FailsafeAdmins__0__Account` / `__DisplayName` / `__Email` | the owner's `AMH\` logon, name and work email (add `__1__`, `__2__` for more) |

**Secrets:** none needed. (`IIS_APP_POOL_PASSWORD` only if a deploy must create the pool itself; `PACK_SIGNING_KEY` no longer needed, ADR-0047.)

**Setup answers** (`docs/jbs/setup-answers.qa.json`): tenant and organisation "JBS Australia", en-AU, Brisbane time zone, AUD; pack `jbs-procurement`; Windows sign-in; SMTP as section 3; readiness acknowledgements for R-ADM-02, R-NOT-02 and R-OPS-01, each at most **300 characters** (longer is cut).

## 5. First install, in order (what finally worked)

1. IT: server prerequisites, folders, certificate, runner as the service account, app pool identity set in IIS Manager; DBA: empty database and the three roles.
2. Set the GitHub variables above.
3. Push to `main` (or run Deploy by hand). The deploy creates the tables, copies the site and runs setup.
4. Each failsafe administrator opens `https://jbs-contracts-qa.jbssa.com.au/Account/WindowsSignIn?testProvider=windows` once from their own PC.
5. Run the deploy again; read any readiness warnings in the log, write a reason for each (owner's call), add them to the answers file, deploy again until "Setup is complete."

## 6. Problems hit and their fixes (don't repeat them)

| Symptom | Cause | Fix now in place |
|---|---|---|
| Deploy failed: missing settings | Required GitHub variables not set | The deploy stops before touching the server and names the missing variable |
| A half-made app pool running as the wrong account | The pool was created before the password check | Identity checked before anything in IIS changes; IT set the identity by hand |
| `/Setup` "Not found" from a PC | The wizard answers only on the server itself, by design | Setup runs from the pipeline instead (`setup-apply`) |
| Site 500, "failed to load coreclr" | The pool couldn't read a data-protection key file in `Data\Keys` | Every deploy resets the key files to inherit the folder's permissions |
| Nobody could see why the site crashed | The 500 had no detail in the pipeline log | A failed smoke test prints the ASP.NET Core Module and .NET event log entries and the app log's error lines |
| Healthy deploy marked failed | Setup waiting on a person returned exit 3, which ended the step as a failure | Exit 3 is a warning |
| Production stage started after every QA deploy | `deploy-prod` ran on push | Production only from a manual run with promote-to-production |
| Email refused in Production mode | Production required an SMTP login | Host setting `Ecp:Email:AllowAnonymousRelay` (ADR-0048) |
| Setup blocked by the malware check | No scanner adapter exists | JBS scans before upload; host setting `Documents:Scanner:Provider = external` (ADR-0049) |
| Pack signing key requested | Production required signed packs | Shipped packs trusted (ADR-0047) |
| Database role and security-policy grants | SQL Row-Level Security | Removed (ADR-0044) |
| A docs-only push didn't deploy | `deploy.yml` ignores `docs/**` and `*.md` | Start the Deploy workflow by hand |
| "Access denied" with no reason | Refusal reasons were dropped | The page shows the reason and the log records it against the reference |
| CI repository check failures | Backticked slash paths in docs are treated as file paths; new files must be listed in `PACKAGE_MANIFEST.md`; `scripts/ci/*.ps1` must be ASCII | Follow those rules |

## 7. Still open

- Trusted certificate for the SQL Server (then remove `TrustServerCertificate=True`).
- Production: server, site, certificate, database, a runner account separate from the app pool account (owner decision 2026-11-25), reviewer.
