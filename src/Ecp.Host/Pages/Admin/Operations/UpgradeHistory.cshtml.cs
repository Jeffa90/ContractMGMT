using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class UpgradeHistoryModel(IUpgradeAdministration upgrades) : EcpPageModel
{
    public IReadOnlyList<UpgradeHistoryEntry> Entries { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Entries = await upgrades.HistoryAsync(ct); return Page(); });

    public Task<IActionResult> OnGetEvidenceAsync(string name, CancellationToken ct) => RunAsync(async () => File(await upgrades.EvidenceFileAsync(name, ct), "application/json", name));
}
