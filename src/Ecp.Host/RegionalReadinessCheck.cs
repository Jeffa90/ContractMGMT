using Ecp.Configuration;
using Ecp.Operations;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>
/// Whether the shipped solution packs' regional proposals are valid against the platform's catalogues (locale, currency, country, time zone available on this host, identifier rules, address fields). A pack that proposes an
/// invalid or unavailable value would start every organisation that uses it with a draft that cannot be published. Read-only. An organisation's own settings, which need a tenant, are reported on the Regional settings page
/// and in onboarding: an organisation with no locale, currency or time zone decided is not ready for Production.
/// </summary>
internal sealed class RegionalReadinessCheck(IPackLoader loader, IConfiguration configuration) : IReadinessCheck
{
    public string Id => "regional";
    private const string Description = "Solution pack regional proposals are valid";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var problems = new List<(string Pack, string Text)>();
        foreach (var entry in await loader.ListAsync(ct))
        {
            if (entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable) continue;
            var json = await loader.GetDocumentAsync(entry.PackKey, entry.Version, RegionalKinds.Name, ct);
            if (json is null) continue;
            try
            {
                foreach (var m in RegionalProfileDocument.Parse(json).Check().Where(m => m.Severity == ValidationSeverity.Error)) problems.Add((entry.PackKey, m.Message));
            }
            catch (DomainException) { problems.Add((entry.PackKey, "its regional document cannot be read")); }
        }
        if (problems.Count == 0) return new ReadinessResult(Id, Description, ReadinessStatus.Healthy, "Every shipped pack's regional proposals are valid on this host.");
        var defaultPack = configuration["Ecp:DefaultPackId"];
        var status = !string.IsNullOrWhiteSpace(defaultPack) && problems.Any(p => p.Pack == defaultPack) ? ReadinessStatus.Unhealthy : ReadinessStatus.Degraded;
        return new ReadinessResult(Id, Description, status, "Invalid regional proposals: " + string.Join("; ", problems.Take(5).Select(p => $"{p.Pack}: {p.Text}")) + ". Correct the pack's regional document, or install the missing time-zone data.");
    }
}
