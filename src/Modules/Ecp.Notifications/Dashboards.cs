using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Notifications;

public sealed record DashboardRow(Guid ContractId, string Reference, string Title, string Status, string Classification, string? Detail, DateOnly? Date, string Link);

public sealed record DashboardCard(string Key, string Title, string Description, int Count, IReadOnlyList<DashboardRow> Rows, string Link);

public static class DashboardKeys
{
    public const string MyContracts = "my-contracts";
    public const string AwaitingMyAction = "awaiting-my-action";
    public const string AwaitingLegal = "awaiting-legal";
    public const string PendingApproval = "pending-approval";
    public const string VariationsInProgress = "variations-in-progress";
    public const string RenewalsInProgress = "renewals-in-progress";
    public const string ExpiringSoon = "expiring-soon";
    public const string ReviewsDue = "reviews-due";
    public const string CriticalNotice = "critical-notice";
    public const string RemindedUnacknowledged = "reminded-unacknowledged";
    public static readonly IReadOnlyList<string> All =
        [MyContracts, AwaitingMyAction, AwaitingLegal, PendingApproval, VariationsInProgress, RenewalsInProgress, ExpiringSoon, ReviewsDue, CriticalNotice, RemindedUnacknowledged];
}

/// <summary>
/// Operational dashboards. Every card is built from the same secured reads as the register, so tenant, organisation, role, ownership and classification limits apply exactly as they do to a list or a
/// direct open: a dashboard never shows, counts or hints at a record its reader could not open. They are lists and counts, not analytics.
/// </summary>
public interface IDashboardService
{
    Task<IReadOnlyList<DashboardCard>> OverviewAsync(int rowsPerCard = 5, CancellationToken ct = default);
    Task<DashboardCard> CardAsync(string key, int page = 1, int pageSize = 25, CancellationToken ct = default);
}

internal sealed class DashboardService(
    ISecuredContractService contracts, ILegalReviewService legal, IRegisterAuthorisationService approvals, IVariationService variations, IRenewalService renewals, IWorkflowDefinitions workflows,
    INotificationPolicyStore policies, IOrganisationStructureService structure, IClock clock, NotificationsDbContext db) : IDashboardService
{
    private static readonly string[] LiveRoles = ["effective", "variation", "renewal", "suspended"];
    private static readonly string[] OwnerActionRoles = ["legalRework", "legalCleared", "drafting", "signature"];

    public async Task<IReadOnlyList<DashboardCard>> OverviewAsync(int rowsPerCard = 5, CancellationToken ct = default)
    {
        var cards = new List<DashboardCard>();
        foreach (var key in DashboardKeys.All) cards.Add(await CardAsync(key, 1, rowsPerCard, ct));
        return cards;
    }

    private static string Link(Guid id) => $"/Contracts/Details/{id}";

    private static DashboardRow Row(ContractListItem c, string? detail, DateOnly? date = null) => new(c.Id, c.AgreementReference, c.Title, c.Status, c.Classification, detail, date, Link(c.Id));

    /// <summary>Today in the first organisation's time zone, and the lead time that organisation's policy sets.</summary>
    private async Task<(DateOnly Today, int Lead, Guid OrgId)> ClockAsync(CancellationToken ct)
    {
        var org = (await structure.ListOrganisationsAsync(ct)).FirstOrDefault();
        var zone = TimeZones.Find(org?.TimeZoneId);
        var lead = org is null ? 90 : (await policies.CurrentAsync(org.Id, ct)).Reminders.DefaultLeadDays;
        return (TimeZones.LocalDate(clock.UtcNow, zone), lead, org?.Id ?? Guid.Empty);
    }

    private async Task<IReadOnlyList<string>> StatusesWithRolesAsync(string[] roles, CancellationToken ct)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var org in await structure.ListOrganisationsAsync(ct))
            foreach (var s in (await workflows.CurrentAsync(WorkflowKinds.ContractLifecycle, org.Id, ct)).Definition.States.Where(s => s.Role is not null && roles.Contains(s.Role))) codes.Add(s.Code);
        return codes.ToList();
    }

    public async Task<DashboardCard> CardAsync(string key, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        switch (key)
        {
            case DashboardKeys.MyContracts:
                {
                    var r = await contracts.ListAsync(new ContractFilter(OwnedByMe: true, Page: p, PageSize: size), ct);
                    return new DashboardCard(key, "My Contracts", "Agreements in categories you currently own.", r.Total, r.Items.Select(c => Row(c, c.Status)).ToList(), "/Contracts?owned=true");
                }
            case DashboardKeys.AwaitingMyAction:
                {
                    var rows = new List<DashboardRow>();
                    foreach (var item in (await legal.QueueAsync(new LegalQueueFilter(OutstandingForMe: true, PageSize: 200), ct)).Items)
                        rows.Add(new DashboardRow(item.ContractId, item.AgreementReference, item.ContractTitle, item.StatusDisplay, item.Classification, item.Outstanding, null, item.SubjectKind == SubjectKinds.Variation ? $"/Variations/Details/{item.SubjectId}" : $"/Legal/Review/{item.ReviewId}"));
                    foreach (var a in (await approvals.PendingAsync(onlyActionable: true, pageSize: 200, ct: ct)).Items.Where(a => a.CanDecide))
                        rows.Add(new DashboardRow(a.ContractId, a.AgreementReference, a.Title, "Awaiting Approval", a.Classification, $"Register authorisation waiting {a.AgeDays} day(s)" + (a.ActingAsBackup ? " (you are a backup)" : ""), null, $"/Approvals/Details/{a.RequestId}"));
                    var ownerStatuses = await StatusesWithRolesAsync(OwnerActionRoles, ct);
                    if (ownerStatuses.Count > 0)
                        foreach (var c in (await contracts.ListAsync(new ContractFilter(OwnedByMe: true, StatusIn: ownerStatuses, PageSize: 200), ct)).Items)
                            if (rows.All(r => r.ContractId != c.Id)) rows.Add(Row(c, "Needs your action"));
                    return new DashboardCard(key, "Awaiting My Action", "Reviews, approvals and agreements waiting on you.", rows.Count, rows.Skip((p - 1) * size).Take(size).ToList(), "/Dashboards?card=" + key);
                }
            case DashboardKeys.AwaitingLegal:
                {
                    var r = await legal.QueueAsync(new LegalQueueFilter(Status: LegalReviewStatus.AwaitingLegal, Page: p, PageSize: size), ct);
                    return new DashboardCard(key, "Awaiting Legal", "Submitted and waiting for Legal to begin.", r.Total, r.Items.Select(i => new DashboardRow(i.ContractId, i.AgreementReference, i.ContractTitle, i.StatusDisplay, i.Classification, $"Waiting {i.AgeDays} day(s)", null, i.SubjectKind == SubjectKinds.Variation ? $"/Variations/Details/{i.SubjectId}" : $"/Legal/Review/{i.ReviewId}")).ToList(), "/Legal");
                }
            case DashboardKeys.PendingApproval:
                {
                    var r = await approvals.PendingAsync(onlyActionable: false, p, size, ct);
                    return new DashboardCard(key, "Pending Approval", "Executed agreements waiting for register authorisation.", r.Total, r.Items.Select(a => new DashboardRow(a.ContractId, a.AgreementReference, a.Title, "Awaiting Approval", a.Classification, $"Submitted by {a.SubmittedByName}, {a.AgeDays} day(s) ago", null, $"/Approvals/Details/{a.RequestId}")).ToList(), "/Approvals");
                }
            case DashboardKeys.VariationsInProgress:
                {
                    var r = await variations.InProgressAsync(p, size, ct);
                    return new DashboardCard(key, "Variations in Progress", "Open Variations on Effective agreements.", r.Total, r.Items.Select(v => new DashboardRow(v.ContractId, v.AgreementReference, $"{v.Reference}: {v.Title}", v.StatusDisplay, "", $"Raised by {v.CreatedBy}", null, $"/Variations/Details/{v.Id}")).ToList(), "/Variations");
                }
            case DashboardKeys.RenewalsInProgress:
                {
                    var r = await renewals.InProgressAsync(p, size, ct);
                    return new DashboardCard(key, "Renewals in Progress", "Agreements being renewed.", r.Total, r.Items.Select(x => new DashboardRow(x.PredecessorContractId, x.PredecessorReference, x.PredecessorTitle, "Renewal in Progress", "", x.SuccessorReference is null ? "Successor not visible to you" : $"Successor {x.SuccessorReference} ({x.SuccessorStatus})", null, Link(x.PredecessorContractId))).ToList(), "/Renewals");
                }
            case DashboardKeys.ExpiringSoon:
                {
                    var (today, lead, _) = await ClockAsync(ct);
                    var live = await StatusesWithRolesAsync(LiveRoles, ct);
                    // Evergreen agreements have no End Date to expire, so they are not listed here (their Review Dates are).
                    var r = await contracts.ListAsync(new ContractFilter(EndsOnOrAfter: today, EndsOnOrBefore: today.AddDays(lead), StatusIn: live, ExcludeExtensionTypes: ["Evergreen"], Page: p, PageSize: size), ct);
                    return new DashboardCard(key, "Expiring Soon", $"Ending within {lead} days.", r.Total, r.Items.Select(c => Row(c, $"Ends {c.EndDate:yyyy-MM-dd}", c.EndDate)).ToList(), "/Contracts");
                }
            case DashboardKeys.ReviewsDue:
                {
                    var (today, lead, _) = await ClockAsync(ct);
                    var live = await StatusesWithRolesAsync(LiveRoles, ct);
                    var r = await contracts.ListAsync(new ContractFilter(ReviewOnOrBefore: today.AddDays(lead), StatusIn: live, Page: p, PageSize: size), ct);
                    return new DashboardCard(key, "Reviews Due", $"Review Date within {lead} days, or already passed.", r.Total, r.Items.Where(c => c.ReviewDate != null).Select(c => Row(c, c.ReviewDate < today ? $"Review overdue since {c.ReviewDate:yyyy-MM-dd}" : $"Review {c.ReviewDate:yyyy-MM-dd}", c.ReviewDate)).ToList(), "/Contracts");
                }
            case DashboardKeys.CriticalNotice:
                {
                    var live = await StatusesWithRolesAsync(LiveRoles, ct);
                    var all = (await contracts.ListAsync(new ContractFilter(CriticalNotice: true, StatusIn: live, PageSize: 200), ct)).Items;
                    var rows = all.Select(c => Row(c, c.EndDate is null && c.ReviewDate is null ? "Critical Notice (no date set)" : "Critical Notice", c.ExtensionType == "Evergreen" ? c.ReviewDate : c.EndDate)).OrderBy(r => r.Date ?? DateOnly.MaxValue).ToList();
                    return new DashboardCard(key, "Critical Notice", "Agreements flagged Critical Notice, soonest first.", rows.Count, rows.Skip((p - 1) * size).Take(size).ToList(), "/Contracts");
                }
            case DashboardKeys.RemindedUnacknowledged:
                {
                    var tracked = await db.Tracking.AsNoTracking().Where(t => t.AcknowledgedUtc == null || t.AcknowledgedUtc < t.LastReminderSentUtc).OrderBy(t => t.LastReminderSentUtc).Take(1000).ToListAsync(ct);
                    var seen = new Dictionary<Guid, ContractListItem>();
                    foreach (var chunk in tracked.Select(t => t.ContractId).Chunk(200))
                        foreach (var item in (await contracts.ListAsync(new ContractFilter(Ids: chunk, PageSize: 200), ct)).Items) seen[item.Id] = item;
                    var rows = tracked.Where(t => seen.ContainsKey(t.ContractId)).Select(t => Row(seen[t.ContractId], $"Reminded {t.LastReminderSentUtc:yyyy-MM-dd} ({t.LastReminderKind}), not acknowledged", t.LastDueDate)).ToList();
                    return new DashboardCard(key, "Reminded but Unacknowledged", "Reminders sent that no owner has acknowledged.", rows.Count, rows.Skip((p - 1) * size).Take(size).ToList(), "/Notifications");
                }
            default:
                throw new ResourceNotFoundException();
        }
    }
}
