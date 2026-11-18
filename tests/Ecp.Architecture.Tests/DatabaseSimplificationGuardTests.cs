using System.Text.RegularExpressions;
using Ecp.Persistence;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Architecture.Tests;

/// <summary>
/// ADR-0044 guards. SQL Server Row-Level Security and the ecp_runtime role are retired: nothing in compiled code may create them again or depend on session
/// context, the DBA removal script must stay the same SQL as the forward migration, and the controls that were deliberately kept (the audit-chain
/// application lock and the audit append-only trigger) must stay.
/// </summary>
public sealed class DatabaseSimplificationGuardTests
{
    private static string Root => RepoRoot.Find();

    private static IEnumerable<(string Path, string Text)> CompiledSources()
        => Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(Root, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static string Normalise(string sql) => string.Join("\n", sql.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public void The_DBA_removal_script_is_the_same_SQL_as_the_forward_migration()
    {
        var script = File.ReadAllText(Path.Combine(Root, "deploy", "sql", "remove-retired-row-level-security.sql"));
        Assert.Equal(Normalise(RowLevelSecurity.RemovalSql), Normalise(script));
        Assert.All(script, ch => Assert.True(ch < 128, "the script must be ASCII"));
    }

    [Fact]
    public void Compiled_code_sets_no_session_context_and_creates_no_security_policy_or_application_role()
    {
        var banned = new Regex(@"sp_set_session_context|SESSION_CONTEXT\s*\(|CREATE\s+SECURITY\s+POLICY|CREATE\s+ROLE|GRANT\s+[^;""]*\bTO\s+\[?ecp_runtime|DENY\s+[^;""]*\bTO\s+\[?ecp_runtime", RegexOptions.IgnoreCase);
        var offenders = CompiledSources().Where(f => banned.IsMatch(f.Text)).Select(f => f.Path).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_retired_helpers_emit_nothing()
    {
        var builder = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.CreateInfrastructure();
        builder.AddTenantPolicy("contracts", "Contract");
        builder.AddTenantPolicy("tenancy", "Tenant", "Id", registry: true);
        builder.GrantRuntimeAccess("contracts");
        builder.DropTenantPolicy("contracts", "Contract");
        builder.DropInfrastructure();
        Assert.Empty(builder.Operations);
    }

    [Fact]
    public void The_kept_controls_are_still_there_the_audit_chain_lock_and_the_append_only_trigger()
    {
        var audit = File.ReadAllText(Path.Combine(Root, "src", "Modules", "Ecp.Audit", "AuditApi.cs"));
        Assert.Contains("sp_getapplock", audit);
        Assert.Contains("@LockOwner = N'Transaction'", audit);
        var initial = File.ReadAllText(Path.Combine(Root, "src", "Modules", "Ecp.Audit", "Persistence", "Migrations", "20261016000541_Initial.cs"));
        Assert.Contains("CREATE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent INSTEAD OF UPDATE, DELETE", initial);
    }
}
