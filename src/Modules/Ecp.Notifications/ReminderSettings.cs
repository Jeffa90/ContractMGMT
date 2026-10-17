using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Notifications.Persistence;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Notifications;

internal sealed class ReminderSettings(NotificationsDbContext db, IContractLifecyclePort port, ISecuredContractService contracts, IAccessService access, INotificationPolicyStore policies, CurrentUser currentUser, ActorContext actor)
    : IReminderSettings
{
    public async Task<ReminderStatusInfo> StatusAsync(Guid contractId, CancellationToken ct = default)
    {
        var descriptor = await contracts.DescribeAsync(contractId, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var facts = await port.FindAsync(contractId, ct) ?? throw new ResourceNotFoundException();
        var policy = await policies.CurrentAsync(facts.OrganisationId, ct);
        var tracking = await db.Tracking.AsNoTracking().FirstOrDefaultAsync(t => t.ContractId == contractId, ct);
        var owners = await port.CurrentOwnersAsync(contractId, ct);
        var lead = facts.ReminderLeadDays ?? policy.Reminders.DefaultLeadDays;
        var evergreen = string.Equals(facts.ExtensionType, "Evergreen", StringComparison.OrdinalIgnoreCase);
        var schedule = new List<string>();
        var effectiveLead = lead + (facts.CriticalNotice && policy.Reminders.CriticalNotice.Enabled ? policy.Reminders.CriticalNotice.EarlierLeadDays : 0);
        var windows = policy.Reminders.AllowedLeadDays.Where(d => d <= effectiveLead).Append(effectiveLead).Distinct().OrderByDescending(d => d).ToList();
        if (policy.Reminders.ReviewDate && facts.ReviewDate is { } review && (!evergreen || policy.Reminders.Evergreen.KeepReviewDate))
            schedule.Add("[[field.review.date]] " + review.ToString("yyyy-MM-dd") + ": " + string.Join(", ", windows.Select(w => review.AddDays(-w).ToString("yyyy-MM-dd"))));
        if (policy.Reminders.EndDate && facts.EndDate is { } end && !(evergreen && policy.Reminders.Evergreen.SuppressEndDate))
            schedule.Add("[[field.end.date]] " + end.ToString("yyyy-MM-dd") + ": " + string.Join(", ", windows.Select(w => end.AddDays(-w).ToString("yyyy-MM-dd"))));
        if (evergreen && policy.Reminders.Evergreen.SuppressEndDate) schedule.Add("[[field.evergreen]]: no [[field.end.date]] reminders.");
        if (facts.CriticalNotice && policy.Reminders.CriticalNotice.Enabled && facts.NoticePeriodDays is { } notice && (evergreen ? facts.ReviewDate : facts.EndDate) is { } basis)
        {
            var deadline = basis.AddDays(-notice);
            schedule.Add("[[field.critical.notice]] deadline " + deadline.ToString("yyyy-MM-dd") + ": " + string.Join(", ", policy.Reminders.CriticalNotice.OffsetsDays.OrderByDescending(d => d).Select(d => deadline.AddDays(-d).ToString("yyyy-MM-dd"))));
        }
        var me = currentUser.UserId;
        return new ReminderStatusInfo(lead, facts.ReminderLeadDays is null, policy.Reminders.AllowedLeadDays, facts.NoticePeriodDays, facts.CriticalNotice, evergreen, tracking?.LastReminderSentUtc, tracking?.LastReminderKind,
            tracking?.LastWindowDays, tracking?.LastDueDate, tracking is not null && !tracking.IsAcknowledged, tracking?.AcknowledgedBy, tracking?.AcknowledgedUtc, tracking?.EscalationDueUtc,
            (await access.CheckSilentAsync("reminders.configure", descriptor, ct)).Allowed, me is { } m && owners.Contains(m) && tracking is not null && !tracking.IsAcknowledged, schedule);
    }

    public async Task SetLeadDaysAsync(Guid contractId, int? leadDays, CancellationToken ct = default)
    {
        var descriptor = await contracts.DescribeAsync(contractId, ct);
        await access.RequireAsync("reminders.configure", descriptor, ct);
        var facts = await port.FindAsync(contractId, ct) ?? throw new ResourceNotFoundException();
        var policy = await policies.CurrentAsync(facts.OrganisationId, ct);
        if (leadDays is { } d && !policy.Reminders.AllowedLeadDays.Contains(d))
            throw new DomainException("lead_not_allowed", $"The reminder lead time must be one of: {string.Join(", ", policy.Reminders.AllowedLeadDays)} days.");
        await port.SetReminderLeadAsync(contractId, leadDays, actor.ActorId ?? currentUser.UserId?.ToString() ?? "system", ct);
    }
}
