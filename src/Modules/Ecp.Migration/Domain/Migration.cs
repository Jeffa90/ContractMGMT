using Ecp.SharedKernel;

namespace Ecp.Migration.Domain;

public enum BatchKind { DryRun, Mock, Final }

public enum BatchStatus { Created, Profiled, Validated, Committing, Committed, Reconciled, Accepted, RolledBack, Compensated, Failed }

public enum DuplicateStrategy { Reject, SkipExisting, UpdateMapped, CreateNewReference, ManualReview }

public enum DocumentDuplicateOption { RejectDuplicateHash, LinkExisting, AddVersion, KeepSeparateWithWarning, ManualReview }

/// <summary>Reconciliation identities (MIGRATION_SUPPLEMENT section 11): source = accepted + rejected + skipped; accepted = created + updated.</summary>
public sealed record ReconciliationTotals(long SourceRows, long Accepted, long Rejected, long Skipped, long Created, long Updated)
{
    public bool IsBalanced => SourceRows == Accepted + Rejected + Skipped && Accepted == Created + Updated
        && new[] { SourceRows, Accepted, Rejected, Skipped, Created, Updated }.All(x => x >= 0);
}

public sealed class MigrationProject : ITenantOwned
{
    private MigrationProject() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Name { get; private set; } = "";
    public string SourceSystem { get; private set; } = "";
    public string? SourceSystemVersion { get; private set; }
    public string DataOwner { get; private set; } = "";
    public DateTime ExtractionUtc { get; private set; }
    public DateTime? FreezeStartUtc { get; private set; }
    public DateTime? FreezeEndUtc { get; private set; }
    public bool LegacyLoadEnabled { get; private set; }
    public bool LegacyAllowRegisteredDateImport { get; private set; }
    public DateTime? LegacyPolicyExpiresUtc { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static MigrationProject Create(Guid tenantId, Guid organisationId, string name, string sourceSystem, string? sourceVersion,
        string dataOwner, DateTime extractionUtc, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Name = Guard.NotBlank(name, "Project name", 200), SourceSystem = Guard.NotBlank(sourceSystem, "Source system", 100),
        SourceSystemVersion = Guard.OptionalBounded(sourceVersion, "Source system version", 100), DataOwner = Guard.NotBlank(dataOwner, "Data owner", 200),
        ExtractionUtc = Guard.Utc(extractionUtc, nameof(extractionUtc)), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void SetFreezeWindow(DateTime startUtc, DateTime endUtc)
    {
        Guard.Utc(startUtc, nameof(startUtc)); Guard.Utc(endUtc, nameof(endUtc));
        if (endUtc <= startUtc) throw new DomainException("invalid_freeze", "The freeze window must end after it starts.");
        FreezeStartUtc = startUtc; FreezeEndUtc = endUtc;
    }

    /// <summary>Allows legacy loads (Effective-or-later records, optional Registered Date import) until the policy expires.</summary>
    public void EnableLegacyLoad(bool allowRegisteredDateImport, DateTime expiresUtc, DateTime nowUtc)
    {
        if (Guard.Utc(expiresUtc, nameof(expiresUtc)) <= nowUtc) throw new DomainException("invalid_policy", "The legacy-load policy must expire in the future.");
        LegacyLoadEnabled = true; LegacyAllowRegisteredDateImport = allowRegisteredDateImport; LegacyPolicyExpiresUtc = expiresUtc;
    }

    public void CloseLegacyLoad() { LegacyLoadEnabled = false; }

    public bool LegacyLoadAllowedAt(DateTime nowUtc) => LegacyLoadEnabled && LegacyPolicyExpiresUtc > nowUtc;

    public MigrationBatch CreateBatch(BatchKind kind, DuplicateStrategy strategy, DocumentDuplicateOption documentOption, bool isLegacyLoad,
        Guid? mappingProfileVersionId, Guid? configReleaseId, DateTime nowUtc)
    {
        if (isLegacyLoad)
        {
            if (kind == BatchKind.DryRun) throw new DomainException("invalid_batch", "A dry run does not load data, so it cannot be a legacy load.");
            if (!LegacyLoadAllowedAt(nowUtc)) throw new DomainException("legacy_policy_inactive", "No active legacy-load policy allows this batch.");
        }
        return MigrationBatch.Create(this, kind, strategy, documentOption, isLegacyLoad, mappingProfileVersionId, configReleaseId, nowUtc);
    }
}

/// <summary>
/// One execution unit of a migration. The state machine enforces: a dry run never commits (INV-40); commit needs a validated batch;
/// a batch cannot be reconciled unless totals balance or exceptions are approved (INV-45); rollback is refused after acceptance.
/// </summary>
public sealed class MigrationBatch : ITenantOwned
{
    private MigrationBatch() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public BatchKind Kind { get; private set; }
    public BatchStatus Status { get; private set; }
    public DuplicateStrategy DuplicateStrategy { get; private set; }
    public DocumentDuplicateOption DocumentDuplicateOption { get; private set; }
    public bool IsLegacyLoad { get; private set; }
    public bool AllowRegisteredDateImport { get; private set; }
    public Guid? MappingProfileVersionId { get; private set; }
    public Guid? ConfigReleaseId { get; private set; }
    public long SourceRows { get; private set; }
    public long Accepted { get; private set; }
    public long Rejected { get; private set; }
    public long Skipped { get; private set; }
    public long Created { get; private set; }
    public long Updated { get; private set; }
    public bool ExceptionsApproved { get; private set; }
    public string? FailureReason { get; private set; }
    public string? AcceptedBy { get; private set; }
    public DateTime? AcceptedUtc { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    internal static MigrationBatch Create(MigrationProject p, BatchKind kind, DuplicateStrategy strategy, DocumentDuplicateOption docOption,
        bool legacy, Guid? profileVersion, Guid? release, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = p.TenantId, ProjectId = p.Id, OrganisationId = p.OrganisationId, Kind = kind, Status = BatchStatus.Created,
        DuplicateStrategy = strategy, DocumentDuplicateOption = docOption, IsLegacyLoad = legacy,
        AllowRegisteredDateImport = legacy && p.LegacyAllowRegisteredDateImport, MappingProfileVersionId = profileVersion, ConfigReleaseId = release,
        CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void MarkProfiled() => Move(BatchStatus.Created, BatchStatus.Profiled);
    public void MarkValidated() => Move(BatchStatus.Profiled, BatchStatus.Validated);

    /// <summary>Begins or resumes a commit. Dry runs can never commit.</summary>
    public void StartCommit()
    {
        if (Kind == BatchKind.DryRun) throw new DomainException("dry_run_cannot_commit", "A dry run performs no target writes and cannot be committed.");
        if (Status is BatchStatus.Validated or BatchStatus.Failed) { Status = BatchStatus.Committing; FailureReason = null; return; }
        throw Invalid(BatchStatus.Committing);
    }

    public void CompleteCommit() => Move(BatchStatus.Committing, BatchStatus.Committed);

    public void Fail(string reason)
    {
        if (Status != BatchStatus.Committing) throw Invalid(BatchStatus.Failed);
        Status = BatchStatus.Failed; FailureReason = Guard.NotBlank(reason, "Failure reason", 1000);
    }

    public void Reconcile(ReconciliationTotals totals, bool exceptionsApproved)
    {
        if (Status != BatchStatus.Committed) throw Invalid(BatchStatus.Reconciled);
        if (!totals.IsBalanced && !exceptionsApproved)
            throw new DomainException("not_balanced", "The totals do not balance and no exceptions have been approved. A batch is not complete until they do.");
        (SourceRows, Accepted, Rejected, Skipped, Created, Updated) = (totals.SourceRows, totals.Accepted, totals.Rejected, totals.Skipped, totals.Created, totals.Updated);
        ExceptionsApproved = exceptionsApproved && !totals.IsBalanced;
        Status = BatchStatus.Reconciled;
    }

    public void Accept(string acceptedBy, DateTime nowUtc)
    {
        if (Status != BatchStatus.Reconciled) throw Invalid(BatchStatus.Accepted);
        AcceptedBy = Guard.NotBlank(acceptedBy, "Accepted by", 200); AcceptedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); Status = BatchStatus.Accepted;
    }

    /// <summary>Destructive rollback of reversible changes. Refused once accepted (use compensating archival through normal processes).</summary>
    public void RollBack()
    {
        if (Status == BatchStatus.Accepted) throw new DomainException("rollback_after_acceptance", "A batch cannot be rolled back after acceptance.");
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Failed or BatchStatus.Committing)) throw Invalid(BatchStatus.RolledBack);
        Status = BatchStatus.RolledBack;
    }

    public void Compensate()
    {
        if (Status == BatchStatus.Accepted) throw new DomainException("rollback_after_acceptance", "A batch cannot be rolled back after acceptance.");
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled)) throw Invalid(BatchStatus.Compensated);
        Status = BatchStatus.Compensated;
    }

    private void Move(BatchStatus from, BatchStatus to)
    {
        if (Status != from) throw Invalid(to);
        Status = to;
    }

    private DomainException Invalid(BatchStatus to)
        => new("invalid_batch_transition", $"A batch in state {Status} cannot move to {to}.");
}
