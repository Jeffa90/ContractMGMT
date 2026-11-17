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

    private async Task<string> LoginWithGrantsAsync(string admin, string database, List<string> logins, string[] roles, string[] grants)
    {
        var login = await LoginAsync(admin, database, logins, roles);
        foreach (var grant in grants) await ExecAsync(admin, $"USE [{database}]; GRANT {grant} TO [{login.Split('|')[0]}]");
        return login;
    }

    /// <summary>Who owns what the migration created: an owner other than dbo keeps CONTROL over those objects even after its role memberships are removed.</summary>
    private static async Task<string> OwnershipAsync(string admin, string database)
    {
        await using var c = new SqlConnection(new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ConnectionString); await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT 'schemas owned by ' + USER_NAME(s.principal_id) + ': ' + CAST(COUNT(*) AS varchar(10)) FROM sys.schemas s
                            WHERE s.schema_id BETWEEN 5 AND 16383 GROUP BY s.principal_id
                            UNION ALL SELECT 'role ecp_runtime owned by ' + USER_NAME(owning_principal_id) FROM sys.database_principals WHERE name = 'ecp_runtime'
                            UNION ALL SELECT 'security policies: ' + CAST(COUNT(*) AS varchar(10)) FROM sys.security_policies";
        var parts = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) parts.Add(r.GetString(0));
        return string.Join("; ", parts);
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

            // Candidate narrower sets: db_ddladmin + db_datareader + db_datawriter plus explicit database permissions. Each runs the whole first install on its own empty database,
            // then a separate runtime login (member of ecp_runtime only) does real work against the result, and the report says who owns the created objects.
            var candidates = new (string Label, string[] Roles, string[] Grants)[]
            {
                ("+ CREATE ROLE + ALTER ANY SECURITY POLICY", ["db_ddladmin", "db_datareader", "db_datawriter"], ["CREATE ROLE", "ALTER ANY SECURITY POLICY"]),
                ("+ CREATE ROLE + ALTER ANY ROLE + ALTER ANY SECURITY POLICY", ["db_ddladmin", "db_datareader", "db_datawriter"], ["CREATE ROLE", "ALTER ANY ROLE", "ALTER ANY SECURITY POLICY"]),
                ("+ db_securityadmin + ALTER ANY SECURITY POLICY", ["db_ddladmin", "db_datareader", "db_datawriter", "db_securityadmin"], ["ALTER ANY SECURITY POLICY"]),
                ("db_owner", ["db_owner"], []),
            };
            foreach (var (label, roles, grants) in candidates)
            {
                await using var target = await TestApp.CreateSqlServerAsync(migrate: false);
                var login = await LoginWithGrantsAsync(admin, Db(target), logins, roles, grants);
                var result = await MigrateAsAsync(target, As(target.ConnectionString, login));
                var line = $"Migration candidate {(label.StartsWith('+') ? "db_ddladmin + db_datareader + db_datawriter " : "")}{label}: {result}";
                if (result == "SUCCEEDED")
                {
                    line += " | ownership: " + await OwnershipAsync(admin, Db(target));
                    var runtimeAfter = await LoginAsync(admin, Db(target), logins, RowLevelSecurity.RuntimeRole);
                    line += " | runtime (ecp_runtime) afterwards: " + await RunAsAsync(target, As(target.ConnectionString, runtimeAfter), "after");
                    if (label == "db_owner")
                    {
                        // Temporary db_owner: remove the membership after the migration and prove the application still works and the database is unchanged by the removal.
                        await ExecAsync(admin, $"USE [{Db(target)}]; ALTER ROLE [db_owner] DROP MEMBER [{login.Split('|')[0]}]");
                        var runtimeLater = await LoginAsync(admin, Db(target), logins, RowLevelSecurity.RuntimeRole);
                        line += " | db_owner removed afterwards; runtime then: " + await RunAsAsync(target, As(target.ConnectionString, runtimeLater), "later");
                    }
                }
                report.Add(line);
            }

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
        Assert.Equal(9, report.Count);   // every case ran and reported; the outcomes are evidence for the DBA, not assertions about their policy
    }
}
