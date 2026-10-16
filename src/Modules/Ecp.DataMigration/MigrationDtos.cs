using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;
using Ecp.Formats;

namespace Ecp.DataMigration;

/// <summary>Settings for the migration module. Limits are open decisions (Q-13) and configurable; the values here are the documented defaults.</summary>
public sealed class MigrationOptions
{
    /// <summary>Rows, files or documents committed per transaction (each chunk commits with its checkpoint).</summary>
    public int ChunkSize { get; set; } = 500;
    /// <summary>How long a commit holds a batch before another process may resume it.</summary>
    public TimeSpan CommitLease { get; set; } = TimeSpan.FromMinutes(2);
    public ReadLimits ReadLimits { get; set; } = new();
    public ArchiveLimits ArchiveLimits { get; set; } = new();
    /// <summary>The largest single source or archive upload accepted.</summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>When true (Production), a file the scanner could not clear is held rather than imported.</summary>
    public bool RequireMalwareScan { get; set; }
}

public sealed record ProjectSummary(Guid Id, Guid OrganisationId, string Name, string SourceSystem, string? SourceSystemVersion, string DataOwner, DateTime ExtractionUtc,
    ProjectStatus Status, int BatchCount, DateTime CreatedUtc);

public sealed record ProjectDetail(Guid Id, Guid OrganisationId, string Name, string SourceSystem, string? SourceSystemVersion, string DataOwner, DateTime ExtractionUtc,
    DateTime? FreezeStartUtc, DateTime? FreezeEndUtc, bool TargetFreezeEnabled, bool LegacyLoadEnabled, bool LegacyAllowRegisteredDateImport, DateTime? LegacyPolicyExpiresUtc,
    IReadOnlyList<string> LegacyAllowedStatuses, bool LegacyAllowOverwriteLocked, string? LegacyOverwriteReason, ReferencePolicy ReferencePolicy, ProjectStatus Status,
    string CreatedBy, DateTime CreatedUtc, IReadOnlyList<BatchSummary> Batches, ProjectCapabilities Can);

/// <summary>What the signed-in person may do on a project, so a page offers only actions that will be accepted.</summary>
public sealed record ProjectCapabilities(bool CanStage, bool CanLegacyLoad, bool CanCommit, bool CanAccept);

public sealed record BatchSummary(Guid Id, Guid ProjectId, string? Name, BatchKind Kind, BatchStatus Status, bool IsLegacyLoad, DuplicateStrategy DuplicateStrategy, string CreatedBy, DateTime CreatedUtc,
    long SourceRows, long Created, long Updated, long Rejected, long Skipped);

/// <summary>An organisation the signed-in person may run migrations for.</summary>
public sealed record OrganisationOption(Guid Id, string Name);

public sealed record CreateProjectInput(Guid OrganisationId, string Name, string SourceSystem, string? SourceSystemVersion, string DataOwner, DateTime ExtractionUtc);

public sealed record LegacyPolicyInput(bool AllowRegisteredDateImport, DateTime ExpiresUtc, IReadOnlyList<string> AllowedStatusCodes, bool AllowOverwriteLocked, string? OverwriteReason);

public sealed record CreateBatchInput(Guid ProjectId, string? Name, BatchKind Kind, DuplicateStrategy DuplicateStrategy, DocumentDuplicateOption DocumentOption, bool IsLegacyLoad, Guid? ProfileVersionId);

public sealed record SourceFileDto(Guid Id, SourceFileRole Role, SourceFormat Format, string FileName, string Sha256, long SizeBytes, int RowCount, string? SheetName, string? Encoding, string? Delimiter,
    string UploadedBy, DateTime UploadedUtc, IReadOnlyList<string> Headers, IReadOnlyList<string> Notices, string? Note);

public sealed record UploadOptions(char? Delimiter = null, string? Encoding = null, string? SheetName = null);

public sealed record StagingCounts(int Rows, int Accepted, int Rejected, int Skipped, int Pending, int Created, int Updated, int ReviewRequired, int Errors, int Warnings,
    int ManifestEntries, int Files, int FilesMatched, int FilesUnmatched, int FilesInvalid, int FilesDuplicate, int FilesImported, int FilesFailed, int FilesDiscarded);

public sealed record BatchCapabilities(bool CanStage, bool CanValidate, bool CanConfirm, bool CanCommit, bool CanReconcile, bool CanApproveExceptions, bool CanAccept, bool CanRollBack, bool CanCompensate);

public sealed record BatchDetail(Guid Id, Guid ProjectId, string ProjectName, Guid OrganisationId, string SourceSystem, string? Name, BatchKind Kind, BatchStatus Status, bool IsLegacyLoad,
    bool AllowRegisteredDateImport, DuplicateStrategy DuplicateStrategy, DocumentDuplicateOption DocumentOption, ReferencePolicy ReferencePolicy, Guid? ProfileVersionId, string? ProfileName,
    int? ProfileVersionNumber, int? ConfigReleaseNumber, string CreatedBy, DateTime CreatedUtc, DateTime? ValidatedUtc, string? ConfirmedBy, DateTime? ConfirmedUtc, bool ConfirmationCurrent,
    string? CommittedBy, DateTime? CommitStartedUtc, DateTime? CommitCompletedUtc, string? FailureReason, string? AcceptedBy, DateTime? AcceptedUtc, string? AcceptanceNote,
    string? RollbackReason, string? RolledBackBy, DateTime? RolledBackUtc, bool ExceptionsApproved, IReadOnlyList<SourceFileDto> Sources, StagingCounts Counts, ValidationSummary? Validation,
    IReadOnlyList<CheckpointDto> Checkpoints, BatchCapabilities Can);

public sealed record CheckpointDto(string Phase, long LastCompletedSequence, bool Complete, DateTime UpdatedUtc);

public sealed record MappingStat(string SourceColumn, string TargetField, string Transformation, int Rows, int Changed, int Defaulted, int Errors);
public sealed record PreviewValue(string SourceColumn, string TargetField, string Original, string Transformed, bool Changed, bool Failed);
public sealed record PreviewRow(int RowNumber, string Outcome, string Summary, IReadOnlyList<PreviewValue> Values);

/// <summary>What the last dry run found: stored with the batch and shown before anything is committed.</summary>
public sealed class ValidationSummary
{
    public DateTime GeneratedUtc { get; set; }
    public int Rows { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int Skipped { get; set; }
    public int ReviewRequired { get; set; }
    public int ToCreate { get; set; }
    public int ToUpdate { get; set; }
    public int SuppliersToLink { get; set; }
    public int SuppliersToCreate { get; set; }
    public int CategoriesToLink { get; set; }
    public int CategoriesToCreate { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public Dictionary<string, int> MessagesByCode { get; set; } = [];
    public Dictionary<string, int> StatusDistribution { get; set; } = [];
    public Dictionary<string, int> CategoryDistribution { get; set; } = [];
    public Dictionary<string, decimal> ValueByCurrency { get; set; } = [];
    public int ManifestRows { get; set; }
    public int ManifestPaired { get; set; }
    public int ManifestOrphans { get; set; }
    public int ManifestInvalid { get; set; }
    public int Files { get; set; }
    public int FilesMatched { get; set; }
    public int FilesUnmatched { get; set; }
    public int FilesInvalid { get; set; }
    public int FilesDuplicate { get; set; }
    public int DocumentsToImport { get; set; }
    public List<MappingStat> Mapping { get; set; } = [];
    public List<PreviewRow> Sample { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}

public sealed record MessageFilter(MessageSeverity? Severity = null, string? Code = null, MessageSubject? Subject = null, int Page = 1, int PageSize = 50);
public sealed record MessageDto(Guid Id, MessagePhase Phase, MessageSubject Subject, Guid? SubjectId, string Location, MessageSeverity Severity, string Code, string Message, string? FieldName);

public sealed record RowFilter(RowOutcome? Outcome = null, bool ReviewOnly = false, int Page = 1, int PageSize = 50);
public sealed record RowDto(Guid Id, int Sequence, int RowNumber, string? SourceRecordId, string? AgreementReference, RowOutcome Outcome, PlannedAction Action, ReviewDecision Review, int Errors, int Warnings,
    string? PlanSummary, Guid? TargetContractId, IReadOnlyDictionary<string, string> Raw, IReadOnlyDictionary<string, string>? Mapped);

public sealed record FileFilter(FileState? State = null, bool QueueOnly = false, int Page = 1, int PageSize = 50);
public sealed record ProposalDto(Guid Id, Guid ContractRowId, int ContractRowSequence, string? SourceRecordId, string? AgreementReference, MatchMethod Method, string Reason, ProposalStatus Status, string? ConfirmedBy);
public sealed record StagedFileDto(Guid Id, string RelativePath, string FileName, string? Sha256, long SizeBytes, FileScan Scan, FileState State, QueueReason QueueReason, MatchMethod? MatchedBy,
    Guid? ContractRowId, string? ContractReference, string? Note, string? ResolvedBy, DateTime? ResolvedUtc, Guid? TargetDocumentId, string? DocumentType, IReadOnlyList<ProposalDto> Proposals);

public sealed record ManifestEntryDto(Guid Id, int RowNumber, string? SourceRecordId, string? AgreementReference, string? RelativePath, string? DocumentTypeRaw, string? DocumentTypeCode,
    ManifestEntryState State, Guid? StagedFileId);

public sealed record ContractChoice(Guid RowId, int Sequence, string? SourceRecordId, string? AgreementReference, RowOutcome Outcome);

public sealed record RollbackPreviewLine(string Entity, string Action, int Count, string Detail);
public sealed record RollbackPreview(bool Allowed, string? BlockedReason, IReadOnlyList<RollbackPreviewLine> Lines);
public sealed record RollbackResult(BatchStatus Status, int Reversed, int Archived, int Retained, int NotReversible, IReadOnlyList<RollbackPreviewLine> Lines);

public sealed record ProvenanceDto(Guid Id, string EntityType, Guid EntityId, ProvenanceAction Action, string SourceSystem, string SourceRecordId, string? Sha256, ReversalState ReversalState,
    string? ReversalNote, DateTime CreatedUtc);

public sealed record ReconciliationDto(Guid? ReportId, int Sequence, DateTime GeneratedUtc, string GeneratedBy, ReconciliationTotals Totals, FileReconciliation Files, ReconciliationDistributions Distributions,
    bool Balanced, bool ExceptionsApproved, IReadOnlyList<string> Imbalances, IReadOnlyList<string> OutstandingItems, string? ApprovedBy, string? ApprovalNote, bool Saved);

public sealed record FileReconciliation(long ManifestRows, long ManifestMatched, long ManifestOrphans, long ManifestInvalid, long FilesInArchive, long Imported, long Failed, long Invalid,
    long DuplicateHandled, long Unmatched, long Discarded, long Other, long DocumentsCreated, long DocumentVersionsAdded)
{
    /// <summary>FilesInArchive = Imported + Failed + Invalid + DuplicateHandled + Unmatched + Discarded (+ files still awaiting a result).</summary>
    public bool FilesBalanced => FilesInArchive == Imported + Failed + Invalid + DuplicateHandled + Unmatched + Discarded + Other && Other == 0;
    public bool ManifestBalanced => ManifestRows == ManifestMatched + ManifestOrphans + ManifestInvalid;
}

public sealed record ReconciliationDistributions(
    IReadOnlyDictionary<string, long> SourceStatus, IReadOnlyDictionary<string, long> TargetStatus, IReadOnlyDictionary<string, long> SourceCategory, IReadOnlyDictionary<string, long> TargetCategory,
    IReadOnlyDictionary<string, decimal> SourceValueByCurrency, IReadOnlyDictionary<string, decimal> TargetValueByCurrency);

public sealed record SampleCheckInput(string Note, bool OutstandingAcknowledged);

public sealed record CommitProgress(BatchStatus Status, string? Phase, long Done, long Total, string? FailureReason);

public sealed record ProfileSummary(Guid Id, Guid OrganisationId, string Name, string SourceSystem, string? SourceSystemVersion, int LatestVersionNumber, int PublishedVersions, string CreatedBy, DateTime CreatedUtc);
public sealed record ProfileVersionSummary(Guid Id, int VersionNumber, Ecp.DataMigration.Domain.ProfileVersionStatus Status, string DefinitionHash, string? Note, string CreatedBy, DateTime CreatedUtc,
    string? PublishedBy, DateTime? PublishedUtc, int FieldMappings, int LookupEntries);
public sealed record ProfileDetail(ProfileSummary Profile, IReadOnlyList<ProfileVersionSummary> Versions);
public sealed record ProfileVersionDetail(Guid ProfileId, string ProfileName, Guid OrganisationId, ProfileVersionSummary Version, Mapping.MappingDefinition Definition, IReadOnlyList<Mapping.DefinitionIssue> Issues);
