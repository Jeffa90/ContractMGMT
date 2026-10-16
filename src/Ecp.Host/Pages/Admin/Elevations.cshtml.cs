using Ecp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

/// <summary>The tenant's own administrators see every support and emergency access to their tenant, while active and afterwards, and what was done under each (IDN-03).</summary>
[Authorize(Policy = "perm:audit.read_admin")]
public sealed class ElevationsModel(IElevationService elevations) : EcpPageModel
{
    public IReadOnlyList<ElevationDto> Items { get; private set; } = [];
    public ElevationDto? Selected { get; private set; }
    public IReadOnlyList<ElevationAction> Actions { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct) => RunAsync(async () =>
    {
        Items = await elevations.ListForCurrentTenantAsync(activeOnly: false, ct);
        if (id is { } selected)
        {
            Selected = Items.FirstOrDefault(i => i.Id == selected);
            if (Selected is null) return NotFound();
            Actions = await elevations.ActionsAsync(selected, ct);
        }
        return Page();
    });

    public Task<IActionResult> OnPostEndAsync(Guid id, string reason, CancellationToken ct) => RunAsync(async () =>
    {
        await elevations.EndAsync((await elevations.ListForCurrentTenantAsync(false, ct)).First(e => e.Id == id).TenantId, id, reason, ct);
        Flash("Access ended.");
        return RedirectToPage();
    });
}
