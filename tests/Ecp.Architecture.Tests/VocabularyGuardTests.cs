using System.Text.RegularExpressions;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Architecture.Tests;

/// <summary>
/// Portability guard, stage B (S4). Stage A (<c>SourceRuleTests</c>) reads C# only. Stage B reads every text file that ships with the platform: Razor, resources, JSON, project and props files, appsettings,
/// deployment files, workflows, scripts, CSS and JavaScript. It refuses business-specific vocabulary and fixed regional defaults wherever they are not on the narrow, written allow-list below.
/// A label such as "Supplier" must reach a person through the terminology resolver (docs/TERMINOLOGY.md), never as a fixed string.
/// Machine identifiers are not labels and are not flagged: <c>ProcurementAdministrator</c>, <c>CategoryOwner</c>, <c>GroupHeadApprover</c> and their permission and state codes keep their spelling (ADR-0028).
/// </summary>
[Trait("Category", "Architecture")]
public class VocabularyGuardTests
{
    internal sealed record Rule(string Id, Regex Pattern, bool Razor = false);

    /// <summary>One permitted place, with the reason and the slice that removes it. An allowance that no longer matches anything fails the build, so the list can only shrink.</summary>
    internal sealed record Allowance(string Rule, string PathSuffix, string Reason);

    private static readonly Rule Vendors = new("vendor-names", new Regex(@"JBS|Medius|Qlik|DocuSign", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private static readonly Rule FixedLabels = new("fixed-business-labels",
        new Regex(@"Group\s+Procurement|(Group\s+)?Head\s+of\s+Procurement|Procurement\s+Administrator|Procurement\s+Contract\s+Register|Category\s+Owner", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private static readonly Rule RegionalDefaults = new("regional-defaults", new Regex(@"en-AU|Australia/|Brisbane|(?<![-\w])AUD(?![-\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    // Capitalised business words typed into a page. Case-sensitive on purpose: tokens such as [[supplier|lower]] are lower case, and a capitalised fixed word is exactly the defect.
    private const string Words = @"Suppliers?|Procurement|Categor(?:y|ies)|Subcategor(?:y|ies)|Legal Review|Contract Register|Agreements?";
    private static readonly Rule PageLiterals = new("fixed-label-in-page-text", new Regex($@"L\[""[^""\]]*\b(?:{Words})\b|(?<![=\-])>[^<>@{{}}\n]*\b(?:{Words})\b[^<>@{{}}\n]*<"), Razor: true);

    // S9-discovered generic fix F1: a quoted label inside a Razor tuple or array literal ("Contract type", "Risk level") never passes through L[...], so PageLiterals cannot see it. The names of the core fields that
    // every business may call something else are refused as bare quoted labels; a token such as "[[field.risk.level|sentence]]" is not a bare label and passes.
    private static readonly Rule QuotedFieldLabels = new("fixed-field-label-literal", new Regex(@"""(?:Contract type|Agreement type|Risk level|Function)"""), Razor: true);

    private static readonly Rule[] Rules = [Vendors, FixedLabels, RegionalDefaults, PageLiterals, QuotedFieldLabels];

    /// <summary>The whole allow-list. Adding to it needs a reason that names the slice that removes the exception, or a statement that the place is data (a sample, an adapter note, a pack).</summary>
    internal static readonly IReadOnlyList<Allowance> Allowances =
    [
        new("regional-defaults", "src/Ecp.Host/appsettings.json", "S10: the installation default locale, time zone and currency; every organisation overrides them."),
        new("regional-defaults", "src/Ecp.SharedKernel/Localisation/LocaleFormatter.cs", "S10: LocalisationOptions defaults, used only until an organisation or installation setting says otherwise."),
        new("regional-defaults", "src/Ecp.SharedKernel/Localisation/GlobalisationProbe.cs", "S10: the readiness probe that proves the image carries ICU and the Australian zone rules the default needs."),
        new("regional-defaults", "src/Ecp.Host/Pages/OrgLocale.cs", "S10: fallback formatter when a page has no organisation."),
        new("regional-defaults", "src/Ecp.Host/Pages/Migration/MigrationPageModel.cs", "S10: fallback formatter before a batch's organisation is known."),
        new("regional-defaults", "src/Modules/Ecp.Contracts/CustomFields.cs", "S10: fallback culture when a field value is parsed outside any organisation."),
        new("regional-defaults", "src/Ecp.SharedKernel/Terminology/TermSet.cs", "Documentation example of a regional locale code."),
        new("regional-defaults", "src/Modules/Ecp.Configuration/Terminology/TerminologyDocument.cs", "Validation message and documentation example of a regional locale code."),
        new("regional-defaults", "src/Ecp.Host/Pages/Admin/Terminology/_Picker.cshtml", "Locale suggestions offered to an administrator (a datalist), not a default."),
        new("regional-defaults", "src/Ecp.Packs/PackManifest.cs", "Validation message and documentation example of a culture code."),
        new("regional-defaults", "tools/check-repo.py", "AUD is the audit requirement-id prefix, not a currency."),
        new("fixed-business-labels", "src/Ecp.Host/DemoSeeder.cs", "Sample-data loader: maps the role names written in the shipped sample CSV to stable role codes. Never used by a customer installation."),
        new("vendor-names", "tools/migration-demo.py", "Demonstration script for the shipped Medius migration profile (a migration compatibility profile, not platform logic)."),
    ];

    private static readonly string[] Extensions = [".cs", ".cshtml", ".csproj", ".props", ".targets", ".json", ".resx", ".css", ".js", ".yml", ".yaml", ".sh", ".py", ".env", ".config", ".xml", ".html", ".sql", ".sln", ".slnx"];

    internal static IEnumerable<(string Path, string Text)> Files()
    {
        var root = RepoRoot.Find();
        foreach (var top in new[] { "src", "deploy", ".github", "tools" })
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains("/Persistence/Migrations/") || rel.StartsWith("tools/tests/", StringComparison.Ordinal) || rel.Contains("__pycache__")) continue;
                if (rel.EndsWith("packages.lock.json", StringComparison.Ordinal)) continue;
                var name = Path.GetFileName(file);
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && name is not ("Dockerfile" or "Dockerfile.dev" or ".dockerignore" or ".gitignore")) continue;
                yield return (rel, File.ReadAllText(file));
            }
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            if (Path.GetFileName(file) is "Directory.Build.props" or "Directory.Packages.props" or "global.json" or "Dockerfile" or ".dockerignore")
                yield return (Path.GetFileName(file), File.ReadAllText(file));
    }

    /// <summary>Every hit of every rule, minus the allowances. Exposed so the tests below can run the same engine over synthetic files.</summary>
    internal static List<string> Violations(IEnumerable<(string Path, string Text)> files, IReadOnlyList<Allowance> allowances, out HashSet<Allowance> used)
    {
        var usedSet = new HashSet<Allowance>();
        var found = new List<string>();
        foreach (var (path, text) in files)
        {
            var razor = path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
            foreach (var rule in Rules)
            {
                if (rule.Razor && !razor) continue;
                if (!rule.Pattern.IsMatch(text)) continue;
                var allowance = allowances.FirstOrDefault(a => a.Rule == rule.Id && path.EndsWith(a.PathSuffix, StringComparison.Ordinal));
                if (allowance is not null) { usedSet.Add(allowance); continue; }
                var line = text.Split('\n').Select((l, i) => (l, i)).First(x => rule.Pattern.IsMatch(x.l));
                found.Add($"[{rule.Id}] {path}:{line.i + 1}: {line.l.Trim()[..Math.Min(line.l.Trim().Length, 120)]}");
            }
        }
        used = usedSet;
        return found;
    }

    [Fact]
    public void Generic_platform_files_carry_no_business_vocabulary_or_fixed_regional_defaults_outside_the_allow_list()
    {
        var violations = Violations(Files(), Allowances, out _);
        Assert.True(violations.Count == 0, "Move the wording to a pack or the terminology resolver (docs/TERMINOLOGY.md), or add a justified allowance:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_allowance_is_still_needed_and_names_a_real_file_so_the_list_can_only_shrink()
    {
        Violations(Files(), Allowances, out var used);
        var stale = Allowances.Where(a => !used.Contains(a)).Select(a => $"{a.Rule}: {a.PathSuffix}").ToList();
        Assert.True(stale.Count == 0, "Remove allowances that no longer match anything: " + string.Join(", ", stale));
        foreach (var a in Allowances)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Reason));
            Assert.True(File.Exists(Path.Combine(RepoRoot.Find(), a.PathSuffix)), a.PathSuffix + " does not exist");
        }
    }

    [Fact]
    public void The_allow_list_is_narrow_files_only_never_a_folder_or_a_wildcard()
    {
        Assert.True(Allowances.Count <= 16, "The allow-list grew; each entry weakens the guard.");
        Assert.All(Allowances, a =>
        {
            Assert.DoesNotContain('*', a.PathSuffix);
            Assert.DoesNotContain("..", a.PathSuffix);
            Assert.False(a.PathSuffix.EndsWith('/'), "an allowance names a file, not a folder");
            Assert.True(Path.HasExtension(a.PathSuffix));
        });
        // Nothing in the platform's generic UI, domain or configuration code may be exempted wholesale for fixed business labels.
        Assert.Equal(1, Allowances.Count(a => a.Rule == "fixed-business-labels"));
        Assert.Equal(1, Allowances.Count(a => a.Rule == "vendor-names"));
    }

    [Theory] // the guard must see each of these spellings
    [InlineData("vendor-names", "src/X.cs", "var x = \"JBSAUS-000001\";")]
    [InlineData("vendor-names", "src/X.cshtml", "<p>Sent through DocuSign</p>")]
    [InlineData("vendor-names", "src/appsettings.json", "\"Source\": \"medius\"")]
    [InlineData("vendor-names", "src/X.csproj", "<RootNamespace>Qlik.Sync</RootNamespace>")]
    [InlineData("vendor-names", "deploy/docker/compose.yml", "JBS_CONNECTION: x")]
    [InlineData("fixed-business-labels", "src/Pages/X.cshtml", "<h1>Group Head of Procurement</h1>")]
    [InlineData("fixed-business-labels", "src/Pages/X.cs", "label = \"Procurement Administrator\";")]
    [InlineData("fixed-business-labels", "src/Pages/X.cs", "label = \"category owner\";")]
    [InlineData("fixed-business-labels", "src/Pages/X.cshtml", "<th>Procurement  Contract Register</th>")]
    [InlineData("fixed-business-labels", "src/Pages/X.resx", "<value>GROUP PROCUREMENT</value>")]
    [InlineData("regional-defaults", "src/Pages/X.cs", "new LocaleFormatter(\"en-AU\", \"Australia/Brisbane\", \"AUD\")")]
    [InlineData("regional-defaults", "src/appsettings.json", "\"DefaultCurrencyCode\": \"AUD\"")]
    [InlineData("regional-defaults", "src/Pages/X.js", "const tz = 'australia/brisbane';")]
    [InlineData("fixed-label-in-page-text", "src/Pages/X.cshtml", "<h1>@L[\"Suppliers\"]</h1>")]
    [InlineData("fixed-label-in-page-text", "src/Pages/X.cshtml", "<label>Legal Review status</label>")]
    [InlineData("fixed-label-in-page-text", "src/Pages/X.cshtml", "<td>Category</td>")]
    [InlineData("fixed-field-label-literal", "src/Pages/X.cshtml", "@foreach (var (key, label) in new[] { (\"riskLevel\", \"Risk level\") })")]
    [InlineData("fixed-field-label-literal", "src/Pages/X.cshtml", "@foreach (var (key, label) in new[] { (\"contractType\", \"Contract type\") })")]
    public void The_guard_detects_fixed_labels_in_every_file_type(string rule, string path, string line)
    {
        var hits = Violations([(path, line)], [], out _);
        Assert.Contains(hits, h => h.StartsWith($"[{rule}]", StringComparison.Ordinal));
    }

    [Theory] // stable machine identifiers, tokens and neutral wording are not labels
    [InlineData("src/Roles.cs", "public const string ProcurementAdministrator = \"ProcurementAdministrator\";")]
    [InlineData("src/Roles.cs", "RoleCodes.CategoryOwner, RoleCodes.GroupHeadApprover")]
    [InlineData("src/Pages/X.cshtml", "<h1>@L[\"[[supplier|plural]]\"]</h1>")]
    [InlineData("src/Pages/X.cshtml", "<td>@Terms.Singular(\"category\")</td>")]
    [InlineData("src/Pages/X.cshtml", "(\"riskLevel\", \"[[field.risk.level|sentence]]\", i.RiskLevel)")]
    [InlineData("src/Pages/X.cshtml", "var q = items.Where(r => r.Category.IsActive);")]
    [InlineData("src/Setup/SetupChecks.cs", "public override string Id => \"R-AUD-01\";")]
    [InlineData("src/Pages/X.cs", "var currency = organisation.DefaultCurrencyCode;")]
    public void Identifiers_tokens_and_neutral_words_are_not_flagged(string path, string line)
        => Assert.Empty(Violations([(path, line)], [], out _));

    [Fact]
    public void An_exception_covers_only_its_own_file_and_its_own_rule()
    {
        var allowance = Allowances.First(a => a.Rule == "regional-defaults");
        var sibling = Path.GetDirectoryName(allowance.PathSuffix)!.Replace('\\', '/') + "/Another.cs";
        var line = "new LocaleFormatter(\"en-AU\", \"Australia/Brisbane\", \"AUD\")";
        Assert.Empty(Violations([(allowance.PathSuffix, line)], Allowances, out _));
        Assert.NotEmpty(Violations([(sibling, line)], Allowances, out _));                                  // a neighbouring file is not covered
        Assert.NotEmpty(Violations([(allowance.PathSuffix, "// Medius adapter")], Allowances, out _));       // another rule in the same file is not covered
        Assert.NotEmpty(Violations([("src/Ecp.Host/Pages/Evil/" + Path.GetFileName(allowance.PathSuffix), line)], Allowances, out _));   // same file name elsewhere
    }

    [Fact]
    public void Solution_pack_sources_and_samples_are_where_business_wording_lives_and_are_not_scanned()
    {
        var scanned = Files().Select(f => f.Path).ToList();
        Assert.DoesNotContain(scanned, p => p.StartsWith("templates/", StringComparison.Ordinal) || p.StartsWith("sample-data/", StringComparison.Ordinal) || p.StartsWith("migration-samples/", StringComparison.Ordinal) || p.StartsWith("docs/", StringComparison.Ordinal));
        Assert.Contains(scanned, p => p.StartsWith("src/Ecp.Host/Pages/", StringComparison.Ordinal) && p.EndsWith(".cshtml", StringComparison.Ordinal));
        Assert.Contains(scanned, p => p == "src/Ecp.Host/appsettings.json");
        Assert.Contains(scanned, p => p.EndsWith(".csproj", StringComparison.Ordinal));
        Assert.Contains(scanned, p => p.StartsWith(".github/workflows/", StringComparison.Ordinal));
    }
}
