using System.Text;
using System.Text.Json;
using Ecp.Audit;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Notifications;

/// <summary>What a message may say. The contract's reference and title are shown only below the policy's classification thresholds; above them the message says only that a restricted record needs attention.</summary>
internal sealed record RenderContext(string Reference, string Title, string Classification, string ActorName, string Link, DateOnly? DueOn = null, int? DaysRemaining = null, int? WindowDays = null, string? StepName = null,
    DateTime? NowUtc = null, decimal? Amount = null, string? CurrencyCode = null, LocaleFormatter? Formatter = null);

internal sealed class NotificationRenderer
{
    public const string WithheldTitle = "(title withheld)";

    /// <summary>
    /// Renders a message. The template's <c>[[term]]</c> tokens become the organisation's own words first (the template is the organisation's published configuration, so this is safe); only then are the <c>{{placeholders}}</c>
    /// filled from record data, so a title or name that happens to contain brackets is never treated as a token.
    /// </summary>
    public (string Subject, string Body) Render(NotificationPolicy policy, MessageTemplate template, RenderContext c, ClassificationScale scale, TermSet terms)
    {
        var rank = scale.Rank(c.Classification);
        var everything = scale.IsKnown(policy.Content.WithholdEverythingFromLevel) && rank >= scale.Rank(policy.Content.WithholdEverythingFromLevel);
        var titleWithheld = scale.IsKnown(policy.Content.WithholdTitleFromLevel) && rank >= scale.Rank(policy.Content.WithholdTitleFromLevel);
        if (everything)
            return (terms.Expand("Action needed on a restricted record"), terms.Expand($"A restricted record needs your attention. Sign in to open it: ") + Clean(c.Link, 300));

        // The organisation's own formats (docs/REGIONAL_SETTINGS.md); with none decided the invariant formatter prints ISO dates and UTC, which is machine-safe and assumes no country.
        var fmt = c.Formatter ?? LocaleFormatter.Invariant;
        string Sub(string text) => NotificationPolicyParser.PlaceholderPattern().Replace(text, m => m.Groups[1].Value switch
        {
            "contract.reference" => Clean(c.Reference, 100),
            "contract.title" => titleWithheld ? WithheldTitle : Clean(c.Title, 200),
            "actor.name" => Clean(c.ActorName, 100),
            "link" => Clean(c.Link, 300),
            "due.date" => c.DueOn is { } due ? fmt.Date(due) : "",
            "due.date.iso" => c.DueOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            "now.date" => c.NowUtc is { } nowD ? fmt.Date(nowD) : "",
            "now.datetime" => c.NowUtc is { } nowT ? fmt.DateTime(nowT) : "",
            "now.utc" => c.NowUtc is { } nowU ? DateTime.SpecifyKind(nowU, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : "",
            "contract.value" => c.Amount is { } amount && !string.IsNullOrWhiteSpace(c.CurrencyCode) ? fmt.MoneyWithCode(amount, c.CurrencyCode) : "",
            "contract.currency" => Clean(c.CurrencyCode, 3),
            "days.remaining" => c.DaysRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            "window.days" => c.WindowDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            "step.name" => Clean(c.StepName ?? "", 100),
            _ => ""
        });
        return (Clean(Sub(terms.Expand(template.Subject)), 300), Clean(Sub(terms.Expand(template.Body)), 3000, keepNewlines: true));
    }

    /// <summary>Plain text only: control characters (which could split a header) are removed, markup characters are left as text, and the value is bounded.</summary>
    public static string Clean(string? value, int max, bool keepNewlines = false)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var ch in value)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(ch)) { if (keepNewlines && ch == '\n') sb.Append('\n'); else sb.Append(' '); }
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}

internal sealed record RecipientContext(Guid OrganisationId, Guid? ContractId, string Classification, Guid? ActorUserId, Guid? SubmitterUserId, Guid? AssignedLegalUserId, Guid? AffectedUserId, string? ApproverGroup, string? Side);

/// <summary>Works out who a rule means at the moment of sending, from current assignments, and keeps only people who could open the record themselves.</summary>
internal sealed class RecipientResolver(IContractLifecyclePort contracts, IAccessDirectory directory, IAccessService access, ISecuredContractService securedContracts, IWorkflowDefinitions workflows)
{
    public async Task<IReadOnlyList<Guid>> ResolveAsync(IReadOnlyList<string> rules, RecipientContext c, bool filterByAccess, CancellationToken ct)
    {
        var people = new List<Guid>();
        foreach (var rule in rules)
            switch (rule)
            {
                case "CategoryOwners": if (c.ContractId is { } id) people.AddRange(await contracts.CurrentOwnersAsync(id, ct)); break;
                case "Submitter": if (c.SubmitterUserId is { } s) people.Add(s); break;
                case "Actor": if (c.ActorUserId is { } a) people.Add(a); break;
                case "AffectedUser": if (c.AffectedUserId is { } u) people.Add(u); break;
                case "AssignedLegal": if (c.AssignedLegalUserId is { } l) people.Add(l); break;
                // The holders of the organisation-level administrator role. Which role that is comes from the role catalogue (portability finding P-02b), not from any policy.
                case "OrganisationAdministrators": people.AddRange(await directory.HoldersOfRoleAsync(RoleCodes.ProcurementAdministrator, c.OrganisationId, ct)); break;
                case "LegalPool": people.AddRange(await directory.HoldersOfRoleAsync(RoleCodes.Legal, c.OrganisationId, ct)); break;
                case "ApproverGroup":
                    {
                        var group = c.ApproverGroup ?? (await workflows.CurrentAsync(WorkflowKinds.ContractLifecycle, c.OrganisationId, ct)).Definition.Settings.Approval.ApproverGroupKey;
                        people.AddRange((await directory.ApproversAsync(group, ct)).Select(x => x.UserId));
                        break;
                    }
                case "Counterpart":
                    if (string.Equals(c.Side, "Legal", StringComparison.Ordinal))
                    {
                        if (c.ContractId is { } cid) people.AddRange(await contracts.CurrentOwnersAsync(cid, ct));
                        if (c.SubmitterUserId is { } sub) people.Add(sub);
                    }
                    else if (c.AssignedLegalUserId is { } al) people.Add(al);
                    else people.AddRange(await directory.HoldersOfRoleAsync(RoleCodes.Legal, c.OrganisationId, ct));
                    break;
            }
        // The person who acted is not told about their own action unless a rule names them.
        var distinct = people.Where(p => p != Guid.Empty).Distinct().Where(p => c.ActorUserId != p || rules.Contains("Actor")).ToList();
        if (!filterByAccess || c.ContractId is not { } contractId) return distinct;
        return await FilterByReadAccessAsync(distinct, contractId, ct);
    }

    /// <summary>A notification that names a record goes only to people who could open that record at its classification, judged now.</summary>
    public async Task<IReadOnlyList<Guid>> FilterByReadAccessAsync(IEnumerable<Guid> people, Guid contractId, CancellationToken ct)
    {
        var descriptor = await securedContracts.DescribeAsync(contractId, ct);
        var allowed = new List<Guid>();
        foreach (var person in people.Distinct())
        {
            var subject = await directory.LoadSubjectAsync(person, ct);
            if (subject is null) continue;
            if ((await access.SimulateAsync(subject, "contracts.read", descriptor, ct)).Allowed) allowed.Add(person);
        }
        return allowed;
    }
}

internal sealed record NotificationDraft(Guid OrganisationId, Guid RecipientUserId, string EventType, string Kind, Guid? SourceEventId, Guid? ContractId, string? SubjectKind, Guid? SubjectId, string Classification,
    string Title, string Body, string? LinkPath, EventRule Rule, bool RequiresAcknowledgement, string DedupeKey, string CorrelationId, string? ReminderKind = null, int? WindowDays = null);

/// <summary>Stores a notification and its deliveries exactly once: the dedupe key is unique, so a retry, a restart or a repeated scan finds the first and creates nothing.</summary>
internal sealed class NotificationWriter(NotificationsDbContext db, ITenantContext tenant, IClock clock, IOptions<NotificationsOptions> options)
{
    /// <summary>Adds the notification when its key is new. Returns the notification, or null if one with that key already exists. The caller saves.</summary>
    public async Task<Notification?> AddAsync(NotificationDraft d, CancellationToken ct)
    {
        if (await db.Notifications.AnyAsync(n => n.DedupeKey == d.DedupeKey, ct)) return null;
        if (db.ChangeTracker.Entries<Notification>().Any(e => e.State == EntityState.Added && e.Entity.DedupeKey == d.DedupeKey)) return null;
        var now = clock.UtcNow;
        var n = Notification.Create(tenant.RequireTenantId(), d.OrganisationId, d.RecipientUserId, d.EventType, d.Kind, d.SourceEventId, d.ContractId, d.SubjectKind, d.SubjectId, d.Classification, d.Title, d.Body,
            d.LinkPath, d.Rule.Mandatory, d.RequiresAcknowledgement, d.DedupeKey, d.CorrelationId, d.ReminderKind, d.WindowDays, now);
        db.Notifications.Add(n);
        // The inbox is always written: mandatory messages and the in-app record never depend on email being up.
        db.Deliveries.Add(NotificationDelivery.Create(n.TenantId, n.Id, d.RecipientUserId, DeliveryChannel.InApp, "inbox", d.CorrelationId, now));
        if (d.Rule.Channels.Contains("Email")) db.Deliveries.Add(NotificationDelivery.Create(n.TenantId, n.Id, d.RecipientUserId, DeliveryChannel.Email, options.Value.EmailProvider, d.CorrelationId, now));
        return n;
    }
}

/// <summary>
/// Turns what happened into messages. Lifecycle events come from the lifecycle's outbox (written in the same transaction as the change); ownership changes come from the audit trail, read from a stored
/// position. Both are handled at least once and written exactly once.
/// </summary>
internal sealed class NotificationDispatcher(
    NotificationsDbContext db, IUnitOfWork uow, ITenantContext tenant, ILifecycleOutbox outbox, INotificationPolicyStore policies, NotificationRenderer renderer, RecipientResolver resolver,
    NotificationWriter writer, IContractLifecyclePort contracts, IAccessDirectory directory, IClassificationScaleProvider scales, IAuditEventSource auditSource, IOptions<NotificationsOptions> options,
    Ecp.Configuration.ITerminologyResolver terminology, Ecp.Configuration.IRegionalSettingsResolver regional, IClock clock) : INotificationDispatcher
{
    private static readonly string[] OwnershipActions = ["category.ownership_assigned", "category.ownership_ended"];
    private const string AuditSource = "audit-ownership";

    public async Task<int> DispatchAsync(CancellationToken ct = default)
    {
        var made = 0;
        foreach (var e in await outbox.PendingAsync(options.Value.BatchSize, ct))
        {
            try
            {
                made += await uow.ExecuteAsync(async c =>
                {
                    var count = await HandleLifecycleEventAsync(e, c);
                    await outbox.MarkDispatchedAsync(e.Id, c);
                    return count;
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The event stays in the outbox with its failure noted and is tried again; after repeated failures it is parked where an administrator can see it.
                await outbox.MarkFailedAsync(e.Id, ex.Message, ct);
            }
        }
        made += await DispatchOwnershipChangesAsync(ct);
        made += await DispatchScanAlertsAsync(ct);
        return made;
    }

    private async Task<int> HandleLifecycleEventAsync(LifecycleEventItem e, CancellationToken ct)
    {
        var payload = JsonDocument.Parse(e.PayloadJson).RootElement;
        var type = e.EventType;
        // A step that flags a contract for disposition is a disposition request; other plain steps have no message of their own.
        if (type == LifecycleEventTypes.Transitioned)
        {
            if (payload.TryGetProperty("role", out var role) && role.GetString() == "flag") type = LifecycleEventTypes.DispositionRequested;
            else return 0;
        }
        var policy = await policies.CurrentAsync(e.OrganisationId, ct);
        var rule = policy.RuleFor(type);
        if (rule is null || !policy.Templates.TryGetValue(rule.Template, out var template)) return 0;

        Guid? Guid_(string name) => payload.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var g) ? g : null;
        var facts = await contracts.FindAsync(e.ContractId, ct);
        if (facts is null) return 0;
        var actorId = Guid_("actorUserId");
        var ctx = new RecipientContext(e.OrganisationId, e.ContractId, e.Classification, actorId, Guid_("submitterUserId"), Guid_("assignedToUserId") ?? Guid_("assignedTo"), null,
            payload.TryGetProperty("group", out var g) ? g.GetString() : null, payload.TryGetProperty("side", out var side) ? side.GetString() : null);
        var recipients = await resolver.ResolveAsync(rule.Recipients, ctx, filterByAccess: true, ct);
        if (recipients.Count == 0) return 0;

        var scale = await scales.GetAsync(ct);
        var actorName = actorId is { } a ? await directory.DisplayNameAsync(a, ct) ?? "Someone" : "The system";
        var link = type.StartsWith("approval.", StringComparison.Ordinal) && Guid_("requestId") is { } req ? $"/Approvals/Details/{req}"
            : type.StartsWith("legal.", StringComparison.Ordinal) && Guid_("reviewId") is { } rv ? $"/Legal/Review/{rv}"
            : e.SubjectKind == SubjectKinds.Variation ? $"/Variations/Details/{e.SubjectId}"
            : $"/Contracts/Details/{e.ContractId}";
        var (subject, body) = renderer.Render(policy, template, new RenderContext(facts.AgreementReference, facts.Title, e.Classification, actorName, link, NowUtc: clock.UtcNow, Amount: facts.EstimatedValue, CurrencyCode: facts.CurrencyCode, Formatter: await regional.FormatterAsync(e.OrganisationId, ct)), scale, await terminology.GetAsync(e.OrganisationId, ct: ct));

        var made = 0;
        foreach (var person in recipients)
        {
            var draft = new NotificationDraft(e.OrganisationId, person, type, "Event", e.Id, e.ContractId, e.SubjectKind, e.SubjectId, e.Classification, subject, body, link, rule, false, $"ev:{e.Id:N}:{person:N}", e.CorrelationId);
            if (await writer.AddAsync(draft, ct) is not null) made++;
        }
        await db.SaveChangesAsync(ct);
        return made;
    }

    private async Task<int> DispatchOwnershipChangesAsync(CancellationToken ct)
    {
        var cursor = await db.Cursors.FirstOrDefaultAsync(x => x.Source == AuditSource, ct);
        long last = cursor?.LastSequence ?? 0;
        // First run: start from the present, so history before notifications existed is not replayed as news.
        if (cursor is null)
        {
            var newest = await auditSource.Events.Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;
            await uow.ExecuteAsync(async c => { db.Cursors.Add(DispatchCursor.Start(tenant.RequireTenantId(), AuditSource, newest)); await db.SaveChangesAsync(c); }, ct);
            return 0;
        }
        var rows = await auditSource.Events.Where(x => x.Sequence > last && OwnershipActions.Contains(x.Action)).OrderBy(x => x.Sequence).Take(options.Value.BatchSize).ToListAsync(ct);
        var made = 0;
        foreach (var row in rows)
        {
            made += await uow.ExecuteAsync(async c =>
            {
                var count = await HandleOwnershipAsync(row.Action, row.Sequence, row.OrganisationId, row.NewValuesJson, row.CorrelationId, c);
                var tracked = await db.Cursors.FirstAsync(x => x.Source == AuditSource, c);
                tracked.Advance(row.Sequence);
                await db.SaveChangesAsync(c);
                return count;
            }, ct);
        }
        return made;
    }

    private async Task<int> HandleOwnershipAsync(string action, long sequence, Guid? organisationId, string? valuesJson, string correlationId, CancellationToken ct)
    {
        if (organisationId is not { } org || valuesJson is null) return 0;
        using var doc = JsonDocument.Parse(valuesJson);
        if (!doc.RootElement.TryGetProperty("userId", out var u) || !Guid.TryParse(u.GetString(), out var userId)) return 0;
        var type = action == "category.ownership_assigned" ? "ownership.assigned" : "ownership.ended";
        var policy = await policies.CurrentAsync(org, ct);
        var rule = policy.RuleFor(type);
        if (rule is null || !policy.Templates.TryGetValue(rule.Template, out var template)) return 0;
        var scale = await scales.GetAsync(ct);
        var link = "/Dashboards";
        var (subject, body) = renderer.Render(policy, template, new RenderContext("", "", "Internal", "", link, NowUtc: clock.UtcNow, Formatter: await regional.FormatterAsync(org, ct)), scale, await terminology.GetAsync(org, ct: ct));
        var draft = new NotificationDraft(org, userId, type, "Event", null, null, null, null, "Internal", subject, body, link, rule, false, $"own:{sequence}:{userId:N}", correlationId);
        var made = await writer.AddAsync(draft, ct) is not null ? 1 : 0;
        await db.SaveChangesAsync(ct);
        return made;
    }

    // ---- document scanning alerts (G-25) -------------------------------------------------------------------------------------------------------------

    private const string ScanAuditSource = "audit-scanning";
    private static readonly string[] ScanActions = ["document.quarantined", "document.scan.failed", "document.scan.retry_exhausted"];

    private static (string Type, string Title, string Body)? ScanMessage(string action) => action switch
    {
        "document.quarantined" => ("scanning.quarantined", "Document scanning: an infected file was quarantined", "A scanner found an infected file. It has been quarantined and cannot be downloaded or used. Open Document scanning to review it."),
        "document.scan.failed" => ("scanning.provider_unavailable", "Document scanning: the scanner could not complete a scan", "The malware scanner could not give a verdict, so the file is held and will be tried again. If this keeps happening the scanner may be unavailable. Open Document scanning for the detail."),
        "document.scan.retry_exhausted" => ("scanning.retry_exhausted", "Document scanning: a file could not be scanned after repeated attempts", "A file is still held because the scanner could not scan it after the allowed number of attempts. It will not be released. Open Document scanning to scan it again once the scanner is working."),
        _ => null
    };

    /// <summary>
    /// Tells the people who administer document scanning (the administrators of the organisation who hold the notifications.administer permission) when a file was quarantined, the scanner could not complete a scan, or a file ran out of retries. The message
    /// never names the file or the contract; it points to the Document scanning page, which applies the viewer's own access. Always delivered in the inbox and by email where email is a delivering provider.
    /// </summary>
    private async Task<int> DispatchScanAlertsAsync(CancellationToken ct)
    {
        var cursor = await db.Cursors.FirstOrDefaultAsync(x => x.Source == ScanAuditSource, ct);
        if (cursor is null)
        {
            var newest = await auditSource.Events.Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;
            await uow.ExecuteAsync(async c => { db.Cursors.Add(DispatchCursor.Start(tenant.RequireTenantId(), ScanAuditSource, newest)); await db.SaveChangesAsync(c); }, ct);
            return 0;
        }
        var rows = await auditSource.Events.Where(x => x.Sequence > cursor.LastSequence && ScanActions.Contains(x.Action)).OrderBy(x => x.Sequence).Take(options.Value.BatchSize).ToListAsync(ct);
        var made = 0;
        foreach (var row in rows)
        {
            made += await uow.ExecuteAsync(async c =>
            {
                var count = row.OrganisationId is { } org && ScanMessage(row.Action) is { } m ? await WriteScanAlertAsync(org, row.Action, row.Sequence, m, row.CorrelationId, c) : 0;
                var tracked = await db.Cursors.FirstAsync(x => x.Source == ScanAuditSource, c);
                tracked.Advance(row.Sequence);
                await db.SaveChangesAsync(c);
                return count;
            }, ct);
        }
        return made;
    }

    private async Task<int> WriteScanAlertAsync(Guid org, string action, long sequence, (string Type, string Title, string Body) m, string correlationId, CancellationToken ct)
    {
        var people = new List<Guid>();
        foreach (var role in new[] { RoleCodes.TenantAdministrator, RoleCodes.ProcurementAdministrator }) people.AddRange(await directory.HoldersOfRoleAsync(role, org, ct));
        var rule = new EventRule(m.Type, ["Operational"], "scanning", ["InApp", "Email"], true);
        var made = 0;
        // A scanner outage produces one failure per file; the same day's failures for an organisation make one message per person.
        var key = action == "document.scan.failed" ? $"scan-failed:{org:N}:{clock.UtcNow:yyyyMMdd}" : $"scan:{sequence}";
        foreach (var person in people.Where(p => p != Guid.Empty).Distinct())
        {
            var draft = new NotificationDraft(org, person, m.Type, "Event", null, null, null, null, "Internal", m.Title, m.Body, "/Admin/Documents/Scanning", rule, false, $"{key}:{person:N}", correlationId);
            if (await writer.AddAsync(draft, ct) is not null) made++;
        }
        await db.SaveChangesAsync(ct);
        return made;
    }
}
