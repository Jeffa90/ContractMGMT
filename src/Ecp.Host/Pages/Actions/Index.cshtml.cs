using Ecp.Host.Navigation;
using Ecp.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Actions;

public sealed record QueueView(string Key, string Href, string Title, string Description, int? Count);

/// <summary>My Actions: the work waiting on the person, one place instead of one menu entry per queue. The queues keep their own pages.</summary>
[Authorize]
public sealed class IndexModel(NavigationService navigation, IDashboardService dashboards) : EcpPageModel
{
    private static readonly IReadOnlyDictionary<string, string> CardOfQueue = new Dictionary<string, string>
    {
        ["legal"] = DashboardKeys.AwaitingLegal, ["approvals"] = DashboardKeys.PendingApproval,
        ["variations"] = DashboardKeys.VariationsInProgress, ["renewals"] = DashboardKeys.RenewalsInProgress,
    };

    public DashboardCard? AwaitingMe { get; private set; }
    public IReadOnlyList<QueueView> Queues { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var nav = await navigation.GetAsync(ct);
        try { AwaitingMe = await dashboards.CardAsync(DashboardKeys.AwaitingMyAction, 1, 10, ct); } catch (Exception ex) when (ex is Ecp.SharedKernel.ForbiddenException or Ecp.SharedKernel.ResourceNotFoundException) { AwaitingMe = null; }
        var views = new List<QueueView>();
        foreach (var q in nav.Queues)
        {
            int? count = null;
            if (CardOfQueue.TryGetValue(q.Key, out var card))
                try { count = (await dashboards.CardAsync(card, 1, 1, ct)).Count; } catch (Exception ex) when (ex is Ecp.SharedKernel.ForbiddenException or Ecp.SharedKernel.ResourceNotFoundException) { count = null; }
            views.Add(new QueueView(q.Key, q.Href, T(q.Title), T(q.Description), count));
        }
        Queues = views;
        return Page();
    }
}
