using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Notifications.Policy;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Notifications;

/// <summary>
/// Finds contracts whose Review Date, End Date or notice deadline has reached a reminder window. For each contract date there is one window at a time: the most urgent window reached is acted on once,
/// the larger ones it covers are marked so they never fire later, and a changed date makes new windows. The recipients are whoever owns the contract's category when the scan runs.
/// </summary>
internal sealed class ReminderScanner(
    NotificationsDbContext db, IUnitOfWork uow, ITenantContext tenant, IClock clock, IContractLifecyclePort contracts, IWorkflowDefinitions workflows, INotificationPolicyStore policies,
    NotificationRenderer renderer, RecipientResolver resolver, NotificationWriter writer, IOrganisationStructureService structure, IClassificationScaleProvider scales, IAccessDirectory directory,
    ActorContext actor) : IReminderScanner
{
    private static readonly string[] LiveRoles = ["effective", "variation", "renewal", "suspended"];

    private sealed record Due(string Kind, DateOnly Date, IReadOnlyList<int> Windows);

    public async Task<ReminderScanResult> ScanAsync(CancellationToken ct = default)
    {
        int scanned = 0, sent = 0, created = 0, suppressed = 0, skipped = 0;
        var candidates = await contracts.ReminderCandidatesAsync(ct);
        var byOrg = candidates.GroupBy(c => c.OrganisationId);
        foreach (var group in byOrg)
        {
            var org = await structure.GetOrganisationAsync(group.Key, ct);
            var zone = TimeZones.Find(org?.TimeZoneId);
            var today = TimeZones.LocalDate(clock.UtcNow, zone);
            var policy = await policies.CurrentAsync(group.Key, ct);
            var def = (await workflows.CurrentAsync(WorkflowKinds.ContractLifecycle, group.Key, ct)).Definition;
            var liveStatuses = def.States.Where(s => s.Role is not null && LiveRoles.Contains(s.Role)).Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
            var scale = await scales.GetAsync(ct);
            foreach (var c in group)
            {
                if (!liveStatuses.Contains(c.Status)) continue;
                scanned++;
                foreach (var due in DueDates(c, policy))
                {
                    var r = await ScanOneAsync(c, due, today, policy, scale, ct);
                    sent += r.Sent; created += r.Created; suppressed += r.Suppressed; skipped += r.Skipped;
                }
            }
        }
        return new ReminderScanResult(scanned, sent, created, suppressed, skipped);
    }

    /// <summary>The dates a contract is reminded about and the windows for each, from its own lead time and the policy. Evergreen contracts are never reminded about an End Date.</summary>
    private static IEnumerable<Due> DueDates(ReminderCandidate c, NotificationPolicy policy)
    {
        var r = policy.Reminders;
        var evergreen = string.Equals(c.ExtensionType, "Evergreen", StringComparison.OrdinalIgnoreCase);
        var lead = c.ReminderLeadDays ?? r.DefaultLeadDays;
        // A Critical Notice contract is reminded earlier: its lead time is lengthened by the policy's extra days.
        var effectiveLead = lead + (c.CriticalNotice && r.CriticalNotice.Enabled ? r.CriticalNotice.EarlierLeadDays : 0);
        var windows = r.AllowedLeadDays.Where(d => d <= effectiveLead).Append(effectiveLead).Distinct().OrderByDescending(d => d).ToList();

        if (r.ReviewDate && c.ReviewDate is { } review && (!evergreen || r.Evergreen.KeepReviewDate)) yield return new Due("reviewDate", review, windows);
        if (r.EndDate && c.EndDate is { } end && !(evergreen && r.Evergreen.SuppressEndDate)) yield return new Due("endDate", end, windows);
        if (c.CriticalNotice && r.CriticalNotice.Enabled && c.NoticePeriodDays is { } notice)
        {
            // The notice deadline is the End Date less the notice period (for an Evergreen contract, the Review Date less it).
            var basis = evergreen ? c.ReviewDate : c.EndDate;
            if (basis is { } b) yield return new Due("criticalNotice", b.AddDays(-notice), r.CriticalNotice.OffsetsDays.OrderByDescending(d => d).ToList());
        }
    }

    private async Task<(int Sent, int Created, int Suppressed, int Skipped)> ScanOneAsync(ReminderCandidate c, Due due, DateOnly today, NotificationPolicy policy, ClassificationScale scale, CancellationToken ct)
    {
        if (due.Date < today) return (0, 0, 0, 0); // already past: the contract needs ending or renewing, not a countdown
        var reached = due.Windows.Where(w => today >= due.Date.AddDays(-w)).OrderBy(w => w).ToList();
        if (reached.Count == 0) return (0, 0, 0, 0);
        var window = reached[0]; // the most urgent window reached
        var existing = await db.ReminderWindows.Where(w => w.ContractId == c.ContractId && w.Kind == due.Kind && w.DueDate == due.Date).ToListAsync(ct);
        if (existing.Any(w => w.WindowDays == window && (w.SentUtc != null || w.Suppressed))) return (0, 0, 0, 0);

        var rule = policy.RuleFor("reminder." + due.Kind);
        if (rule is null || !policy.Templates.TryGetValue(rule.Template, out var template)) return (0, 0, 0, 0);
        var ctx = new RecipientContext(c.OrganisationId, c.ContractId, c.Classification, null, null, null, null, null, null);
        var recipients = await resolver.ResolveAsync(rule.Recipients, ctx, filterByAccess: true, ct);
        if (recipients.Count == 0)
        {
            // Nobody currently owns the category (or could open the record): fall back to the organisation's Procurement Administrators so a reminder is not lost; if there are none, try again next scan.
            recipients = await resolver.FilterByReadAccessAsync(await directory.HoldersOfRoleAsync(RoleCodes.ProcurementAdministrator, c.OrganisationId, ct), c.ContractId, ct);
            if (recipients.Count == 0) return (0, 0, 0, 1);
        }

        var days = due.Date.DayNumber - today.DayNumber;
        var link = $"/Contracts/Details?id={c.ContractId}";
        var (subject, body) = renderer.Render(policy, template, new RenderContext(c.AgreementReference, c.Title, c.Classification, "The system", link, due.Date.ToString("yyyy-MM-dd"), days, window), scale);
        var correlation = actor.CorrelationId;
        var created = 0;
        var wasSuppressed = 0;
        try
        {
            await uow.ExecuteAsync(async cx =>
            {
                var now = clock.UtcNow;
                // Larger windows covered by this one never fire later.
                foreach (var larger in due.Windows.Where(w => w > window))
                {
                    var row = existing.FirstOrDefault(w => w.WindowDays == larger);
                    if (row is null) { row = ReminderWindow.Open(tenant.RequireTenantId(), c.OrganisationId, c.ContractId, due.Kind, larger, due.Date, now); db.ReminderWindows.Add(row); }
                    if (row.SentUtc is null && !row.Suppressed) { row.Suppress(); wasSuppressed++; }
                }
                var current = existing.FirstOrDefault(w => w.WindowDays == window);
                if (current is null) { current = ReminderWindow.Open(tenant.RequireTenantId(), c.OrganisationId, c.ContractId, due.Kind, window, due.Date, now); db.ReminderWindows.Add(current); }
                foreach (var person in recipients)
                {
                    var key = $"rem:{c.ContractId:N}:{due.Kind}:{window}:{due.Date:yyyyMMdd}:{person:N}";
                    var draft = new NotificationDraft(c.OrganisationId, person, "reminder." + due.Kind, "Reminder", null, c.ContractId, SubjectKinds.Contract, c.ContractId, c.Classification, subject, body, link, rule, true, key, correlation, due.Kind, window);
                    if (await writer.AddAsync(draft, cx) is not null) created++;
                }
                current.Sent(recipients.Count, now);
                var tracking = await db.Tracking.FirstOrDefaultAsync(t => t.ContractId == c.ContractId, cx);
                var escalate = policy.Reminders.EscalateUnacknowledgedAfterDays > 0 ? policy.Reminders.EscalateUnacknowledgedAfterDays : (int?)null;
                if (tracking is null) db.Tracking.Add(ReminderTracking.Start(tenant.RequireTenantId(), c.OrganisationId, c.ContractId, c.Classification, due.Kind, window, due.Date, now, escalate));
                else tracking.Reminded(c.Classification, due.Kind, window, due.Date, now, escalate);
                await db.SaveChangesAsync(cx);
            }, ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return (0, 0, 0, 0); // a concurrent scan handled this window first; its notifications stand
        }
        return (1, created, wasSuppressed, 0);
    }
}
