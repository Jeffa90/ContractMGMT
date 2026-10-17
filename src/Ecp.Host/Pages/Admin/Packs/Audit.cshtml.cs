using Ecp.Configuration;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

[Authorize(Policy = "perm:packs.audit.read")]
public sealed class AuditModel(IPackManager packs, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public PagedResult<PackAuditItem> Items { get; private set; } = new([], 0, 1, 50);

    public Task<IActionResult> OnGetAsync(int? page, CancellationToken ct) => RunAsync(async () =>
    {
        Items = await packs.AuditAsync(page ?? 1, ct);
        return Page();
    });
}
