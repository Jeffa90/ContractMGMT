using System.Text.RegularExpressions;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Architecture.Tests;

/// <summary>
/// Portability guard for S6 (docs/CAPABILITIES.md). A capability is operating-model configuration, so the code that honours it must be reachable from one place and findable by a scan: capability keys are named only through
/// <c>CapabilityKeys</c>, a step is gated at the one engine chokepoint, navigation consults the resolver, and every shipped pack says what it decides.
/// </summary>
[Trait("Category", "Architecture")]
public class CapabilityGuardTests
{
    private static string Root => RepoRoot.Find();
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static IEnumerable<string> SourceFiles()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs") && !file.EndsWith(".cshtml")) continue;
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            yield return relative;
        }
    }

    [Fact]
    public void Capability_keys_are_named_only_through_the_CapabilityKeys_constants()
    {
        // The catalogue is the one place a key is spelled. Everywhere else a literal would be a second spelling that a rename or a new capability would miss.
        // The keys are also ordinary words elsewhere (the permission and terminology key "legal.review"), so the rule looks at the places a capability is *asked for or stated*, not at every string.
        var literal = new Regex("(IsEnabled|IsEnabledAsync|Uses|UsesAnywhere|Find|LayerOf|OfTransitionRole|CapabilityEntry|CapabilityInput)\\([^)\\n]*\"(legal\\.review|register\\.authorisation|signature\\.requests|variations|renewals)\"", RegexOptions.CultureInvariant);
        var found = new List<string>();
        foreach (var file in SourceFiles())
        {
            if (file.EndsWith("src/Ecp.SharedKernel/Capabilities/CapabilityCatalogue.cs")) continue;
            var text = File.ReadAllText(Path.Combine(Root, file));
            foreach (Match m in literal.Matches(text)) found.Add($"{file}: {m.Value}");
        }
        Assert.True(found.Count == 0, "Capability keys must be referenced through CapabilityKeys, not spelled again:\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_lifecycle_engine_gates_every_managed_step_at_its_single_chokepoint()
    {
        var engine = Read("src", "Modules", "Ecp.Lifecycle", "Engine", "LifecycleEngine.cs");
        Assert.Contains("capabilityGate.RequireAsync(", engine);        // ExecuteAsync: nothing new starts in a capability that is off
        Assert.Contains("capabilityGate.BlockedByAsync(", engine);      // AvailableAsync: a step that would be refused is not offered
        // The gate is applied before the step's effects: it comes before the managed-step and permission handling in ExecuteAsync.
        var execute = engine[engine.IndexOf("ExecuteAsync(", StringComparison.Ordinal)..];
        Assert.True(execute.IndexOf("capabilityGate.RequireAsync(", StringComparison.Ordinal) < execute.IndexOf("transition.Managed && !cmd.AllowManaged", StringComparison.Ordinal),
            "The capability gate must run before a managed step is accepted.");
        var gate = Read("src", "Modules", "Ecp.Lifecycle", "Capabilities", "CapabilityGate.cs");
        Assert.Contains("CapabilityCatalogue.OfTransitionRole(", gate);     // found through the closed vocabulary, never by a state or step name
        Assert.DoesNotMatch("\"[A-Z][A-Za-z]+\"", gate.Replace("\"capability_disabled\"", ""));   // no state or step code is spelled in the gate
    }

    [Fact]
    public void Top_level_navigation_for_each_optional_area_consults_the_capability_resolver()
    {
        var layout = Read("src", "Ecp.Host", "Pages", "Shared", "_Layout.cshtml");
        var lines = layout.Split('\n');
        foreach (var (path, key) in new[] { ("\"/Legal\"", "CapabilityKeys.LegalReview"), ("\"/Approvals\"", "CapabilityKeys.RegisterAuthorisation"), ("\"/Variations\"", "CapabilityKeys.Variations"), ("\"/Renewals\"", "CapabilityKeys.Renewals") })
        {
            var line = lines.FirstOrDefault(l => l.Contains(path) && l.Contains("items.Add"));
            Assert.True(line is not null, $"No navigation entry for {path}.");
            Assert.True(line!.Contains(key), $"The navigation entry for {path} must be guarded by {key}.");
        }
    }

    [Fact]
    public void The_capability_resolver_is_the_only_reader_of_the_capability_configuration()
    {
        // Nothing else reads the Capabilities kind: they ask the resolver. (The administration service, the kind and the resolver live together; the readiness check reads shipped packs' own documents, not an organisation's.)
        var allowed = new[] { "src/Modules/Ecp.Configuration/Capabilities/", "src/Ecp.SharedKernel/Capabilities/", "src/Ecp.Host/Pages/Admin/Capabilities/", "src/Modules/Ecp.Configuration/Packs/", "src/Modules/Ecp.Configuration/PacksAndBranding.cs",
            "src/Ecp.Packs/", "src/Modules/Ecp.Configuration/ConfigurationApi.cs", "src/Ecp.Host/Program.cs", "src/Ecp.Host/CapabilityReadinessCheck.cs" };
        var bad = SourceFiles().Where(f => !allowed.Any(a => f.StartsWith(a, StringComparison.Ordinal) || f == a.TrimEnd('/')))
            .Where(f => File.ReadAllText(Path.Combine(Root, f)).Contains("CapabilityKinds.Name", StringComparison.Ordinal)).ToList();
        Assert.True(bad.Count == 0, "Only the capability resolver and administration read the capability configuration:\n" + string.Join("\n", bad));
    }

    [Theory]
    [InlineData("jbs-procurement")]
    [InlineData("blank")]
    public void Every_shipped_pack_says_what_it_decides_about_every_capability(string pack)
    {
        var source = Read("templates", "packs", pack, "pack-source.json");
        Assert.Contains("\"Capabilities\"", source);
        var doc = Read("templates", "packs", pack, "capabilities", "capabilities.json");
        foreach (var key in CapabilityKeys.All) Assert.Contains($"\"{key}\"", doc);
    }

    [Fact]
    public void A_capability_document_is_data_with_no_expressions_or_code()
    {
        foreach (var pack in new[] { "jbs-procurement", "blank" })
        {
            var doc = Read("templates", "packs", pack, "capabilities", "capabilities.json");
            Assert.DoesNotMatch(@"<|\$\{|\{\{|\bexpr\b|\bwhere\b|\bselect\b|javascript:", doc);
        }
    }
}
