using Ecp.Contracts;
using Ecp.Host.Navigation;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages;

/// <summary>Why a person cannot start a contract, in words they can act on.</summary>
public enum CreateBlock { None, NoContractAccess, NeedsOwnership, NoCategories, NoRole }

/// <summary>Home: the person's work first (start a contract, what is waiting, what is due), and a separate, quieter card for administrators. Nothing here decides access; it only reads what the services return.</summary>
public sealed class IndexModel(IAccessService access, IDashboardService dashboards, INotificationInbox inbox, ISecuredCategoryService categories, NavigationService navigation) : EcpPageModel
{
    /// <summary>The cards shown on Home, in order. Each exists only when the capability behind it is on and the person may see it.</summary>
    public static readonly string[] HomeCards = [DashboardKeys.AwaitingMyAction, DashboardKeys.MyContracts, DashboardKeys.ReviewsDue, DashboardKeys.ExpiringSoon];

    public bool CanReadContracts { get; private set; }
    public bool CanCreate { get; private set; }
    public CreateBlock Blocked { get; private set; }
    public bool HasAnyRole { get; private set; }
    public bool IsAdministrator { get; private set; }
    public bool CanManageUsers { get; private set; }
    public int ConfigurationReviewsVisible { get; private set; }
    public IReadOnlyList<DashboardCard> Cards { get; private set; } = [];
    public IReadOnlyList<InboxItem> Notifications { get; private set; } = [];
    public int Unread { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (access.CurrentUserId is not { } me) return Challenge();
        var nav = await navigation.GetAsync(ct);
        IsAdministrator = nav.HasAdministration;
        CanManageUsers = await access.HoldsAnywhereAsync("users.manage", ct);
        CanReadContracts = await access.HoldsAnywhereAsync("contracts.read", ct);
        var subject = await access.GetSubjectAsync(ct);
        HasAnyRole = subject is not null && subject.Assignments.Any();
        (CanCreate, Blocked) = await CanCreateAsync(me, ct);

        var cards = new List<DashboardCard>();
        if (CanReadContracts || HasAnyRole)
            try { foreach (var c in await dashboards.OverviewAsync(5, ct)) if (HomeCards.Contains(c.Key)) cards.Add(c); }
            catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException or DomainException) { /* a session that is not a person of an organisation has no cards */ }
        Cards = HomeCards.Select(k => cards.FirstOrDefault(c => c.Key == k)).Where(c => c is not null).Select(c => c!).ToList();

        try
        {
            var page = await inbox.ListAsync(new InboxFilter(UnreadOnly: true, PageSize: 5));
            Notifications = page.Items.ToList(); Unread = nav.Unread;
        }
        catch (Exception) { Notifications = []; /* a session without a person (an installation administrator) has no inbox */ }
        return Page();
    }

    /// <summary>
    /// A person can start a contract only where the role they hold lets them. A person who administers contracts may do so in any category; a person who owns categories only in a category they currently own
    /// (the access service reports that as a relationship, not a permission). The page asks, so the button appears only when starting one can work.
    /// </summary>
    private async Task<(bool Can, CreateBlock Why)> CanCreateAsync(Guid me, CancellationToken ct)
    {
        var grants = await access.GrantsAsync("contracts.create", ct);
        if (grants.Count == 0) return (false, CanReadContracts ? CreateBlock.NoRole : CreateBlock.NoContractAccess);
        if (grants.Any(g => g.Relationship == GrantRelationship.None)) return (true, CreateBlock.None);
        var all = await categories.ListAsync(ct);
        if (all.Count(c => c.IsActive) == 0) return (false, CreateBlock.NoCategories);
        var owned = all.Any(c => c.IsActive && c.Owners.Any(o => o.UserId == me));
        return owned ? (true, CreateBlock.None) : (false, CreateBlock.NeedsOwnership);
    }
}
