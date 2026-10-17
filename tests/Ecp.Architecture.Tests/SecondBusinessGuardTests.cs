using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Architecture.Tests;

/// <summary>
/// Portability guard for S9 (docs/SECOND_BUSINESS_PROOF.md, SBP-AC-01 and SBP-AC-17). The second business (Harborline Construction, pack key <c>construction-subcontracts</c>) must be nothing but an external solution pack.
/// Three independent checks keep that true. (1) No distinctive construction word appears in any file that ships with the platform. (2) No project, build or deployment file names the pack. (3) The git diff of the S9 commits
/// is confined to the pack, tests, fixtures and documentation; the platform paths that did change are on a written list of generic fixes, and their added lines contain no construction vocabulary.
/// </summary>
[Trait("Category", "Architecture")]
public class SecondBusinessGuardTests
{
    /// <summary>The last commit before S9 began (CI run 53, S9-readiness prerequisites). It is the defined platform baseline the diff is measured from.</summary>
    internal const string Baseline = "40b2204";

    /// <summary>Distinctive words of the second business. Case-insensitive; every one is ordinary construction vocabulary that the generic platform must never spell.</summary>
    internal static readonly Regex Words = new(
        @"Harborline|construction-subcontracts|subcontract(?:s|or|ors)?\b|change\s+orders?\b|trade\s+packages?\b|stop\s+work\b|mobili[sz]ation|superintendent|practical\s+completion|Chicago|Project\s+Directors?\b|Package\s+Managers?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Platform paths that S9 changed, each a generic defect found by the proof. Adding here needs a finding in docs/PORTABILITY_AUDIT.md and a regression test that benefits every pack.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ApprovedPlatformChanges = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["src/Ecp.Host/Pages/Shared/_ContractFields.cshtml"] = "S9-discovered generic fix F1: the contract type, agreement type, risk and function labels were fixed English words; they now resolve through the terminology catalogue.",
        ["tools/check-packs.sh"] = "Pack check only: every shipped pack (not just the first) is signed and verified with the ephemeral key; no pack is named.",
        [".github/workflows/ci.yml"] = "CI only: the checkout keeps full history so this guard can inspect the S9 diff against the baseline.",
    };

    /// <summary>Folders and files where business-specific additions are expected: the pack, samples and fixtures, tests, requirements and documentation.</summary>
    private static readonly string[] ApprovedPrefixes =
    [
        "templates/packs/construction-subcontracts/", "tests/", "docs/", "decisions/", "requirements/", "sample-data/", "migration-samples/",
    ];
    private static readonly string[] ApprovedFiles = ["TASKS.md", "PACKAGE_MANIFEST.md", "README.md", "CLAUDE.md", "deploy/STATUS.md"];

    private static bool IsApproved(string path) => ApprovedPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)) || ApprovedFiles.Contains(path, StringComparer.Ordinal);

    [Fact]
    public void No_file_that_ships_with_the_platform_spells_a_construction_word()
    {
        var found = new List<string>();
        foreach (var (path, text) in VocabularyGuardTests.Files())
        {
            var m = Words.Match(text);
            if (!m.Success) continue;
            var line = text.Split('\n').Select((l, i) => (l, i)).First(x => Words.IsMatch(x.l));
            found.Add($"{path}:{line.i + 1}: {line.l.Trim()[..Math.Min(line.l.Trim().Length, 120)]}");
        }
        Assert.True(found.Count == 0, "The second business lives only in its pack. Move the wording to the pack's terminology:\n" + string.Join("\n", found));
    }

    [Fact]
    public void No_compiled_platform_assembly_contains_the_second_business()
    {
        // SBP-AC-01: the pack reaches the running installation as an uploaded .ecpkg, so nothing compiled can know it. Look inside the built assemblies (UTF-8 and UTF-16 string heaps) rather than trusting the source scan.
        var root = RepoRoot.Find();
        var needles = new[] { "Harborline", "construction-subcontracts", "Subcontractor", "Change Order", "Trade Package" };
        var patterns = needles.SelectMany(n => new[] { System.Text.Encoding.UTF8.GetBytes(n), System.Text.Encoding.Unicode.GetBytes(n) }).ToList();
        var scanned = 0;
        var found = new List<string>();
        foreach (var dll in Directory.EnumerateFiles(Path.Combine(root, "src"), "Ecp.*.dll", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, dll).Replace('\\', '/');
            if (!rel.Contains("/bin/") || rel.Contains("/ref/") || rel.Contains("/refint/")) continue;
            var bytes = File.ReadAllBytes(dll);
            scanned++;
            foreach (var pattern in patterns)
                if (bytes.AsSpan().IndexOf(pattern) >= 0) { found.Add($"{rel}: {System.Text.Encoding.UTF8.GetString(pattern)}"); break; }
        }
        Assert.True(scanned >= 10, "Expected to find the built platform assemblies under src/*/bin; found " + scanned);
        Assert.True(found.Count == 0, "A compiled assembly contains the second business's vocabulary:\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_pack_is_not_named_by_any_project_build_or_deployment_file()
    {
        // The host builds every folder under templates/packs without naming one; a reference to a particular pack would mean it needs a rebuild to be added.
        var root = RepoRoot.Find();
        var named = new List<string>();
        var build = new[] { ".csproj", ".props", ".targets", ".sln", ".slnx", ".yml", ".yaml", ".sh", ".py", ".config" };
        foreach (var top in new[] { "src", "deploy", ".github", "tools", "tests" })
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.EndsWith("packages.lock.json", StringComparison.Ordinal)) continue;
                var name = Path.GetFileName(file);
                var isBuild = build.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || name is "Dockerfile" or "Dockerfile.dev" or ".dockerignore";
                if (!isBuild) continue;
                if (File.ReadAllText(file).Contains("construction-subcontracts", StringComparison.OrdinalIgnoreCase)) named.Add(rel);
            }
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            if (build.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && File.ReadAllText(file).Contains("construction-subcontracts", StringComparison.OrdinalIgnoreCase)) named.Add(Path.GetFileName(file));
        Assert.True(named.Count == 0, "No project or deployment file may name the pack:\n" + string.Join("\n", named));
    }

    [Fact]
    public void The_approved_platform_changes_are_few_justified_and_real_files()
    {
        Assert.True(ApprovedPlatformChanges.Count <= 4, "The list of platform files S9 may change grew; each entry weakens the proof.");
        foreach (var (path, reason) in ApprovedPlatformChanges)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.DoesNotContain('*', path);
            Assert.True(File.Exists(Path.Combine(RepoRoot.Find(), path)), path + " does not exist");
        }
    }

    [Fact]
    public void The_S9_commits_changed_only_the_pack_tests_documentation_and_the_written_generic_fixes()
    {
        var commits = S9Commits();
        Assert.True(commits.Count > 0, "No commit with the subject prefix 'S9' follows the baseline " + Baseline + ".");
        var outside = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var addedLines = new List<string>();
        foreach (var (hash, subject) in commits)
        {
            foreach (var line in Git("diff-tree", "--no-commit-id", "--name-status", "-r", "--no-renames", hash).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                var path = parts[^1];
                if (!IsApproved(path)) outside[path] = $"{parts[0]} in {hash[..7]} ({subject})";
            }
            // Only platform paths need their added lines read; the pack, tests and documents are allowed to say what they like.
            var patch = Git("show", "--format=", "-U0", "--no-renames", hash, "--", "src", "tools", "deploy", ".github", "Dockerfile", "Directory.Build.props", "Directory.Packages.props", "global.json");
            string? file = null;
            foreach (var l in patch.Split('\n'))
            {
                if (l.StartsWith("+++ b/", StringComparison.Ordinal)) { file = l[6..]; continue; }
                if (l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal) && file is not null && !file.EndsWith("deploy/STATUS.md", StringComparison.Ordinal) && Words.IsMatch(l)) addedLines.Add($"{file}: {l.Trim()}");
            }
        }
        var unlisted = outside.Where(kv => !ApprovedPlatformChanges.ContainsKey(kv.Key)).Select(kv => $"{kv.Key}  [{kv.Value}]").ToList();
        Assert.True(unlisted.Count == 0, "S9 may change platform files only when each is a written generic fix (ApprovedPlatformChanges):\n" + string.Join("\n", unlisted));
        var stale = ApprovedPlatformChanges.Keys.Where(k => !outside.ContainsKey(k)).ToList();
        Assert.True(stale.Count == 0, "A listed platform change is no longer in the S9 diff; remove it: " + string.Join(", ", stale));
        Assert.True(addedLines.Count == 0, "Lines added to platform files must not carry construction vocabulary:\n" + string.Join("\n", addedLines));
    }

    [Fact]
    public void The_diff_check_notices_a_construction_word_added_to_platform_code()
    {
        // The guard must be able to fail. Run the same word list over a synthetic added line and a synthetic platform path.
        Assert.Matches(Words, "+    var label = \"Change Order\";");
        Assert.Matches(Words, "+ <h1>Subcontractor</h1>");
        Assert.False(IsApproved("src/Modules/Ecp.Lifecycle/Engine/LifecycleEngine.cs"));
        Assert.False(IsApproved("tools/check-packs.sh"));
        Assert.True(IsApproved("templates/packs/construction-subcontracts/pack.json"));
        Assert.False(IsApproved("templates/packs/jbs-procurement/pack.json"));
        Assert.True(IsApproved("tests/Ecp.UnitTests/ConstructionPackTests.cs"));
        Assert.True(IsApproved("docs/CONSTRUCTION_SUBCONTRACTS_PACK.md"));
    }

    private static List<(string Hash, string Subject)> S9Commits()
    {
        var range = Baseline + "..HEAD";
        if (Git("rev-parse", "--verify", "--quiet", Baseline + "^{commit}").Trim().Length == 0)
            Assert.Fail($"The baseline commit {Baseline} is not in this checkout. Fetch full history (git fetch --unshallow); the CI checkout uses fetch-depth: 0.");
        return Git("log", "--format=%H%x09%s", "--no-merges", range).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t', 2)).Where(p => p.Length == 2 && p[1].StartsWith("S9", StringComparison.Ordinal)).Select(p => (p[0], p[1])).ToList();
    }

    private static string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = RepoRoot.Find(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("git could not be started");
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
