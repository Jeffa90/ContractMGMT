using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Portability slice S3 (ADR-0027): the migration that gives the signature boundary a provider-neutral vocabulary must keep every existing row and value, rename in place, leave the security policy in force
/// under the new name, and be safe to run twice. This needs a real SQL Server (migrations are applied there; the lightweight provider builds its schema from the model), so without one it is reported NOT VERIFIED.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SignatureVocabularyMigrationTests
{
    private const string Previous = "20261016074947_AddApprovalPlans";

    private static async Task<SqlConnection> OpenAsync(TestApp app, Guid tenant)
    {
        var c = new SqlConnection(app.ConnectionString);
        await c.OpenAsync();
        await Exec(c, "EXEC sp_set_session_context N'TenantId', @t; EXEC sp_set_session_context N'IsInstallationScope', 0;", ("@t", tenant));
        return c;
    }

    private static async Task<int> Exec(SqlConnection c, string sql, params (string, object?)[] ps)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> Scalar<T>(SqlConnection c, string sql, params (string, object?)[] ps)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static async Task MigrateAsync(TestApp app, string? lifecycleTarget)
    {
        await using var scope = app.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        foreach (var m in app.Services.GetServices<ModuleDescriptor>().OrderBy(m => m.Tier).ThenBy(m => m.Name))
        {
            var ctx = (DbContext)scope.ServiceProvider.GetRequiredService(m.DbContextType);
            if (m.Name == "Lifecycle" && lifecycleTarget is not null) await ctx.GetService<IMigrator>().MigrateAsync(lifecycleTarget);
            else await ctx.Database.MigrateAsync();
        }
    }

    [SqlServerFact]
    public async Task The_rename_keeps_every_existing_row_and_value_and_the_security_policy()
    {
        await using var app = await TestApp.CreateSqlServerAsync(migrate: false);
        await MigrateAsync(app, Previous);                                                       // every module as it was, the lifecycle schema before this migration

        var tenant = Guid.NewGuid(); var id = Guid.NewGuid(); var org = Guid.NewGuid(); var subject = Guid.NewGuid(); var contract = Guid.NewGuid(); var document = Guid.NewGuid();
        var started = new DateTime(2026, 10, 1, 2, 3, 4, DateTimeKind.Utc); var executed = new DateTime(2026, 10, 2, 5, 6, 7, DateTimeKind.Utc);
        await using (var c = await OpenAsync(app, tenant))
        {
            Assert.Equal(1, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.tables WHERE name = N'ExecutionRecord' AND schema_id = SCHEMA_ID(N'lifecycle')"));
            await Exec(c, @"INSERT INTO lifecycle.ExecutionRecord (Id, TenantId, OrganisationId, SubjectKind, SubjectId, ContractId, Provider, EnvelopeId, Status, IsCurrent, ExecutedDocumentId, ExecutedUtc, StartedBy, StartedUtc, StatusUpdatedUtc, Detail)
                VALUES (@id, @t, @o, N'Contract', @s, @c, N'mock', N'MOCK-ABC123', N'Completed', 1, @d, @e, N'owner@example.test', @st, @e, N'Completed by the mock signature service')",
                ("@id", id), ("@t", tenant), ("@o", org), ("@s", subject), ("@c", contract), ("@d", document), ("@e", executed), ("@st", started));
        }

        await MigrateAsync(app, null);                                                           // up to date: this migration runs

        await using (var c = await OpenAsync(app, tenant))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ProviderRequestId, Status, Provider, ExecutedDocumentId, CompletedUtc, RequestedBy, RequestedUtc, Detail, ProviderStatus, FailureCode, IdempotencyKey, LastProviderEventUtc FROM lifecycle.SignatureRequest WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("MOCK-ABC123", r.GetString(0));                                         // the value moved with its column
            Assert.Equal("Completed", r.GetString(1));
            Assert.Equal("mock", r.GetString(2));
            Assert.Equal(document, r.GetGuid(3));
            Assert.Equal(executed, r.GetDateTime(4));
            Assert.Equal("owner@example.test", r.GetString(5));
            Assert.Equal(started, r.GetDateTime(6));
            Assert.Equal("Completed by the mock signature service", r.GetString(7));
            Assert.True(r.IsDBNull(8) && r.IsDBNull(9) && r.IsDBNull(10) && r.IsDBNull(11));     // new columns start empty; nothing is invented
            Assert.False(await r.ReadAsync());
        }

        await using (var c = await OpenAsync(app, tenant))
        {
            Assert.Equal(0, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.tables WHERE name = N'ExecutionRecord' AND schema_id = SCHEMA_ID(N'lifecycle')"));
            Assert.Equal(0, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'lifecycle.SignatureRequest') AND name = N'EnvelopeId'"));
            Assert.Equal(1, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.security_policies WHERE name = N'TenantPolicy_lifecycle_SignatureRequest'"));
            Assert.Equal(1, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.security_policies WHERE name = N'TenantPolicy_lifecycle_SignatureProviderEvent'"));
            Assert.Equal(0, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.security_policies WHERE name = N'TenantPolicy_lifecycle_ExecutionRecord'"));
            Assert.Equal(1, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'PK_SignatureRequest' AND object_id = OBJECT_ID(N'lifecycle.SignatureRequest')"));
            Assert.Equal(1, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_SignatureRequest_TenantId_Provider_ProviderRequestId' AND has_filter = 1"));
            Assert.Equal(0, await Scalar<int>(c, "SELECT COUNT(*) FROM sys.indexes WHERE name LIKE N'%ExecutionRecord%' OR name LIKE N'%Envelope%'"));
        }

        // The tenant boundary still holds under the new name: another tenant sees nothing.
        await using (var other = await OpenAsync(app, Guid.NewGuid()))
            Assert.Equal(0, await Scalar<int>(other, "SELECT COUNT(*) FROM lifecycle.SignatureRequest"));

        await MigrateAsync(app, null);                                                           // running again changes nothing
        await using (var c = await OpenAsync(app, tenant))
            Assert.Equal(1, await Scalar<int>(c, "SELECT COUNT(*) FROM lifecycle.SignatureRequest WHERE ProviderRequestId = N'MOCK-ABC123'"));
    }
}
