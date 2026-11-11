# GitHub Actions deployment to IIS (JBS)

Status: **Configured, not yet run.** IIS hosting itself is still *Scaffolded / Not Verified*
(`deploy/iis/README.md`, gap plan P0-5); the first QA deploy is that verification run.
Docker and Azure models are unchanged (`deploy/docker`, `deploy/azure`).

## Workflows

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci.yml` (existing) | Push to any branch | Full build, unit, SQL Server integration, Docker image checks |
| `deploy.yml` | Push to `main` (code changes) or manual | Build `src/Ecp.Host` once on Windows, run `Ecp.UnitTests`, deploy to **QA**, then the same artifact to **Production** after approval |
| `dotnet-spa-build.yml` | Reusable | Locked-mode restore, test, publish; uploads the `app` artifact |
| `iis-deploy.yml` | Reusable | Runs on the self-hosted runner on the IIS server; `scripts/ci/Deploy-IisSite.ps1` creates/updates the site and stamps `APPENV_*` settings into `web.config` |

The deploy workflows and `scripts/ci` are reused from the JBS reference pipeline (`jbs-ai-initiatives`).
`scripts/ci/*.ps1` run on Windows PowerShell 5.1 - keep them ASCII-only.

**Database migrations (planned, not built yet):** the pipeline will run the app's upgrade commands
before swapping files (`docs/JBS_IIS_DEPLOYMENT_REVIEW.md`, section 3). They run **as the app pool
account**, started with its password (`IIS_APP_POOL_PASSWORD`), so Integrated Security connects as that
account rather than as the runner's account. Until then, run `dotnet Ecp.Host.dll migrate` by hand as the
app pool account.

## Environments

Run once, as a repo admin: `PROD_REVIEWER=<github-user> bash scripts/setup/setup-github-environments.sh`

| Setting | QA | Production |
|---|---|---|
| App server | `AUQLDDC01QA2WI` | `AUQLDDC01APP3WI` |
| Database server | `AUQLDDC01QA2XI` | `AUQLDDC01DBP3WI` |
| `IIS_SITE_NAME` / `IIS_APP_POOL` | `ContractPlatform` | `ContractPlatform` |
| `IIS_SITE_PATH` | `C:\inetpub\ContractPlatform` | `C:\inetpub\ContractPlatform` |
| `IIS_BINDING` | `https:*:443:contracts-qa.jbssa.com.au` | `https:*:443:contracts.jbssa.com.au` |
| `IIS_APP_POOL_USER` | `AMH\svc_jbsai_qa` (shared with JBS AI; also runs migrations) | UPDATE ME |
| `IIS_CERT_THUMBPRINT` | UPDATE ME | UPDATE ME |
| `SMOKE_TEST_URL` | the /health/live address of the site | the /health/live address of the site |
| Data root | `C:\EcpData\ContractPlatform` | `D:\EcpData\ContractPlatform` |

App settings are `APPENV_*` **variables** mirroring `deploy/iis/web.config` (`Ecp__Mode=Production`,
demo off, SQL Server, `Database__AutoMigrate=false`). The database connection uses Windows
authentication (Integrated Security), so it holds no password. It is the variable
`APPENV_ECP_SQL_CONNECTION`, and the app reads it through `Database__ConnectionStringRef=env:ECP_SQL_CONNECTION`
(the Production guard rejects a connection string written directly into settings).

Secrets: `IIS_APP_POOL_PASSWORD` per environment. The pipeline also uses it to run database migrations as
the app pool account (see "Database migrations" below).

## Server checklist (QA server is already prepared for IIS + Hosting Bundle)

1. **Second runner.** The existing runner on `AUQLDDC01QA2WI` belongs to the JBS AI Initiatives repo.
   Install another instance in its own folder (e.g. `C:\actions-runner-contracts`), registered to this
   repo with label `iis-qa`, as a local admin. Restart its service after group changes.
2. **Service account** `AMH\svc_jbsai_qa` (already used by JBS AI Initiatives, so already in
   `AMH\Allow Log On As A Batch Job`).
3. **DNS + certificate** for `contracts-qa.jbssa.com.au` from IT; set `IIS_CERT_THUMBPRINT`.
   `setspn -S HTTP/contracts-qa.jbssa.com.au AMH\svc_jbsai_qa` (Kerberos for Windows Authentication).
4. **Folders:** `<data root>\Data` (modify for the pool account).
5. **Database:** DBA creates `ContractPlatform` on `AUQLDDC01QA2XI` and gives `AMH\svc_jbsai_qa` the rights
   to run and migrate it (`db_owner`, or `db_ddladmin` + `db_datareader` + `db_datawriter` + rights to create
   row-level security policies). The first deploy creates the schema; then run `setup-token` and the
   `/Setup` wizard from the server (`deploy/iis/README.md`).
6. **App pool:** Always Running with preload and no idle time-out so background jobs keep running (P0-3).

## Sign-in

Same as JBS AI Initiatives: the deploy scripts turn IIS Windows Authentication on and Anonymous off;
only `/health/live` (the warm-up path) is anonymous. The app does not yet accept the IIS Windows identity
(see `docs/JBS_IIS_DEPLOYMENT_REVIEW.md`, P0-1).

## Known blockers from the gap plan

- P0-1 the app does not yet accept the IIS Windows identity, so users cannot sign in yet.
- P0-2 request limits in the published `web.config`, and P0-4 durable logging, are not done.
Deploying to QA is useful to prove hosting (P0-5); it is not production approval.
