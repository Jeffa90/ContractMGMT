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
        ("Ecp.Formats", typeof(Ecp.Formats.CsvReader).Assembly, 0),
        ("Ecp.Packs", typeof(Ecp.Packs.PackReader).Assembly, 0),
        ("Ecp.Audit", typeof(AuditModule).Assembly, 1),
        ("Ecp.Tenancy", typeof(TenancyModule).Assembly, 2),
        ("Ecp.Identity", typeof(IdentityModule).Assembly, 3),
        ("Ecp.Configuration", typeof(ConfigurationModule).Assembly, 5),
        ("Ecp.Metadata", typeof(Ecp.Metadata.MetadataModule).Assembly, 5),
        ("Ecp.Contracts", typeof(ContractsModule).Assembly, 6),
        ("Ecp.Documents", typeof(DocumentsModule).Assembly, 6),
        ("Ecp.Approvals", typeof(Ecp.Approvals.ApprovalsModule).Assembly, 6),
        ("Ecp.DataMigration", typeof(MigrationModule).Assembly, 7),
        ("Ecp.Lifecycle", typeof(Ecp.Lifecycle.LifecycleModule).Assembly, 7),
        ("Ecp.Operations", typeof(OperationsModule).Assembly, 8),
        ("Ecp.Access", typeof(Ecp.Access.AccessModule).Assembly, 8),
        ("Ecp.Notifications", typeof(Ecp.Notifications.NotificationsModule).Assembly, 9),
        ("Ecp.Setup", typeof(SetupModule).Assembly, 9),
        ("Ecp.Recovery", typeof(Ecp.Recovery.RecoveryModule).Assembly, 9),
        ("Ecp.Host", typeof(Program).Assembly, 10),
    ];

    /// <summary>The only same-tier edges permitted (docs/ARCHITECTURE.md section 4).</summary>
    public static readonly HashSet<(string From, string To)> SameTierEdges =
    [
        ("Ecp.Persistence", "Ecp.SharedKernel"),
        ("Ecp.Contracts", "Ecp.Documents"),
        ("Ecp.Metadata", "Ecp.Configuration"),
    ];

    public static readonly string[] ModuleNames = ["Audit", "Tenancy", "Identity", "Configuration", "Metadata", "Contracts", "Documents", "Approvals", "DataMigration", "Lifecycle", "Notifications", "Operations", "Access", "Setup", "Recovery"];
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
        // Any spelling, and inside identifiers too: the pack identifier is lower case ("jbs-procurement") and a class named after a vendor would sit inside a longer word, and the original rule (case-sensitive, whole words only)
        // could see neither (portability audit P-01, P-09). Stage A of the guard; Razor, project and JSON
        // files and the role and function names follow with the slices that remove them (docs/PORTABILITY_REMEDIATION_PLAN.md, R4).
        AssertNone(new Regex("(JBS|Medius|Qlik|DocuSign)", RegexOptions.IgnoreCase), "Business or vendor names belong in packs, samples and adapters, not core code");
        AssertNone(new Regex("\"(Draft|AwaitingLegal|InLegalReview|LegalReworkRequired|LegalCleared|InitialCreation|OutForSignature|AwaitingApproval|Effective|VariationInProgress|Ended|DisposedArchived)\""),
            "Workflow state codes come from workflow definitions, not code");
    }

    [Theory] // the guard must see the spellings that slipped past it before PER-002/S1
    [InlineData("DefaultPackId = \"jbs-procurement\"")]
    [InlineData("var x = \"JBSAUS-000001\";")]
    [InlineData("// Medius import")]
    [InlineData("new QlikSettings()")]
    [InlineData("docusign.Send()")]
    [InlineData("class DocuSignClient")]
    [InlineData("MediusImporter.Run()")]
    public void The_business_name_guard_recognises_every_spelling(string line)
        => Assert.Matches(new Regex("(JBS|Medius|Qlik|DocuSign)", RegexOptions.IgnoreCase), line);

    private static readonly Regex SignatureVendorWords = new(@"envelope|signing[ _-]?ceremony|recipient[ _-]?tabs?|signature[ _-]?tabs?|docusign", RegexOptions.IgnoreCase);

    [Fact] // S3 / R6 (ADR-0027): the signature boundary is provider-neutral
    public void The_signature_boundary_uses_no_signature_vendor_vocabulary_in_any_source_file()
    {
        // C#, Razor, project, JSON and resource files under src/. The one allowed hit is the read-only compatibility name that lets a register-authorisation snapshot frozen before the rename still be read.
        var src = Path.Combine(RepoRoot.Find(), "src");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var f = file.Replace('\\', '/');
            if (f.Contains("/obj/") || f.Contains("/bin/") || f.Contains("/Persistence/Migrations/") || f.EndsWith("packages.lock.json", StringComparison.Ordinal)) continue;
            if (Path.GetExtension(f) is not (".cs" or ".cshtml" or ".csproj" or ".json" or ".resx" or ".css" or ".js")) continue;
            if (f.EndsWith("/Modules/Ecp.Lifecycle/RegisterAuthorisation.cs", StringComparison.Ordinal)) continue;     // the compatibility name, checked below
            if (SignatureVendorWords.IsMatch(File.ReadAllText(file))) hits.Add(f);
        }
        Assert.True(hits.Count == 0, "Signature-vendor vocabulary belongs in a provider adapter, not the platform: " + string.Join(", ", hits));

        var compat = File.ReadAllLines(Path.Combine(src, "Modules", "Ecp.Lifecycle", "RegisterAuthorisation.cs")).Where(l => SignatureVendorWords.IsMatch(l)).ToList();
        Assert.Single(compat);
        Assert.Contains("JsonPropertyName(\"envelopeId\")", compat[0]);
    }

    [Theory]
    [InlineData("string EnvelopeId")]
    [InlineData("CreateEnvelopeAsync(")]
    [InlineData("<dt>Envelope</dt>")]
    [InlineData("recipient tabs")]
    [InlineData("RecipientTabs")]
    [InlineData("the signing ceremony")]
    [InlineData("SignatureTabs")]
    [InlineData("DocuSign Connect")]
    public void The_signature_vendor_guard_recognises_every_spelling(string line) => Assert.Matches(SignatureVendorWords, line);

    [Fact]
    public void Secrets_are_not_hard_coded_in_source()
        => AssertNone(new Regex(@"(?i)(password|pwd|secret|apikey|token)\s*=\s*[""'][^""']{4,}[""']"), "Possible hard-coded secret");
}

/// <summary>
/// Solution packs are external, read-only data (docs/PACK_ARCHITECTURE.md). These rules keep it that way: no pack document becomes an embedded resource again, the pack reader has a small set of homes, the loader
/// names no pack, the sources and built packages stay separate, and no signing key is ever committed.
/// </summary>
[Trait("Category", "Architecture")]
public class PackRuleTests
{
    private static string Root => RepoRoot.Find();

    private static IEnumerable<string> Files(string relativeDirectory, string pattern)
        => Directory.EnumerateFiles(Path.Combine(Root, relativeDirectory), pattern, SearchOption.AllDirectories)
            .Select(f => f.Replace('\\', '/')).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/"));

    [Fact]
    public void No_project_embeds_a_solution_pack_document()
    {
        var hits = Files("src", "*.csproj").Concat(Files("tests", "*.csproj"))
            .SelectMany(f => File.ReadAllLines(f).Select(line => (f, line)))
            .Where(x => x.line.Contains("EmbeddedResource", StringComparison.Ordinal) && Regex.IsMatch(x.line, @"templates[/\\]packs|pack\.json|contract-lifecycle|variation\.json|notification-policy", RegexOptions.IgnoreCase))
            .Select(x => x.f).Distinct().ToList();
        Assert.True(hits.Count == 0, "Pack documents must be shipped as external .ecpkg packages, not embedded resources: " + string.Join(", ", hits));
    }

    [Fact]
    public void No_compiled_assembly_carries_a_pack_document_as_a_resource()
    {
        foreach (var (name, assembly, _) in Modules.All)
        {
            var resources = assembly.GetManifestResourceNames().Where(r => Regex.IsMatch(r, @"\.(pack|packs)\b|contract-lifecycle|variation|notification-policy|ecpkg", RegexOptions.IgnoreCase) && r.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || r.EndsWith(".ecpkg", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(resources.Count == 0, $"{name} embeds pack documents: {string.Join(", ", resources)}");
        }
    }

    [Fact]
    public void Packages_are_opened_only_by_the_pack_library_the_tool_and_the_loader_folder()
    {
        var allowed = new[] { "/src/Ecp.Packs/", "/src/Ecp.PackTool/", "/src/Modules/Ecp.Configuration/Packs/" };
        var hits = Files("src", "*.cs").Where(f => !allowed.Any(a => f.Contains(a, StringComparison.Ordinal)))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"PackReader\.Open\s*\(|PackZipReader\.Read\s*\("))
            .ToList();
        Assert.True(hits.Count == 0, "Reading a package belongs to the pack loader only: " + string.Join(", ", hits));
    }

    [Fact] // G-07
    public void No_private_signing_key_is_created_held_or_asked_for_by_the_web_application_or_any_module()
    {
        // Signing is the publisher's task, done with the ecp-pack tool on the publisher's own machine with a key held outside this product. The application only ever verifies, with public keys.
        var allowed = new[] { "/src/Ecp.Packs/", "/src/Ecp.PackTool/", "/src/Modules/Ecp.Recovery/Redaction.cs" };
        var pattern = new Regex(@"EcdsaP256Signer|IPackSigner|PackSigner\b|(?<!PublicKey)FromPem\s*\(|ImportPkcs8PrivateKey|ImportECPrivateKey|ExportECPrivateKey|ExportPkcs8PrivateKey|BEGIN (EC |RSA )?PRIVATE KEY|CreateSigner|\.SignData\s*\(|\.SignHash\s*\(", RegexOptions.CultureInvariant);
        var hits = Files("src", "*.cs").Concat(Files("src", "*.cshtml")).Where(f => !allowed.Any(a => f.Replace('\\', '/').Contains(a, StringComparison.Ordinal)))
            .Where(f => pattern.IsMatch(File.ReadAllText(f))).ToList();
        Assert.True(hits.Count == 0, "Only the pack tool signs: " + string.Join(", ", hits));
        // No page offers a field for a private key.
        var fields = Files("src/Ecp.Host", "*.cshtml").Where(f => Regex.IsMatch(File.ReadAllText(f), @"name=""(PrivateKey|PrivateKeyPem|SigningKey)""", RegexOptions.IgnoreCase)).ToList();
        Assert.True(fields.Count == 0, "A page asks for a private key: " + string.Join(", ", fields));
    }

    [Fact]
    public void The_pack_loader_names_no_pack_and_makes_no_decision_by_pack_key()
    {
        var offenders = Files("src/Modules/Ecp.Configuration/Packs", "*.cs").Concat(Files("src/Ecp.Packs", "*.cs"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "\"(blank|jbs[a-z-]*|reference|default)\"", RegexOptions.IgnoreCase))
            .ToList();
        Assert.True(offenders.Count == 0, "The loader and library must not name any pack: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Pack_sources_and_built_packages_stay_separate_and_the_host_builds_the_shipped_packs()
    {
        Assert.Empty(Files("templates", "*.ecpkg"));
        var sources = Directory.EnumerateDirectories(Path.Combine(Root, "templates", "packs")).Where(d => File.Exists(Path.Combine(d, "pack-source.json"))).ToList();
        Assert.True(sources.Count >= 2, "The Blank and reference pack sources are expected under templates/packs.");
        var host = File.ReadAllText(Path.Combine(Root, "src", "Ecp.Host", "Ecp.Host.csproj"));
        Assert.Contains("BuildShippedPacks", host);
        Assert.Contains("CopyShippedPacksToPublish", host);
        Assert.Contains("build-all", host);
    }

    [Fact]
    public void Every_shipped_pack_source_builds_and_verifies()
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(Root, "templates", "packs")).Where(d => File.Exists(Path.Combine(d, Ecp.Packs.PackBuilder.RecipeFile))))
        {
            var built = Ecp.Packs.PackBuilder.Build(dir);
            Assert.True(built.Succeeded, Path.GetFileName(dir) + ": " + string.Join("; ", built.Issues.Select(i => i.Code + " " + i.Message)));
            var again = Ecp.Packs.PackBuilder.Build(dir);
            Assert.Equal(built.Opened!.Sha256, again.Opened!.Sha256);   // reproducible: the same sources give the same bytes
        }
    }

    [Fact]
    public void No_private_signing_key_is_committed()
    {
        var pattern = new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----\s*[A-Za-z0-9+/=]{40,}");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var f = file.Replace('\\', '/');
            if (f.Contains("/.git/") || f.Contains("/obj/") || f.Contains("/bin/") || f.Contains("/TestResults/") || f.Contains("/artifacts/") || f.Contains("/node_modules/")) continue;
            if (new FileInfo(file).Length > 2_000_000) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".dll" or ".pdb" or ".png" or ".ico" or ".zip" or ".ecpkg" or ".xlsx" or ".woff" or ".woff2") continue;
            string text;
            try { text = File.ReadAllText(file); } catch (Exception) { continue; }
            if (pattern.IsMatch(text)) hits.Add(f);
        }
        Assert.True(hits.Count == 0, "A private key is present in the working tree: " + string.Join(", ", hits));
    }
}
