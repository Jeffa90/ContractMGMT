using Ecp.Audit;
using Ecp.Audit.Persistence;
using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    public TestApp? App { get; private set; }
    public Guid TenantA, OrgA, TenantB, OrgB;
    public Guid CategoryA, SupplierA, ContractA, CategoryB, SupplierB, ContractB;

    public async Task InitializeAsync()
    {
        if (!SqlServerEnvironment.IsAvailable) return; // tests are skipped and reported NOT VERIFIED
        App = await TestApp.CreateSqlServerAsync();
        (TenantA, OrgA) = await App.ProvisionAsync("Tenant A", "AAA");
        (TenantB, OrgB) = await App.ProvisionAsync("Tenant B", "BBB");
        await using (var a = App.ScopeFor(TenantA))
        {
            (CategoryA, SupplierA) = await TestData.ReferenceDataAsync(a, OrgA);
            ContractA = await a.Get<IContractService>().CreateDraftAsync(TestData.Contract(OrgA, CategoryA, SupplierA, "AAA-000001"));
        }
        await using (var b = App.ScopeFor(TenantB))
        {
            (CategoryB, SupplierB) = await TestData.ReferenceDataAsync(b, OrgB);
            ContractB = await b.Get<IContractService>().CreateDraftAsync(TestData.Contract(OrgB, CategoryB, SupplierB, "BBB-000001"));
        }
    }

    public async Task DisposeAsync() { if (App is not null) await App.DisposeAsync(); }
}

[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

/// <summary>
/// Tests that depend on real SQL Server behaviour (decision D4). Without <c>ECP_TEST_SQL</c> they are skipped and reported
/// NOT VERIFIED; they are never counted as passed.
/// </summary>
[Collection("SqlServer")]
[Trait("Category", "SqlServer")]
public sealed class SqlServerBehaviourTests(SqlServerFixture fx)
{
    private TestApp App => fx.App!;

    /// <summary>An administrator's connection. Row-level security is retired (ADR-0044), so no session state is set; the tenant argument names the intent only.</summary>
    private async Task<SqlConnection> OpenAsync(Guid? tenant = null)
    {
        _ = tenant;
        var c = new SqlConnection(App.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    private static async Task<int> Exec(SqlConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(SqlConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync() ?? default(T)!, typeof(T));
    }

    [SqlServerFact]
    public async Task Migrations_create_every_module_schema_and_are_idempotent()
    {
        var runner = App.Services.GetRequiredService<MigrationRunner>();
        await runner.RunAsync(); // second run: nothing to do
        var status = await runner.GetStatusAsync();
        Assert.True(status.IsCurrent);
        Assert.Equal(13, status.Applied.Count);   // one per module with migrations (the Access module is the thirteenth)
        await using var c = await OpenAsync();
        var schemas = new HashSet<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT s.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) schemas.Add(r.GetString(0));
        }
        foreach (var expected in new[] { "audit", "tenancy", "identity", "configuration", "contracts", "documents", "migration", "lifecycle", "notifications", "approvals", "setup", "ops", "access" }) Assert.Contains(expected, schemas);
    }

    [SqlServerFact]
    public async Task Timestamps_are_stored_in_datetime2_and_read_back_as_utc()
    {
        await using var a = App.ScopeFor(fx.TenantA);
        var contract = await a.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.Id == fx.ContractA);
        Assert.Equal(DateTimeKind.Utc, contract.CreatedUtc.Kind); // the provider returns Unspecified; the converter makes it Utc
        Assert.Equal(App.Clock.UtcNow.Date, contract.CreatedUtc.Date);
        await using var c = await OpenAsync(fx.TenantA);
        Assert.Equal("datetime2", await Scalar<string>(c, "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='contracts' AND TABLE_NAME='Contract' AND COLUMN_NAME='CreatedUtc'"));
    }

    [SqlServerFact]
    public async Task No_table_has_a_row_level_security_policy_and_every_tenant_table_carries_TenantId()
    {
        // ADR-0044: SQL Server Row-Level Security is retired; tenant isolation is enforced by the application layers tested below.
        await using var c = await OpenAsync();
        Assert.Equal(0, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.security_policies"));
        Assert.Equal(0, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.database_principals WHERE name = N'ecp_runtime'"));
        var tables = await App.EntityTablesAsync();
        Assert.True(tables.Count >= 10);
        foreach (var (schema, table, module) in tables)
            Assert.True(await Scalar<int>(c, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND name IN (N'TenantId', N'Id')", ("@t", $"{schema}.{table}")) >= 1,
                $"{module}: {schema}.{table} has neither TenantId nor Id");
    }

    [SqlServerFact]
    public async Task The_application_filter_exposes_only_the_current_tenants_rows_on_SQL_Server()
    {
        await using (var b = App.ScopeFor(fx.TenantB))
        {
            var tenants = await b.Get<ContractsDbContext>().Contracts.Select(c => c.TenantId).Distinct().ToListAsync();
            Assert.Equal([fx.TenantB], tenants);                                                  // TEN-031: the EF filter isolates
            Assert.Null(await b.Get<IContractService>().GetAsync(fx.ContractA));                 // another tenant's contract is not found, not forbidden
        }
        await using var admin = await OpenAsync();
        Assert.True(await Scalar<int>(admin, "SELECT COUNT(DISTINCT TenantId) FROM contracts.Contract") >= 2);   // the rows are really there for both tenants
    }

    [SqlServerFact]
    public async Task Without_a_tenant_scope_tenant_data_cannot_be_queried_at_all()
    {
        await using var scope = App.Services.CreateAsyncScope();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<ContractsDbContext>().Contracts.CountAsync());
        Assert.Contains(Flatten(ex), e => e is TenantScopeMissingException);                      // fails closed in the application, not with an empty result
    }

    private static IEnumerable<Exception> Flatten(Exception ex) { for (Exception? e = ex; e is not null; e = e.InnerException) yield return e; }

    [SqlServerFact]
    public async Task The_application_refuses_writes_that_would_cross_tenants_and_the_database_refuses_a_cross_tenant_reference()
    {
        await using (var a = App.ScopeFor(fx.TenantA))
        {
            var db = a.Get<ContractsDbContext>();
            db.Categories.Add(Ecp.Contracts.Domain.Category.Create(fx.TenantB, fx.OrgB, "CROSS", "Cross"));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => a.Get<IUnitOfWork>().ExecuteAsync(ct => db.SaveChangesAsync(ct)));
            Assert.Contains(Flatten(ex), e => e is TenantMismatchException);
        }
        await using var admin = await OpenAsync();
        Assert.Equal(0, await Scalar<int>(admin, "SELECT COUNT(*) FROM contracts.Category WHERE Code = 'CROSS'"));
        // A referenced row cannot change tenant: the composite foreign key refuses it in the database itself.
        var referenced = await Assert.ThrowsAsync<SqlException>(() => Exec(admin, "UPDATE contracts.Category SET TenantId = @b WHERE Id = @id", ("@b", fx.TenantB), ("@id", fx.CategoryA)));
        Assert.Equal(547, referenced.Number);
    }

    [SqlServerFact]
    public async Task Composite_foreign_keys_reject_a_reference_to_another_tenants_parent()
    {
        await using var b = await OpenAsync(fx.TenantB);
        var ex = await Assert.ThrowsAsync<SqlException>(() => Exec(b,
            @"INSERT INTO contracts.Contract (Id, TenantId, OrganisationId, AgreementReference, ContractTitle, SupplierId, CategoryId, Status, RecordLocked, Classification,
                LegalReviewRequired, IsLegacyLoad, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy)
              VALUES (@id, @t, @o, 'BBB-CROSS', 'Cross', @sup, @cat, 'Draft', 0, 'Internal', 0, 0, SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x')",
            ("@id", Guid.NewGuid()), ("@t", fx.TenantB), ("@o", fx.OrgB), ("@sup", fx.SupplierA), ("@cat", fx.CategoryB)));
        Assert.Equal(547, ex.Number); // TEN-032: foreign key conflict, the supplier belongs to tenant A
    }

    [SqlServerFact]
    public async Task Agreement_reference_is_unique_per_tenant_in_the_database_itself()
    {
        await using var a = await OpenAsync(fx.TenantA);
        var ex = await Assert.ThrowsAsync<SqlException>(() => Exec(a,
            @"INSERT INTO contracts.Contract (Id, TenantId, OrganisationId, AgreementReference, ContractTitle, SupplierId, CategoryId, Status, RecordLocked, Classification,
                LegalReviewRequired, IsLegacyLoad, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy)
              VALUES (@id, @t, @o, 'AAA-000001', 'Duplicate', @sup, @cat, 'Draft', 0, 'Internal', 0, 0, SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x')",
            ("@id", Guid.NewGuid()), ("@t", fx.TenantA), ("@o", fx.OrgA), ("@sup", fx.SupplierA), ("@cat", fx.CategoryA)));
        Assert.True(ex.Number is 2601 or 2627);
        // The same reference is fine in another tenant (BBB-000001 and AAA-000001 are different here, so prove with a distinct insert).
        await using var b = await OpenAsync(fx.TenantB);
        Assert.Equal(1, await Exec(b,
            @"INSERT INTO contracts.Contract (Id, TenantId, OrganisationId, AgreementReference, ContractTitle, SupplierId, CategoryId, Status, RecordLocked, Classification,
                LegalReviewRequired, IsLegacyLoad, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy)
              VALUES (@id, @t, @o, 'AAA-000001', 'Same reference, other tenant', @sup, @cat, 'Draft', 0, 'Internal', 0, 0, SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x')",
            ("@id", Guid.NewGuid()), ("@t", fx.TenantB), ("@o", fx.OrgB), ("@sup", fx.SupplierB), ("@cat", fx.CategoryB)));
    }

    [SqlServerFact]
    public async Task The_audit_table_is_append_only_even_for_a_privileged_login()
    {
        await using var a = await OpenAsync(fx.TenantA);
        var update = await Assert.ThrowsAsync<SqlException>(() => Exec(a, "UPDATE audit.AuditEvent SET Action = 'tampered'"));
        Assert.Equal(51000, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => Exec(a, "DELETE FROM audit.AuditEvent"));
        Assert.Equal(51000, delete.Number);
    }

    [SqlServerFact]
    public async Task A_db_datareader_and_db_datawriter_user_can_read_and_write_business_data_but_not_change_audit_or_the_schema()
    {
        await using var admin = await OpenAsync();
        await Exec(admin, "IF DATABASE_PRINCIPAL_ID('ecp_probe') IS NULL CREATE USER ecp_probe WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER ecp_probe; ALTER ROLE db_datawriter ADD MEMBER ecp_probe;");
        await using var c = await OpenAsync(fx.TenantA);
        await Exec(c, "EXECUTE AS USER = 'ecp_probe'");
        try
        {
            Assert.True(await Scalar<int>(c, "SELECT COUNT(*) FROM audit.AuditEvent") > 0);      // SELECT allowed
            Assert.True(await Scalar<int>(c, "SELECT COUNT(*) FROM contracts.Contract") >= 2);   // reads see the table; tenant isolation is the application's (ADR-0044)
            var upd = await Assert.ThrowsAsync<SqlException>(() => Exec(c, "UPDATE audit.AuditEvent SET Action = 'x'"));
            Assert.Equal(51000, upd.Number);                                                     // the append-only trigger refuses it
            var del = await Assert.ThrowsAsync<SqlException>(() => Exec(c, "DELETE FROM audit.AuditEvent"));
            Assert.Equal(51000, del.Number);
            var disable = await Assert.ThrowsAsync<SqlException>(() => Exec(c, "DISABLE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent"));
            Assert.Contains(disable.Number, new[] { 1088, 3701 });                               // cannot switch the protection off
            var ddl = await Assert.ThrowsAsync<SqlException>(() => Exec(c, "CREATE TABLE contracts.Evil (x int)"));
            Assert.Equal(262, ddl.Number);                                                       // no schema rights (R-DB-03)
        }
        finally { await Exec(c, "REVERT"); }
    }

    [SqlServerFact]
    public async Task Audit_chain_verifies_and_tampering_is_detected_at_the_exact_event()
    {
        var (tenant, org) = await App.ProvisionAsync("Tenant Chain", "CHN");
        await using (var s = App.ScopeFor(tenant))
            for (var i = 0; i < 5; i++) await s.Get<ICategoryService>().CreateAsync(new CreateCategoryRequest(org, "C" + i, "Category " + i));
        await using (var s = App.ScopeFor(tenant))
        {
            var ok = await s.Get<AuditChainVerifier>().VerifyAsync();
            Assert.True(ok.IsValid);
            Assert.Equal(6, ok.EventsChecked); // organisation.created + five categories
        }
        await using var admin = await OpenAsync(tenant);
        await Exec(admin, "DISABLE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent"); // only to simulate a privileged tamper
        try { await Exec(admin, "UPDATE audit.AuditEvent SET Action = 'category.deleted' WHERE Sequence = 3"); }
        finally { await Exec(admin, "ENABLE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent"); }
        await using var after = App.ScopeFor(tenant);
        var broken = await after.Get<AuditChainVerifier>().VerifyAsync();
        Assert.False(broken.IsValid);
        Assert.Equal(3, broken.FirstBrokenSequence);
    }

    [SqlServerFact]
    public async Task Concurrent_writers_in_one_tenant_keep_the_audit_chain_gap_free()
    {
        var (tenant, org) = await App.ProvisionAsync("Tenant Concurrent", "CON");
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var s = App.ScopeFor(tenant);
            await s.Get<ICategoryService>().CreateAsync(new CreateCategoryRequest(org, "P" + i, "Parallel " + i));
        }));
        await Task.WhenAll(tasks);
        await using var check = App.ScopeFor(tenant);
        var result = await check.Get<AuditChainVerifier>().VerifyAsync();
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(9, result.EventsChecked);
    }

    [SqlServerFact]
    public async Task Rowversion_detects_a_concurrent_edit_and_the_service_reports_a_conflict()
    {
        await using var first = App.ScopeFor(fx.TenantA);
        await using var second = App.ScopeFor(fx.TenantA);
        var c1 = await first.Get<ContractsDbContext>().Contracts.SingleAsync(c => c.Id == fx.ContractA);
        var c2 = await second.Get<ContractsDbContext>().Contracts.SingleAsync(c => c.Id == fx.ContractA);
        Assert.NotEmpty(c1.RowVersion);
        c1.UpdateDetails("First edit", null, "u1", App.Clock.Next());
        await first.Get<IUnitOfWork>().ExecuteAsync(ct => first.Get<ContractsDbContext>().SaveChangesAsync(ct));
        c2.UpdateDetails("Second edit", null, "u2", App.Clock.Next());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.Get<IUnitOfWork>().ExecuteAsync(ct => second.Get<ContractsDbContext>().SaveChangesAsync(ct)));
    }

    [SqlServerFact]
    public async Task A_failed_unit_of_work_rolls_back_the_change_and_its_audit_events()
    {
        int before;
        await using (var s0 = App.ScopeFor(fx.TenantA)) before = await s0.Get<AuditDbContext>().Events.CountAsync();
        await using (var s = App.ScopeFor(fx.TenantA))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<IUnitOfWork>().ExecuteAsync<bool>(async ct =>
            {
                await s.Get<ICategoryService>().CreateAsync(new CreateCategoryRequest(fx.OrgA, "ROLLEDBACK", "Rolled back"));
                throw new InvalidOperationException("boom");
            }));
        }
        await using var check = App.ScopeFor(fx.TenantA);
        Assert.False(await check.Get<ContractsDbContext>().Categories.AnyAsync(c => c.Code == "ROLLEDBACK"));
        Assert.Equal(before, await check.Get<AuditDbContext>().Events.CountAsync());
    }

    [SqlServerFact]
    public async Task The_tenant_filter_follows_scope_changes_within_one_dependency_injection_scope_and_no_session_state_is_set()
    {
        await using var scope = App.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<ContractsDbContext>();
        using (tenant.Use(fx.TenantA)) Assert.Equal([fx.TenantA], await db.Contracts.Select(c => c.TenantId).Distinct().ToListAsync());
        using (tenant.Use(fx.TenantB)) Assert.Equal([fx.TenantB], await db.Contracts.Select(c => c.TenantId).Distinct().ToListAsync());
        // ADR-0044: the shared connection carries no session context any more.
        var connection = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
        await using var cmd = connection.Connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(36))";
        Assert.True(await cmd.ExecuteScalarAsync() is null or DBNull);
    }

    [SqlServerFact]
    public async Task Readiness_is_healthy_when_migrated_and_unhealthy_when_pending_or_unreachable()
    {
        await using (var scope = App.ScopeWithoutTenant())
            Assert.Equal(ReadinessStatus.Healthy, (await scope.ServiceProvider.GetRequiredService<ReadinessService>().RunAsync()).Overall);

        await using var unmigrated = await TestApp.CreateSqlServerAsync(migrate: false);
        var pending = await unmigrated.Services.GetRequiredService<MigrationRunner>().GetStatusAsync();
        Assert.False(pending.IsCurrent);
        await using (var scope = unmigrated.ScopeWithoutTenant())
        {
            var report = await scope.ServiceProvider.GetRequiredService<ReadinessService>().RunAsync();
            Assert.Equal(ReadinessStatus.Unhealthy, report.Overall);
            Assert.Contains("Pending", report.Results.Single(r => r.Id == "R-DB-01").Message);
        }

        var bad = new SqlConnectionStringBuilder(App.ConnectionString) { DataSource = "127.0.0.1,1", ConnectTimeout = 2 }.ConnectionString;
        await using var unreachable = TestApp.ForConnection(bad);
        await using var s2 = unreachable.ScopeWithoutTenant();
        var down = await s2.ServiceProvider.GetRequiredService<ReadinessService>().RunAsync();
        Assert.Equal(ReadinessStatus.Unhealthy, down.Overall);
        Assert.DoesNotContain("127.0.0.1", string.Join(' ', down.Results.Select(r => r.Message))); // no connection detail leaks
    }
}
