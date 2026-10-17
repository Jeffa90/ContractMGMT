using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>Whether the installation can find and use its shipped packs, and which organisations point at a pack that cannot be used. It reports and changes nothing.</summary>
[Authorize(Policy = "perm:packs.read")]
public sealed class ChecksModel(IPackDiagnostics diagnostics, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public PackSourceStatus? Sources { get; private set; }
    public IReadOnlyList<OrganisationPackFinding> Findings { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Sources = await diagnostics.SourcesAsync(ct);
        Findings = await diagnostics.OrganisationFindingsAsync(ct);
        return Page();
    });
}
