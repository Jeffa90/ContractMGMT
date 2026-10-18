using System.Text;
using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class MigrationReportModel(IUpgradeAdministration upgrades) : EcpPageModel
{
    public MigrationReport Report { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Report = await upgrades.MigrationReportAsync(ct); return Page(); });

    public Task<IActionResult> OnGetScriptAsync(string module, CancellationToken ct) => RunAsync(async () => File(Encoding.UTF8.GetBytes(await upgrades.ScriptAsync(module, ct)), "text/plain; charset=utf-8", "pending-" + new string(module.Where(char.IsLetterOrDigit).ToArray()) + ".sql"), async () => Report = await upgrades.MigrationReportAsync(ct));
}
