using Ecp.SharedKernel;

namespace Ecp.DataMigration.Domain;

public enum SourceFileRole { Contracts, Manifest, Archive }
public enum SourceFormat { Csv, Xlsx, Zip }

/// <summary>What happened to a source row. Pending until the dry run; Accepted rows become Created or Updated when committed (reconciliation identities, section 11).</summary>
public enum RowOutcome { Pending, Accepted, Rejected, Skipped, Created, Updated }

/// <summary>What the commit will do with an accepted row.</summary>
public enum PlannedAction { None, Create, Update, Skip }

/// <summary>A person's decision on a row the duplicate strategy sent to manual review.</summary>
public enum ReviewDecision { None, Required, Skip, Update, Reject, CreateNew }

public enum MessageSeverity { Error, Warning }
public enum MessageSubject { Row, ManifestEntry, File, Batch }

/// <summary>When a message was produced. Validation messages are recomputed on every dry run; intake and commit messages are kept.</summary>
public enum MessagePhase { Intake, Validation, Commit }

/// <summary>Staged file states (data dictionary). Unmatched also covers files held for a person (see <see cref="QueueReason"/>).</summary>
public enum FileState { Staged, Matched, Unmatched, Invalid, Duplicate, Imported, Failed, Discarded }
public enum QueueReason { None, NoMatch, Ambiguous, TargetRejected, DuplicateReview }
public enum MatchMethod { SourceRecordId, AgreementReference, NamingRule, FolderName, Manual }
public enum ProposalStatus { Proposed, Accepted, Rejected, Superseded }
public enum ManifestEntryState { Pending, Paired, OrphanManifest, Invalid }
public enum FileScan { Pending, Clean, Infected, Skipped, Error }

/// <summary>A file registered with a batch: the contracts file, the attachment manifest, or the attachment archive. The raw bytes are kept (content-addressed, tenant-bound) so the run can be re-read and audited.</summary>
public sealed class SourceFile : ITenantOwned
{
    private SourceFile() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public SourceFileRole Role { get; private set; }
    public SourceFormat Format { get; private set; }
    public string FileName { get; private set; } = "";
    public string Sha256 { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string StorageKey { get; private set; } = "";
    public int RowCount { get; private set; }
    public string? SheetName { get; private set; }
    public string? Encoding { get; private set; }
    public string? Delimiter { get; private set; }
    /// <summary>Headers and per-column counts (profile), as JSON.</summary>
    public string ProfileJson { get; private set; } = "{}";
    public string? Note { get; private set; }
    public string UploadedBy { get; private set; } = "";
    public DateTime UploadedUtc { get; private set; }

    public static SourceFile Create(Guid tenantId, Guid batchId, Guid organisationId, SourceFileRole role, SourceFormat format, string fileName, string sha256, long size, string storageKey,
        int rowCount, string? sheetName, string? encoding, string? delimiter, string profileJson, string? note, string uploadedBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Role = role, Format = format, FileName = Guard.NotBlank(fileName, "File name", 260), Sha256 = Guard.NotBlank(sha256, "SHA-256", 64), SizeBytes = size,
        StorageKey = Guard.NotBlank(storageKey, "Storage key", 150), RowCount = rowCount, SheetName = Guard.OptionalBounded(sheetName, "Sheet", 200), Encoding = Guard.OptionalBounded(encoding, "Encoding", 40),
        Delimiter = Guard.OptionalBounded(delimiter, "Delimiter", 5), ProfileJson = profileJson, Note = Guard.OptionalBounded(note, "Note", 1000),
        UploadedBy = Guard.NotBlank(uploadedBy, "Uploaded by", 200), UploadedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

/// <summary>One source row, with the raw values exactly as supplied (source values are retained), the mapped values, and what the dry run decided.</summary>
public sealed class StagingRow : ITenantOwned
{
    private StagingRow() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid SourceFileId { get; private set; }
    /// <summary>Position in the file, 1-based. The processing order and the checkpoint position.</summary>
    public int Sequence { get; private set; }
    /// <summary>The physical row or line number in the source, for error reports.</summary>
    public int RowNumber { get; private set; }
    public string RawValuesJson { get; private set; } = "{}";
    public string? MappedValuesJson { get; private set; }
    public string RowHash { get; private set; } = "";
    public string? IdempotencyKey { get; private set; }
    public string? SourceRecordId { get; private set; }
    public string? AgreementReference { get; private set; }
    public RowOutcome Outcome { get; private set; }
    public PlannedAction PlannedAction { get; private set; }
    public ReviewDecision ReviewDecision { get; private set; }
    public Guid? TargetContractId { get; private set; }
    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }
    /// <summary>A short readable plan (for the preview): what the commit would do.</summary>
    public string? PlanSummary { get; private set; }

    public static StagingRow Create(Guid tenantId, Guid batchId, Guid organisationId, Guid sourceFileId, int sequence, int rowNumber, string rawJson, string rowHash) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        SourceFileId = sourceFileId, Sequence = sequence, RowNumber = rowNumber, RawValuesJson = rawJson, RowHash = rowHash, Outcome = RowOutcome.Pending
    };

    /// <summary>Records the dry run's decision. Called again on every validation run: the row is recomputed, never accumulated.</summary>
    public void RecordValidation(string? mappedJson, string? sourceRecordId, string? agreementReference, string? idempotencyKey, RowOutcome outcome, PlannedAction action, Guid? targetContractId,
        int errors, int warnings, string? planSummary, bool? reviewApplicable)
    {
        if (Outcome is RowOutcome.Created or RowOutcome.Updated) throw new DomainException("row_committed", "A committed row cannot be validated again.");
        MappedValuesJson = mappedJson; SourceRecordId = sourceRecordId; AgreementReference = agreementReference; IdempotencyKey = idempotencyKey;
        Outcome = outcome; PlannedAction = action; TargetContractId = targetContractId; ErrorCount = errors; WarningCount = warnings; PlanSummary = Guard.OptionalBounded(planSummary, "Plan", 1000);
        // A decision a person made is kept for as long as the duplicate question still applies to the row.
        // null: the mapping pass has not decided yet, so a decision already made is left alone for the planning pass.
        if (reviewApplicable is null) return;
        if (reviewApplicable.Value) { if (ReviewDecision == ReviewDecision.None) ReviewDecision = ReviewDecision.Required; }
        else ReviewDecision = ReviewDecision.None;
    }

    public void Decide(ReviewDecision decision)
    {
        if (ReviewDecision == ReviewDecision.None) throw new DomainException("no_review_needed", "This row is not waiting for a decision.");
        if (decision is ReviewDecision.None or ReviewDecision.Required) throw new DomainException("invalid_decision", "Choose skip, update, create with a new reference, or reject.");
        ReviewDecision = decision;
    }

    public void MarkCreated(Guid contractId) { Outcome = RowOutcome.Created; TargetContractId = contractId; }
    public void MarkUpdated(Guid contractId) { Outcome = RowOutcome.Updated; TargetContractId = contractId; }
    public void MarkSkipped(Guid? contractId, string? note) { Outcome = RowOutcome.Skipped; TargetContractId = contractId ?? TargetContractId; PlanSummary = note ?? PlanSummary; PlannedAction = PlannedAction.Skip; }
    public void MarkRejectedAtCommit(string note) { Outcome = RowOutcome.Rejected; PlanSummary = Guard.OptionalBounded(note, "Plan", 1000); PlannedAction = PlannedAction.None; ErrorCount++; }
}

/// <summary>A message about a row, manifest entry, file or batch. Stable catalogue code, no secrets, no other tenants' data.</summary>
public sealed class StagingMessage : ITenantOwned
{
    private StagingMessage() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public MessageSubject Subject { get; private set; }
    public MessagePhase Phase { get; private set; }
    public Guid? SubjectId { get; private set; }
    /// <summary>Row number in the source, or the file path, so the message can be found without opening the application.</summary>
    public string Location { get; private set; } = "";
    public MessageSeverity Severity { get; private set; }
    public string Code { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string? FieldName { get; private set; }
    public DateTime CreatedUtc { get; private set; }

    public static StagingMessage Create(Guid tenantId, Guid batchId, MessagePhase phase, MessageSubject subject, Guid? subjectId, string location, MessageSeverity severity, string code, string message,
        string? fieldName, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), Phase = phase, Subject = subject, SubjectId = subjectId,
        Location = Guard.NotBlank(location, "Location", 500), Severity = severity, Code = Guard.NotBlank(code, "Code", 20), Message = Guard.NotBlank(message.Length > 1000 ? message[..1000] : message, "Message", 1000),
        FieldName = Guard.OptionalBounded(fieldName, "Field", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

/// <summary>One row of the attachment manifest, exactly as supplied plus what was understood from it.</summary>
public sealed class AttachmentManifestEntry : ITenantOwned
{
    private AttachmentManifestEntry() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid SourceFileId { get; private set; }
    public int Sequence { get; private set; }
    public int RowNumber { get; private set; }
    public string ColumnsJson { get; private set; } = "{}";
    public string? SourceRecordId { get; private set; }
    public string? AgreementReference { get; private set; }
    public string? RawRelativePath { get; private set; }
    /// <summary>Normalised: forward slashes, no traversal. Null when the path was rejected.</summary>
    public string? RelativePath { get; private set; }
    public string? FileName { get; private set; }
    public string? DocumentTypeRaw { get; private set; }
    public string? DocumentTypeCode { get; private set; }
    public DateOnly? DocumentDate { get; private set; }
    public string? VersionLabel { get; private set; }
    public bool IsExecutedCopy { get; private set; }
    public string? ParentDocumentSourceId { get; private set; }
    public string? DeclaredSha256 { get; private set; }
    public string? DocumentSourceId { get; private set; }
    public ManifestEntryState State { get; private set; }
    public Guid? StagedFileId { get; private set; }

    public static AttachmentManifestEntry Create(Guid tenantId, Guid batchId, Guid sourceFileId, int sequence, int rowNumber, string columnsJson, string? sourceRecordId, string? agreementReference,
        string? rawPath, string? relativePath, string? fileName, string? typeRaw, string? typeCode, DateOnly? documentDate, string? versionLabel, bool executed, string? parentSourceId,
        string? declaredSha256, string? documentSourceId, ManifestEntryState state) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), SourceFileId = sourceFileId, Sequence = sequence, RowNumber = rowNumber,
        ColumnsJson = columnsJson, SourceRecordId = sourceRecordId, AgreementReference = agreementReference, RawRelativePath = rawPath, RelativePath = relativePath, FileName = fileName,
        DocumentTypeRaw = typeRaw, DocumentTypeCode = typeCode, DocumentDate = documentDate, VersionLabel = versionLabel, IsExecutedCopy = executed, ParentDocumentSourceId = parentSourceId,
        DeclaredSha256 = declaredSha256, DocumentSourceId = documentSourceId, State = state
    };

    public void Pair(Guid stagedFileId) { StagedFileId = stagedFileId; State = ManifestEntryState.Paired; }
    public void MarkOrphan() { StagedFileId = null; if (State != ManifestEntryState.Invalid) State = ManifestEntryState.OrphanManifest; }
}

/// <summary>An attachment held in restricted staging: it is never visible to ordinary users and never imported until it has a verified target.</summary>
public sealed class StagedFile : ITenantOwned
{
    private StagedFile() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid SourceFileId { get; private set; }
    public string RelativePath { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string? Extension { get; private set; }
    public string? Sha256 { get; private set; }
    public long SizeBytes { get; private set; }
    /// <summary>Restricted staging location (content-addressed, tenant-bound). Null for a file that was rejected before it was stored.</summary>
    public string? QuarantineKey { get; private set; }
    public string? MimeType { get; private set; }
    public FileScan Scan { get; private set; }
    public FileState State { get; private set; }
    public QueueReason QueueReason { get; private set; }
    public Guid? ManifestEntryId { get; private set; }
    public Guid? ContractStagingRowId { get; private set; }
    public MatchMethod? MatchedBy { get; private set; }
    public Guid? TargetDocumentId { get; private set; }
    public string? DocumentSourceId { get; private set; }
    public string? Note { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTime? ResolvedUtc { get; private set; }
    /// <summary>A person released this file from a duplicate review: it is imported as a separate document.</summary>
    public bool ReleasedFromHold { get; private set; }

    public static StagedFile Create(Guid tenantId, Guid batchId, Guid organisationId, Guid sourceFileId, string relativePath, string fileName, string? extension, string? sha256, long size,
        string? quarantineKey, string? mime, FileScan scan, FileState state, string? note) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = Guard.NotEmpty(batchId, nameof(batchId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        SourceFileId = sourceFileId, RelativePath = Guard.NotBlank(relativePath, "Relative path", 500), FileName = Guard.NotBlank(fileName, "File name", 260), Extension = Guard.OptionalBounded(extension, "Extension", 20),
        Sha256 = sha256, SizeBytes = size, QuarantineKey = quarantineKey, MimeType = Guard.OptionalBounded(mime, "MIME type", 150), Scan = scan, State = state, Note = Guard.OptionalBounded(note, "Note", 1000)
    };

    /// <summary>Records the matching outcome of a validation run. A file a person has already resolved, discarded or imported is never touched.</summary>
    public void RecordMatch(FileState state, QueueReason reason, Guid? manifestEntryId, Guid? contractRowId, MatchMethod? method, string? documentSourceId, string? note)
    {
        if (State is FileState.Discarded or FileState.Imported or FileState.Invalid) return;
        State = state; QueueReason = reason; ManifestEntryId = manifestEntryId; ContractStagingRowId = contractRowId; MatchedBy = method; DocumentSourceId = documentSourceId;
        Note = Guard.OptionalBounded(note, "Note", 1000);
    }

    public void MarkInvalid(string note) { State = FileState.Invalid; QueueReason = QueueReason.None; Note = Guard.OptionalBounded(note, "Note", 1000); }
    public void MarkImported(Guid documentId) { State = FileState.Imported; QueueReason = QueueReason.None; TargetDocumentId = documentId; }
    public void MarkDuplicate(Guid? documentId, string note) { State = FileState.Duplicate; QueueReason = QueueReason.None; TargetDocumentId = documentId; Note = Guard.OptionalBounded(note, "Note", 1000); }
    public void MarkFailed(string note) { State = FileState.Failed; Note = Guard.OptionalBounded(note, "Note", 1000); }
    public void HoldForReview(string note) { State = FileState.Unmatched; QueueReason = QueueReason.DuplicateReview; Note = Guard.OptionalBounded(note, "Note", 1000); }

    /// <summary>A person pairs the file with a contract row. The match is recorded as manual with the person's name.</summary>
    public void ResolveManually(Guid contractRowId, string by, DateTime nowUtc)
    {
        if (State is not (FileState.Unmatched or FileState.Matched or FileState.Staged)) throw new DomainException("file_not_open", $"A file in state {State} cannot be matched by hand.");
        ContractStagingRowId = contractRowId; MatchedBy = MatchMethod.Manual; State = FileState.Matched; QueueReason = QueueReason.None;
        ResolvedBy = Guard.NotBlank(by, "Resolved by", 200); ResolvedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    /// <summary>A person deliberately discards the file (an audited action). It stays in staging, marked, and is never silently dropped.</summary>
    public void Discard(string reason, string by, DateTime nowUtc)
    {
        if (State is FileState.Imported) throw new DomainException("file_imported", "An imported file cannot be discarded; use rollback.");
        State = FileState.Discarded; QueueReason = QueueReason.None; Note = Guard.NotBlank(reason, "Reason", 1000);
        ResolvedBy = Guard.NotBlank(by, "Resolved by", 200); ResolvedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    /// <summary>A person releases a file held for a duplicate review so that it is imported as a separate document.</summary>
    public void ReleaseHeld(string by, DateTime nowUtc)
    {
        if (State != FileState.Unmatched || QueueReason != QueueReason.DuplicateReview) throw new DomainException("file_not_held", "Only a file held for duplicate review can be released.");
        State = FileState.Matched; QueueReason = QueueReason.None; ReleasedFromHold = true; ResolvedBy = Guard.NotBlank(by, "Resolved by", 200); ResolvedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    /// <summary>The restricted staging copy was deleted (after the batch ended). The record of the file, its hash and what became of it stay.</summary>
    public void ClearQuarantine() { QuarantineKey = null; }
}

/// <summary>A reason a file was paired with a contract. Automatic proposals are exact and deterministic; several candidates are never resolved by the platform.</summary>
public sealed class MatchProposal : ITenantOwned
{
    private MatchProposal() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid StagedFileId { get; private set; }
    public Guid ContractStagingRowId { get; private set; }
    public MatchMethod Method { get; private set; }
    public string Reason { get; private set; } = "";
    public int CandidateCount { get; private set; }
    public ProposalStatus Status { get; private set; }
    public string? ConfirmedBy { get; private set; }
    public DateTime? ConfirmedUtc { get; private set; }

    public static MatchProposal Create(Guid tenantId, Guid batchId, Guid stagedFileId, Guid contractRowId, MatchMethod method, string reason, int candidates, ProposalStatus status) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), BatchId = batchId, StagedFileId = stagedFileId, ContractStagingRowId = contractRowId, Method = method,
        Reason = Guard.NotBlank(reason, "Reason", 1000), CandidateCount = candidates, Status = status
    };

    public void Confirm(string by, DateTime nowUtc) { Status = ProposalStatus.Accepted; ConfirmedBy = Guard.NotBlank(by, "Confirmed by", 200); ConfirmedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); }
    public void Supersede() { if (Status == ProposalStatus.Proposed) Status = ProposalStatus.Superseded; }
}
