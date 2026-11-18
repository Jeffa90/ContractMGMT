using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// The simplified JBS database model (ADR-0044), asserted on a real SQL Server: a fresh installation needs only db_ddladmin + db_datareader + db_datawriter
/// for the migration account and leaves no retired row-level security object; the runtime account works with db_datareader + db_datawriter only and can
/// neither change the schema nor tamper with the audit trail; a database that still holds the retired objects is cleaned by the forward migration with every
/// row kept. SQL logins stand in for the Windows accounts because CI has no domain; database-role permissions behave the same for Windows logins.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SqlServerSimplifiedModelTests
{
    private static string Db(TestApp a) => new SqlConnectionStringBuilder(a.ConnectionString).InitialCatalog;
    private static string In(string admin, string database) => new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ConnectionString;

    internal static async Task ExecAsync(string connection, string sql)
    {
        await using var c = new SqlConnection(connection); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 120; await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<long> ScalarAsync(string connection, string sql)
    {
        await using var c = new SqlConnection(connection); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Creates a login and database user holding exactly the given database roles and nothing else. Returns "name|password".</summary>
    internal static async Task<string> LoginAsync(string admin, string database, List<string> logins, params string[] roles)
    {
        var name = "ecp_jbs_" + Guid.NewGuid().ToString("N")[..10];
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        await ExecAsync(admin, $"CREATE LOGIN [{name}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        logins.Add(name);
        await ExecAsync(admin, $"USE [{database}]; CREATE USER [{name}] FOR LOGIN [{name}]");
        foreach (var role in roles) await ExecAsync(admin, $"USE [{database}]; ALTER ROLE [{role}] ADD MEMBER [{name}]");
        return $"{name}|{password}";
    }

    internal static string As(string connection, string login) => new SqlConnectionStringBuilder(connection) { UserID = login.Split('|')[0], Password = login.Split('|')[1], IntegratedSecurity = false }.ConnectionString;

    internal static async Task DropLoginsAsync(string admin, List<string> logins)
    {
        SqlConnection.ClearAllPools();
        foreach (var login in logins)
            try { await ExecAsync(admin, $"DROP LOGIN [{login}]"); } catch (SqlException) { /* the test database is dropped with its users; a leftover login is harmless */ }
    }

    /// <summary>Counts of the retired objects: security policies, objects in the security schema, the ecp_runtime role, and any tenant predicate function anywhere.</summary>
    internal static async Task<(long Policies, long SecuritySchemaObjects, long Role, long PredicateFunctions)> RetiredObjectsAsync(string connection)
        => (await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.security_policies"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID(N'security')"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.database_principals WHERE name = N'ecp_runtime'"),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.objects WHERE name IN (N'fn_TenantPredicate', N'fn_TenantRegistryPredicate')"));

    private static async Task<string> MigrateAsAsync(TestApp app, string connection)
    {
        await using var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, connection, app.Clock, Path.Combine(app.DocumentRoot, "jbs-" + Guid.NewGuid().ToString("N")), EcpMode.Test);
        try { await services.GetRequiredService<MigrationRunner>().RunAsync(); return "SUCCEEDED"; }
        catch (Exception ex) { return "REFUSED - " + Describe(ex); }
    }

    private static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is SqlException sql) return $"SQL {sql.Number}: {sql.Message.Split('\n')[0]}";
        return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
    }

    /// <summary>Real work through the application as the given login: provision a tenant, add reference data, create a draft contract, read it back.</summary>
    private static async Task RuntimeWorkAsync(TestApp app, string connection, string label)
    {
        var root = Path.Combine(app.DocumentRoot, "jbs-" + Guid.NewGuid().ToString("N"));
        await using var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, connection, app.Clock, root, EcpMode.Test);
        var attached = TestApp.Attach(services, connection, app.Clock, root);
        var (tenant, org) = await attached.ProvisionAsync("Runtime " + label, "R" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant());
        await using var s = attached.ScopeFor(tenant);
        var (category, supplier) = await TestData.ReferenceDataAsync(s, org);
        var id = await s.Get<Ecp.Contracts.IContractService>().CreateDraftAsync(TestData.Contract(org, category, supplier, "JBS-" + Guid.NewGuid().ToString("N")[..6]));
        Assert.NotNull(await s.Get<Ecp.Contracts.IContractService>().GetAsync(id));
    }

    /// <summary>SQL Server's permission refusals: denied (262, 229, 15247), or the object is invisible or not alterable to the caller (1088, 3701, 15151).</summary>
    private static readonly int[] PermissionRefusals = [229, 262, 1088, 3701, 15151, 15247];

    private static async Task AssertRefusedAsync(string connection, string sql)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, sql));
        Assert.Contains(ex.Number, PermissionRefusals);
    }

    [SqlServerFact]
    public async Task A_fresh_installation_needs_only_db_ddladmin_db_datareader_and_db_datawriter_and_the_runtime_needs_only_db_datareader_and_db_datawriter()
    {
        var admin = SqlServerEnvironment.ConnectionString!;
        var logins = new List<string>();
        await using var target = await TestApp.CreateSqlServerAsync(migrate: false);
        var database = Db(target);
        try
        {
            // 1. Fresh installation with exactly the three standard roles: no CREATE ROLE, no ALTER ANY SECURITY POLICY, no db_owner, no db_securityadmin.
            var migration = await LoginAsync(admin, database, logins, "db_ddladmin", "db_datareader", "db_datawriter");
            var migrationUser = migration.Split('|')[0];
            Assert.Equal(0L, await ScalarAsync(In(admin, database), $"SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'{migrationUser}') AND permission_name <> N'CONNECT'"));
            Assert.Equal("SUCCEEDED", await MigrateAsAsync(target, As(target.ConnectionString, migration)));
            Assert.Equal(1L, await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM audit.__EFMigrationsHistory WHERE MigrationId = N'20261118000000_RemoveRowLevelSecurity'"));

            // 3. The completed database holds no retired object.
            Assert.Equal((0L, 0L, 0L, 0L), await RetiredObjectsAsync(In(admin, database)));

            // A re-run with nothing pending (what a later deploy without migrations does) succeeds with the same three roles.
            Assert.Equal("SUCCEEDED", await MigrateAsAsync(target, As(target.ConnectionString, migration)));

            // 4. Runtime operations with only db_datareader + db_datawriter.
            var runtime = await LoginAsync(admin, database, logins, "db_datareader", "db_datawriter");
            var runtimeConnection = As(target.ConnectionString, runtime);
            await RuntimeWorkAsync(target, runtimeConnection, "rw");
            var privilege = await RuntimePrivilegeProbe.ProbeAsync(runtimeConnection);
            Assert.NotNull(privilege);
            Assert.False(privilege!.IsDbOwner); Assert.False(privilege.IsSysAdmin); Assert.False(privilege.CanChangeSchema);
            Assert.True(privilege.IsDataReader); Assert.True(privilege.IsDataWriter);

            // 5. The runtime cannot change the schema, and nothing changed.
            var columnsBefore = await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM sys.columns");
            await AssertRefusedAsync(runtimeConnection, "CREATE TABLE contracts.Evil (x int)");
            await AssertRefusedAsync(runtimeConnection, "ALTER TABLE contracts.Contract ADD Evil int NULL");
            await AssertRefusedAsync(runtimeConnection, "DROP TABLE contracts.Contract");
            await AssertRefusedAsync(runtimeConnection, "CREATE SCHEMA evil");
            await AssertRefusedAsync(runtimeConnection, "CREATE INDEX IX_Evil ON contracts.Contract (ContractTitle)");
            Assert.Equal(columnsBefore, await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM sys.columns"));
            Assert.Equal(0L, await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM sys.objects WHERE name IN (N'Evil', N'IX_Evil') OR SCHEMA_NAME(schema_id) = N'evil'"));

            // 6. The runtime cannot disable, alter or drop the audit append-only trigger, which stays enabled and unchanged.
            var definition = await ScalarAsync(In(admin, database), "SELECT CHECKSUM(OBJECT_DEFINITION(OBJECT_ID(N'audit.TR_AuditEvent_AppendOnly')))");
            await AssertRefusedAsync(runtimeConnection, "DISABLE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent");
            await AssertRefusedAsync(runtimeConnection, "ALTER TABLE audit.AuditEvent DISABLE TRIGGER ALL");
            await AssertRefusedAsync(runtimeConnection, "DROP TRIGGER audit.TR_AuditEvent_AppendOnly");
            await AssertRefusedAsync(runtimeConnection, "ALTER TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; END");
            Assert.Equal(1L, await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_AuditEvent_AppendOnly' AND is_disabled = 0"));
            Assert.Equal(definition, await ScalarAsync(In(admin, database), "SELECT CHECKSUM(OBJECT_DEFINITION(OBJECT_ID(N'audit.TR_AuditEvent_AppendOnly')))"));

            // 7. Audit UPDATE and DELETE stay refused for the runtime (the trigger refuses them for every login) and the events are unchanged.
            var events = await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM audit.AuditEvent");
            Assert.True(events > 0);
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => ExecAsync(runtimeConnection, "UPDATE audit.AuditEvent SET Action = N'tampered'"))).Number);
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => ExecAsync(runtimeConnection, "DELETE FROM audit.AuditEvent"))).Number);
            Assert.Equal(events, await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM audit.AuditEvent"));
            Assert.Equal(0L, await ScalarAsync(In(admin, database), "SELECT COUNT(*) FROM audit.AuditEvent WHERE Action = N'tampered'"));
        }
        finally { await DropLoginsAsync(admin, logins); }
    }

    [SqlServerFact]
    public async Task A_database_that_still_has_the_retired_row_level_security_is_cleaned_by_the_forward_migration_and_keeps_every_row()
    {
        var admin = SqlServerEnvironment.ConnectionString!;
        var logins = new List<string>();
        await using var app = await TestApp.CreateSqlServerAsync();
        var database = Db(app);
        var adminDb = In(admin, database);
        try
        {
            var (tenant, org) = await app.ProvisionAsync("Legacy", "LEG");
            Guid contractId;
            await using (var s = app.ScopeFor(tenant))
            {
                var (category, supplier) = await TestData.ReferenceDataAsync(s, org);
                contractId = await s.Get<Ecp.Contracts.IContractService>().CreateDraftAsync(TestData.Contract(org, category, supplier, "LEG-000001"));
            }
            var tables = new[] { "contracts.Contract", "contracts.Supplier", "tenancy.Organisation", "tenancy.Tenant", "audit.AuditEvent" };
            var before = new Dictionary<string, long>();
            foreach (var t in tables) before[t] = await ScalarAsync(adminDb, $"SELECT COUNT_BIG(*) FROM {t}");

            // The database as an earlier version left it: the retired objects, a runtime member of the role, and the removal migration not yet applied.
            var legacyRuntime = await LoginAsync(admin, database, logins);
            await LegacyRowLevelSecurity.InstallAsync(adminDb, legacyRuntime.Split('|')[0]);
            await ExecAsync(adminDb, "DELETE FROM audit.__EFMigrationsHistory WHERE MigrationId = N'20261118000000_RemoveRowLevelSecurity'");
            var legacy = await RetiredObjectsAsync(adminDb);
            Assert.True(legacy.Policies > 40); Assert.Equal(1L, legacy.Role); Assert.Equal(2L, legacy.PredicateFunctions);
            // The hazard the removal prevents: this version sets no session context, so the policies hide every row, even from an administrator.
            Assert.Equal(0L, await ScalarAsync(adminDb, "SELECT COUNT_BIG(*) FROM contracts.Contract"));

            // 2. The forward migration removes everything, guarded, and keeps every row.
            await using (var services = TestApp.BuildDetached(DatabaseProvider.SqlServer, app.ConnectionString, app.Clock, Path.Combine(app.DocumentRoot, "legacy"), EcpMode.Test))
                await services.GetRequiredService<MigrationRunner>().RunAsync();
            Assert.Equal((0L, 0L, 0L, 0L), await RetiredObjectsAsync(adminDb));
            Assert.Equal(0L, await ScalarAsync(adminDb, "SELECT COUNT(*) FROM sys.schemas WHERE name = N'security'"));
            Assert.Equal(0L, await ScalarAsync(adminDb, "SELECT COUNT(*) FROM sys.database_permissions p LEFT JOIN sys.database_principals r ON r.principal_id = p.grantee_principal_id WHERE r.principal_id IS NULL"));
            foreach (var t in tables) Assert.Equal(before[t], await ScalarAsync(adminDb, $"SELECT COUNT_BIG(*) FROM {t}"));
            Assert.Equal(1L, await ScalarAsync(adminDb, "SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_AuditEvent_AppendOnly' AND is_disabled = 0"));

            // The DBA script is the same SQL and is safe to run again.
            await ExecAsync(adminDb, RowLevelSecurity.RemovalSql);

            // The application works again with a db_datareader + db_datawriter runtime account, and the earlier contract is visible through the application.
            var runtime = await LoginAsync(admin, database, logins, "db_datareader", "db_datawriter");
            await RuntimeWorkAsync(app, As(app.ConnectionString, runtime), "after");
            await using (var s = app.ScopeFor(tenant))
                Assert.NotNull(await s.Get<Ecp.Contracts.IContractService>().GetAsync(contractId));
        }
        finally { await DropLoginsAsync(admin, logins); }
    }
}

/// <summary>
/// Test-only reconstruction of what versions before ADR-0044 created (the retired RowLevelSecurity helpers): the security schema, the two predicate
/// functions, the ecp_runtime role with schema grants and the audit DENY, and a filter and block policy on every table with a TenantId column (the tenant
/// registry with the installation-scope predicate). Used to prove the removal on a database that has them.
/// </summary>
internal static class LegacyRowLevelSecurity
{
    public static async Task InstallAsync(string adminDatabaseConnection, string runtimeMember)
    {
        string[] setup =
        [
            "IF SCHEMA_ID(N'security') IS NULL EXEC(N'CREATE SCHEMA security');",
            "IF DATABASE_PRINCIPAL_ID(N'ecp_runtime') IS NULL CREATE ROLE [ecp_runtime];",
            "CREATE FUNCTION security.fn_TenantPredicate(@TenantId uniqueidentifier) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS allowed WHERE @TenantId = TRY_CAST(CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(36)) AS uniqueidentifier);",
            "CREATE FUNCTION security.fn_TenantRegistryPredicate(@TenantId uniqueidentifier) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS allowed WHERE @TenantId = TRY_CAST(CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(36)) AS uniqueidentifier) OR TRY_CAST(SESSION_CONTEXT(N'IsInstallationScope') AS bit) = 1;",
            "GRANT SELECT ON OBJECT::security.fn_TenantPredicate TO [ecp_runtime];",
            "GRANT SELECT ON OBJECT::security.fn_TenantRegistryPredicate TO [ecp_runtime];",
            "DENY UPDATE, DELETE ON audit.AuditEvent TO [ecp_runtime];",
            $"ALTER ROLE [ecp_runtime] ADD MEMBER [{runtimeMember}];",
        ];
        foreach (var sql in setup) await SqlServerSimplifiedModelTests.ExecAsync(adminDatabaseConnection, sql);
        var targets = new List<(string Schema, string Table, string Column, bool Registry)>();
        var schemas = new List<string>();
        await using (var c = new SqlConnection(adminDatabaseConnection))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id JOIN sys.columns col ON col.object_id = t.object_id AND col.name = N'TenantId'
                                UNION ALL SELECT N'tenancy', N'Tenant'
                                ORDER BY 1, 2;
                                SELECT DISTINCT s.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name NOT IN (N'dbo', N'security') ORDER BY 1;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var registry = r.GetString(0) == "tenancy" && r.GetString(1) == "Tenant";
                targets.Add((r.GetString(0), r.GetString(1), registry ? "Id" : "TenantId", registry));
            }
            await r.NextResultAsync();
            while (await r.ReadAsync()) schemas.Add(r.GetString(0));
        }
        foreach (var (schema, table, column, registry) in targets)
        {
            var fn = registry ? "security.fn_TenantRegistryPredicate" : "security.fn_TenantPredicate";
            await SqlServerSimplifiedModelTests.ExecAsync(adminDatabaseConnection, $@"CREATE SECURITY POLICY security.[TenantPolicy_{schema}_{table}]
                ADD FILTER PREDICATE {fn}([{column}]) ON [{schema}].[{table}],
                ADD BLOCK PREDICATE {fn}([{column}]) ON [{schema}].[{table}] AFTER INSERT,
                ADD BLOCK PREDICATE {fn}([{column}]) ON [{schema}].[{table}] AFTER UPDATE
                WITH (STATE = ON, SCHEMABINDING = OFF);");
        }
        foreach (var schema in schemas)
            await SqlServerSimplifiedModelTests.ExecAsync(adminDatabaseConnection, $"GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[{schema}] TO [ecp_runtime];");
    }
}
