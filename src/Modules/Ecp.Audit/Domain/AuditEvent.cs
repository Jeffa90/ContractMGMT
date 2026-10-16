using System.Globalization;
using Ecp.SharedKernel;

namespace Ecp.Audit.Domain;

/// <summary>
/// One immutable audit event. Events form a per-tenant hash chain: <c>Hash = SHA-256(canonical content | PreviousHash)</c>.
/// The table is append-only (database trigger and permissions) and events are written in the same transaction as the change.
/// </summary>
public sealed class AuditEvent : ITenantOwned
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private AuditEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? OrganisationId { get; private set; }
    public long Sequence { get; private set; }
    public DateTime OccurredUtc { get; private set; }
    public string ActorType { get; private set; } = "";
    public string? ActorId { get; private set; }
    public Guid? ElevationId { get; private set; }
    public string Action { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";
    public string Outcome { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";
    public string Source { get; private set; } = "";
    public Guid? MigrationBatchId { get; private set; }
    public Guid? ConfigReleaseId { get; private set; }
    public string? Reason { get; private set; }
    public string? OldValuesJson { get; private set; }
    public string? NewValuesJson { get; private set; }
    /// <summary>The classification of the record this event describes, when it describes one. Audit evidence is filtered by it exactly as the record itself is.</summary>
    public string? Classification { get; private set; }
    public string PreviousHash { get; private set; } = GenesisHash;
    public string Hash { get; private set; } = "";

    internal static AuditEvent Create(Guid tenantId, long sequence, DateTime occurredUtc, IActorContext actor, AuditEntry entry, string previousHash)
    {
        var e = new AuditEvent
        {
            Id = Ids.New(), TenantId = tenantId, OrganisationId = entry.OrganisationId, Sequence = sequence,
            OccurredUtc = Guard.Utc(occurredUtc, nameof(occurredUtc)), ActorType = actor.Type.ToString(), ActorId = actor.ActorId,
            ElevationId = entry.ElevationId ?? actor.ElevationId, Action = Guard.NotBlank(entry.Action, "Action", 200),
            EntityType = Guard.NotBlank(entry.EntityType, "EntityType", 200), EntityId = Guard.NotBlank(entry.EntityId, "EntityId", 200),
            Outcome = entry.Outcome.ToString(), CorrelationId = actor.CorrelationId, Source = actor.Source,
            MigrationBatchId = entry.MigrationBatchId, ConfigReleaseId = entry.ConfigReleaseId, Reason = entry.Reason,
            OldValuesJson = entry.OldValuesJson, NewValuesJson = entry.NewValuesJson, Classification = entry.Classification, PreviousHash = previousHash
        };
        e.Hash = AuditHasher.Compute(e, previousHash);
        return e;
    }
}

public static class AuditHasher
{
    /// <summary>Length-prefixed canonical form: unambiguous without escaping; null is "-".</summary>
    public static string Canonical(AuditEvent e)
    {
        static string F(string? s) => s is null ? "-" : $"{s.Length}:{s}";
        static string G(Guid? g) => F(g?.ToString("N"));
        // The classification joins the canonical form only when present, so events written before it existed keep the hash they were sealed with.
        var tail = e.Classification is null ? "" : "|" + F(e.Classification);
        return string.Join('|',
            F(e.TenantId.ToString("N")), F(e.Sequence.ToString(CultureInfo.InvariantCulture)),
            F(e.OccurredUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
            F(e.ActorType), F(e.ActorId), G(e.ElevationId), F(e.Action), F(e.EntityType), F(e.EntityId), F(e.Outcome),
            F(e.CorrelationId), F(e.Source), G(e.OrganisationId), G(e.MigrationBatchId), G(e.ConfigReleaseId),
            F(e.Reason), F(e.OldValuesJson), F(e.NewValuesJson)) + tail;
    }

    public static string Compute(AuditEvent e, string previousHash) => Hashing.Sha256Hex(Canonical(e) + "|" + previousHash);
}

public sealed record AuditChainVerification(bool IsValid, long EventsChecked, long? FirstBrokenSequence, string? Reason);

public static class AuditChain
{
    /// <summary>Verifies a tenant's events in sequence order: gap-free sequence, linked hashes, recomputed hashes.</summary>
    public static AuditChainVerification Verify(IEnumerable<AuditEvent> eventsInOrder)
    {
        long expected = 1; long checkedCount = 0; var previous = AuditEvent.GenesisHash;
        foreach (var e in eventsInOrder)
        {
            if (e.Sequence != expected) return new(false, checkedCount, expected, $"Sequence gap: expected {expected}, found {e.Sequence}.");
            if (e.PreviousHash != previous) return new(false, checkedCount, e.Sequence, "Previous hash does not match the preceding event.");
            if (AuditHasher.Compute(e, e.PreviousHash) != e.Hash) return new(false, checkedCount, e.Sequence, "Event content does not match its hash.");
            previous = e.Hash; expected++; checkedCount++;
        }
        return new(true, checkedCount, null, null);
    }
}
