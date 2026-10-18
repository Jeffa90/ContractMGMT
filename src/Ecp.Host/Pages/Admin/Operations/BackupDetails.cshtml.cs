using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class BackupDetailsModel(IOperationsAdministration operations) : EcpPageModel
{
    public BackupSummary Summary { get; private set; } = null!;
    public BackupManifest? Manifest { get; private set; }
    public string? ManifestProblem { get; private set; }

    public Task<IActionResult> OnGetAsync(string id, CancellationToken ct) => RunAsync(async () =>
    {
        Summary = await operations.BackupAsync(id, ct) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        try { Manifest = await operations.ManifestAsync(id, ct); }
        catch (Ecp.SharedKernel.DomainException ex) { ManifestProblem = ex.Message; }
        return Page();
    });
}
