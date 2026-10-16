using System.Reflection;
using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.DataMigration;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using Xunit;

namespace Ecp.Architecture.Tests;

internal static class Modules
{
    public static readonly (string Name, Assembly Assembly, int Tier)[] All =
    [
        ("Ecp.SharedKernel", typeof(Ids).Assembly, 0),
        ("Ecp.Persistence", typeof(TenantDbContext).Assembly, 0),
        ("Ecp.Audit", typeof(AuditModule).Assembly, 1),
        ("Ecp.Tenancy", typeof(TenancyModule).Assembly, 2),
        ("Ecp.Identity", typeof(IdentityModule).Assembly, 3),
        ("Ecp.Configuration", typeof(ConfigurationModule).Assembly, 5),
        ("Ecp.Contracts", typeof(ContractsModule).Assembly, 6),
        ("Ecp.Documents", typeof(DocumentsModule).Assembly, 6),
        ("Ecp.DataMigration", typeof(MigrationModule).Assembly, 7),
        ("Ecp.Operations", typeof(OperationsModule).Assembly, 8),
        ("Ecp.Setup", typeof(SetupModule).Assembly, 9),
        ("Ecp.Host", typeof(Program).Assembly, 10),
    ];

    /// <summary>The only same-tier edges permitted (docs/ARCHITECTURE.md section 4).</summary>
    public static readonly HashSet<(string From, string To)> SameTierEdges =
    [
        ("Ecp.Persistence", "Ecp.SharedKernel"),
        ("Ecp.Contracts", "Ecp.Documents"),
    ];

    public static readonly string[] ModuleNames = ["Audit", "Tenancy", "Identity", "Configuration", "Contracts", "Documents", "DataMigration", "Operations", "Setup"];
}

[Trait("Category", "Architecture")]
public class ModuleDependencyTests
{
    [Fact] // ARC-001
    public void Modules_depend_only_on_lower_tiers_or_the_permitted_same_tier_edges()
    {
        var tiers = Modules.All.ToDictionary(m => m.Name, m => m.Tier);
        foreach (var (name, assembly, tier) in Modules.All)
        {
            foreach (var reference in assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Ecp.", StringComparison.Ordinal)))
            {
                Assert.True(tiers.ContainsKey(reference), $"{name} references unknown project {reference}");
                var refTier = tiers[reference];
                var ok = refTier < tier || (refTier == tier && Modules.SameTierEdges.Contains((name, reference)));
                Assert.True(ok, $"{name} (tier {tier}) must not depend on {reference} (tier {refTier})");
            }
        }
    }

    [Fact] // ARC-002
    public void No_module_reaches_into_another_modules_persistence_namespace()
    {
        foreach (var (name, assembly, _) in Modules.All)
        {
            var foreign = Modules.ModuleNames.Where(m => $"Ecp.{m}" != name).Select(m => $"Ecp.{m}.Persistence").ToArray();
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(foreign).GetResult();
            Assert.True(result.IsSuccessful, $"{name} depends on another module's Persistence namespace: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact] // ARC-009 (purity)
    public void Domain_namespaces_have_no_infrastructure_or_web_dependencies()
    {
        foreach (var module in Modules.ModuleNames)
        {
            var assembly = Modules.All.Single(m => m.Name == $"Ecp.{module}").Assembly;
            var result = Types.InAssembly(assembly).That().ResideInNamespace($"Ecp.{module}.Domain")
                .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Ecp.Persistence", $"Ecp.{module}.Persistence", "System.Data", "Microsoft.Data")
                .GetResult();
            Assert.True(result.IsSuccessful, $"{module} domain depends on infrastructure: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact] // ARC-009 (encapsulation)
    public void Tenant_owned_entities_have_no_public_setters()
    {
        var entities = Modules.All.SelectMany(m => m.Assembly.GetTypes()).Where(t => t.IsClass && typeof(ITenantOwned).IsAssignableFrom(t)).ToList();
        Assert.True(entities.Count >= 9);
        foreach (var type in entities)
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic, $"{type.Name}.{p.Name} has a public setter");
    }
}

[Trait("Category", "Architecture")]
public class TenantIsolationModelTests
{
    [Fact] // ARC-003 (model part; the database part is verified against SQL Server)
    public async Task Every_mapped_entity_carries_the_tenant_marker_and_a_query_filter()
    {
        await using var app = await TestApp.CreateSqliteAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var count = 0;
        foreach (var module in app.Services.GetServices<ModuleDescriptor>())
        {
            var ctx = (DbContext)scope.ServiceProvider.GetRequiredService(module.DbContextType);
            foreach (var entity in ctx.Model.GetEntityTypes())
            {
                count++;
                Assert.True(entity.GetDeclaredQueryFilters().Any(), $"{module.Name}.{entity.ClrType.Name} has no query filter");
                if (entity.FindProperty("TenantId") is not null)
                    Assert.True(typeof(ITenantOwned).IsAssignableFrom(entity.ClrType), $"{entity.ClrType.Name} has TenantId but is not ITenantOwned");
            }
        }
        Assert.True(count >= 10);
    }

    [Fact]
    public async Task Every_tenant_owned_entity_has_a_composite_alternate_key_or_is_not_a_foreign_key_target()
    {
        // Composite foreign keys must always include TenantId so a child can never reference another tenant's parent.
        await using var app = await TestApp.CreateSqliteAsync();
        await using var scope = app.Services.CreateAsyncScope();
        foreach (var module in app.Services.GetServices<ModuleDescriptor>())
        {
            var ctx = (DbContext)scope.ServiceProvider.GetRequiredService(module.DbContextType);
            foreach (var fk in ctx.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
                Assert.Contains(fk.Properties, p => p.Name == "TenantId");
        }
    }
}

[Trait("Category", "Architecture")]
public class SourceRuleTests
{
    private static IEnumerable<(string Path, string Text)> Sources()
    {
        var src = Path.Combine(RepoRoot.Find(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var normalised = file.Replace('\\', '/');
            if (normalised.Contains("/obj/") || normalised.Contains("/bin/") || normalised.Contains("/Persistence/Migrations/")) continue;
            yield return (normalised, File.ReadAllText(file));
        }
    }

    private static void AssertNone(Regex pattern, string rule, Func<string, bool>? allow = null)
    {
        var hits = Sources().Where(s => !(allow?.Invoke(s.Path) ?? false) && pattern.IsMatch(s.Text)).Select(s => s.Path).ToList();
        Assert.True(hits.Count == 0, $"{rule}: {string.Join(", ", hits)}");
    }

    [Fact] // ARC-005
    public void Query_filters_are_never_ignored()
        => AssertNone(new Regex(@"IgnoreQueryFilters\s*\("), "IgnoreQueryFilters is banned in application code");

    [Fact] // ARC-005
    public void Raw_sql_appears_only_in_the_reviewed_allowlist()
        => AssertNone(new Regex(@"\b(FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|FromSql|ExecuteSql|ExecuteSqlAsync|ExecuteSqlInterpolated\w*|FromSqlInterpolated)\s*\("),
            "Raw SQL outside the allowlist", p => p.EndsWith("/Modules/Ecp.Audit/AuditApi.cs", StringComparison.Ordinal) || p.EndsWith("/Modules/Ecp.Contracts/ReferenceAllocator.cs", StringComparison.Ordinal));

    [Fact] // ARC-006
    public void No_cache_is_used_without_the_tenant_keyed_helper()
        => AssertNone(new Regex(@"\b(IMemoryCache|MemoryCache|IDistributedCache|OutputCache)\b"), "Caches must go through a tenant-keyed helper (none exists yet)");

    [Fact] // ARC-006
    public void Tenant_is_never_stashed_in_ambient_or_static_state()
        => AssertNone(new Regex(@"HttpContext\.Items|\[ThreadStatic\]|AsyncLocal<"), "Ambient tenant state is banned");

    [Fact] // ARC-007
    public void Code_execution_and_scripting_apis_are_banned()
        => AssertNone(new Regex(@"Process\.Start|Assembly\.Load|CSharpScript|Microsoft\.CodeAnalysis|\bJint\b|Expression\.Lambda\s*<|\.Compile\s*\(\s*\)|Activator\.CreateInstance\s*\(\s*Type\.GetType"),
            "Arbitrary code execution is banned", p => p.EndsWith("/Modules/Ecp.Contracts/ContractAccessFilter.cs", StringComparison.Ordinal));

    [Fact] // ARC-010
    public void No_business_pack_names_or_workflow_state_literals_in_compiled_code()
    {
        AssertNone(new Regex(@"\b(JBS|JBSAUS|Medius|Qlik|DocuSign)\b"), "Business or vendor names belong in packs, samples and adapters, not core code");
        AssertNone(new Regex("\"(Draft|AwaitingLegal|InLegalReview|LegalReworkRequired|LegalCleared|InitialCreation|OutForSignature|AwaitingApproval|Effective|VariationInProgress|Ended|DisposedArchived)\""),
            "Workflow state codes come from workflow definitions, not code");
    }

    [Fact]
    public void Secrets_are_not_hard_coded_in_source()
        => AssertNone(new Regex(@"(?i)(password|pwd|secret|apikey|token)\s*=\s*[""'][^""']{4,}[""']"), "Possible hard-coded secret");
}
