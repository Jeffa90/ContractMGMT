using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Notifications.Policy;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Notifications;

internal sealed class NotificationInbox(
    NotificationsDbContext db, IUnitOfWork uow, IAuditWriter audit, IClock clock, CurrentUser currentUser, IAccessService access, ISecuredContractService contracts, IContractLifecyclePort port,
    IAccessDirectory directory, INotificationPolicyStore policies, NotificationRenderer renderer, RecipientResolver resolver, NotificationWriter writer, IClassificationScaleProvider scales, ActorContext actor, Ecp.Configuration.ITerminologyResolver terminology, Ecp.Configuration.IRegionalSettingsResolver regional) : INotificationInbox
{
    private Guid Me => currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");

    public async Task<PagedResult<InboxItem>> ListAsync(InboxFilter f, CancellationToken ct = default)
    {
        var me = Me;
        var query = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == me);
        if (f.UnreadOnly) query = query.Where(n => n.ReadUtc == null);
        if (f.NeedsAcknowledgement) query = query.Where(n => n.RequiresAcknowledgement && n.AcknowledgedUtc == null);
        if (!string.IsNullOrWhiteSpace(f.Kind)) query = query.Where(n => n.Kind == f.Kind);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        var rows = await query.OrderByDescending(n => n.CreatedUtc).ThenByDescending(n => n.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);

        // What a message says is checked again when it is read: if the person can no longer open the record (it was reclassified, or their access changed), the message shows nothing about it.
        var canOpen = new Dictionary<Guid, bool>();
        var items = new List<InboxItem>();
        foreach (var n in rows)
        {
            var open = true;
            if (n.ContractId is { } cid)
            {
                if (!canOpen.TryGetValue(cid, out open))
                {
                    try { open = (await access.CheckSilentAsync("contracts.read", await contracts.DescribeAsync(cid, ct), ct)).Allowed; }
                    catch (DomainException) { open = false; }
                    canOpen[cid] = open;
                }
            }
            items.Add(open
                ? new InboxItem(n.Id, n.EventType, n.Kind, n.Title, n.Body, n.LinkPath, n.Classification, n.Mandatory, n.RequiresAcknowledgement, n.CreatedUtc, n.ReadUtc, n.AcknowledgedUtc, n.AcknowledgedBy, n.ContractId, n.ReminderKind, n.WindowDays)
                : new InboxItem(n.Id, n.EventType, n.Kind, "A record you can no longer open", "You no longer have access to the record this message was about.", null, "Restricted", n.Mandatory, false, n.CreatedUtc, n.ReadUtc, null, null, null, n.ReminderKind, n.WindowDays));
        }
        return new PagedResult<InboxItem>(items, total, page, size);
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        var me = Me;
        return await db.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == me && n.ReadUtc == null, ct);
    }

    public Task MarkReadAsync(Guid notificationId, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var me = Me;
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == notificationId && x.RecipientUserId == me, c) ?? throw new ResourceNotFoundException();
            n.MarkRead(clock.UtcNow);
            await db.SaveChangesAsync(c);
        }, ct);

    public Task MarkAllReadAsync(CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var me = Me;
            var now = clock.UtcNow;
            foreach (var n in await db.Notifications.Where(x => x.RecipientUserId == me && x.ReadUtc == null).ToListAsync(c)) n.MarkRead(now);
            await db.SaveChangesAsync(c);
        }, ct);

    public async Task AcknowledgeAsync(Guid notificationId, CancellationToken ct = default)
    {
        var me = Me;
        var n = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == notificationId && x.RecipientUserId == me, ct) ?? throw new ResourceNotFoundException();
        await AcknowledgeCoreAsync(n, me, ct);
    }

    public async Task AcknowledgeContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var me = Me;
        var n = await db.Notifications.AsNoTracking().Where(x => x.ContractId == contractId && x.RecipientUserId == me && x.RequiresAcknowledgement && x.AcknowledgedUtc == null)
            .OrderByDescending(x => x.CreatedUtc).FirstOrDefaultAsync(ct);
        if (n is null)
        {
            // Not addressed to this person (for example a colleague owns it now): a current owner may still acknowledge on the contract's behalf.
            var owners = await port.CurrentOwnersAsync(contractId, ct);
            if (!owners.Contains(me)) throw new ForbiddenException("Only a current owner of the contract can acknowledge its reminder.");
            n = await db.Notifications.AsNoTracking().Where(x => x.ContractId == contractId && x.RequiresAcknowledgement && x.AcknowledgedUtc == null).OrderByDescending(x => x.CreatedUtc).FirstOrDefaultAsync(ct)
                ?? throw new DomainException("nothing_to_acknowledge", "There is no reminder waiting to be acknowledged.");
        }
        await AcknowledgeCoreAsync(n, me, ct);
    }

    private async Task AcknowledgeCoreAsync(Notification source, Guid me, CancellationToken ct)
    {
        if (!source.RequiresAcknowledgement) throw new DomainException("not_acknowledgeable", "This [[notification|lower]] does not need acknowledging.");
        var name = await directory.DisplayNameAsync(me, ct) ?? me.ToString();
        await uow.ExecuteAsync(async c =>
        {
            var now = clock.UtcNow;
            // Everyone who received the same reminder sees it as done, with who acknowledged it and when.
            var siblings = await db.Notifications.Where(x => x.ContractId == source.ContractId && x.Kind == "Reminder" && x.ReminderKind == source.ReminderKind && x.WindowDays == source.WindowDays && x.AcknowledgedUtc == null).ToListAsync(c);
            foreach (var s in siblings) s.Acknowledge(name, now);
            if (source.ContractId is { } cid)
            {
                var tracking = await db.Tracking.FirstOrDefaultAsync(t => t.ContractId == cid, c);
                tracking?.Acknowledge(name, now);
            }
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("notification.reminder_acknowledged", nameof(Notification), source.Id.ToString(), OrganisationId: source.OrganisationId, Classification: source.Classification,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { contractId = source.ContractId, kind = source.ReminderKind, window = source.WindowDays, by = me })));
            await TellOtherOwnersAsync(source, me, name, c);
        }, ct);
    }

    /// <summary>The other current owners are told that someone has dealt with the reminder, so two people do not both chase it.</summary>
    private async Task TellOtherOwnersAsync(Notification source, Guid me, string name, CancellationToken ct)
    {
        if (source.ContractId is not { } cid) return;
        var policy = await policies.CurrentAsync(source.OrganisationId, ct);
        var rule = policy.RuleFor("reminder.acknowledged");
        if (rule is null || !policy.Templates.TryGetValue(rule.Template, out var template)) return;
        var facts = await port.FindAsync(cid, ct);
        if (facts is null) return;
        var recipients = await resolver.ResolveAsync(rule.Recipients, new RecipientContext(source.OrganisationId, cid, source.Classification, me, null, null, null, null, null), true, ct);
        var link = $"/Contracts/Details/{cid}";
        var (subject, body) = renderer.Render(policy, template, new RenderContext(facts.AgreementReference, facts.Title, source.Classification, name, link, NowUtc: clock.UtcNow, Formatter: await regional.FormatterAsync(source.OrganisationId, ct)), await scales.GetAsync(ct), await terminology.GetAsync(source.OrganisationId, ct: ct));
        foreach (var person in recipients)
            await writer.AddAsync(new NotificationDraft(source.OrganisationId, person, "reminder.acknowledged", "Event", null, cid, SubjectKinds.Contract, cid, source.Classification, subject, body, link, rule, false,
                $"ack:{cid:N}:{source.ReminderKind}:{source.WindowDays}:{person:N}", actor.CorrelationId), ct);
        await db.SaveChangesAsync(ct);
    }
}
