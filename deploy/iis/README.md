# Windows Server / IIS deployment

**Status: Implemented as files, CI green on Linux, Windows and SQL Server (commit e0d2ac7), Not Verified on IIS.** The build workspace is Linux. The package, its `web.config`, the sign-in flow and the pipeline have been built and tested without IIS; the first QA run on a real server is the verification. The full description is `docs/DEPLOYMENT.md`; status and open questions are in `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.

1. Install the .NET 10 Hosting Bundle and the IIS role with the ASP.NET Core Module V2 and Windows Authentication.
2. The package comes from `dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release`. The project's `src/Ecp.Host/web.config` is published with it (in-process hosting, request limits per upload URL, no environment variables and no secrets). `tools/check-iis-publish.py <folder>` inspects the published files.
3. The site folder is replaced on every deploy. **Documents, the data-protection key ring and logs live in the data root outside the site** (`Ecp__DataDirectory`); the application refuses to start in Production if it is blank, temporary, or inside the application folder.
4. Application pool: *No Managed Code*, a low-privilege account, **load user profile on** (the deploy script sets it). Grant the account Modify on the data root only.
5. SQL Server: the connection string may hold no credential when it uses Windows integrated authentication (`Integrated Security=True`), so it can be ordinary configuration. A string with a user name or password must be a secret reference (`file:` or `env:`). The runtime account should only hold the `ecp_runtime` role; schema changes use a separate migration identity. Production never migrates automatically.
6. Run the migration deliberately: `dotnet Ecp.Host.dll migrate` under the migration identity (a first install only; later releases use the upgrade commands in `docs/UPGRADE_RUNBOOK.md`). The migration identity needs db_ddladmin + db_datareader + db_datawriter + `CREATE ROLE` + `ALTER ANY SECURITY POLICY` (proven minimal on SQL Server 2022 and 2025; the two permissions may be revoked after the migration). Then add the runtime account to `ecp_runtime`.
7. First-run setup: on the server run `dotnet Ecp.Host.dll setup-token` and open `http://localhost/Setup` from the server. Choose "Windows sign-in" for the identity provider and give the first administrator's `DOMAIN\name`. Use "Test sign-in" to prove it; the person must be invited, active and hold a role, so the setup wizard gives the administrator the administrator role.
8. Verify `/health/live`, `/health/startup` and `/health/ready` (detail is hidden from anonymous callers in Production). `R-DB-02`, `R-SEC-04`, `R-LOG-01` and `R-BKP-01` say how the database sign-in, key ring, log and recovery are set up.
9. Recovery is owned by the database administrators when `Ecp:Recovery:Mode` is `External`: they take and verify the recovery point; the platform records it (`declare-recovery-point`) and says "Declared by DBA; not verified by this application".

The application refuses to start in Production with SQLite, inline connection strings that hold credentials, demonstration identity or seed data, automatic migration, or storage inside the application folder (ProductionGuard).

## JBS QA layout (CONFIRMED by IT 2026-11-17)

```
C:\inetpub\JbsContracts\
  app\                 site files, replaced on every deploy (site and pool JbsContracts; pool reads it)
  logs\                log-YYYYMMDD.txt, 31 kept (pool modifies)
  Data\                Ecp__DataDirectory (pool modifies)
    Documents\         contract documents and staged migration files
    Keys\              data-protection key ring (pool and Administrators only)
    SolutionPacks\     installed solution packs
    Uploads\           created by IT; not used by this application
```

Values: `docs/jbs/jbs-github-environments.env`. Status and the items still required before QA: `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.
