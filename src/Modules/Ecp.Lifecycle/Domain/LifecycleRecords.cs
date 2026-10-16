using Ecp.SharedKernel;

namespace Ecp.Lifecycle.Domain;

/// <summary>What a workflow runs on: a contract, or one of its Variations.</summary>
public static class SubjectKinds
{
    public const string Contract = "Contract";
    public const string Variation = "Variation";
    public static bool IsKnown(string? k) => k is Contract or Variation;
}

/// <summary>One state change of a contract or Variation. Append-only: history is never edited or removed.</summary>
public sealed class StatusHistory : ITenantOwned
{
    private StatusHistory() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string SubjectKind { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public Guid ContractId { get; private set; }
    public string FromStatus { get; private set; } = "";
    public string ToStatus { get; private set; } = "";
    public string TransitionCode { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public Guid? ActorUserId { get; private set; }
    public string? Comment { get; private set; }
    public string? WorkflowRef { get; private set; }
    public Guid? ConfigReleaseId { get; private set; }
    public string CorrelationId { get; private set; } = "";
    /// <summary>Position in the contract's history (its own states and its Variations'). Timestamps can tie; the sequence cannot.</summary>
    public int Sequence { get; private set; }
    /// <summary>The caller's idempotency key. A repeat of the same key returns the first outcome and writes nothing.</summary>
    public string? ClientRequestId { get; private set; }
    public Guid? ElevationId { get; private set; }
    public DateTime OccurredUtc { get; private set; }

    public static StatusHistory Record(Guid tenantId, Guid organisationId, string subjectKind, Guid subjectId, Guid contractId, string from, string to, string transition, string actor,
        Guid? actorUserId, string? comment, string? workflowRef, Guid? releaseId, string correlationId, string? clientRequestId, Guid? elevationId, DateTime nowUtc, int sequence) => new()
    {
        Sequence = sequence, Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, SubjectKind = subjectKind, SubjectId = subjectId, ContractId = contractId,
        FromStatus = Guard.NotBlank(from, "From status", 100), ToStatus = Guard.NotBlank(to, "To status", 100), TransitionCode = Guard.NotBlank(transition, "Transition", 100),
        Actor = Guard.NotBlank(actor, "Actor", 200), ActorUserId = actorUserId, Comment = Guard.OptionalBounded(comment, "Comment", 4000), WorkflowRef = workflowRef, ConfigReleaseId = releaseId,
        CorrelationId = Guard.NotBlank(correlationId, "Correlation id", 64), ClientRequestId = Guard.OptionalBounded(clientRequestId, "Client request id", 100), ElevationId = elevationId,
        OccurredUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

public enum LegalReviewStatus { AwaitingLegal, InReview, ReworkRequired, Cleared, Withdrawn }

public enum LegalRoundOutcome { Submitted, InReview, ReturnedForRework, Cleared, Withdrawn }

/// <summary>
/// The Legal review of a contract (or of a Variation). It holds rounds; a round is never overwritten: a return for rework closes it and a resubmission opens the next.
/// Clearance is Legal's view that the content is acceptable. It is not register authorisation and does not make anything Effective.
/// </summary>
public sealed class LegalReview : ITenantOwned
{
    private LegalReview() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string SubjectKind { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public Guid ContractId { get; private set; }
    public LegalReviewStatus Status { get; private set; }
    public int CurrentRound { get; private set; }
    public Guid? AssignedToUserId { get; private set; }
    public string? AssignedToName { get; private set; }
    public Guid SubmittedByUserId { get; private set; }
    public string SubmittedByName { get; private set; } = "";
    public DateTime SubmittedUtc { get; private set; }
    public DateTime LastActivityUtc { get; private set; }
    public string? ClearedBy { get; private set; }
    public DateTime? ClearedUtc { get; private set; }
    /// <summary>What was current when Legal cleared: the contract's last-updated stamp, so a later change to a controlled field is detectable.</summary>
    public DateTime? ClearedContractStampUtc { get; private set; }
    public string? LastOutcome { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static LegalReview Open(Guid tenantId, Guid organisationId, string subjectKind, Guid subjectId, Guid contractId, Guid submittedBy, string submittedByName, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, SubjectKind = subjectKind, SubjectId = subjectId, ContractId = contractId,
        Status = LegalReviewStatus.AwaitingLegal, CurrentRound = 1, SubmittedByUserId = submittedBy, SubmittedByName = Guard.NotBlank(submittedByName, "Submitted by", 200),
        SubmittedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), LastActivityUtc = nowUtc
    };

    public bool IsOpen => Status is LegalReviewStatus.AwaitingLegal or LegalReviewStatus.InReview or LegalReviewStatus.ReworkRequired;

    public void Assign(Guid userId, string name, DateTime nowUtc)
    {
        if (Status is LegalReviewStatus.Cleared or LegalReviewStatus.Withdrawn) throw new DomainException("review_closed", "This Legal review has finished.");
        AssignedToUserId = userId; AssignedToName = Guard.NotBlank(name, "Assigned to", 200); LastActivityUtc = nowUtc;
    }

    public void Begin(Guid userId, string name, DateTime nowUtc)
    {
        if (Status != LegalReviewStatus.AwaitingLegal) throw new DomainException("review_state", "Legal can begin a review only when it is waiting for Legal.");
        Status = LegalReviewStatus.InReview; AssignedToUserId ??= userId; AssignedToName ??= Guard.NotBlank(name, "Assigned to", 200); LastActivityUtc = nowUtc;
    }

    public void ReturnForRework(DateTime nowUtc)
    {
        if (Status != LegalReviewStatus.InReview) throw new DomainException("review_state", "Only a review that is in progress can be returned for rework.");
        Status = LegalReviewStatus.ReworkRequired; LastOutcome = "ReturnedForRework"; LastActivityUtc = nowUtc;
    }

    public void Resubmit(Guid by, string byName, DateTime nowUtc)
    {
        if (Status != LegalReviewStatus.ReworkRequired && Status != LegalReviewStatus.Cleared) throw new DomainException("review_state", "A Legal review can be resubmitted only after it was returned for rework or cleared.");
        CurrentRound++; Status = LegalReviewStatus.AwaitingLegal; SubmittedByUserId = by; SubmittedByName = Guard.NotBlank(byName, "Submitted by", 200); SubmittedUtc = nowUtc;
        ClearedBy = null; ClearedUtc = null; ClearedContractStampUtc = null; AssignedToUserId = null; AssignedToName = null; LastActivityUtc = nowUtc;
    }

    public void Clear(string by, DateTime contractStampUtc, DateTime nowUtc)
    {
        if (Status != LegalReviewStatus.InReview) throw new DomainException("review_state", "Only a review that is in progress can be cleared.");
        Status = LegalReviewStatus.Cleared; ClearedBy = Guard.NotBlank(by, "Cleared by", 200); ClearedUtc = nowUtc; ClearedContractStampUtc = contractStampUtc; LastOutcome = "Cleared"; LastActivityUtc = nowUtc;
    }

    public void Withdraw(DateTime nowUtc)
    {
        if (!IsOpen) throw new DomainException("review_closed", "This Legal review has finished.");
        Status = LegalReviewStatus.Withdrawn; LastOutcome = "Withdrawn"; LastActivityUtc = nowUtc;
    }

    public void Touch(DateTime nowUtc) => LastActivityUtc = nowUtc;
}

public sealed class LegalRound : ITenantOwned
{
    private LegalRound() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ReviewId { get; private set; }
    public int Number { get; private set; }
    public LegalRoundOutcome Outcome { get; private set; }
    public Guid SubmittedByUserId { get; private set; }
    public string SubmittedByName { get; private set; } = "";
    public DateTime SubmittedUtc { get; private set; }
    public string? SubmissionNote { get; private set; }
    public Guid? ReviewerUserId { get; private set; }
    public string? ReviewerName { get; private set; }
    public DateTime? StartedUtc { get; private set; }
    public string? ReworkReason { get; private set; }
    public string? ClosedBy { get; private set; }
    public DateTime? ClosedUtc { get; private set; }
    /// <summary>The document versions the round was submitted with, as JSON, so what Legal saw is provable after later versions are added.</summary>
    public string DocumentsJson { get; private set; } = "[]";
    public byte[] RowVersion { get; private set; } = [];

    public static LegalRound Open(Guid tenantId, Guid reviewId, int number, Guid by, string byName, string? note, string documentsJson, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, ReviewId = reviewId, Number = number, Outcome = LegalRoundOutcome.Submitted, SubmittedByUserId = by,
        SubmittedByName = Guard.NotBlank(byName, "Submitted by", 200), SubmittedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), SubmissionNote = Guard.OptionalBounded(note, "Note", 4000), DocumentsJson = documentsJson
    };

    public bool IsOpen => Outcome is LegalRoundOutcome.Submitted or LegalRoundOutcome.InReview;

    public void Start(Guid reviewer, string name, DateTime nowUtc)
    {
        if (Outcome != LegalRoundOutcome.Submitted) throw new DomainException("round_state", "This round has already been started or closed.");
        Outcome = LegalRoundOutcome.InReview; ReviewerUserId = reviewer; ReviewerName = Guard.NotBlank(name, "Reviewer", 200); StartedUtc = nowUtc;
    }

    public void Return(string by, string reason, DateTime nowUtc)
    {
        if (Outcome != LegalRoundOutcome.InReview) throw new DomainException("round_state", "Only a round in review can be returned.");
        ReworkReason = Guard.NotBlank(reason, "Reason for rework", 4000); Close(LegalRoundOutcome.ReturnedForRework, by, nowUtc);
    }

    public void Clear(string by, DateTime nowUtc)
    {
        if (Outcome != LegalRoundOutcome.InReview) throw new DomainException("round_state", "Only a round in review can be cleared.");
        Close(LegalRoundOutcome.Cleared, by, nowUtc);
    }

    public void Withdraw(string by, string? reason, DateTime nowUtc)
    {
        if (!IsOpen) throw new DomainException("round_state", "This round has already been closed.");
        ReworkReason = Guard.OptionalBounded(reason, "Reason", 4000); Close(LegalRoundOutcome.Withdrawn, by, nowUtc);
    }

    private void Close(LegalRoundOutcome outcome, string by, DateTime nowUtc) { Outcome = outcome; ClosedBy = Guard.NotBlank(by, "Closed by", 200); ClosedUtc = nowUtc; }
}

/// <summary>A comment or reply in a round. Append-only: a comment is never edited or deleted, so what was said to whom, and when, stays provable.</summary>
public sealed class LegalComment : ITenantOwned
{
    private LegalComment() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ReviewId { get; private set; }
    public Guid RoundId { get; private set; }
    public Guid? ParentCommentId { get; private set; }
    public Guid AuthorUserId { get; private set; }
    public string AuthorName { get; private set; } = "";
    /// <summary>Legal, or Owner (the contract owner's response).</summary>
    public string Side { get; private set; } = "";
    public string Body { get; private set; } = "";
    /// <summary>The record's classification when written. A reader who cannot see that level does not see the review at all.</summary>
    public string Classification { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }

    public static LegalComment Add(Guid tenantId, Guid reviewId, Guid roundId, Guid? parent, Guid author, string authorName, string side, string body, string classification, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, ReviewId = reviewId, RoundId = roundId, ParentCommentId = parent, AuthorUserId = author, AuthorName = Guard.NotBlank(authorName, "Author", 200),
        Side = side is "Legal" or "Owner" ? side : throw new DomainException("invalid_side", "A comment is from Legal or the owner."), Body = Guard.NotBlank(body, "Comment", 8000),
        Classification = Guard.NotBlank(classification, "Classification", 30), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

public enum LegalAttachmentKind { Markup, Supporting, Response }

/// <summary>A document version attached to a round. The document is a classified version in the document store; this row says what role it played in the review.</summary>
public sealed class LegalAttachment : ITenantOwned
{
    private LegalAttachment() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ReviewId { get; private set; }
    public Guid RoundId { get; private set; }
    public Guid? CommentId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int VersionNumber { get; private set; }
    public string Sha256 { get; private set; } = "";
    public LegalAttachmentKind Kind { get; private set; }
    public string FileName { get; private set; } = "";
    public string AddedBy { get; private set; } = "";
    public DateTime AddedUtc { get; private set; }

    public static LegalAttachment Add(Guid tenantId, Guid reviewId, Guid roundId, Guid? commentId, Guid documentId, int versionNumber, string sha256, LegalAttachmentKind kind, string fileName, string addedBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, ReviewId = reviewId, RoundId = roundId, CommentId = commentId, DocumentId = Guard.NotEmpty(documentId, nameof(documentId)), VersionNumber = versionNumber,
        Sha256 = Guard.NotBlank(sha256, "Hash", 64), Kind = kind, FileName = Guard.NotBlank(fileName, "File name", 260), AddedBy = Guard.NotBlank(addedBy, "Added by", 200), AddedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

/// <summary>An entry on the Legal review timeline. Append-only.</summary>
public sealed class LegalEvent : ITenantOwned
{
    private LegalEvent() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ReviewId { get; private set; }
    public Guid? RoundId { get; private set; }
    public string Kind { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public string? Detail { get; private set; }
    public Guid? RefId { get; private set; }
    public DateTime OccurredUtc { get; private set; }
    public int Sequence { get; private set; }

    public static LegalEvent Record(Guid tenantId, Guid reviewId, Guid? roundId, string kind, string actor, string? detail, Guid? refId, DateTime nowUtc, int sequence) => new()
    {
        Sequence = sequence, Id = Ids.New(), TenantId = tenantId, ReviewId = reviewId, RoundId = roundId, Kind = Guard.NotBlank(kind, "Kind", 40), Actor = Guard.NotBlank(actor, "Actor", 200),
        Detail = Guard.OptionalBounded(detail, "Detail", 1000), RefId = refId, OccurredUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

public enum ExecutionStatus { NotStarted, Sent, Delivered, Completed, Declined, Voided }

/// <summary>
/// Where execution stands. Signing and the Delegation of Authority belong to the external signature service: the platform records the envelope id and status and receives the executed copy.
/// Execution never approves, registers or locks anything; it only lets the executed-copy guard be satisfied.
/// </summary>
public sealed class ExecutionRecord : ITenantOwned
{
    private ExecutionRecord() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string SubjectKind { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public Guid ContractId { get; private set; }
    public string Provider { get; private set; } = "";
    public string? EnvelopeId { get; private set; }
    public ExecutionStatus Status { get; private set; }
    public bool IsCurrent { get; private set; }
    public Guid? ExecutedDocumentId { get; private set; }
    public DateTime? ExecutedUtc { get; private set; }
    public string StartedBy { get; private set; } = "";
    public DateTime StartedUtc { get; private set; }
    public DateTime StatusUpdatedUtc { get; private set; }
    public string? Detail { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ExecutionRecord Start(Guid tenantId, Guid organisationId, string subjectKind, Guid subjectId, Guid contractId, string provider, string? envelopeId, ExecutionStatus status, string by, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, SubjectKind = subjectKind, SubjectId = subjectId, ContractId = contractId, Provider = Guard.NotBlank(provider, "Provider", 40),
        EnvelopeId = Guard.OptionalBounded(envelopeId, "Envelope id", 200), Status = status, IsCurrent = true, StartedBy = Guard.NotBlank(by, "Started by", 200), StartedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), StatusUpdatedUtc = nowUtc
    };

    public void Supersede() => IsCurrent = false;

    public void SetStatus(ExecutionStatus status, string? detail, DateTime nowUtc)
    {
        if (Status == ExecutionStatus.Completed && status != ExecutionStatus.Completed) throw new DomainException("execution_completed", "Execution has completed and cannot go back.");
        Status = status; Detail = Guard.OptionalBounded(detail, "Detail", 1000); StatusUpdatedUtc = nowUtc;
    }

    public void Complete(Guid executedDocumentId, DateTime nowUtc)
    {
        ExecutedDocumentId = Guard.NotEmpty(executedDocumentId, nameof(executedDocumentId)); ExecutedUtc = nowUtc; Status = ExecutionStatus.Completed; StatusUpdatedUtc = nowUtc;
    }
}

public enum ApprovalStatus { Pending, Approved, Rejected, Withdrawn }

/// <summary>
/// A request for register authorisation. Everything the approver relies on is frozen into <see cref="SnapshotJson"/> at submission and never changes. The decision is made once:
/// the first response wins, and a second attempt (from a backup, a double click or a crafted call) fails.
/// </summary>
public sealed class ApprovalRequest : ITenantOwned
{
    private ApprovalRequest() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string SubjectKind { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public Guid ContractId { get; private set; }
    public ApprovalStatus Status { get; private set; }
    public string ApproverGroupKey { get; private set; } = "";
    public Guid SubmittedByUserId { get; private set; }
    public string SubmittedByName { get; private set; } = "";
    public DateTime SubmittedUtc { get; private set; }
    public Guid? ExecutedDocumentId { get; private set; }
    public string? ExecutedDocumentSha256 { get; private set; }
    public string SnapshotJson { get; private set; } = "{}";
    public string SnapshotSha256 { get; private set; } = "";
    /// <summary>Who could act at submission, as JSON (primary approvers and backups), so the decision can be judged against the group as it was.</summary>
    public string EligibleApproversJson { get; private set; } = "[]";
    public Guid? DecidedByUserId { get; private set; }
    public string? DecidedByName { get; private set; }
    public string? DecidedAsRole { get; private set; }
    public Guid? OnBehalfOfUserId { get; private set; }
    public string? OnBehalfOfName { get; private set; }
    public string? DecisionComment { get; private set; }
    public DateTime? DecidedUtc { get; private set; }
    public Guid? EvidenceDocumentId { get; private set; }
    /// <summary>The approval matrix the workflow named, when it names one. Null for the reference model (one approver group, no plan).</summary>
    public string? MatrixCode { get; private set; }
    public int? MatrixRelease { get; private set; }
    /// <summary>The resolved plan (steps, stages and people) as JSON, stored once and never changed: later matrix versions do not alter an in-flight submission.</summary>
    public string? PlanJson { get; private set; }
    public string? PlanSha256 { get; private set; }
    /// <summary>The approver groups that currently have something to decide (the groups of the open steps), comma separated; null means the single <see cref="ApproverGroupKey"/>.</summary>
    public string? ActiveGroupKeys { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool HasPlan => PlanJson is not null;
    public IReadOnlyList<string> PendingGroups => ActiveGroupKeys is { Length: > 0 } g ? g.Split(',', StringSplitOptions.RemoveEmptyEntries) : [ApproverGroupKey];

    public void AttachPlan(string matrixCode, int? release, string planJson, string planSha256, IEnumerable<string> activeGroups)
    {
        if (Status != ApprovalStatus.Pending || PlanJson is not null) throw new DomainException("plan_fixed", "The plan of a submission is set once, when it is submitted.");
        MatrixCode = Guard.NotBlank(matrixCode, "Matrix", 50); MatrixRelease = release; PlanJson = Guard.NotBlank(planJson, "Plan", 1_000_000); PlanSha256 = Guard.NotBlank(planSha256, "Plan checksum", 64);
        SetActiveGroups(activeGroups);
    }

    public void SetActiveGroups(IEnumerable<string> groups)
    {
        var list = groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.Ordinal).ToList();
        ActiveGroupKeys = list.Count == 0 ? null : Guard.NotBlank(string.Join(",", list), "Active groups", 1000);
    }

    public static ApprovalRequest Submit(Guid tenantId, Guid organisationId, string subjectKind, Guid subjectId, Guid contractId, string groupKey, Guid submittedBy, string submittedByName,
        Guid? executedDocumentId, string? executedSha256, string snapshotJson, string snapshotSha256, string eligibleJson, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, SubjectKind = subjectKind, SubjectId = subjectId, ContractId = contractId, Status = ApprovalStatus.Pending,
        ApproverGroupKey = Guard.NotBlank(groupKey, "Approver group", 100), SubmittedByUserId = submittedBy, SubmittedByName = Guard.NotBlank(submittedByName, "Submitted by", 200),
        SubmittedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), ExecutedDocumentId = executedDocumentId, ExecutedDocumentSha256 = executedSha256, SnapshotJson = snapshotJson,
        SnapshotSha256 = snapshotSha256, EligibleApproversJson = eligibleJson
    };

    public void Decide(ApprovalStatus status, Guid by, string byName, string role, Guid? onBehalfOf, string? onBehalfOfName, string? comment, Guid? evidenceDocumentId, DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending) throw new DomainException("already_decided", "This request has already been decided. The first response stands.");
        if (status is not (ApprovalStatus.Approved or ApprovalStatus.Rejected)) throw new DomainException("invalid_decision", "A decision is an approval or a rejection.");
        if (by == SubmittedByUserId) throw new DomainException("self_approval", "A submitter cannot decide their own submission.");
        if (status == ApprovalStatus.Rejected && string.IsNullOrWhiteSpace(comment)) throw new DomainException("comment_required", "A rejection needs a comment.");
        Status = status; DecidedByUserId = by; DecidedByName = Guard.NotBlank(byName, "Decided by", 200); DecidedAsRole = Guard.NotBlank(role, "Role", 50);
        OnBehalfOfUserId = onBehalfOf; OnBehalfOfName = onBehalfOfName; DecisionComment = Guard.OptionalBounded(comment, "Comment", 4000); DecidedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        EvidenceDocumentId = evidenceDocumentId;
    }

    public void Withdraw(DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending) throw new DomainException("already_decided", "This request has already been decided.");
        Status = ApprovalStatus.Withdrawn; DecidedUtc = nowUtc;
    }
}

/// <summary>
/// One response to one step of a plan-based approval: who responded, in which capacity (their own seat, a backup, or a delegate for someone), and whether they approved. Recorded in order, never changed.
/// The request is decided as a whole when the plan is satisfied (approved) or when anyone with a say on an open step rejects it.
/// </summary>
public sealed class ApprovalStepResponse : ITenantOwned
{
    private ApprovalStepResponse() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RequestId { get; private set; }
    public int Sequence { get; private set; }
    public string StepKey { get; private set; } = "";
    public Guid UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public string Role { get; private set; } = "";
    public Guid? OnBehalfOfUserId { get; private set; }
    public string? OnBehalfOfName { get; private set; }
    public bool Approved { get; private set; }
    public string? Comment { get; private set; }
    public DateTime RespondedUtc { get; private set; }

    public static ApprovalStepResponse Record(Guid tenantId, Guid requestId, int sequence, string stepKey, Guid userId, string userName, string role, Guid? onBehalfOf, string? onBehalfOfName, bool approved,
        string? comment, DateTime nowUtc)
    {
        if (!approved && string.IsNullOrWhiteSpace(comment)) throw new DomainException("comment_required", "A rejection needs a comment.");
        return new ApprovalStepResponse
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), RequestId = requestId, Sequence = sequence, StepKey = Guard.NotBlank(stepKey, "Step", 120), UserId = userId,
            UserName = Guard.NotBlank(userName, "Responder", 200), Role = Guard.NotBlank(role, "Role", 20), OnBehalfOfUserId = onBehalfOf, OnBehalfOfName = Guard.OptionalBounded(onBehalfOfName, "On behalf of", 200),
            Approved = approved, Comment = Guard.OptionalBounded(comment, "Comment", 4000), RespondedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }
}

/// <summary>An audited emergency unlock of a locked contract. Reason-bound, time-limited and limited to the fields it names; it never touches the original approval or its snapshot.</summary>
public sealed class RecordUnlock : ITenantOwned
{
    private RecordUnlock() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid OpenedByUserId { get; private set; }
    public string OpenedByName { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string FieldsJson { get; private set; } = "[]";
    public DateTime OpenedUtc { get; private set; }
    public DateTime ExpiresUtc { get; private set; }
    public DateTime ReauthenticatedUtc { get; private set; }
    public DateTime? ClosedUtc { get; private set; }
    public string? ClosedBy { get; private set; }
    /// <summary>Early, Expired or Superseded.</summary>
    public string? CloseKind { get; private set; }
    public int ChangeCount { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsOpenAt(DateTime nowUtc) => ClosedUtc is null && nowUtc < ExpiresUtc;

    public static RecordUnlock Open(Guid tenantId, Guid organisationId, Guid contractId, Guid by, string byName, string reason, string fieldsJson, DateTime reauthUtc, TimeSpan duration, DateTime nowUtc)
    {
        var why = Guard.NotBlank(reason, "Reason", 1000);
        if (why.Trim().Length < 20) throw new DomainException("reason_too_short", "Give a reason of at least 20 characters: it is shown on the record and kept in the audit.");
        if (duration <= TimeSpan.Zero) throw new DomainException("invalid_duration", "An unlock needs a time limit.");
        return new()
        {
            Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, ContractId = contractId, OpenedByUserId = by, OpenedByName = Guard.NotBlank(byName, "Opened by", 200), Reason = why.Trim(),
            FieldsJson = fieldsJson, OpenedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), ExpiresUtc = nowUtc + duration, ReauthenticatedUtc = reauthUtc
        };
    }

    public void Close(string by, string kind, DateTime nowUtc)
    {
        if (ClosedUtc is not null) return;
        ClosedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); ClosedBy = Guard.NotBlank(by, "Closed by", 200); CloseKind = kind;
    }

    public void CountChange() => ChangeCount++;
}

public enum VariationOutcome { Applied, Rejected, Cancelled }

/// <summary>
/// A formal change to an Effective agreement. The parent stays locked while it is open. Before and after values are captured when the Variation is raised and again, authoritatively,
/// at the moment it is applied.
/// </summary>
public sealed class Variation : ITenantOwned
{
    private Variation() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid ContractId { get; private set; }
    public string Reference { get; private set; } = "";
    public int Sequence { get; private set; }
    public string Title { get; private set; } = "";
    public string? Description { get; private set; }
    public string Reason { get; private set; } = "";
    public DateOnly? ProposedDate { get; private set; }
    public string Status { get; private set; } = "";
    public bool IsOpen { get; private set; }
    public bool LegalReviewRequired { get; private set; }
    public decimal? FinancialImpactAmount { get; private set; }
    public string? FinancialImpactCurrency { get; private set; }
    public string? FinancialImpactNote { get; private set; }
    public string ProposedChangesJson { get; private set; } = "{}";
    /// <summary>The values of the proposed fields when the Variation was raised.</summary>
    public string BeforeJson { get; private set; } = "{}";
    /// <summary>The values of the changed fields immediately before and after they were applied (set once, when applied).</summary>
    public string? AppliedBeforeJson { get; private set; }
    public string? AppliedAfterJson { get; private set; }
    public VariationOutcome? Outcome { get; private set; }
    public string? OutcomeReason { get; private set; }
    public string? WorkflowRef { get; private set; }
    public Guid? ConfigReleaseId { get; private set; }
    public string ParentStatusBefore { get; private set; } = "";
    public string CreatedBy { get; private set; } = "";
    public Guid CreatedByUserId { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public DateTime? CompletedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Variation Raise(Guid tenantId, Guid organisationId, Guid contractId, string reference, int sequence, string initialStatus, string title, string? description, string reason, DateOnly? proposedDate,
        bool legalRequired, decimal? impactAmount, string? impactCurrency, string? impactNote, string proposedJson, string beforeJson, string? workflowRef, Guid? releaseId, string parentStatus, string by, Guid byUserId, DateTime nowUtc)
    {
        if (impactAmount.HasValue && string.IsNullOrWhiteSpace(impactCurrency)) throw new DomainException("currency_required", "A financial impact needs a currency.");
        return new()
        {
            Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, ContractId = contractId, Reference = Guard.NotBlank(reference, "Reference", 120), Sequence = sequence, Status = initialStatus, IsOpen = true,
            Title = Guard.NotBlank(title, "Title", 300), Description = Guard.OptionalBounded(description, "Description", 4000), Reason = Guard.NotBlank(reason, "Reason", 2000), ProposedDate = proposedDate,
            LegalReviewRequired = legalRequired, FinancialImpactAmount = impactAmount, FinancialImpactCurrency = impactCurrency?.Trim().ToUpperInvariant(), FinancialImpactNote = Guard.OptionalBounded(impactNote, "Impact note", 1000),
            ProposedChangesJson = proposedJson, BeforeJson = beforeJson, WorkflowRef = workflowRef, ConfigReleaseId = releaseId, ParentStatusBefore = parentStatus, CreatedBy = Guard.NotBlank(by, "Created by", 200),
            CreatedByUserId = byUserId, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), UpdatedUtc = nowUtc
        };
    }

    public void Edit(string title, string? description, string reason, DateOnly? proposedDate, bool legalRequired, decimal? impactAmount, string? impactCurrency, string? impactNote, string proposedJson, string beforeJson, DateTime nowUtc)
    {
        EnsureOpen();
        if (impactAmount.HasValue && string.IsNullOrWhiteSpace(impactCurrency)) throw new DomainException("currency_required", "A financial impact needs a currency.");
        Title = Guard.NotBlank(title, "Title", 300); Description = Guard.OptionalBounded(description, "Description", 4000); Reason = Guard.NotBlank(reason, "Reason", 2000); ProposedDate = proposedDate;
        LegalReviewRequired = legalRequired; FinancialImpactAmount = impactAmount; FinancialImpactCurrency = impactCurrency?.Trim().ToUpperInvariant(); FinancialImpactNote = Guard.OptionalBounded(impactNote, "Impact note", 1000);
        ProposedChangesJson = proposedJson; BeforeJson = beforeJson; UpdatedUtc = nowUtc;
    }

    public void MoveTo(string status, DateTime nowUtc) { EnsureOpen(); Status = Guard.NotBlank(status, "Status", 100); UpdatedUtc = nowUtc; }

    public void Complete(VariationOutcome outcome, string finalStatus, string? reason, string? appliedBefore, string? appliedAfter, DateTime nowUtc)
    {
        EnsureOpen();
        Outcome = outcome; Status = Guard.NotBlank(finalStatus, "Status", 100); OutcomeReason = Guard.OptionalBounded(reason, "Reason", 4000); IsOpen = false; CompletedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); UpdatedUtc = nowUtc;
        if (outcome == VariationOutcome.Applied) { AppliedBeforeJson = appliedBefore; AppliedAfterJson = appliedAfter; }
    }

    private void EnsureOpen() { if (!IsOpen) throw new DomainException("variation_closed", "This Variation has finished and cannot be changed."); }
}

public enum RenewalStatus { InProgress, Completed, Cancelled }

/// <summary>A renewal in progress or finished. Links the agreement being renewed to its successor; completing it does not alter the predecessor's content.</summary>
public sealed class Renewal : ITenantOwned
{
    private Renewal() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid PredecessorContractId { get; private set; }
    public Guid SuccessorContractId { get; private set; }
    public RenewalStatus Status { get; private set; }
    public string StartedBy { get; private set; } = "";
    public DateTime StartedUtc { get; private set; }
    public string? Note { get; private set; }
    public string CopiedFieldsJson { get; private set; } = "[]";
    public string CopiedDocumentsJson { get; private set; } = "[]";
    public string? ClosedBy { get; private set; }
    public DateTime? ClosedUtc { get; private set; }
    public string? CloseReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Renewal Start(Guid tenantId, Guid organisationId, Guid predecessor, Guid successor, string by, string? note, string copiedFields, string copiedDocuments, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, PredecessorContractId = predecessor, SuccessorContractId = successor, Status = RenewalStatus.InProgress,
        StartedBy = Guard.NotBlank(by, "Started by", 200), StartedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), Note = Guard.OptionalBounded(note, "Note", 1000), CopiedFieldsJson = copiedFields, CopiedDocumentsJson = copiedDocuments
    };

    public void Close(RenewalStatus status, string by, string? reason, DateTime nowUtc)
    {
        if (Status != RenewalStatus.InProgress) throw new DomainException("renewal_closed", "This renewal has already finished.");
        if (status == RenewalStatus.InProgress) throw new DomainException("invalid_status", "A renewal is closed as completed or cancelled.");
        Status = status; ClosedBy = Guard.NotBlank(by, "Closed by", 200); ClosedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); CloseReason = Guard.OptionalBounded(reason, "Reason", 1000);
    }
}

/// <summary>
/// A fact the lifecycle publishes for other modules (notifications, reporting). Written in the same transaction as the change, then read by the dispatcher, so an event exists exactly when the change
/// committed. Payloads hold identifiers and codes, never free text a reader might not be cleared to see.
/// </summary>
public sealed class LifecycleEvent : ITenantOwned
{
    private LifecycleEvent() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string EventType { get; private set; } = "";
    public string SubjectKind { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public Guid ContractId { get; private set; }
    public string Classification { get; private set; } = "";
    public string PayloadJson { get; private set; } = "{}";
    public string CorrelationId { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public DateTime? DispatchedUtc { get; private set; }
    public int DispatchAttempts { get; private set; }
    public string? LastError { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static LifecycleEvent Raise(Guid tenantId, Guid organisationId, string eventType, string subjectKind, Guid subjectId, Guid contractId, string classification, string payloadJson, string correlationId, string actor, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, EventType = Guard.NotBlank(eventType, "Event type", 100), SubjectKind = subjectKind, SubjectId = subjectId, ContractId = contractId,
        Classification = Guard.NotBlank(classification, "Classification", 30), PayloadJson = payloadJson, CorrelationId = correlationId, Actor = Guard.NotBlank(actor, "Actor", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void MarkDispatched(DateTime nowUtc) { DispatchedUtc = nowUtc; DispatchAttempts++; LastError = null; }
    public void MarkFailed(string error, DateTime nowUtc) { DispatchAttempts++; LastError = Guard.OptionalBounded(error, "Error", 500); }
}

/// <summary>Ties a document to a Variation (a Variation's documents are documents of the parent contract; this says which Variation and in what role).</summary>
public sealed class SubjectDocument : ITenantOwned
{
    private SubjectDocument() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string SubjectKind { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid DocumentId { get; private set; }
    /// <summary>Final (the document to be signed), Executed (the signed copy) or Supporting.</summary>
    public string Role { get; private set; } = "";
    public string AddedBy { get; private set; } = "";
    public DateTime AddedUtc { get; private set; }

    public static SubjectDocument Link(Guid tenantId, string subjectKind, Guid subjectId, Guid contractId, Guid documentId, string role, string by, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, SubjectKind = subjectKind, SubjectId = subjectId, ContractId = contractId, DocumentId = Guard.NotEmpty(documentId, nameof(documentId)),
        Role = role is "Final" or "Executed" or "Supporting" ? role : throw new DomainException("invalid_role", "A document is Final, Executed or Supporting."), AddedBy = Guard.NotBlank(by, "Added by", 200), AddedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}
