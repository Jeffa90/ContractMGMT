# Deployment to IIS with GitHub Actions (JBS reference)

Status: **Implemented as files; Not Verified on IIS and not yet run.** The first QA deployment is the verification run. Docker and Azure models are unchanged (`deploy/docker`, `deploy/azure`). Nothing here is production approval. Open questions are in `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.

## Workflows

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci.yml` | Push to any branch, pull requests | Full build, unit, architecture, API, SQL Server integration, Docker image checks, repository and documentation checks, secrets scan, supply-chain checks, IIS publish output check; plus the **Windows** job (`windows-build-and-test`) that builds and runs every non-SQL test on Windows |
| `deploy.yml` | Push to `main` (code changes) or manual | **Waits for CI to be green on the exact commit** (`verify-ci`; it does not repeat CI), builds once on Windows, deploys to **QA**, then the **same artifact** to **Production** after the Production environment's required reviewer approves. Production is deployed only from `main`; a manual run must ask for it (`promote-to-production`) |
| `dotnet-spa-build.yml` | Reusable | Locked-mode restore, unit tests, publish, and a **build manifest** (commit, run, SHA-256 of every file) |
| `iis-deploy.yml` | Reusable | Runs on the self-hosted runner on the IIS server; verifies the manifest, stamps `APPENV_*` **variables** into `web.config`, and runs `scripts/ci/Deploy-IisSite.ps1` |

`scripts/ci/*.ps1` run on Windows PowerShell 5.1: keep them ASCII-only.

**Safety properties of the pipeline** (each is in the files; none has been run on a server):

- One immutable build: the package QA receives is the package Production receives; `scripts/ci/Test-BuildManifest.ps1` fails on any missing, changed or extra file (web.config is excluded because it is stamped per environment).
- Fail before touching the server: the deploy step refuses to run without `Ecp__Mode`, `Ecp__DataDirectory` and `Database__ConnectionString`, refuses Demo mode, and refuses a connection string that contains a password.
- No secret is ever written to `web.config`: only `APPENV_` **variables** are stamped, and the pipeline does not read secrets for that purpose. The only secret it uses is `IIS_APP_POOL_PASSWORD` (set on the environment by hand), and only to set the application-pool account.
- A deploy that fails while copying restores the snapshot taken just before; if the restore also fails, the site is left **offline**, never half-copied and online.
- The smoke test does not roll back files automatically, because an older application must never run against a database that has already been upgraded. Its message says so.
- Database migrations are **not** run by the pipeline. They are a deliberate step (`dotnet Ecp.Host.dll migrate` for a first install, the upgrade commands of `docs/UPGRADE_RUNBOOK.md` afterwards) under a migration identity.

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
5. Database: created by the DBA; runtime account in the `ecp_runtime` role; a migration identity with rights to change the schema.
6. IIS: Hosting Bundle, Windows Authentication feature, Application Initialization (warm-up). The application pool is set to Always Running with no idle time-out and **load user profile**.
7. First install: `dotnet Ecp.Host.dll migrate` as the migration identity, then `setup-token` and the `/Setup` wizard from the server (`deploy/iis/README.md`).

Deploying to QA proves the hosting; it is not production approval.
