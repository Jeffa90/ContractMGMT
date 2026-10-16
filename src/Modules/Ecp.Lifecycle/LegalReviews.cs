using System.Text.Json;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.Identity.Domain;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

public sealed record LegalQueueFilter(LegalReviewStatus? Status = null, Guid? AssignedToUserId = null, bool AssignedToMe = false, bool Unassigned = false, Guid? CategoryId = null, Guid? SupplierId = null,
    int? MinAgeDays = null, bool OutstandingForMe = false, bool OpenOnly = true, int Page = 1, int PageSize = 25);

public sealed record LegalQueueItem(
    Guid ReviewId, string SubjectKind, Guid SubjectId, Guid ContractId, string AgreementReference, string ContractTitle, string? VariationReference, string SupplierName, string CategoryName, string Classification,
    LegalReviewStatus Status, string StatusDisplay, int CurrentRound, Guid? AssignedToUserId, string? AssignedToName, string SubmittedByName, DateTime SubmittedUtc, DateTime LastActivityUtc, int AgeDays,
    string Outstanding, bool OutstandingForMe);

public sealed record LegalCommentItem(Guid Id, Guid? ParentCommentId, Guid AuthorUserId, string AuthorName, string Side, string Body, DateTime CreatedUtc);

public sealed record LegalAttachmentItem(Guid Id, Guid? CommentId, Guid DocumentId, int VersionNumber, string Sha256, LegalAttachmentKind Kind, string FileName, string AddedBy, DateTime AddedUtc, string Classification);

public sealed record LegalRoundItem(Guid Id, int Number, LegalRoundOutcome Outcome, string SubmittedByName, DateTime SubmittedUtc, string? SubmissionNote, string? ReviewerName, DateTime? StartedUtc,
    string? ReworkReason, string? ClosedBy, DateTime? ClosedUtc, IReadOnlyList<RoundDocument> Documents, IReadOnlyList<LegalCommentItem> Comments, IReadOnlyList<LegalAttachmentItem> Attachments, int HiddenAttachments);

public sealed record RoundDocument(Guid DocumentId, string Title, string DocumentType, int VersionNumber, string Sha256);

public sealed record LegalTimelineItem(DateTime OccurredUtc, string Kind, string Actor, string? Detail, int? Round);

/// <summary>What the caller may do on a review right now, so a page offers only real actions (the server checks each again).</summary>
public sealed record LegalActions(bool CanSubmit, bool CanRecall, bool CanAssign, bool CanBegin, bool CanComment, bool CanReply, bool CanAttachMarkup, bool CanAttachSupporting, bool CanReturn, bool CanClear, bool CanResubmit);

public sealed record LegalReviewDetail(
    Guid ReviewId, string SubjectKind, Guid SubjectId, Guid ContractId, string AgreementReference, string ContractTitle, string? VariationReference, string Classification, LegalReviewStatus Status, string StatusDisplay,
    int CurrentRound, Guid? AssignedToUserId, string? AssignedToName, string SubmittedByName, DateTime SubmittedUtc, string? ClearedBy, DateTime? ClearedUtc, IReadOnlyList<LegalRoundItem> Rounds,
    IReadOnlyList<LegalTimelineItem> Timeline, string Outstanding, LegalActions Actions);

public sealed record LegalReviewer(Guid UserId, string DisplayName);

public sealed record SubmitLegalRequest(string SubjectKind, Guid SubjectId, string? Note = null, string? ClientRequestId = null);

public sealed record AddLegalCommentRequest(Guid ReviewId, string Body, Guid? ParentCommentId = null);

public sealed record AddLegalAttachmentRequest(Guid ReviewId, LegalAttachmentKind Kind, string FileName, string MimeType, Stream Content, string Title, Guid? ExistingDocumentId = null, Guid? CommentId = null,
    string? Classification = null);

/// <summary>
/// Structured Legal review of a contract or a Variation. A review has rounds that are never overwritten; a return for rework needs a reason and closes the round; the owner responds and resubmits for the next round.
/// Clearance records Legal's view only. It does not register, lock or make anything Effective.
/// </summary>
public interface ILegalReviewService
{
    Task<PagedResult<LegalQueueItem>> QueueAsync(LegalQueueFilter filter, CancellationToken ct = default);
    Task<LegalReviewDetail?> FindForSubjectAsync(string subjectKind, Guid subjectId, CancellationToken ct = default);
    Task<LegalReviewDetail> GetAsync(Guid reviewId, CancellationToken ct = default);
    Task<IReadOnlyList<LegalReviewer>> ReviewersAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>Submits for Legal review, resubmits after rework, or returns a cleared contract to Legal after a controlled change. Opens the next round.</summary>
    Task<Guid> SubmitAsync(SubmitLegalRequest request, CancellationToken ct = default);
    Task RecallAsync(string subjectKind, Guid subjectId, string reason, CancellationToken ct = default);
    Task AssignAsync(Guid reviewId, Guid reviewerUserId, CancellationToken ct = default);
    Task BeginAsync(Guid reviewId, CancellationToken ct = default);
    Task<Guid> CommentAsync(AddLegalCommentRequest request, CancellationToken ct = default);
    Task<LegalAttachmentItem> AttachAsync(AddLegalAttachmentRequest request, CancellationToken ct = default);
    Task ReturnForReworkAsync(Guid reviewId, string reason, CancellationToken ct = default);
    Task ClearAsync(Guid reviewId, string? note, CancellationToken ct = default);
}

internal sealed class LegalReviewService(
    LifecycleDbContext db, LifecycleEngine engine, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, CurrentUser currentUser, IAccessService access,
    IAccessDirectory directory, IContractLifecyclePort port, ISecuredContractService contracts, ISecuredContractDocuments secureDocuments, IDocumentService documentService,
    IDocumentQuery documentQuery, LifecycleEvents events, VisibleContracts visible) : ILegalReviewService
{
    private static readonly string[] SubmitRoles = ["submitLegal", "resubmitLegal", "returnToLegal"];

    private string Label => engine.ActorLabel;
    private Guid UserId => currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");

    // ---- reading --------------------------------------------------------------------------------------------------------------------------------

    public async Task<PagedResult<LegalQueueItem>> QueueAsync(LegalQueueFilter f, CancellationToken ct = default)
    {
        var me = currentUser.UserId;
        var query = db.LegalReviews.AsNoTracking().AsQueryable();
        if (f.OpenOnly) query = query.Where(r => r.Status == LegalReviewStatus.AwaitingLegal || r.Status == LegalReviewStatus.InReview || r.Status == LegalReviewStatus.ReworkRequired);
        if (f.Status is { } status) query = query.Where(r => r.Status == status);
        if (f.AssignedToUserId is { } assignee) query = query.Where(r => r.AssignedToUserId == assignee);
        if (f.AssignedToMe && me is { } mine) query = query.Where(r => r.AssignedToUserId == mine);
        if (f.Unassigned) query = query.Where(r => r.AssignedToUserId == null);
        if (f.MinAgeDays is { } days) { var cutoff = clock.UtcNow.AddDays(-days); query = query.Where(r => r.SubmittedUtc <= cutoff); }
        var rows = await query.OrderBy(r => r.SubmittedUtc).Take(2000).ToListAsync(ct);

        // Only reviews of contracts the caller may read are listed, at the classification they may see: the same scopes as the register.
        var seen = await visible.GetAsync(rows.Select(r => r.ContractId), new ContractFilter(CategoryId: f.CategoryId, SupplierId: f.SupplierId), ct);
        var variations = await db.Variations.AsNoTracking().Where(v => rows.Select(r => r.SubjectId).Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Reference, ct);
        var now = clock.UtcNow;
        var items = new List<LegalQueueItem>();
        foreach (var r in rows)
        {
            if (!seen.TryGetValue(r.ContractId, out var c)) continue;
            var forMe = await OutstandingForMeAsync(r, me, ct);
            if (f.OutstandingForMe && !forMe) continue;
            items.Add(new LegalQueueItem(r.Id, r.SubjectKind, r.SubjectId, r.ContractId, c.AgreementReference, c.Title, r.SubjectKind == SubjectKinds.Variation ? variations.GetValueOrDefault(r.SubjectId) : null,
                c.SupplierName, c.CategoryName, c.Classification, r.Status, StatusDisplay(r.Status), r.CurrentRound, r.AssignedToUserId, r.AssignedToName, r.SubmittedByName, r.SubmittedUtc, r.LastActivityUtc,
                (int)Math.Max(0, (now - r.SubmittedUtc).TotalDays), OutstandingText(r.Status), forMe));
        }
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        return new PagedResult<LegalQueueItem>(items.Skip((page - 1) * size).Take(size).ToList(), items.Count, page, size);
    }

    private readonly Dictionary<string, bool> _legalCache = [];

    /// <summary>Whether the action now waits on the caller: Legal to begin or finish, or the owner to respond.</summary>
    private async Task<bool> OutstandingForMeAsync(LegalReview r, Guid? me, CancellationToken ct)
    {
        if (me is null) return false;
        switch (r.Status)
        {
            case LegalReviewStatus.AwaitingLegal:
                return r.AssignedToUserId == me || (r.AssignedToUserId is null && await HoldsAsync("legal.review", r, ct));
            case LegalReviewStatus.InReview:
                return r.AssignedToUserId == me;
            case LegalReviewStatus.ReworkRequired:
                return r.SubmittedByUserId == me || await HoldsAsync("legal.respond", r, ct);
            default: return false;
        }
    }

    private async Task<bool> HoldsAsync(string permission, LegalReview r, CancellationToken ct)
    {
        var key = permission + ":" + r.ContractId;
        if (_legalCache.TryGetValue(key, out var cached)) return cached;
        var descriptor = await contracts.DescribeAsync(r.ContractId, ct);
        var ok = (await access.CheckSilentAsync(permission, descriptor, ct)).Allowed;
        _legalCache[key] = ok;
        return ok;
    }

    private static string StatusDisplay(LegalReviewStatus s) => s switch
    {
        LegalReviewStatus.AwaitingLegal => "Awaiting Legal",
        LegalReviewStatus.InReview => "In review",
        LegalReviewStatus.ReworkRequired => "Rework required",
        LegalReviewStatus.Cleared => "Legal cleared",
        _ => "Withdrawn"
    };

    private static string OutstandingText(LegalReviewStatus s) => s switch
    {
        LegalReviewStatus.AwaitingLegal => "Waiting for Legal to begin the review.",
        LegalReviewStatus.InReview => "Legal is reviewing.",
        LegalReviewStatus.ReworkRequired => "Waiting for the owner to respond and resubmit.",
        LegalReviewStatus.Cleared => "Cleared by Legal. Not yet registered.",
        _ => "Withdrawn."
    };

    public async Task<LegalReviewDetail?> FindForSubjectAsync(string subjectKind, Guid subjectId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(r => r.SubjectKind == subjectKind && r.SubjectId == subjectId, ct);
        return review is null ? null : await BuildDetailAsync(review, subject, ct);
    }

    public async Task<LegalReviewDetail> GetAsync(Guid reviewId, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct); // outside the caller's scope or classification: 404
        return await BuildDetailAsync(review, subject, ct);
    }

    private async Task<LegalReviewDetail> BuildDetailAsync(LegalReview review, SubjectView subject, CancellationToken ct)
    {
        var rounds = await db.LegalRounds.AsNoTracking().Where(r => r.ReviewId == review.Id).OrderBy(r => r.Number).ToListAsync(ct);
        var comments = await db.LegalComments.AsNoTracking().Where(c => c.ReviewId == review.Id).OrderBy(c => c.CreatedUtc).ThenBy(c => c.Id).ToListAsync(ct);
        var attachments = await db.LegalAttachments.AsNoTracking().Where(a => a.ReviewId == review.Id).OrderBy(a => a.AddedUtc).ToListAsync(ct);
        var events = await db.LegalEvents.AsNoTracking().Where(e => e.ReviewId == review.Id).OrderBy(e => e.Sequence).ToListAsync(ct);
        // A document classified above the caller's access is hidden altogether, even though the contract (and the review) is visible.
        var visibleDocs = (await secureDocuments.ListAsync(subject.ContractId, ct)).ToDictionary(d => d.DocumentId);
        var numbers = rounds.ToDictionary(r => r.Id, r => r.Number);

        var items = rounds.Select(r =>
        {
            var docs = (JsonSerializer.Deserialize<List<RoundDocument>>(r.DocumentsJson) ?? []).Where(d => visibleDocs.ContainsKey(d.DocumentId)).ToList();
            var atts = attachments.Where(a => a.RoundId == r.Id).ToList();
            var shown = atts.Where(a => visibleDocs.ContainsKey(a.DocumentId)).Select(a => new LegalAttachmentItem(a.Id, a.CommentId, a.DocumentId, a.VersionNumber, a.Sha256, a.Kind, a.FileName, a.AddedBy, a.AddedUtc, subject.Classification)).ToList();
            return new LegalRoundItem(r.Id, r.Number, r.Outcome, r.SubmittedByName, r.SubmittedUtc, r.SubmissionNote, r.ReviewerName, r.StartedUtc, r.ReworkReason, r.ClosedBy, r.ClosedUtc, docs,
                comments.Where(c => c.RoundId == r.Id).Select(c => new LegalCommentItem(c.Id, c.ParentCommentId, c.AuthorUserId, c.AuthorName, c.Side, c.Body, c.CreatedUtc)).ToList(), shown, atts.Count - shown.Count);
        }).ToList();

        var timeline = events.Select(e => new LegalTimelineItem(e.OccurredUtc, e.Kind, e.Actor, e.Detail, e.RoundId is { } rid && numbers.TryGetValue(rid, out var n) ? n : null)).ToList();
        var variationRef = subject.IsVariation ? subject.Variation!.Reference : null;
        var title = subject.IsVariation ? subject.Variation!.Title : subject.Contract.Title;
        return new LegalReviewDetail(review.Id, review.SubjectKind, review.SubjectId, review.ContractId, subject.Contract.AgreementReference, title, variationRef, subject.Classification, review.Status,
            StatusDisplay(review.Status), review.CurrentRound, review.AssignedToUserId, review.AssignedToName, review.SubmittedByName, review.SubmittedUtc, review.ClearedBy, review.ClearedUtc, items, timeline,
            OutstandingText(review.Status), await ActionsAsync(review, subject, ct));
    }

    private async Task<LegalActions> ActionsAsync(LegalReview review, SubjectView subject, CancellationToken ct)
    {
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        async Task<bool> Can(string permission) => (await access.CheckSilentAsync(permission, descriptor, ct)).Allowed;
        bool Offers(string role) => resolved.Definition.From(subject.Status).Any(t => t.Role == role);
        var legal = await Can("legal.review");
        var respond = await Can("legal.respond");
        var canSubmitPerm = await Can(subject.IsVariation ? "variations.create" : "contracts.submit_legal");
        var open = review.IsOpen;
        return new LegalActions(
            CanSubmit: canSubmitPerm && SubmitRoles.Any(Offers),
            CanRecall: canSubmitPerm && Offers("recallLegal"),
            CanAssign: legal && review.IsOpen,
            CanBegin: legal && Offers("beginLegal"),
            CanComment: legal && review.Status == LegalReviewStatus.InReview,
            CanReply: (respond || canSubmitPerm) && open,
            CanAttachMarkup: await Can("legal.markup") && review.Status == LegalReviewStatus.InReview,
            CanAttachSupporting: (respond || canSubmitPerm) && open,
            CanReturn: legal && Offers("returnLegal"),
            CanClear: await Can("legal.clear") && Offers("clearLegal"),
            CanResubmit: canSubmitPerm && Offers("resubmitLegal"));
    }

    public async Task<IReadOnlyList<LegalReviewer>> ReviewersAsync(Guid organisationId, CancellationToken ct = default)
    {
        var ids = await directory.HoldersOfRoleAsync(RoleCodes.Legal, organisationId, ct);
        var list = new List<LegalReviewer>();
        foreach (var id in ids) list.Add(new LegalReviewer(id, await directory.DisplayNameAsync(id, ct) ?? id.ToString()));
        return list.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- steps ----------------------------------------------------------------------------------------------------------------------------------

    public async Task<Guid> SubmitAsync(SubmitLegalRequest r, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(r.SubjectKind, r.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => SubmitRoles.Contains(t.Role))
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} cannot be submitted to Legal while it is {resolved.Definition.DisplayOf(subject.Status)}.");
        // What goes to Legal is fixed now: the documents the submitter can see, at the versions they are at.
        var docs = await SnapshotDocumentsAsync(subject, ct);
        Guid reviewId = Guid.Empty;
        await engine.ExecuteAsync(new TransitionCommand(r.SubjectKind, r.SubjectId, transition.Code, r.Note, null, subject.Status, r.ClientRequestId, AllowManaged: true), async (scope, c) =>
        {
            var now = scope.NowUtc;
            var name = await directory.DisplayNameAsync(UserId, c) ?? Label;
            var review = await db.LegalReviews.FirstOrDefaultAsync(x => x.SubjectKind == r.SubjectKind && x.SubjectId == r.SubjectId, c);
            var resubmission = review is not null;
            if (review is null)
            {
                review = LegalReview.Open(tenant.RequireTenantId(), scope.Subject.OrganisationId, r.SubjectKind, r.SubjectId, scope.Subject.ContractId, UserId, name, now);
                db.LegalReviews.Add(review);
            }
            else
            {
                if (review.Status == LegalReviewStatus.Cleared)
                {
                    // Returned to Legal after clearance: the cleared round stays as it was, and a new round opens.
                }
                review.Resubmit(UserId, name, now);
            }
            var round = LegalRound.Open(tenant.RequireTenantId(), review.Id, review.CurrentRound, UserId, name, r.Note, JsonSerializer.Serialize(docs), now);
            db.LegalRounds.Add(round);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), review.Id, round.Id, resubmission ? "Resubmitted" : "Submitted", Label, $"Round {review.CurrentRound}", round.Id, now, await engine.NextLegalSequenceAsync(review.Id, c)));
            await db.SaveChangesAsync(c);
            reviewId = review.Id;
            events.Raise(resubmission ? LifecycleEventTypes.LegalResubmitted : LifecycleEventTypes.LegalSubmitted, scope.Subject, new { reviewId = review.Id, round = review.CurrentRound, submitterUserId = UserId, assignedToUserId = review.AssignedToUserId });
            audit.Record(new AuditEntry(resubmission ? "legal.resubmitted" : "legal.submitted", nameof(LegalReview), review.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Classification: scope.Subject.Classification,
                Reason: r.Note, NewValuesJson: AuditJson.Of(new { subjectKind = r.SubjectKind, subjectId = r.SubjectId, round = review.CurrentRound, documents = docs.Count })));
        }, ct);
        return reviewId;
    }

    private async Task<List<RoundDocument>> SnapshotDocumentsAsync(SubjectView subject, CancellationToken ct)
    {
        var visibleDocs = await secureDocuments.ListAsync(subject.ContractId, ct);
        IEnumerable<ContractDocumentItem> chosen = visibleDocs.Where(d => d.Status == nameof(DocumentStatus.Active));
        if (subject.IsVariation)
        {
            var linked = await db.SubjectDocuments.AsNoTracking().Where(l => l.SubjectKind == SubjectKinds.Variation && l.SubjectId == subject.Id).Select(l => l.DocumentId).ToListAsync(ct);
            chosen = chosen.Where(d => linked.Contains(d.DocumentId));
        }
        return chosen.Select(d => new RoundDocument(d.DocumentId, d.Title, d.DocumentType, d.Versions, d.Sha256 ?? "")).ToList();
    }

    public async Task RecallAsync(string subjectKind, Guid subjectId, string reason, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "recallLegal")
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} cannot be recalled from Legal while it is {resolved.Definition.DisplayOf(subject.Status)}.");
        await engine.ExecuteAsync(new TransitionCommand(subjectKind, subjectId, transition.Code, null, reason, subject.Status, AllowManaged: true), (scope, c) => WithdrawOpenAsync(scope, reason, c), ct);
    }

    /// <summary>Withdraws the open review and round when the subject leaves Legal's hands without a decision (recalled, or withdrawn altogether). Both are kept.</summary>
    internal async Task WithdrawOpenAsync(TransitionScope scope, string? reason, CancellationToken c)
    {
        var review = await db.LegalReviews.FirstOrDefaultAsync(x => x.SubjectKind == scope.Subject.Kind && x.SubjectId == scope.Subject.Id, c);
        if (review is null || !review.IsOpen) return;
        var now = scope.NowUtc;
        var round = await db.LegalRounds.FirstOrDefaultAsync(x => x.ReviewId == review.Id && x.Number == review.CurrentRound, c);
        round?.Withdraw(Label, reason, now);
        review.Withdraw(now);
        db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), review.Id, round?.Id, "Withdrawn", Label, reason, null, now, await engine.NextLegalSequenceAsync(review.Id, c)));
        await db.SaveChangesAsync(c);
        events.Raise(LifecycleEventTypes.LegalWithdrawn, scope.Subject, new { reviewId = review.Id, submitterUserId = review.SubmittedByUserId });
        audit.Record(new AuditEntry("legal.withdrawn", nameof(LegalReview), review.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: reason, Classification: scope.Subject.Classification));
    }

    public async Task AssignAsync(Guid reviewId, Guid reviewerUserId, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        await access.RequireAsync("legal.review", await engine.DescribeAsync(subject, null, ct), ct);
        var eligible = await directory.HoldersOfRoleAsync(RoleCodes.Legal, subject.OrganisationId, ct);
        if (!eligible.Contains(reviewerUserId)) throw new DomainException("not_a_reviewer", "That person does not hold the Legal role for this organisation.");
        var name = await directory.DisplayNameAsync(reviewerUserId, ct) ?? reviewerUserId.ToString();
        await uow.ExecuteAsync(async c =>
        {
            var now = clock.UtcNow;
            var round = await db.LegalRounds.FirstAsync(x => x.ReviewId == review.Id && x.Number == review.CurrentRound, c);
            review.Assign(reviewerUserId, name, now);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), review.Id, round.Id, "Assigned", Label, $"Assigned to {name}", reviewerUserId, now, await engine.NextLegalSequenceAsync(review.Id, c)));
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.LegalAssigned, subject, new { reviewId, assignedTo = reviewerUserId });
            audit.Record(new AuditEntry("legal.assigned", nameof(LegalReview), review.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification,
                NewValuesJson: AuditJson.Of(new { assignedTo = reviewerUserId, name })));
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public async Task BeginAsync(Guid reviewId, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "beginLegal")
            ?? throw new DomainException("invalid_transition", $"Legal can begin a review only when the {subject.Noun} is waiting for Legal; it is {resolved.Definition.DisplayOf(subject.Status)}.");
        await engine.ExecuteAsync(new TransitionCommand(review.SubjectKind, review.SubjectId, transition.Code, ExpectedStatus: subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var tracked = await db.LegalReviews.FirstAsync(x => x.Id == reviewId, c);
            var round = await db.LegalRounds.FirstAsync(x => x.ReviewId == reviewId && x.Number == tracked.CurrentRound, c);
            var name = await directory.DisplayNameAsync(UserId, c) ?? Label;
            tracked.Begin(UserId, name, scope.NowUtc);
            round.Start(UserId, name, scope.NowUtc);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), reviewId, round.Id, "Started", Label, $"Round {round.Number} started by {name}", null, scope.NowUtc, await engine.NextLegalSequenceAsync(reviewId, c)));
            await db.SaveChangesAsync(c);
            events.Raise(LifecycleEventTypes.LegalStarted, scope.Subject, new { reviewId, round = round.Number, submitterUserId = tracked.SubmittedByUserId });
            audit.Record(new AuditEntry("legal.started", nameof(LegalReview), reviewId.ToString(), OrganisationId: scope.Subject.OrganisationId, Classification: scope.Subject.Classification, NewValuesJson: AuditJson.Of(new { round = round.Number })));
        }, ct);
    }

    public async Task<Guid> CommentAsync(AddLegalCommentRequest r, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.FirstOrDefaultAsync(x => x.Id == r.ReviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await engine.RequireReadAsync(descriptor, ct);
        // Legal comments during its review; the owner replies while the review is open (and responds to a return for rework).
        string side;
        if ((await access.CheckSilentAsync("legal.review", descriptor, ct)).Allowed && review.Status == LegalReviewStatus.InReview) side = "Legal";
        else
        {
            var owner = await access.CheckSilentAsync(subject.IsVariation ? "variations.create" : "legal.respond", descriptor, ct);
            var respond = owner.Allowed || (await access.CheckSilentAsync("legal.respond", descriptor, ct)).Allowed;
            if (!respond) throw new ForbiddenException("You cannot comment on this review.");
            if (!review.IsOpen) throw new DomainException("review_closed", "This Legal review has finished, so it takes no more comments.");
            side = "Owner";
        }
        var name = await directory.DisplayNameAsync(UserId, ct) ?? Label;
        Guid id = Guid.Empty;
        await uow.ExecuteAsync(async c =>
        {
            var round = await db.LegalRounds.FirstAsync(x => x.ReviewId == review.Id && x.Number == review.CurrentRound, c);
            if (r.ParentCommentId is { } parent && !await db.LegalComments.AnyAsync(x => x.Id == parent && x.ReviewId == review.Id, c))
                throw new DomainException("parent_not_found", "The comment being replied to was not found in this review.");
            var comment = LegalComment.Add(tenant.RequireTenantId(), review.Id, round.Id, r.ParentCommentId, UserId, name, side, r.Body, subject.Classification, clock.UtcNow);
            db.LegalComments.Add(comment);
            review.Touch(clock.UtcNow);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), review.Id, round.Id, side == "Legal" ? "LegalComment" : "OwnerComment", Label, r.ParentCommentId is null ? "Comment" : "Reply", comment.Id, clock.UtcNow, await engine.NextLegalSequenceAsync(review.Id, c)));
            await engine.SaveAsync(c);
            id = comment.Id;
            events.Raise(LifecycleEventTypes.LegalCommented, subject, new { reviewId = review.Id, commentId = comment.Id, side, submitterUserId = review.SubmittedByUserId, assignedToUserId = review.AssignedToUserId });
            audit.Record(new AuditEntry("legal.commented", nameof(LegalReview), review.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification, NewValuesJson: AuditJson.Of(new { commentId = comment.Id, side, round = round.Number })));
            await db.SaveChangesAsync(c);
        }, ct);
        return id;
    }

    public async Task<LegalAttachmentItem> AttachAsync(AddLegalAttachmentRequest r, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.FirstOrDefaultAsync(x => x.Id == r.ReviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await engine.RequireReadAsync(descriptor, ct);
        var settings = resolved.Definition.Settings.Documents;
        if (r.Kind == LegalAttachmentKind.Markup)
        {
            await access.RequireAsync("legal.markup", descriptor, ct);
            if (review.Status != LegalReviewStatus.InReview) throw new DomainException("review_state", "Mark-ups are added while Legal is reviewing.");
        }
        else
        {
            var okOwner = (await access.CheckSilentAsync(subject.IsVariation ? "variations.create" : "legal.respond", descriptor, ct)).Allowed || (await access.CheckSilentAsync("legal.respond", descriptor, ct)).Allowed;
            if (!okOwner && !(await access.CheckSilentAsync("legal.markup", descriptor, ct)).Allowed) throw new ForbiddenException("You cannot attach documents to this review.");
            if (!review.IsOpen) throw new DomainException("review_closed", "This Legal review has finished.");
        }
        var scale = await access.ClassificationScaleAsync(ct);
        var requested = scale.Canonical(r.Classification) ?? (string.IsNullOrWhiteSpace(r.Classification) ? null : throw new DomainException("invalid_classification", $"Classification '{r.Classification}' is not recognised."));
        // A mark-up is at least as classified as its contract (it may be higher) and never above the caller's own access.
        var level = requested is not null && scale.Rank(requested) >= scale.Rank(subject.Classification) ? requested : subject.Classification;
        if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = level }, ct)).Allowed) throw new ForbiddenException("You cannot classify a document above your own access.");

        DocumentVersionDto dto;
        if (r.ExistingDocumentId is { } existing)
        {
            var summary = await documentQuery.GetAsync(existing, ct);
            if (summary?.ContractId != subject.ContractId) throw new ResourceNotFoundException();
            if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = scale.Rank(summary.Classification) > scale.Rank(level) ? summary.Classification : level }, ct)).Allowed) throw new ResourceNotFoundException();
            dto = await documentService.AddVersionAsync(new AddVersionRequest(existing, r.FileName, r.MimeType, r.Content), ct);
        }
        else
        {
            var supportingType = r.Kind == LegalAttachmentKind.Markup ? settings.LegalMarkupType : "SupportingDocument";
            dto = await documentService.AddAsync(new AddDocumentRequest(subject.OrganisationId, subject.ContractId, supportingType, r.Title, r.FileName, r.MimeType, r.Content, level), ct);
        }
        var item = await uow.ExecuteAsync(async c =>
        {
            var round = await db.LegalRounds.FirstAsync(x => x.ReviewId == review.Id && x.Number == review.CurrentRound, c);
            var attachment = LegalAttachment.Add(tenant.RequireTenantId(), review.Id, round.Id, r.CommentId, dto.DocumentId, dto.VersionNumber, dto.Sha256, r.Kind, dto.FileName, Label, clock.UtcNow);
            db.LegalAttachments.Add(attachment);
            if (subject.IsVariation && !await db.SubjectDocuments.AnyAsync(l => l.SubjectKind == SubjectKinds.Variation && l.SubjectId == subject.Id && l.DocumentId == dto.DocumentId, c))
                db.SubjectDocuments.Add(SubjectDocument.Link(tenant.RequireTenantId(), SubjectKinds.Variation, subject.Id, subject.ContractId, dto.DocumentId, "Supporting", Label, clock.UtcNow));
            review.Touch(clock.UtcNow);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), review.Id, round.Id, r.Kind == LegalAttachmentKind.Markup ? "MarkupAdded" : "DocumentAdded", Label, $"{dto.FileName} (version {dto.VersionNumber})", attachment.Id, clock.UtcNow, await engine.NextLegalSequenceAsync(review.Id, c)));
            await engine.SaveAsync(c);
            audit.Record(new AuditEntry("legal.attachment_added", nameof(LegalReview), review.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: level,
                NewValuesJson: AuditJson.Of(new { documentId = dto.DocumentId, dto.VersionNumber, dto.Sha256, kind = r.Kind.ToString(), classification = level })));
            await db.SaveChangesAsync(c);
            return new LegalAttachmentItem(attachment.Id, attachment.CommentId, attachment.DocumentId, attachment.VersionNumber, attachment.Sha256, attachment.Kind, attachment.FileName, attachment.AddedBy, attachment.AddedUtc, level);
        }, ct);
        return item;
    }

    public async Task ReturnForReworkAsync(Guid reviewId, string reason, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "returnLegal")
            ?? throw new DomainException("invalid_transition", $"A review can be returned only while Legal is reviewing; the {subject.Noun} is {resolved.Definition.DisplayOf(subject.Status)}.");
        await engine.ExecuteAsync(new TransitionCommand(review.SubjectKind, review.SubjectId, transition.Code, reason, null, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var tracked = await db.LegalReviews.FirstAsync(x => x.Id == reviewId, c);
            var round = await db.LegalRounds.FirstAsync(x => x.ReviewId == reviewId && x.Number == tracked.CurrentRound, c);
            round.Return(Label, reason, scope.NowUtc);
            tracked.ReturnForRework(scope.NowUtc);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), reviewId, round.Id, "Returned", Label, $"Round {round.Number} returned for rework", null, scope.NowUtc, await engine.NextLegalSequenceAsync(reviewId, c)));
            await db.SaveChangesAsync(c);
            events.Raise(LifecycleEventTypes.LegalReturned, scope.Subject, new { reviewId, round = round.Number, submitterUserId = tracked.SubmittedByUserId });
            audit.Record(new AuditEntry("legal.returned", nameof(LegalReview), reviewId.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: reason, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { round = round.Number })));
        }, ct);
    }

    public async Task ClearAsync(Guid reviewId, string? note, CancellationToken ct = default)
    {
        var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reviewId, ct) ?? throw new ResourceNotFoundException();
        var subject = await engine.LoadSubjectAsync(review.SubjectKind, review.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "clearLegal")
            ?? throw new DomainException("invalid_transition", $"A review can be cleared only while Legal is reviewing; the {subject.Noun} is {resolved.Definition.DisplayOf(subject.Status)}.");
        await engine.ExecuteAsync(new TransitionCommand(review.SubjectKind, review.SubjectId, transition.Code, note, null, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var tracked = await db.LegalReviews.FirstAsync(x => x.Id == reviewId, c);
            var round = await db.LegalRounds.FirstAsync(x => x.ReviewId == reviewId && x.Number == tracked.CurrentRound, c);
            // The contract's stamp after the transition is what a later change is compared with.
            var stamp = (await port.FindAsync(scope.Subject.ContractId, c))?.UpdatedUtc ?? scope.NowUtc;
            round.Clear(Label, scope.NowUtc);
            tracked.Clear(Label, stamp, scope.NowUtc);
            db.LegalEvents.Add(LegalEvent.Record(tenant.RequireTenantId(), reviewId, round.Id, "Cleared", Label, note ?? $"Round {round.Number} cleared", null, scope.NowUtc, await engine.NextLegalSequenceAsync(reviewId, c)));
            await db.SaveChangesAsync(c);
            events.Raise(LifecycleEventTypes.LegalCleared, scope.Subject, new { reviewId, round = round.Number, submitterUserId = tracked.SubmittedByUserId });
            audit.Record(new AuditEntry("legal.cleared", nameof(LegalReview), reviewId.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: note, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { round = round.Number, note = "Legal clearance is not register authorisation" })));
        }, ct);
    }
}

/// <summary>The contracts, among those named, that the caller may read, with their listing facts. The register's own scope and classification rules apply; nothing is added or widened.</summary>
internal sealed class VisibleContracts(ISecuredContractService contracts)
{
    public async Task<Dictionary<Guid, ContractListItem>> GetAsync(IEnumerable<Guid> ids, ContractFilter? extra, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ContractListItem>();
        foreach (var chunk in ids.Distinct().Chunk(200))
        {
            var page = await contracts.ListAsync((extra ?? new ContractFilter()) with { Ids = chunk, Page = 1, PageSize = 200 }, ct);
            foreach (var item in page.Items) result[item.Id] = item;
        }
        return result;
    }
}
