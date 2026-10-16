using Ecp.SharedKernel;

namespace Ecp.DataMigration.Domain;

public enum ProvenanceAction { Created, Updated, Linked }

/// <summary>What a rollback or compensation did with a provenance record.</summary>
public enum ReversalState { Active, Reversed, Archived, Retained, NotReversible }

public enum ProfileVersionStatus { Draft, Published }

/// <summary>Resume point for a phase of a commit. A chunk's work and its checkpoint commit in one transaction.</summary>
public sealed class Checkpoint : ITenantOwned
{
    private Checkpoint() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public string Phase { get; private set; } = "";
    public long LastCompletedSequence { get; private set; }
    public bool PhaseComplete { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Checkpoint Start(Guid tenantId, Guid batchId, string phase, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), Phase = Guard.NotBlank(phase, "Phase", 40),
        UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Advance(long sequence, bool complete, DateTime nowUtc)
    {
        if (sequence < LastCompletedSequence) return;
        LastCompletedSequence = sequence; PhaseComplete = complete; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }
}

/// <summary>
/// The ledger of everything a batch did to the target: what was created, updated (with the before-image) or linked, and the source it came from. Rollback selects
/// strictly by batch id from here, so records that existed before the batch or belong to another batch can never be touched.
/// </summary>
public sealed class ProvenanceRecord : ITenantOwned
{
    private ProvenanceRecord() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string TargetEntityType { get; private set; } = "";
    public Guid TargetEntityId { get; private set; }
    public ProvenanceAction Action { get; private set; }
    /// <summary>Previous values of the fields an Update changed, so a rollback can restore them.</summary>
    public string? BeforeImageJson { get; private set; }
    /// <summary>The target's last-updated stamp straight after the batch wrote it. A later change makes the update non-reversible.</summary>
    public DateTime? AfterStampUtc { get; private set; }
    public string SourceSystem { get; private set; } = "";
    public string SourceRecordId { get; private set; } = "";
    public string? Sha256 { get; private set; }
    public Guid? StagingRowId { get; private set; }
    public Guid? StagedFileId { get; private set; }
    public string? SourceFileSha256 { get; private set; }
    public Guid? MappingProfileVersionId { get; private set; }
    public DateTime? SourceExtractedUtc { get; private set; }
    public string? SourceStatus { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public ReversalState ReversalState { get; private set; }
    public string? ReversalNote { get; private set; }
    public DateTime? ReversedUtc { get; private set; }

    public static ProvenanceRecord Create(Guid tenantId, Guid batchId, Guid organisationId, string entityType, Guid entityId, ProvenanceAction action, string? beforeImage, DateTime? afterStamp,
        string sourceSystem, string sourceRecordId, string? sha256, Guid? stagingRowId, Guid? stagedFileId, string? sourceFileSha256, Guid? profileVersionId, DateTime? extractedUtc,
        string? sourceStatus, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        TargetEntityType = Guard.NotBlank(entityType, "Entity type", 50), TargetEntityId = Guard.NotEmpty(entityId, nameof(entityId)), Action = action,
        BeforeImageJson = action == ProvenanceAction.Updated ? Guard.NotBlank(beforeImage, "Before image", 8000) : null, AfterStampUtc = afterStamp,
        SourceSystem = Guard.NotBlank(sourceSystem, "Source system", 100), SourceRecordId = Guard.NotBlank(sourceRecordId, "Source record id", 400), Sha256 = sha256,
        StagingRowId = stagingRowId, StagedFileId = stagedFileId, SourceFileSha256 = sourceFileSha256, MappingProfileVersionId = profileVersionId, SourceExtractedUtc = extractedUtc,
        SourceStatus = Guard.OptionalBounded(sourceStatus, "Source status", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), ReversalState = ReversalState.Active
    };

    public void Resolve(ReversalState state, string note, DateTime nowUtc)
    {
        ReversalState = state; ReversalNote = Guard.OptionalBounded(note, "Note", 1000); ReversedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }
}

/// <summary>A reconciliation of a batch. Re-running appends a new sequence, so earlier evidence is kept.</summary>
public sealed class ReconciliationReport : ITenantOwned
{
    private ReconciliationReport() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public int Sequence { get; private set; }
    public DateTime GeneratedUtc { get; private set; }
    public string GeneratedBy { get; private set; } = "";
    public string TotalsJson { get; private set; } = "{}";
    public string FileTotalsJson { get; private set; } = "{}";
    public string DistributionsJson { get; private set; } = "{}";
    public bool Balanced { get; private set; }
    public bool ExceptionsApproved { get; private set; }
    public string? ApprovedExceptionsJson { get; private set; }
    public string? ApprovedBy { get; private set; }
    public string? ApprovalNote { get; private set; }

    public static ReconciliationReport Create(Guid tenantId, Guid batchId, int sequence, string by, DateTime nowUtc, string totalsJson, string fileTotalsJson, string distributionsJson,
        bool balanced, bool exceptionsApproved, string? exceptionsJson, string? approvedBy, string? note) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), Sequence = sequence,
        GeneratedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), GeneratedBy = Guard.NotBlank(by, "Generated by", 200), TotalsJson = totalsJson, FileTotalsJson = fileTotalsJson,
        DistributionsJson = distributionsJson, Balanced = balanced, ExceptionsApproved = exceptionsApproved, ApprovedExceptionsJson = exceptionsJson,
        ApprovedBy = Guard.OptionalBounded(approvedBy, "Approved by", 200), ApprovalNote = Guard.OptionalBounded(note, "Note", 2000)
    };
}

/// <summary>A reusable mapping profile for one source system (and version). Its content lives in immutable, numbered versions.</summary>
public sealed class MappingProfile : ITenantOwned
{
    private MappingProfile() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Name { get; private set; } = "";
    public string SourceSystem { get; private set; } = "";
    public string? SourceSystemVersion { get; private set; }
    public int LatestVersionNumber { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static MappingProfile Create(Guid tenantId, Guid organisationId, string name, string sourceSystem, string? sourceSystemVersion, string by, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Name = Guard.NotBlank(name, "Profile name", 200), SourceSystem = Guard.NotBlank(sourceSystem, "Source system", 100),
        SourceSystemVersion = Guard.OptionalBounded(sourceSystemVersion, "Source system version", 100), CreatedBy = Guard.NotBlank(by, "Created by", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public MappingProfileVersion NewVersion(string definitionJson, string definitionHash, string by, DateTime nowUtc, string? note)
    {
        LatestVersionNumber++;
        return MappingProfileVersion.Create(TenantId, Id, LatestVersionNumber, definitionJson, definitionHash, by, nowUtc, note);
    }
}

/// <summary>
/// One version of a profile's definition. A draft can be edited; once published it is immutable (a batch that used it can always be explained), and changes make a new version.
/// </summary>
public sealed class MappingProfileVersion : ITenantOwned
{
    private MappingProfileVersion() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ProfileId { get; private set; }
    public int VersionNumber { get; private set; }
    public ProfileVersionStatus Status { get; private set; }
    public string DefinitionJson { get; private set; } = "{}";
    public string DefinitionHash { get; private set; } = "";
    public string? Note { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public string? PublishedBy { get; private set; }
    public DateTime? PublishedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    internal static MappingProfileVersion Create(Guid tenantId, Guid profileId, int number, string definitionJson, string hash, string by, DateTime nowUtc, string? note) => new()
    {
        Id = Ids.New(), TenantId = tenantId, ProfileId = profileId, VersionNumber = number, Status = ProfileVersionStatus.Draft, DefinitionJson = definitionJson, DefinitionHash = hash,
        Note = Guard.OptionalBounded(note, "Note", 1000), CreatedBy = Guard.NotBlank(by, "Created by", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void ReplaceDefinition(string definitionJson, string hash)
    {
        if (Status == ProfileVersionStatus.Published) throw new DomainException("profile_version_published", "A published profile version is immutable. Create a new version to change it.");
        DefinitionJson = definitionJson; DefinitionHash = hash;
    }

    public void Publish(string by, DateTime nowUtc)
    {
        if (Status == ProfileVersionStatus.Published) throw new DomainException("profile_version_published", "This version is already published.");
        Status = ProfileVersionStatus.Published; PublishedBy = Guard.NotBlank(by, "Published by", 200); PublishedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }
}
