using System.Text.RegularExpressions;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Configuration;

public sealed record PackSourceStatus(string Directory, bool DirectoryFound, int ShippedFiles, int UsableShippedPacks, IReadOnlyList<RejectedPackFile> Rejected, IReadOnlyList<string> TrustProblems, bool UnsignedAllowed, bool Production);
public sealed record OrganisationPackFinding(Guid OrganisationId, string OrganisationName, string? PackKey, string Code, string Message);

/// <summary>What is wrong, if anything, with where packs come from and which organisations depend on one that cannot be found. Reporting only: diagnostics never change an organisation.</summary>
public interface IPackDiagnostics
{
    Task<PackSourceStatus> SourcesAsync(CancellationToken ct = default);
    /// <summary>Organisations whose pack key cannot be resolved to a usable pack, and organisations that received the starting definitions of a different pack than the one they are assigned.</summary>
    Task<IReadOnlyList<OrganisationPackFinding>> OrganisationFindingsAsync(CancellationToken ct = default);
}

internal sealed partial class PackDiagnostics(ShippedPackSource shipped, PlatformKeyResolver platform, IPackLoader loader, ConfigurationDbContext db, IOrganisationStructureService organisations,
    Microsoft.Extensions.Options.IOptions<PackOptions> options) : IPackDiagnostics
{
    [GeneratedRegex(@"^Installed the (?<pack>[a-z0-9-]+)(?: (?<version>\d+\.\d+\.\d+[^ ]*))? reference", RegexOptions.NonBacktracking)] private static partial Regex StartNote();

    public async Task<PackSourceStatus> SourcesAsync(CancellationToken ct = default)
    {
        var entries = (await loader.ListAsync(ct)).Where(e => e.IsShipped && e.IsUsable).ToList();
        return new PackSourceStatus(Path.GetFileName(shipped.Directory.TrimEnd(Path.DirectorySeparatorChar)), shipped.DirectoryFound, shipped.Packs.Count + shipped.Rejected.Count, entries.Count, shipped.Rejected,
            platform.Problems, options.Value.AllowUnsignedSyntheticPacks && !options.Value.IsProduction, options.Value.IsProduction);
    }

    public async Task<IReadOnlyList<OrganisationPackFinding>> OrganisationFindingsAsync(CancellationToken ct = default)
    {
        var findings = new List<OrganisationPackFinding>();
        foreach (var o in await organisations.ListOrganisationsAsync(ct))
        {
            if (o.SolutionPackKey is null) continue;
            if (await loader.FindAsync(o.SolutionPackKey, null, usableOnly: true, ct) is null)
                findings.Add(new OrganisationPackFinding(o.Id, o.Name, o.SolutionPackKey, "pack_unresolved",
                    $"The pack '{o.SolutionPackKey}' is not available (not installed, not trusted in this environment, retired or incompatible). Configuration already published is unaffected; the organisation cannot receive starting definitions from it."));
            var notes = await db.Releases.AsNoTracking().Where(r => r.OrganisationId == o.Id && r.Note.StartsWith("Installed the ")).Select(r => new { r.Kind, r.Number, r.Note }).ToListAsync(ct);
            foreach (var n in notes)
            {
                var m = StartNote().Match(n.Note);
                if (m.Success && m.Groups["pack"].Value != o.SolutionPackKey)
                    findings.Add(new OrganisationPackFinding(o.Id, o.Name, o.SolutionPackKey, "starting_definitions_from_other_pack",
                        $"Release {n.Number} of {n.Kind} was installed from the pack '{m.Groups["pack"].Value}', but the organisation is assigned '{o.SolutionPackKey}'. Nothing was changed. See docs/PACK_MANAGER.md for the manual options."));
            }
        }
        return findings;
    }
}
