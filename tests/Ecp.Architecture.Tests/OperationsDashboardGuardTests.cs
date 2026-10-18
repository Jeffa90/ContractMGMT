using System.Text.RegularExpressions;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Architecture.Tests;

/// <summary>
/// OPS-03 rules (docs/OPERATIONS_DASHBOARD.md): the operator pages show what services build and decide nothing themselves; the modules' operational probes can only return counts and times; and the dashboard builder reads
/// no module's tables directly.
/// </summary>
[Trait("Category", "Architecture")]
public class OperationsDashboardGuardTests
{
    private static string Root => RepoRoot.Find();

    [Fact]
    public void Operations_page_models_call_services_only_and_never_a_database_a_connection_or_a_check_directly()
    {
        var forbidden = new Regex(@"DbContext|SqlConnection|DbConnection|IScopedConnection|ReadinessService|IReadinessCheck|IOperationLeases|IOperationHistory|File\.(Read|Write|Open)|Directory\.|Process\.Start|ExecuteSql|FromSql", RegexOptions.NonBacktracking);
        var pages = Directory.EnumerateFiles(Path.Combine(Root, "src", "Ecp.Host", "Pages", "Admin", "Operations"), "*.cshtml.cs").ToList();
        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            var text = File.ReadAllText(page);
            var hit = forbidden.Match(text);
            Assert.False(hit.Success, $"{Path.GetFileName(page)} uses '{hit.Value}' directly. Operational logic belongs in a service, not in a page model.");
        }
    }

    [Fact]
    public void The_operational_fact_type_can_carry_only_a_count_and_a_time_so_a_probe_cannot_return_content()
    {
        var fact = typeof(Ecp.SharedKernel.OperationalFact);
        var types = fact.GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);
        Assert.Equal(["Area", "Combine", "Key", "Utc", "Value"], types.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(typeof(long), types["Value"]);
        Assert.Equal(typeof(DateTime?), types["Utc"]);
        Assert.Equal(typeof(string), types["Area"]); Assert.Equal(typeof(string), types["Key"]);          // fixed words chosen by the module, not data
    }

    [Fact]
    public void Operational_probes_read_counts_and_times_and_never_the_text_columns_that_could_hold_content_or_errors()
    {
        var probes = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*OperationalProbe*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        Assert.True(probes.Count >= 3, "the notifications, data-migration and configuration modules each contribute a probe");
        var textColumns = new Regex(@"\.(LastError|Detail|Subject|Body|Recipient|RecipientUserId|ContentJson|Title|Message|Summary|Name)\b", RegexOptions.NonBacktracking);
        foreach (var file in probes)
        {
            var text = File.ReadAllText(file);
            var hit = textColumns.Match(text);
            Assert.False(hit.Success, $"{Path.GetFileName(file)} reads '{hit.Value}'. An operational probe may count and time records, never read their text.");
        }
    }

    [Fact]
    public void The_dashboard_builder_reads_no_module_table_itself()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Modules", "Ecp.Recovery", "Dashboard", "OperationsDashboardService.cs"));
        Assert.DoesNotMatch(@"DbContext|\.Database\.|SqlConnection|FromSql|ExecuteSql", text);
        var collector = File.ReadAllText(Path.Combine(Root, "src", "Modules", "Ecp.Recovery", "Dashboard", "OperationalFactCollector.cs"));
        Assert.Contains("TenantContext>().Use(tenantId)", collector);       // each tenant is read inside its own scope
        Assert.DoesNotContain("UseInstallationScope", collector);
    }
}
