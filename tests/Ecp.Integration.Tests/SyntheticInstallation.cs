using System.Security.Cryptography;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Notifications;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// A populated, entirely synthetic installation for the backup and restore proof: one tenant that runs the reference (JBS) business, a Blank organisation and Harborline Construction side by side, with contracts moved through
/// their lifecycles, documents, counterparties with tax identifiers, published configuration, installed packs and queued notifications; and a second tenant that shares nothing with it.
/// </summary>
internal sealed class SyntheticInstallation
{
    public required LifecycleWorld Lw { get; init; }
    public required PackResolutionScenarios.PackOrg Jbs { get; init; }
    public required PackResolutionScenarios.PackOrg Blank { get; init; }
    public required HarborlineWorld Harbor { get; init; }
    public required Guid JbsContract { get; init; }
    public required Guid BlankContract { get; init; }
    public required Guid HarborContract { get; init; }
    public required Guid OtherTenant { get; init; }
    public required string DocumentText { get; init; }
    public Guid TenantId => Lw.W.TenantId;

    public static async Task<SyntheticInstallation> BuildAsync(TestApp app)
    {
        var lw = await LifecycleWorld.CreateAsync(app);
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(lw, "Reference Org", "BRJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(lw, "Blank Org", "BRB", TestPacks.Blank);
        await using (var s = lw.W.AsScopeless())
            await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(jbs.Org, "Eleven Digits Pty", TaxIdentifier: "51 824 753 556"));

        var jc = await lw.W.AsAsync(jbs.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(jbs.Org, "BRJ-000001", "Reference agreement", jbs.Supplier, jbs.Category,
            CurrencyCode: "AUD", EstimatedValue: 1000m, EndDate: DateOnly.FromDateTime(lw.W.Now.AddDays(300)))));
        var bc = await lw.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "BRB-000001", "Blank agreement", blank.Supplier, blank.Category,
            CurrencyCode: "AUD", EstimatedValue: 2000m, EndDate: DateOnly.FromDateTime(lw.W.Now.AddDays(300)))));
        await lw.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(bc, "Activate")));

        const string text = "synthetic document content for the backup proof";
        await using (var s = lw.W.AsScopeless())
        {
            await s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(jbs.Org, jc, "Executed Contract", "Executed copy", "executed.pdf", "application/pdf", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), "Internal", IsExecutedCopy: true));
            await s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(blank.Org, bc, "Executed Contract", "Blank copy", "blank.pdf", "application/pdf", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text + " (blank)")), "Internal"));
        }

        var h = await HarborlineWorld.CreateInTenantAsync(app, lw.W.TenantId);
        await h.UploadAndInstallAsync();
        await h.AssignAsync();
        await h.PublishAllAsync(includeTenantLevel: false);
        await h.OnboardAsync();
        var hc = await h.NewSubcontractAsync();
        await h.AddDocumentAsync("packageManagerA", hc);
        await h.ToReadyForSignatureAsync(hc);
        await lw.RunJobsAsync();

        var other = await app.ProvisionAsync("Other Tenant " + Guid.NewGuid().ToString("N")[..6], "OTH");
        return new SyntheticInstallation { Lw = lw, Jbs = jbs, Blank = blank, Harbor = h, JbsContract = jc, BlankContract = bc, HarborContract = hc, OtherTenant = other.TenantId, DocumentText = text };
    }

    private static async Task<T> AsAsync<T>(IServiceProvider services, Guid tenant, Guid user, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant);
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(user);
        var actor = scope.ServiceProvider.GetRequiredService<ActorContext>();
        actor.Type = ActorType.User; actor.ActorId = user.ToString(); actor.Source = "Test";
        return await work(scope.ServiceProvider);
    }

    /// <summary>
    /// Representative reads of all three businesses through the real services: what each contract's lifecycle says, what each organisation's words, capabilities, regional settings, packs, published configuration and
    /// documents are. The same probe runs on the source and on a restored copy; the two results must be identical.
    /// </summary>
    public async Task<SortedDictionary<string, string>> ProbeAsync(IServiceProvider services)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var orgs = new (string Name, Guid Org, Guid Contract, Guid User)[]
        {
            ("jbs", Jbs.Org, JbsContract, Lw.W.Users[Jbs.User]), ("blank", Blank.Org, BlankContract, Lw.W.Users[Blank.User]), ("harbor", Harbor.Org, HarborContract, Harbor.Users["packageManagerA"])
        };
        foreach (var (name, org, contract, user) in orgs)
        {
            var state = await AsAsync(services, TenantId, user, s => s.GetRequiredService<ILifecycleService>().StateAsync(contract));
            facts[name + ".state"] = state.Status + "|locked=" + state.Locked + "|" + string.Join(",", state.Path.Select(p => p.Code));
            var terms = await AsAsync(services, TenantId, user, s => s.GetRequiredService<ITerminologyResolver>().GetAsync(org));
            facts[name + ".terms"] = terms.Singular("supplier") + "/" + terms.Singular("contract.reference") + "/" + terms.Singular("supplier.tax.id");
            var regional = await AsAsync(services, TenantId, user, s => s.GetRequiredService<IRegionalSettingsResolver>().GetAsync(org));
            facts[name + ".regional"] = InstallationInventory.RegionalFingerprint(regional) + "|" + regional.PrimaryLocale + "|" + regional.DefaultCurrency + "|" + regional.TimeZoneId;
            var caps = await AsAsync(services, TenantId, user, s => s.GetRequiredService<ICapabilityResolver>().GetAsync(org));
            facts[name + ".capabilities"] = string.Join(",", new[] { CapabilityKeys.LegalReview, CapabilityKeys.SignatureRequests, CapabilityKeys.Variations, CapabilityKeys.Renewals }.Select(k => k + "=" + caps.IsEnabled(k)));
            var pack = await AsAsync(services, TenantId, user, async s => (await s.GetRequiredService<IConfigurationReleases>().LatestAsync("SolutionPack", org))?.ContentHash ?? "none");
            facts[name + ".packRelease"] = pack;
        }
        var releases = await AsAsync(services, TenantId, orgs[0].User, async s => await s.GetRequiredService<IConfigurationInventory>().ReleasesAsync());
        facts["releases"] = Hash(string.Join("\n", releases.Select(r => $"{r.OrganisationId}|{r.Kind}|{r.Number}|{r.StoredHash}")));
        facts["releases.count"] = releases.Count.ToString();
        var packs = await AsAsync(services, TenantId, orgs[0].User, async s => await s.GetRequiredService<IPackLoader>().ListAsync());
        facts["packs"] = string.Join(";", packs.OrderBy(p => p.PackKey, StringComparer.Ordinal).ThenBy(p => p.Version, StringComparer.Ordinal).Select(p => $"{p.PackKey}@{p.Version}:{p.PackageSha256[..12]}:{p.Trust.Status}:{p.IsUsable}"));
        var assignments = await AsAsync(services, TenantId, orgs[0].User, async s => await s.GetRequiredService<IConfigurationInventory>().AssignmentsAsync());
        facts["assignments"] = string.Join(";", assignments.OrderBy(a => a.OrganisationId).Select(a => $"{a.OrganisationId:N}:{a.PackKey}@{a.Version}:{a.PackageSha256[..12]}:{a.Status}"));
        // Documents: every stored version is there and its bytes hash to what the database says.
        var documents = new List<string>();
        await using (var scope = services.CreateAsyncScope())
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(TenantId);
            var provider = scope.ServiceProvider.GetRequiredService<IDocumentStorageProvider>();
            await foreach (var v in scope.ServiceProvider.GetRequiredService<IDocumentIntegrity>().VersionsAsync())
            {
                await using var stream = await provider.OpenReadAsync(TenantId, v.StorageKey);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
                documents.Add($"{v.DocumentId:N}:{v.StorageKey[^8..]}:{v.Sha256[..12]}:{(actual == v.Sha256 ? "ok" : "MISMATCH")}");
            }
        }
        facts["documents"] = string.Join(";", documents.Order(StringComparer.Ordinal));
        facts["documents.count"] = documents.Count.ToString();
        var queue = await AsAsync(services, TenantId, orgs[0].User, async s => await s.GetRequiredService<Ecp.Notifications.Persistence.NotificationsDbContext>().Notifications.CountAsyncForProbe());
        facts["notifications.count"] = queue.ToString();
        return facts;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
}

internal static class ProbeExtensions
{
    public static Task<int> CountAsyncForProbe<T>(this Microsoft.EntityFrameworkCore.DbSet<T> set) where T : class => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(set);
}

/// <summary>A small installation for the failure-mode tests: one tenant, one Blank organisation, one contract and two documents.</summary>
internal sealed class SmallInstallation
{
    public required LifecycleWorld Lw { get; init; }
    public required PackResolutionScenarios.PackOrg Org { get; init; }
    public required Guid Contract { get; init; }
    public Guid TenantId => Lw.W.TenantId;
    public const string FirstText = "first synthetic document";
    public const string SecondText = "second synthetic document";

    public static async Task<SmallInstallation> BuildAsync(TestApp app)
    {
        var lw = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(lw, "Small Blank Org", "SMB", TestPacks.Blank);
        var contract = await lw.W.AsAsync(org.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(org.Org, "SMB-000001", "Small agreement", org.Supplier, org.Category,
            CurrencyCode: "AUD", EstimatedValue: 500m, EndDate: DateOnly.FromDateTime(lw.W.Now.AddDays(200)))));
        await using (var s = lw.W.AsScopeless())
        {
            await s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(org.Org, contract, "Executed Contract", "First", "first.pdf", "application/pdf", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(FirstText)), "Internal"));
            await s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(org.Org, contract, "Executed Contract", "Second", "second.pdf", "application/pdf", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(SecondText)), "Internal"));
        }
        return new SmallInstallation { Lw = lw, Org = org, Contract = contract };
    }

    /// <summary>The storage keys of the tenant's document versions, in order.</summary>
    public async Task<List<string>> KeysAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(TenantId);
        var keys = new List<string>();
        await foreach (var v in scope.ServiceProvider.GetRequiredService<IDocumentIntegrity>().VersionsAsync()) keys.Add(v.StorageKey);
        return keys.Order(StringComparer.Ordinal).ToList();
    }
}
