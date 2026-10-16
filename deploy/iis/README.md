# Windows Server / IIS deployment

**Status: Scaffolded / Not Verified.** The build workspace is Linux; nothing here has been executed on Windows Server or IIS. Treat the steps as a checklist to be proven in a Windows test environment (task T1.12 verification).

1. Install the .NET 10 Hosting Bundle and enable the IIS role with the ASP.NET Core Module V2.
2. `dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release -o publish`; copy to the site folder.
3. Create an application pool: *No Managed Code*, a dedicated low-privilege identity (a group-managed service account is preferred). Grant it modify rights only on the data directory and read rights on the secret file.
4. Store the SQL connection string in a file outside the web root (`D:\EcpSecrets\sql-connection.txt`, NTFS permissions limited to the pool identity). `web.config` carries only the *reference* (`file:`), never the value (SEC-030).
5. Create the database login/role for runtime (least privilege; see `docs/ENVIRONMENT.md` and the migration role notes), then run the migration deliberately: `dotnet Ecp.Host.dll migrate` under a separate migration identity. Production never auto-migrates.
6. Bind HTTPS with a certificate from your store; redirect HTTP; HSTS is applied by the application in Production.
7. Verify `/health/live`, `/health/startup`, `/health/ready` (detail is hidden from anonymous callers in Production).

The application refuses to start in Production with SQLite, inline connection strings, demo identity or demo seed (ProductionGuard R-* checks).
