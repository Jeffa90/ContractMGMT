using Ecp.Notifications;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Dashboards;

[Authorize]
public sealed class IndexModel(IDashboardService dashboards) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Card { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<DashboardCard> Cards { get; private set; } = [];
    public DashboardCard? Selected { get; private set; }
    public int PageSize => 25;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (!string.IsNullOrWhiteSpace(Card)) Selected = await dashboards.CardAsync(Card, Math.Max(1, PageNumber), PageSize, ct);
        else Cards = await dashboards.OverviewAsync(5, ct);
        return Page();
    });

    public int PageCount => Selected is null ? 1 : Math.Max(1, (Selected.Count + PageSize - 1) / PageSize);
}
