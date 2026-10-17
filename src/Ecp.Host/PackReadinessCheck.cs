using Ecp.Configuration;
using Ecp.Operations;

namespace Ecp.Host;

/// <summary>
/// Whether the installation can find and use its shipped solution packs. A missing pack folder, or an installation default pack that cannot be used, is a failure; a folder with no pack usable in this
/// environment (for example unsigned packs in Production) is reported as degraded with what to do about it. It reads only; it changes nothing.
/// </summary>
internal sealed class PackReadinessCheck(IPackDiagnostics diagnostics, IPackLoader loader, IConfiguration configuration) : IReadinessCheck
{
    public string Id => "solution-packs";
    private const string Description = "Solution packs are available";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var sources = await diagnostics.SourcesAsync(ct);
        if (!sources.DirectoryFound)
            return new ReadinessResult(Id, Description, ReadinessStatus.Unhealthy, "The folder of shipped solution packs was not found. Check the deployment (Ecp:Packs:ShippedDirectory).");
        var required = configuration["Ecp:DefaultPackId"];
        if (!string.IsNullOrWhiteSpace(required) && await loader.FindAsync(required, null, usableOnly: true, ct) is null)
            return new ReadinessResult(Id, Description, ReadinessStatus.Unhealthy, $"The installation default solution pack '{required}' is not available or not trusted in this environment.");
        if (sources.UsableShippedPacks == 0)
            return new ReadinessResult(Id, Description, ReadinessStatus.Degraded, sources.Production
                ? "No shipped solution pack is trusted in Production. Packs must be signed by a trusted publisher (docs/PACK_SIGNING.md); until then new organisations can only be configured by hand."
                : "No shipped solution pack is usable in this environment.");
        if (sources.Rejected.Count > 0)
            return new ReadinessResult(Id, Description, ReadinessStatus.Degraded, $"{sources.Rejected.Count} shipped pack file(s) were not accepted. See Administration, Solution packs, Checks.");
        return new ReadinessResult(Id, Description, ReadinessStatus.Healthy, $"{sources.UsableShippedPacks} shipped solution pack(s) are available.");
    }
}
