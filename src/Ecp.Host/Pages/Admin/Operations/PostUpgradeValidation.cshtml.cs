using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class PostUpgradeValidationModel(IUpgradeAdministration upgrades) : EcpPageModel
{
    public PostUpgradeValidation? Validation { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Validation = await upgrades.LastPostUpgradeValidationAsync(ct); return Page(); });
}
