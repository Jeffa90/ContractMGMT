using Ecp.Configuration;
using Ecp.Packs;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// UPG-004 (docs/UPGRADES.md, "Pack and configuration compatibility"): the pack probe judges installed packs against the version being upgraded to, blocks only for what is in use, and only ever reads.
/// </summary>
public sealed class UpgradeProbeTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private sealed class ReadOnlyLoader(IReadOnlyList<PackCatalogueEntry> entries) : IPackLoader
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<PackCatalogueEntry>> ListAsync(CancellationToken ct = default) { Calls++; return Task.FromResult(entries); }
        public Task<PackCatalogueEntry?> FindAsync(string packKey, string? version = null, bool usableOnly = true, CancellationToken ct = default) => throw new InvalidOperationException("The probe must only list packs.");
        public Task<string?> GetDocumentAsync(string packKey, string? version, string kind, CancellationToken ct = default) => throw new InvalidOperationException("The probe must not read pack documents.");
        public Task<(PackCatalogueEntry Entry, OpenedPackage Package)?> OpenAsync(string packKey, string version, CancellationToken ct = default) => throw new InvalidOperationException("The probe must not open packages.");
    }

    private sealed class Inventory(params (string Key, string Version)[] inUse) : IConfigurationInventory
    {
        public Task<IReadOnlyList<ReleaseFingerprint>> ReleasesAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<int> OpenDraftCountAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<PackRecordFingerprint>> InstalledPacksAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<PackAssignmentFingerprint>> AssignmentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackAssignmentFingerprint>>(inUse.Select(u => new PackAssignmentFingerprint(Org, u.Key, u.Version, new string('a', 64), "Active")).ToList());
        public Task<IReadOnlyList<TrustKeyFingerprint>> TrustKeysAsync(CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private static PackCatalogueEntry Entry(string key, string version, bool compatible = true, TrustStatus trust = TrustStatus.Trusted, string? unavailable = null)
        => new(key, version, key + "-" + version, new LocalisedText(new Dictionary<string, string> { ["en"] = key }), new LocalisedText(new Dictionary<string, string> { ["en"] = key }), "publisher", "Publisher", "Installed",
            new string('b', 64), new string('c', 64), new TrustResult(trust, null, null, null, []),
            compatible ? [] : [new PackIssue(PackCodes.PlatformTooNew, PackSeverity.Error, "This pack does not support the platform version.")],
            true, [], [], null, false, unavailable, null, null);

    private static async Task<IReadOnlyList<UpgradeFinding>> Probe(IReadOnlyList<PackCatalogueEntry> entries, params (string, string)[] inUse)
    {
        var loader = new ReadOnlyLoader(entries);
        var findings = await new PackUpgradeProbe(loader, new Inventory(inUse)).ProbeAsync();
        Assert.Equal(1, loader.Calls);                                        // it listed, nothing else (the fake throws on any other call)
        return findings;
    }

    [Fact]
    public async Task A_pack_in_use_that_the_new_version_cannot_accept_blocks_and_one_nobody_uses_only_warns()
    {
        var findings = await Probe([Entry("reference", "2.5.0", compatible: false), Entry("spare", "1.0.0", compatible: false), Entry("blank", "1.5.0")], ("reference", "2.5.0"), ("blank", "1.5.0"));
        var blocker = Assert.Single(findings, f => f.Code == "UPG-PCK-001");
        Assert.Equal(UpgradeFindingSeverity.Blocker, blocker.Severity);
        Assert.Contains("reference@2.5.0", blocker.Detail); Assert.DoesNotContain("spare", blocker.Detail); Assert.DoesNotContain("blank", blocker.Detail);
        var warning = Assert.Single(findings, f => f.Code == "UPG-PCK-002");
        Assert.Equal(UpgradeFindingSeverity.Warning, warning.Severity);
        Assert.Contains("spare@1.0.0", warning.Detail);
        Assert.False(Redaction.ContainsSecret(string.Join("\n", findings.Select(f => f.Detail + f.Description + f.Remediation))));
    }

    [Fact]
    public async Task A_pack_in_use_that_fails_the_signature_policy_or_cannot_be_read_blocks()
    {
        var findings = await Probe([Entry("signed-wrongly", "1.0.0", trust: TrustStatus.UnknownKey), Entry("lost", "1.0.0", unavailable: "The package file is missing."), Entry("fine", "1.0.0")],
            ("signed-wrongly", "1.0.0"), ("lost", "1.0.0"), ("fine", "1.0.0"));
        Assert.Equal(UpgradeFindingSeverity.Blocker, Assert.Single(findings, f => f.Code == "UPG-PCK-003").Severity);
        Assert.Equal(UpgradeFindingSeverity.Blocker, Assert.Single(findings, f => f.Code == "UPG-PCK-004").Severity);
        Assert.DoesNotContain(findings, f => f.Code is "UPG-PCK-001" or "UPG-PCK-002");
    }

    [Fact]
    public async Task Compatible_packs_raise_nothing_and_an_unreadable_pack_nobody_uses_is_not_a_blocker()
    {
        Assert.Empty(await Probe([Entry("blank", "1.5.0"), Entry("reference", "2.5.0")], ("blank", "1.5.0")));
        Assert.Empty(await Probe([Entry("old", "0.9.0", unavailable: "damaged")]));
    }
}
