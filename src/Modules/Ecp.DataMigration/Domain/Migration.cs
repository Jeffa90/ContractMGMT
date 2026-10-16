using Ecp.SharedKernel;

namespace Ecp.DataMigration.Domain;

public enum BatchKind { DryRun, Mock, Final }

public enum BatchStatus { Created, Profiled, Validated, Committing, Committed, Reconciled, Accepted, RolledBack, Compensated, Failed }

public enum DuplicateStrategy { Reject, SkipExisting, UpdateMapped, CreateNewReference, ManualReview }

public enum DocumentDuplicateOption { RejectDuplicateHash, LinkExisting, AddVersion, KeepSeparateWithWarning, ManualReview }

public enum ProjectStatus { Active, Closed }

/// <summary>How the target Agreement Reference is chosen (MIGRATION_SUPPLEMENT section 2): keep the source reference where it is free, or allocate from the organisation's numbering rule.</summary>
public enum ReferencePolicy { KeepSource, GenerateNew }

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
    /// <summary>Target states a legacy load may create, comma separated (the policy's "allowed target states").</summary>
    public string LegacyAllowedStatusCodes { get; private set; } = "";
    /// <summary>Whether an Update-mapped-fields migration may overwrite an existing locked legacy record. Off unless the policy says so and states why.</summary>
    public bool LegacyAllowOverwriteLocked { get; private set; }
    public string? LegacyOverwriteReason { get; private set; }
    public ProjectStatus Status { get; private set; } = ProjectStatus.Active;
    public ReferencePolicy ReferencePolicy { get; private set; } = ReferencePolicy.KeepSource;
    /// <summary>When on, people cannot create contracts in the organisation during the freeze window (cutover freeze).</summary>
    public bool TargetFreezeEnabled { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static MigrationProject Create(Guid tenantId, Guid organisationId, string name, string sourceSystem, string? sourceVersion,
        string dataOwner, DateTime extractionUtc, DateTime nowUtc, string createdBy = "system") => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Name = Guard.NotBlank(name, "Project name", 200), SourceSystem = Guard.NotBlank(sourceSystem, "Source system", 100),
        SourceSystemVersion = Guard.OptionalBounded(sourceVersion, "Source system version", 100), DataOwner = Guard.NotBlank(dataOwner, "Data owner", 200),
        ExtractionUtc = Guard.Utc(extractionUtc, nameof(extractionUtc)), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), CreatedBy = Guard.NotBlank(createdBy, "Created by", 200)
    };

    public void SetFreezeWindow(DateTime startUtc, DateTime endUtc)
    {
        EnsureActive();
        Guard.Utc(startUtc, nameof(startUtc)); Guard.Utc(endUtc, nameof(endUtc));
        if (endUtc <= startUtc) throw new DomainException("invalid_freeze", "The freeze window must end after it starts.");
        FreezeStartUtc = startUtc; FreezeEndUtc = endUtc;
    }

    public void SetTargetFreeze(bool enabled)
    {
        EnsureActive();
        if (enabled && (FreezeStartUtc is null || FreezeEndUtc is null)) throw new DomainException("invalid_freeze", "Set a freeze window before turning the target freeze on.");
        TargetFreezeEnabled = enabled;
    }

    public bool FreezeInForceAt(DateTime nowUtc) => Status == ProjectStatus.Active && TargetFreezeEnabled && FreezeStartUtc <= nowUtc && nowUtc < FreezeEndUtc;

    /// <summary>
    /// Allows legacy loads (records in a state of Effective or later, optional Registered Date import) until the policy expires. The policy names the target states
    /// a legacy load may create; an empty list allows none beyond the ordinary initial state.
    /// </summary>
    public void EnableLegacyLoad(bool allowRegisteredDateImport, DateTime expiresUtc, DateTime nowUtc, IEnumerable<string>? allowedStatusCodes = null,
        bool allowOverwriteLocked = false, string? overwriteReason = null)
    {
        EnsureActive();
        if (Guard.Utc(expiresUtc, nameof(expiresUtc)) <= nowUtc) throw new DomainException("invalid_policy", "The legacy-load policy must expire in the future.");
        var codes = (allowedStatusCodes ?? []).Select(c => Guard.NotBlank(c, "Status code", 100)).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (allowOverwriteLocked && string.IsNullOrWhiteSpace(overwriteReason))
            throw new DomainException("invalid_policy", "Allowing a legacy load to overwrite locked records needs a recorded reason.");
        LegacyLoadEnabled = true; LegacyAllowRegisteredDateImport = allowRegisteredDateImport; LegacyPolicyExpiresUtc = expiresUtc;
        LegacyAllowedStatusCodes = string.Join(',', codes);
        LegacyAllowOverwriteLocked = allowOverwriteLocked;
        LegacyOverwriteReason = allowOverwriteLocked ? Guard.NotBlank(overwriteReason, "Overwrite reason", 1000) : null;
    }

    public void CloseLegacyLoad() { LegacyLoadEnabled = false; }

    public IReadOnlyList<string> LegacyAllowedStatuses => LegacyAllowedStatusCodes.Length == 0 ? [] : LegacyAllowedStatusCodes.Split(',');

    public bool LegacyLoadAllowedAt(DateTime nowUtc) => LegacyLoadEnabled && LegacyPolicyExpiresUtc > nowUtc;

    public void SetReferencePolicy(ReferencePolicy policy) { EnsureActive(); ReferencePolicy = policy; }

    /// <summary>Finishes the project: no new batches, the legacy-load policy is closed and the target freeze is lifted (after go-live, loads of Effective-state data are refused).</summary>
    public void Close() { Status = ProjectStatus.Closed; LegacyLoadEnabled = false; TargetFreezeEnabled = false; }

    private void EnsureActive()
    {
        if (Status != ProjectStatus.Active) throw new DomainException("project_closed", "The migration project is closed.");
    }

    public MigrationBatch CreateBatch(BatchKind kind, DuplicateStrategy strategy, DocumentDuplicateOption documentOption, bool isLegacyLoad,
        Guid? mappingProfileVersionId, Guid? configReleaseId, DateTime nowUtc, string createdBy = "system", Guid? createdByUserId = null, string? name = null, int? configReleaseNumber = null)
    {
        EnsureActive();
        if (isLegacyLoad)
        {
            if (kind == BatchKind.DryRun) throw new DomainException("invalid_batch", "A dry run does not load data, so it cannot be a legacy load.");
            if (!LegacyLoadAllowedAt(nowUtc)) throw new DomainException("legacy_policy_inactive", "No active legacy-load policy allows this batch.");
        }
        return MigrationBatch.Create(this, kind, strategy, documentOption, isLegacyLoad, mappingProfileVersionId, configReleaseId, nowUtc, createdBy, createdByUserId, name, configReleaseNumber);
    }
}

/// <summary>
/// One execution unit of a migration. The state machine enforces: a dry run never commits (INV-40); commit needs a validated batch; a legacy load needs a second person's
/// confirmation of exactly what was validated; a batch cannot be reconciled unless totals balance or exceptions are approved (INV-45); rollback is refused after acceptance.
/// </summary>
public sealed class MigrationBatch : ITenantOwned
{
    private MigrationBatch() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string? Name { get; private set; }
    public BatchKind Kind { get; private set; }
    public BatchStatus Status { get; private set; }
    public DuplicateStrategy DuplicateStrategy { get; private set; }
    public DocumentDuplicateOption DocumentDuplicateOption { get; private set; }
    public bool IsLegacyLoad { get; private set; }
    public bool AllowRegisteredDateImport { get; private set; }
    public Guid? MappingProfileVersionId { get; private set; }
    public Guid? ConfigReleaseId { get; private set; }
    public int? ConfigReleaseNumber { get; private set; }
    public long SourceRows { get; private set; }
    public long Accepted { get; private set; }
    public long Rejected { get; private set; }
    public long Skipped { get; private set; }
    public long Created { get; private set; }
    public long Updated { get; private set; }
    public bool ExceptionsApproved { get; private set; }
    public string? FailureReason { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public Guid? CreatedByUserId { get; private set; }
    public string? AcceptedBy { get; private set; }
    public string? AcceptanceNote { get; private set; }
    public DateTime? AcceptedUtc { get; private set; }
    public DateTime CreatedUtc { get; private set; }

    /// <summary>Hash of everything the validation looked at (source files, profile version, options, existing-record facts). A confirmation or commit is valid only for the fingerprint it saw.</summary>
    public string? ValidationFingerprint { get; private set; }
    public DateTime? ValidatedUtc { get; private set; }
    public string? ValidationSummaryJson { get; private set; }

    public Guid? ConfirmedByUserId { get; private set; }
    public string? ConfirmedBy { get; private set; }
    public DateTime? ConfirmedUtc { get; private set; }
    public string? ConfirmedFingerprint { get; private set; }

    public string? CommittedBy { get; private set; }
    public Guid? CommittedByUserId { get; private set; }
    public DateTime? CommitStartedUtc { get; private set; }
    public DateTime? CommitCompletedUtc { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTime? LeaseExpiresUtc { get; private set; }

    public string? RollbackReason { get; private set; }
    public string? RolledBackBy { get; private set; }
    public DateTime? RolledBackUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    internal static MigrationBatch Create(MigrationProject p, BatchKind kind, DuplicateStrategy strategy, DocumentDuplicateOption docOption,
        bool legacy, Guid? profileVersion, Guid? release, DateTime nowUtc, string createdBy, Guid? createdByUserId, string? name, int? releaseNumber) => new()
    {
        Id = Ids.New(), TenantId = p.TenantId, ProjectId = p.Id, OrganisationId = p.OrganisationId, Kind = kind, Status = BatchStatus.Created,
        DuplicateStrategy = strategy, DocumentDuplicateOption = docOption, IsLegacyLoad = legacy,
        AllowRegisteredDateImport = legacy && p.LegacyAllowRegisteredDateImport, MappingProfileVersionId = profileVersion, ConfigReleaseId = release, ConfigReleaseNumber = releaseNumber,
        Name = Guard.OptionalBounded(name, "Batch name", 200), CreatedBy = Guard.NotBlank(createdBy, "Created by", 200), CreatedByUserId = createdByUserId,
        CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public bool InputsEditable => Status is BatchStatus.Created or BatchStatus.Profiled or BatchStatus.Validated;

    /// <summary>Source files, the mapping profile or the options changed: any earlier validation and confirmation no longer describe the inputs.</summary>
    public void InputsChanged(bool hasSources)
    {
        if (!InputsEditable) throw new DomainException("batch_locked", $"A batch in state {Status} can no longer take new inputs.");
        Status = hasSources ? BatchStatus.Profiled : BatchStatus.Created;
        ValidationFingerprint = null; ValidatedUtc = null; ValidationSummaryJson = null;
        ConfirmedByUserId = null; ConfirmedBy = null; ConfirmedUtc = null; ConfirmedFingerprint = null;
    }

    /// <summary>Pins (or re-pins) the mapping profile version and the configuration release the batch relies on. Frozen once commit starts.</summary>
    public void CapturePinnedVersions(Guid? profileVersionId, Guid? configReleaseId, int? configReleaseNumber)
    {
        if (!InputsEditable) throw new DomainException("batch_locked", "The mapping profile is frozen once the batch has started to commit.");
        MappingProfileVersionId = profileVersionId; ConfigReleaseId = configReleaseId; ConfigReleaseNumber = configReleaseNumber;
    }

    public void SetStrategies(DuplicateStrategy strategy, DocumentDuplicateOption option)
    {
        if (!InputsEditable) throw new DomainException("batch_locked", "The duplicate strategies are frozen once the batch has started to commit.");
        DuplicateStrategy = strategy; DocumentDuplicateOption = option;
    }

    public void MarkProfiled() => Move(BatchStatus.Created, BatchStatus.Profiled);
    public void MarkValidated() => Move(BatchStatus.Profiled, BatchStatus.Validated);

    /// <summary>Records a completed dry-run validation of the current inputs. It may be repeated (a dry run is repeatable).</summary>
    public void MarkValidated(string fingerprint, string summaryJson, DateTime nowUtc)
    {
        if (Status is not (BatchStatus.Profiled or BatchStatus.Validated)) throw Invalid(BatchStatus.Validated);
        var changed = ValidationFingerprint != fingerprint;
        Status = BatchStatus.Validated;
        ValidationFingerprint = Guard.NotBlank(fingerprint, "Fingerprint", 100); ValidatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); ValidationSummaryJson = summaryJson;
        if (changed) { ConfirmedByUserId = null; ConfirmedBy = null; ConfirmedUtc = null; ConfirmedFingerprint = null; }
    }

    /// <summary>A second person confirms a legacy load of exactly the validated inputs (MIGRATION_SUPPLEMENT section 7). The person who commits cannot confirm their own commit.</summary>
    public void ConfirmLegacyLoad(Guid confirmerUserId, string confirmerLabel, DateTime nowUtc)
    {
        if (!IsLegacyLoad) throw new DomainException("not_legacy_load", "Only a legacy load needs a second person's confirmation.");
        if (Status != BatchStatus.Validated || ValidationFingerprint is null) throw new DomainException("not_validated", "Validate the batch before confirming it.");
        ConfirmedByUserId = Guard.NotEmpty(confirmerUserId, nameof(confirmerUserId)); ConfirmedBy = Guard.NotBlank(confirmerLabel, "Confirmed by", 200);
        ConfirmedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); ConfirmedFingerprint = ValidationFingerprint;
    }

    public bool HasValidConfirmation => ConfirmedFingerprint is not null && ConfirmedFingerprint == ValidationFingerprint;

    /// <summary>Begins or resumes a commit. Dry runs can never commit.</summary>
    public void StartCommit()
    {
        if (Kind == BatchKind.DryRun) throw new DomainException("dry_run_cannot_commit", "A dry run performs no target writes and cannot be committed.");
        if (Status is BatchStatus.Validated or BatchStatus.Failed) { Status = BatchStatus.Committing; FailureReason = null; return; }
        throw Invalid(BatchStatus.Committing);
    }

    /// <summary>
    /// Begins or resumes a commit under a lease. A batch whose commit process stopped without finishing (lease expired) can be resumed; one that is still running cannot be started twice.
    /// A legacy load needs the second person's confirmation of the same fingerprint, from someone other than the committer.
    /// </summary>
    public void StartCommit(DateTime nowUtc, string leaseOwner, TimeSpan lease, string committerLabel, Guid? committerUserId)
    {
        if (Kind == BatchKind.DryRun) throw new DomainException("dry_run_cannot_commit", "A dry run performs no target writes and cannot be committed.");
        if (Status == BatchStatus.Committing && LeaseExpiresUtc > nowUtc && LeaseOwner != leaseOwner)
            throw new DomainException("commit_in_progress", "Another commit of this batch is still running.");
        if (Status is not (BatchStatus.Validated or BatchStatus.Failed or BatchStatus.Committing)) throw Invalid(BatchStatus.Committing);
        if (IsLegacyLoad)
        {
            if (!HasValidConfirmation) throw new DomainException("confirmation_required", "A legacy load needs a second person to confirm the validated batch before it is committed.");
            if (committerUserId is null || committerUserId == ConfirmedByUserId)
                throw new DomainException("second_person_required", "The person who confirms a legacy load cannot also commit it.");
        }
        var first = Status != BatchStatus.Committing;
        Status = BatchStatus.Committing; FailureReason = null;
        LeaseOwner = Guard.NotBlank(leaseOwner, "Lease owner", 100); LeaseExpiresUtc = Guard.Utc(nowUtc, nameof(nowUtc)) + lease;
        CommittedBy = Guard.NotBlank(committerLabel, "Committed by", 200); CommittedByUserId = committerUserId;
        if (first || CommitStartedUtc is null) CommitStartedUtc = nowUtc;
    }

    public void RenewLease(string leaseOwner, DateTime nowUtc, TimeSpan lease)
    {
        if (Status != BatchStatus.Committing || LeaseOwner != leaseOwner) throw new DomainException("lease_lost", "This commit no longer holds the batch.");
        LeaseExpiresUtc = nowUtc + lease;
    }

    public void CompleteCommit() => Move(BatchStatus.Committing, BatchStatus.Committed);

    public void CompleteCommit(DateTime nowUtc)
    {
        Move(BatchStatus.Committing, BatchStatus.Committed);
        CommitCompletedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); LeaseOwner = null; LeaseExpiresUtc = null;
    }

    public void Fail(string reason)
    {
        if (Status != BatchStatus.Committing) throw Invalid(BatchStatus.Failed);
        Status = BatchStatus.Failed; FailureReason = Guard.NotBlank(reason, "Failure reason", 1000);
        LeaseOwner = null; LeaseExpiresUtc = null;
    }

    /// <param name="otherwiseBalanced">The file, manifest and target checks balance as well as the row totals. If not, the batch is reconciled only on approved exceptions.</param>
    public void Reconcile(ReconciliationTotals totals, bool exceptionsApproved, bool otherwiseBalanced = true)
    {
        if (Status != BatchStatus.Committed) throw Invalid(BatchStatus.Reconciled);
        var balanced = totals.IsBalanced && otherwiseBalanced;
        if (!balanced && !exceptionsApproved)
            throw new DomainException("not_balanced", "The totals do not balance and no exceptions have been approved. A batch is not complete until they do.");
        (SourceRows, Accepted, Rejected, Skipped, Created, Updated) = (totals.SourceRows, totals.Accepted, totals.Rejected, totals.Skipped, totals.Created, totals.Updated);
        ExceptionsApproved = exceptionsApproved && !balanced;
        Status = BatchStatus.Reconciled;
    }

    public void Accept(string acceptedBy, DateTime nowUtc) => Accept(acceptedBy, null, nowUtc);

    public void Accept(string acceptedBy, string? note, DateTime nowUtc)
    {
        if (Status != BatchStatus.Reconciled) throw Invalid(BatchStatus.Accepted);
        AcceptedBy = Guard.NotBlank(acceptedBy, "Accepted by", 200); AcceptanceNote = Guard.OptionalBounded(note, "Acceptance note", 2000);
        AcceptedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); Status = BatchStatus.Accepted;
    }

    /// <summary>Whether a rollback may run in this state. Refused once accepted (use compensating archival through normal processes).</summary>
    public void EnsureCanRollBack(DateTime nowUtc)
    {
        if (Status == BatchStatus.Accepted) throw new DomainException("rollback_after_acceptance", "A batch cannot be rolled back after acceptance.");
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Failed or BatchStatus.Committing)) throw Invalid(BatchStatus.RolledBack);
        if (Status == BatchStatus.Committing && LeaseExpiresUtc > nowUtc) throw new DomainException("commit_in_progress", "The batch is still committing.");
    }

    /// <summary>Destructive rollback of reversible changes. Refused once accepted.</summary>
    public void RollBack()
    {
        if (Status == BatchStatus.Accepted) throw new DomainException("rollback_after_acceptance", "A batch cannot be rolled back after acceptance.");
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Failed or BatchStatus.Committing)) throw Invalid(BatchStatus.RolledBack);
        Status = BatchStatus.RolledBack;
    }

    public void CompleteRollback(string reason, string by, DateTime nowUtc)
    {
        if (Status == BatchStatus.Accepted) throw new DomainException("rollback_after_acceptance", "A batch cannot be rolled back after acceptance.");
        if (Status == BatchStatus.RolledBack) return;
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Failed or BatchStatus.Committing)) throw Invalid(BatchStatus.RolledBack);
        Status = BatchStatus.RolledBack; RollbackReason = Guard.NotBlank(reason, "Rollback reason", 1000); RolledBackBy = Guard.NotBlank(by, "Rolled back by", 200);
        RolledBackUtc = Guard.Utc(nowUtc, nameof(nowUtc)); LeaseOwner = null; LeaseExpiresUtc = null;
    }

    public void Compensate()
    {
        if (Status == BatchStatus.Accepted) throw new DomainException("rollback_after_acceptance", "A batch cannot be rolled back after acceptance.");
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled)) throw Invalid(BatchStatus.Compensated);
        Status = BatchStatus.Compensated;
    }

    /// <summary>Compensating archival of an accepted (or committed) batch: nothing is deleted, the records are archived with the reason and batch id.</summary>
    public void CompleteCompensation(string reason, string by, DateTime nowUtc)
    {
        if (Status == BatchStatus.Compensated) return;
        if (Status is not (BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted)) throw Invalid(BatchStatus.Compensated);
        Status = BatchStatus.Compensated; RollbackReason = Guard.NotBlank(reason, "Reason", 1000); RolledBackBy = Guard.NotBlank(by, "Compensated by", 200);
        RolledBackUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    private void Move(BatchStatus from, BatchStatus to)
    {
        if (Status != from) throw Invalid(to);
        Status = to;
    }

    private DomainException Invalid(BatchStatus to)
        => new("invalid_batch_transition", $"A batch in state {Status} cannot move to {to}.");
}
