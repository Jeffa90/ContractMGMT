using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecp.Integration.Tests;

/// <summary>
/// Evidence for the database administrator, not a pass/fail gate on their decision. It tries the database roles the deployment owner proposed
/// (migration account: db_ddladmin + db_datareader + db_datawriter; runtime account: db_datareader + db_datawriter) against the real schema and
/// prints what works and what is refused, with the server's own message. CI prints the report in the step "SQL Server permission probe".
/// Uses SQL logins only because the CI server has no Windows domain; database-role permissions behave the same for Windows logins.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SqlPermissionProbeTests(ITestOutputHelper output)
{
    private static async Task ExecAsync(string connection, string sql)
    {
        await using var c = new SqlConnection(connection); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync();
    }

    private static string Shorten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is SqlException sql) return $"SQL {sql.Number}: {sql.Message.Split('\n')[0]}";
        return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
    }

    private async Task<string> LoginAsync(string admin, string database, List<string> logins, params string[] roles)
    {
        var name = "ecp_probe_" + Guid.NewGuid().ToString("N")[..10];
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        await ExecAsync(admin, $"CREATE LOGIN [{name}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        logins.Add(name);
        await ExecAsync(admin, $"USE [{database}]; CREATE USER [{name}] FOR LOGIN [{name}]");
        foreach (var role in roles) await ExecAsync(admin, $"USE [{database}]; ALTER ROLE [{role}] ADD MEMBER [{name}]");
        return $"{name}|{password}";
    }

    private static string As(string connection, string login) => new SqlConnectionStringBuilder(connection) { UserID = login.Split('|')[0], Password = login.Split('|')[1], IntegratedSecurity = false }.ConnectionString;

    private async Task<string> MigrateAsAsync(TestApp app, string connection)
    {
        await using var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, connection, app.Clock, Path.Combine(app.DocumentRoot, "probe-" + Guid.NewGuid().ToString("N")), EcpMode.Test);
        try { await services.GetRequiredService<MigrationRunner>().RunAsync(); return "SUCCEEDED"; }
        catch (Exception ex) { return "REFUSED - " + Shorten(ex); }
    }

    private async Task<string> RunAsAsync(TestApp app, string connection, string label)
    {
        var root = Path.Combine(app.DocumentRoot, "probe-" + Guid.NewGuid().ToString("N"));
        await using var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, connection, app.Clock, root, EcpMode.Test);
        try
        {
            var attached = TestApp.Attach(services, connection, app.Clock, root);
            var (tenant, org) = await attached.ProvisionAsync("Probe " + label, "P" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant());
            await using (var s = attached.ScopeFor(tenant))
            {
                var (category, supplier) = await TestData.ReferenceDataAsync(s, org);
                await s.Get<Ecp.Contracts.IContractService>().CreateDraftAsync(TestData.Contract(org, category, supplier, "PRB-" + Guid.NewGuid().ToString("N")[..6]));
            }
            var privilege = await RuntimePrivilegeProbe.ProbeAsync(connection);
            return $"SUCCEEDED (provision a tenant, add reference data, create a draft contract; runtime role member: {privilege?.IsRuntimeRoleMember}, db_owner: {privilege?.IsDbOwner})";
        }
        catch (Exception ex) { return "REFUSED - " + Shorten(ex); }
    }

    [SqlServerFact]
    public async Task Report_what_the_proposed_database_roles_can_and_cannot_do()
    {
        var admin = SqlServerEnvironment.ConnectionString!;
        var logins = new List<string>();
        var report = new List<string> { "== SQL Server permission probe (synthetic database; SQL logins standing in for the Windows accounts) ==" };
        await using var empty = await TestApp.CreateSqlServerAsync(migrate: false);
        await using var empty2 = await TestApp.CreateSqlServerAsync(migrate: false);
        await using var migrated = await TestApp.CreateSqlServerAsync();
        try
        {
            string Db(TestApp a) => new SqlConnectionStringBuilder(a.ConnectionString).InitialCatalog;
            var migration = await LoginAsync(admin, Db(empty), logins, "db_ddladmin", "db_datareader", "db_datawriter");
            report.Add("Migration account, db_ddladmin + db_datareader + db_datawriter, first install (migrate): " + await MigrateAsAsync(empty, As(empty.ConnectionString, migration)));
            var migrationPlus = await LoginAsync(admin, Db(empty2), logins, "db_ddladmin", "db_datareader", "db_datawriter", "db_securityadmin");
            report.Add("Migration account, the same + db_securityadmin, first install (migrate): " + await MigrateAsAsync(empty2, As(empty2.ConnectionString, migrationPlus)));

            var runtime = await LoginAsync(admin, Db(migrated), logins, "db_datareader", "db_datawriter");
            report.Add("Runtime account, db_datareader + db_datawriter: " + await RunAsAsync(migrated, As(migrated.ConnectionString, runtime), "rw"));
            var runtimeRole = await LoginAsync(admin, Db(migrated), logins, RowLevelSecurity.RuntimeRole);
            report.Add($"Runtime account, {RowLevelSecurity.RuntimeRole} only: " + await RunAsAsync(migrated, As(migrated.ConnectionString, runtimeRole), "role"));
        }
        finally
        {
            foreach (var line in report) { output.WriteLine(line); Console.WriteLine(line); }
            SqlConnection.ClearAllPools();
            foreach (var login in logins)
                try { await ExecAsync(admin, $"DROP LOGIN [{login}]"); } catch (SqlException) { /* the test database is dropped with its users; a leftover login is harmless and named for the run */ }
        }
        Assert.Equal(5, report.Count);   // every case ran and reported; the outcomes are evidence for the DBA, not assertions about their policy
    }
}
