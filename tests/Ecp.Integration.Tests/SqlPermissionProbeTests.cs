using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static Ecp.Integration.Tests.SqlServerSimplifiedModelTests;

namespace Ecp.Integration.Tests;

/// <summary>
/// Evidence for the database administrator (CI prints it in the step "SQL Server permission probe"). It runs the simplified JBS model (ADR-0044) against the
/// real schema and prints what each account can and cannot do, with the server's own message: the migration account with db_ddladmin + db_datareader +
/// db_datawriter, and the runtime account with db_datareader + db_datawriter. The assertions that hold the design in place are in
/// <see cref="SqlServerSimplifiedModelTests"/>; this report shows the same facts in the words a DBA reads.
/// Uses SQL logins only because the CI server has no Windows domain; database-role permissions behave the same for Windows logins.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SqlPermissionProbeTests(ITestOutputHelper output)
{
    private static string Shorten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is SqlException sql) return $"SQL {sql.Number}: {sql.Message.Split('\n')[0]}";
        return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
    }

    private static async Task<string> TryAsync(Func<Task> action)
    {
        try { await action(); return "SUCCEEDED"; }
        catch (Exception ex) { return "REFUSED - " + Shorten(ex); }
    }

    private static async Task<string> MigrateAsAsync(TestApp app, string connection)
    {
        await using var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, connection, app.Clock, Path.Combine(app.DocumentRoot, "probe-" + Guid.NewGuid().ToString("N")), EcpMode.Test);
        return await TryAsync(() => services.GetRequiredService<MigrationRunner>().RunAsync());
    }

    private static async Task<string> RunAsAsync(TestApp app, string connection)
    {
        var root = Path.Combine(app.DocumentRoot, "probe-" + Guid.NewGuid().ToString("N"));
        await using var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, connection, app.Clock, root, EcpMode.Test);
        var result = await TryAsync(async () =>
        {
            var attached = TestApp.Attach(services, connection, app.Clock, root);
            var (tenant, org) = await attached.ProvisionAsync("Probe", "P" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant());
            await using var s = attached.ScopeFor(tenant);
            var (category, supplier) = await TestData.ReferenceDataAsync(s, org);
            await s.Get<Ecp.Contracts.IContractService>().CreateDraftAsync(TestData.Contract(org, category, supplier, "PRB-" + Guid.NewGuid().ToString("N")[..6]));
        });
        var privilege = await RuntimePrivilegeProbe.ProbeAsync(connection);
        return result + $" (provision a tenant, add reference data, create a draft contract; db_owner: {privilege?.IsDbOwner}, can change schema: {privilege?.CanChangeSchema})";
    }

    [SqlServerFact]
    public async Task Report_what_the_simplified_database_roles_can_and_cannot_do()
    {
        var admin = SqlServerEnvironment.ConnectionString!;
        var logins = new List<string>();
        var report = new List<string> { "== SQL Server permission probe, simplified JBS model ADR-0044 (synthetic database; SQL logins standing in for the Windows accounts) ==" };
        await using var target = await TestApp.CreateSqlServerAsync(migrate: false);
        var database = new SqlConnectionStringBuilder(target.ConnectionString).InitialCatalog;
        try
        {
            var migration = await LoginAsync(admin, database, logins, "db_ddladmin", "db_datareader", "db_datawriter");
            report.Add("Migration account, db_ddladmin + db_datareader + db_datawriter, first install (migrate): " + await MigrateAsAsync(target, As(target.ConnectionString, migration)));
            var retired = await RetiredObjectsAsync(new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ConnectionString);
            report.Add($"Retired objects after the install: security policies {retired.Policies}, security-schema objects {retired.SecuritySchemaObjects}, ecp_runtime role {retired.Role}, predicate functions {retired.PredicateFunctions}");
            report.Add("Migration account, the same roles, re-run with nothing pending: " + await MigrateAsAsync(target, As(target.ConnectionString, migration)));

            var runtime = As(target.ConnectionString, await LoginAsync(admin, database, logins, "db_datareader", "db_datawriter"));
            report.Add("Runtime account, db_datareader + db_datawriter: " + await RunAsAsync(target, runtime));
            report.Add("Runtime account, CREATE TABLE: " + await TryAsync(() => ExecAsync(runtime, "CREATE TABLE contracts.ProbeEvil (x int)")));
            report.Add("Runtime account, DISABLE TRIGGER on the audit table: " + await TryAsync(() => ExecAsync(runtime, "DISABLE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent")));
            report.Add("Runtime account, UPDATE audit.AuditEvent: " + await TryAsync(() => ExecAsync(runtime, "UPDATE audit.AuditEvent SET Action = N'tampered'")));
            report.Add("Runtime account, DELETE FROM audit.AuditEvent: " + await TryAsync(() => ExecAsync(runtime, "DELETE FROM audit.AuditEvent")));
        }
        finally
        {
            foreach (var line in report) { output.WriteLine(line); Console.WriteLine(line); }
            await DropLoginsAsync(admin, logins);
        }
        Assert.Equal(9, report.Count);   // every case ran and reported; the design itself is asserted by SqlServerSimplifiedModelTests
    }
}
