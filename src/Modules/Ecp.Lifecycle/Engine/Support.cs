using System.Text.Json;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Identity;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

internal static class AuditJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Of(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>The events the lifecycle publishes. Notifications and reporting consume them; they are identifiers and codes, not free text.</summary>
public static class LifecycleEventTypes
{
    public const string Transitioned = "contract.transitioned";
    public const string LegalSubmitted = "legal.submitted";
    public const string LegalAssigned = "legal.assigned";
    public const string LegalStarted = "legal.started";
    public const string LegalReturned = "legal.returned";
    public const string LegalResubmitted = "legal.resubmitted";
    public const string LegalCleared = "legal.cleared";
    public const string LegalWithdrawn = "legal.withdrawn";
    public const string LegalCommented = "legal.commented";
    public const string ApprovalSubmitted = "approval.submitted";
    public const string ApprovalApproved = "approval.approved";
    public const string ApprovalRejected = "approval.rejected";
    public const string ContractRegistered = "contract.registered";
    public const string VariationRaised = "variation.raised";
    public const string VariationApplied = "variation.applied";
    public const string VariationRejected = "variation.rejected";
    public const string VariationCancelled = "variation.cancelled";
    public const string RenewalStarted = "renewal.started";
    public const string RenewalCompleted = "renewal.completed";
    public const string RenewalCancelled = "renewal.cancelled";
    public const string DispositionRequested = "disposition.requested";
    public const string UnlockOpened = "unlock.opened";
    public const string UnlockClosed = "unlock.closed";
    public const string ExecutionChanged = "execution.changed";
}

/// <summary>Writes outbound events into the lifecycle's own table in the caller's transaction. The caller saves. Every payload carries the acting person's id so recipients can be resolved without reading lifecycle tables.</summary>
internal sealed class LifecycleEvents(LifecycleDbContext db, ITenantContext tenant, IClock clock, ActorContext actor, CurrentUser currentUser)
{
    public void Raise(string eventType, SubjectView subject, object payload)
        => db.Events.Add(LifecycleEvent.Raise(tenant.RequireTenantId(), subject.OrganisationId, eventType, subject.Kind, subject.Id, subject.ContractId, subject.Classification, Payload(payload),
            actor.CorrelationId, actor.ActorId ?? "system", clock.UtcNow));

    public void Raise(string eventType, Guid organisationId, string subjectKind, Guid subjectId, Guid contractId, string classification, object payload)
        => db.Events.Add(LifecycleEvent.Raise(tenant.RequireTenantId(), organisationId, eventType, subjectKind, subjectId, contractId, classification, Payload(payload), actor.CorrelationId, actor.ActorId ?? "system", clock.UtcNow));

    private string Payload(object payload)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(AuditJson.Of(payload))!.AsObject();
        if (currentUser.UserId is { } id) node["actorUserId"] = id.ToString("D");
        return node.ToJsonString();
    }
}

public sealed record LifecycleEventItem(Guid Id, Guid OrganisationId, string EventType, string SubjectKind, Guid SubjectId, Guid ContractId, string Classification, string PayloadJson,
    string CorrelationId, string Actor, DateTime CreatedUtc, int Attempts);

/// <summary>The outbox other modules read: events that committed with a lifecycle change and have not yet been handled. Each handler marks what it handled; nothing is handled twice.</summary>
public interface ILifecycleOutbox
{
    Task<IReadOnlyList<LifecycleEventItem>> PendingAsync(int max, CancellationToken ct = default);
    Task MarkDispatchedAsync(Guid eventId, CancellationToken ct = default);
    Task MarkFailedAsync(Guid eventId, string error, CancellationToken ct = default);
}

internal sealed class LifecycleOutbox(LifecycleDbContext db, IUnitOfWork uow, IClock clock) : ILifecycleOutbox
{
    public async Task<IReadOnlyList<LifecycleEventItem>> PendingAsync(int max, CancellationToken ct = default)
        => (await db.Events.AsNoTracking().Where(e => e.DispatchedUtc == null && e.DispatchAttempts < 10).OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id).Take(Math.Clamp(max, 1, 500)).ToListAsync(ct))
            .Select(e => new LifecycleEventItem(e.Id, e.OrganisationId, e.EventType, e.SubjectKind, e.SubjectId, e.ContractId, e.Classification, e.PayloadJson, e.CorrelationId, e.Actor, e.CreatedUtc, e.DispatchAttempts)).ToList();

    public Task MarkDispatchedAsync(Guid eventId, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var e = await db.Events.FirstOrDefaultAsync(x => x.Id == eventId, c);
            if (e is null || e.DispatchedUtc is not null) return;
            e.MarkDispatched(clock.UtcNow);
            await db.SaveChangesAsync(c);
        }, ct);

    public Task MarkFailedAsync(Guid eventId, string error, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var e = await db.Events.FirstOrDefaultAsync(x => x.Id == eventId, c);
            if (e is null || e.DispatchedUtc is not null) return;
            e.MarkFailed(error, clock.UtcNow);
            await db.SaveChangesAsync(c);
        }, ct);
}
