using System.Security.Cryptography;
using System.Text;
using Ecp.SharedKernel;

namespace Ecp.Configuration.Domain;

public enum DraftStatus { Draft, Validated, Published, Discarded }

/// <summary>
/// Where a draft stands in review. A submission is bound to the exact content that was submitted: any change to the content takes the draft back to <see cref="None"/>.
/// </summary>
public enum ReviewState { None, Submitted, Rejected, ReturnedForRework }

public static class ConfigHash
{
    public static string Of(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

/// <summary>
/// An editable, unpublished configuration document. Editing resets validation; only a validated draft whose content has not changed since
/// validation can be published. Configuration is data validated against closed vocabularies: it never carries code, SQL or scripts.
/// </summary>
public sealed class ConfigurationDraft : ITenantOwned
{
    private ConfigurationDraft() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// <summary>Organisation the configuration applies to; <see cref="Guid.Empty"/> for tenant-level configuration.</summary>
    public Guid OrganisationId { get; private set; }
    public string Kind { get; private set; } = "";
    public string ContentJson { get; private set; } = "{}";
    public string ContentHash { get; private set; } = "";
    public DraftStatus Status { get; private set; }
    public string? ValidatedHash { get; private set; }
    public string? ValidationJson { get; private set; }
    public int? BasedOnReleaseNumber { get; private set; }
    public string LastEditedBy { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ConfigurationDraft Create(Guid tenantId, Guid organisationId, string kind, string contentJson, int? basedOn, string editedBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, Kind = Guard.NotBlank(kind, "Kind", 100),
        ContentJson = contentJson, ContentHash = ConfigHash.Of(contentJson), Status = DraftStatus.Draft, BasedOnReleaseNumber = basedOn,
        LastEditedBy = Guard.NotBlank(editedBy, "Edited by", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), UpdatedUtc = nowUtc
    };

    /// <summary>
    /// Changes the content. Saving content that is identical to what is already held changes nothing: it does not make the saver the editor and does not undo a validation. Returns whether the content changed.
    /// </summary>
    public bool Edit(string contentJson, string editedBy, DateTime nowUtc)
    {
        if (Status is DraftStatus.Published or DraftStatus.Discarded) throw new DomainException("draft_closed", "This draft has been published or discarded; start a new draft.");
        var hash = ConfigHash.Of(contentJson);
        if (hash == ContentHash) return false;
        ContentJson = contentJson; ContentHash = hash;
        Status = DraftStatus.Draft; ValidatedHash = null; ValidationJson = null;
        LastEditedBy = Guard.NotBlank(editedBy, "Edited by", 200); UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        return true;
    }

    public void RecordValidation(bool valid, string validationJson, DateTime nowUtc)
    {
        if (Status is DraftStatus.Published or DraftStatus.Discarded) throw new DomainException("draft_closed", "This draft has been published or discarded.");
        ValidationJson = validationJson; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        if (valid) { Status = DraftStatus.Validated; ValidatedHash = ContentHash; } else { Status = DraftStatus.Draft; ValidatedHash = null; }
    }

    public bool IsPublishable => Status == DraftStatus.Validated && ValidatedHash == ContentHash;

    public void MarkPublished(DateTime nowUtc) { Status = DraftStatus.Published; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); }
    public void Discard(DateTime nowUtc) { if (Status != DraftStatus.Published) { Status = DraftStatus.Discarded; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); } }
}

/// <summary>An immutable published version of a configuration document. Rolling back publishes an earlier version's content as a new release.</summary>
public sealed class ConfigurationRelease : ITenantOwned
{
    private ConfigurationRelease() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Kind { get; private set; } = "";
    public int Number { get; private set; }
    public string ContentJson { get; private set; } = "{}";
    public string ContentHash { get; private set; } = "";
    public Guid? DraftId { get; private set; }
    public int? RollbackOfNumber { get; private set; }
    public string Note { get; private set; } = "";
    public string PublishedBy { get; private set; } = "";
    public DateTime PublishedUtc { get; private set; }

    public static ConfigurationRelease Create(Guid tenantId, Guid organisationId, string kind, int number, string contentJson, Guid? draftId, int? rollbackOf,
        string note, string publishedBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, Kind = Guard.NotBlank(kind, "Kind", 100), Number = number,
        ContentJson = contentJson, ContentHash = ConfigHash.Of(contentJson), DraftId = draftId, RollbackOfNumber = rollbackOf,
        Note = Guard.OptionalBounded(note, "Note", 1000) ?? "", PublishedBy = Guard.NotBlank(publishedBy, "Published by", 200), PublishedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

/// <summary>
/// The review of one draft, kept beside it (not on it) so that the original draft record is untouched. It holds the person who created the draft, the submission (who, when, against which content hash) and a reviewer's decision.
/// A change to the draft's content takes it back to <see cref="ReviewState.None"/>. A draft with no row here has never been put forward.
/// </summary>
public sealed class DraftReview : ITenantOwned
{
    private DraftReview() { }

    /// <summary>The draft this belongs to.</summary>
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public ReviewState State { get; private set; }
    public string? SubmittedBy { get; private set; }
    public DateTime? SubmittedUtc { get; private set; }
    /// <summary>The content hash that was submitted. A publication must find the draft still holding exactly this content.</summary>
    public string? SubmittedHash { get; private set; }
    public string? SubmissionNote { get; private set; }
    public string? DecidedBy { get; private set; }
    public DateTime? DecidedUtc { get; private set; }
    public string? DecisionComment { get; private set; }
    /// <summary>Set when the draft was made to put an earlier release back, so that publishing it records which release it restores.</summary>
    public int? RollbackOfNumber { get; private set; }
    public string? RollbackReason { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DraftReview Create(Guid tenantId, Guid draftId, string createdBy, DateTime nowUtc) => new()
    {
        Id = Guard.NotEmpty(draftId, nameof(draftId)), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), CreatedBy = Guard.NotBlank(createdBy, "Created by", 200), State = ReviewState.None, UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public bool IsSubmittedAsIs(string contentHash) => State == ReviewState.Submitted && SubmittedHash is not null && SubmittedHash == contentHash;

    public void MarkRollback(int toReleaseNumber, string reason, DateTime nowUtc) { RollbackOfNumber = toReleaseNumber; RollbackReason = Guard.NotBlank(reason, "Reason", 1000); UpdatedUtc = nowUtc; }

    /// <summary>The content changed: whoever looked at the earlier content did not look at this. Returns whether a submission was withdrawn by it.</summary>
    public bool ContentChanged(DateTime nowUtc)
    {
        var wasSubmitted = State == ReviewState.Submitted;
        State = ReviewState.None; SubmittedBy = null; SubmittedUtc = null; SubmittedHash = null; SubmissionNote = null;
        RollbackOfNumber = null; RollbackReason = null;                 // edited content is no longer the earlier release
        UpdatedUtc = nowUtc;
        return wasSubmitted;
    }

    /// <summary>Puts the validated, unchanged draft in front of a reviewer, binding the submission to its content hash.</summary>
    public void Submit(ConfigurationDraft draft, string submitter, string? note, DateTime nowUtc)
    {
        if (draft.Status is DraftStatus.Published or DraftStatus.Discarded) throw new DomainException("draft_closed", "This draft has been published or discarded; start a new draft.");
        if (!draft.IsPublishable) throw new DomainException("not_validated", "The draft must be validated, and unchanged since validation, before it is submitted for review.");
        State = ReviewState.Submitted; SubmittedBy = Guard.NotBlank(submitter, "Submitted by", 200); SubmittedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); SubmittedHash = draft.ContentHash;
        SubmissionNote = Guard.OptionalBounded(note, "Note", 1000); DecidedBy = null; DecidedUtc = null; DecisionComment = null; UpdatedUtc = nowUtc;
    }

    /// <summary>Takes the submission back. The content stays as it is; it has to be submitted again to be reviewed.</summary>
    public void Withdraw(DateTime nowUtc)
    {
        if (State != ReviewState.Submitted) throw new DomainException("not_submitted", "This draft is not waiting for review.");
        State = ReviewState.None; SubmittedBy = null; SubmittedUtc = null; SubmittedHash = null; SubmissionNote = null; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    /// <summary>A reviewer's decision not to publish this content (<see cref="ReviewState.Rejected"/>) or to ask for changes (<see cref="ReviewState.ReturnedForRework"/>). The comment is kept.</summary>
    public void Decide(ReviewState decision, string reviewer, string comment, DateTime nowUtc)
    {
        if (decision is not (ReviewState.Rejected or ReviewState.ReturnedForRework)) throw new ArgumentOutOfRangeException(nameof(decision));
        if (State != ReviewState.Submitted) throw new DomainException("not_submitted", "This draft is not waiting for review.");
        State = decision; DecidedBy = Guard.NotBlank(reviewer, "Reviewer", 200); DecidedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); DecisionComment = Guard.NotBlank(comment, "Comment", 2000); UpdatedUtc = nowUtc;
    }

    /// <summary>Marks the row as touched by a publication, so a reviewer's decision and a publication that raced each other cannot both succeed.</summary>
    public void Touch(DateTime nowUtc) => UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
}

/// <summary>Who did what to produce one release, kept beside the immutable release so that the release record itself is never changed.</summary>
public sealed class ReleaseEvidence : ITenantOwned
{
    private ReleaseEvidence() { }

    /// <summary>The release this describes.</summary>
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public string LastEditedBy { get; private set; } = "";
    public string? SubmittedBy { get; private set; }
    public DateTime? SubmittedUtc { get; private set; }
    public string? SubmittedHash { get; private set; }
    /// <summary>The rule in force when this was published: <c>SecondPerson</c> (a submission by one person, published by another), <c>SinglePerson</c> (the installation does not require a second person) or <c>System</c> (installation or setup).</summary>
    public string GovernanceMode { get; private set; } = "";

    public static ReleaseEvidence Create(Guid tenantId, Guid releaseId, ConfigurationDraft draft, DraftReview? review, string governanceMode) => new()
    {
        Id = Guard.NotEmpty(releaseId, nameof(releaseId)), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), CreatedBy = review?.CreatedBy ?? draft.LastEditedBy, LastEditedBy = draft.LastEditedBy,
        SubmittedBy = review?.SubmittedBy, SubmittedUtc = review?.SubmittedUtc, SubmittedHash = review?.SubmittedHash, GovernanceMode = Guard.NotBlank(governanceMode, "Governance", 20)
    };
}

/// <summary>One step in a draft's review, kept for good: who submitted, withdrew, rejected, returned or published it, when, against which content, and what they said.</summary>
public sealed class ConfigurationReviewEvent : ITenantOwned
{
    private ConfigurationReviewEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid DraftId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Kind { get; private set; } = "";
    /// <summary>Submitted, Withdrawn, Rejected, ReturnedForRework, Invalidated or Published.</summary>
    public string EventType { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public DateTime OccurredUtc { get; private set; }
    public string ContentHash { get; private set; } = "";
    public string? Comment { get; private set; }
    public int? ReleaseNumber { get; private set; }

    public static ConfigurationReviewEvent Create(Guid tenantId, ConfigurationDraft draft, string eventType, string actor, string? comment, DateTime nowUtc, int? releaseNumber = null) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), DraftId = draft.Id, OrganisationId = draft.OrganisationId, Kind = draft.Kind,
        EventType = Guard.NotBlank(eventType, "Event", 30), Actor = Guard.NotBlank(actor, "Actor", 200), OccurredUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
        ContentHash = draft.ContentHash, Comment = Guard.OptionalBounded(comment, "Comment", 2000), ReleaseNumber = releaseNumber
    };
}
